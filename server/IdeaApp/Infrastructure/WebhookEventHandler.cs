using IdeaApp.Domain;
using Microsoft.Extensions.Options;

namespace IdeaApp.Infrastructure;

/// <summary>
/// webhook 订阅者：把事件总线上的事件变成一次待投递的任务。
///
/// <para>
/// <b>必须注册成 Singleton</b>：事件总线在**根** provider 里用
/// <c>sp.GetServices&lt;IEventHandler&gt;()</c> 解析订阅者，
/// 注册成 Scoped 会在开发期（<c>ValidateScopes</c>）抛
/// <see cref="InvalidOperationException"/>，在生产期则泄漏作用域。
/// </para>
///
/// <para>
/// 命中规则（spec §12.1）：
/// <list type="number">
/// <item>webhook 必须**启用**；</item>
/// <item><c>global</c> 作用域收全部事件；<c>project</c> 作用域只收事件所属项目就是它的项目的事件；</item>
/// <item>再叠加：订阅的事件类型里要有这个事件（订阅列表为空 = 不订阅任何）；</item>
/// <item>再叠加：<c>project</c> 作用域的 webhook，**它绑定的项目当前不能在回收站里** ——
/// 项目级 webhook 随项目隐式失效，项目恢复后自动恢复投递。</item>
/// </list>
/// </para>
///
/// <para>
/// 这一层只做「匹配 + 渲染 + 入队」，**不碰网络**：
/// 投递在后台 worker 里进行，写路径不等它。
/// </para>
/// </summary>
public sealed class WebhookEventHandler(
    WebhookRepository webhooks,
    ProjectRepository projects,
    WebhookDeliveryWorker worker,
    IOptions<AppOptions> options,
    ILogger<WebhookEventHandler> logger) : IEventHandler
{
    /// <summary>项目作用域的标识（与 <c>WebhookService.ScopeProject</c> 同一个值）。</summary>
    private const string ProjectScope = "project";

    /// <summary>
    /// 处理一个事件：找出该收它的 webhook，逐个渲染好请求体投进投递队列。
    /// </summary>
    public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        // 事件所属的项目：项目事件与想法事件**都带项目快照**
        // （spec §11.2：想法事件的结构里也有 project），所以直接读它。
        var projectId = evt.Project?.Id;

        // 项目级 webhook 的所属项目是不是在回收站里（spec §12.1）：
        // 在回收站期间**不投递**。同一个项目可能被多个 webhook 绑着，
        // 查一次库就够 —— 结果按项目 id 缓存下来复用。
        var inTrash = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var webhook in webhooks.ListEnabledFor(projectId))
        {
            if (!Matches(webhook, evt, projectId))
            {
                continue;
            }

            if (webhook.Scope == ProjectScope && IsProjectInTrash(webhook.ProjectId, inTrash))
            {
                continue;
            }

            var payload = Render(webhook, evt);
            if (payload is null)
            {
                continue;
            }

            worker.Enqueue(new WebhookDeliveryJob(webhook, evt, payload));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 这个 webhook 绑定的项目当前是不是在回收站里。
    /// </summary>
    /// <param name="projectId">webhook 绑定的项目 id。</param>
    /// <param name="cache">按项目 id 缓存的查询结果（同一个项目只查一次库）。</param>
    /// <remarks>
    /// 项目级 webhook **随项目隐式失效**（spec §12.1）：
    /// 所属项目在回收站期间不投递，项目恢复后自动恢复投递，项目到期清理时随项目一并删除。
    /// <para>
    /// 判的是**当前**状态：删项目那一刻的 <c>project.deleted</c> 事件在事务提交后才发，
    /// 那时项目已经在回收站里，所以它自己（以及连带删掉的想法）的那批通知都发不出去 ——
    /// 一次删除本来会往同一个 webhook 里灌「1 条 project.deleted + N 条 idea.deleted」。
    /// </para>
    /// </remarks>
    private bool IsProjectInTrash(string? projectId, Dictionary<string, bool> cache)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            // 项目级 webhook 却没有绑项目（库里被手改过的行）：判成「不可投递」。
            return true;
        }

        if (cache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var project = projects.Get(projectId);

        // 项目行没了（回收站到期清理把它彻底删掉）：它的 webhook 也会被外键带走，
        // 同样判成「不可投递」。
        var inTrash = project is null || project.DeletedAt is not null;

        cache[projectId] = inTrash;
        return inTrash;
    }

    /// <summary>这个 webhook 该不该收这个事件。</summary>
    private static bool Matches(Webhook webhook, IdeaEvent evt, string? projectId)
    {
        if (!webhook.Enabled)
        {
            return false;
        }

        if (webhook.Scope == ProjectScope && webhook.ProjectId != projectId)
        {
            return false;
        }

        // 订阅列表为空 = 不订阅任何事件。
        return webhook.Events.Contains(evt.Type, StringComparer.Ordinal);
    }

    /// <summary>
    /// 渲染请求体。返回 <c>null</c> 表示这次渲染失败了（跳过这个 webhook）。
    /// </summary>
    /// <remarks>
    /// 模板在保存时校验过，但库里可能有手改过的行 ——
    /// 模板坏了只该让**这一个** webhook 的这次投递失败，
    /// 不能把整个事件（以及别的 webhook 的投递）一起带崩。
    /// </remarks>
    private string? Render(Webhook webhook, IdeaEvent evt)
    {
        try
        {
            var values = EventPlaceholders.Build(evt, options.Value.PublicBaseUrl);

            return TemplateRenderer.RenderJson(webhook.BodyTemplate, values);
        }
        catch (ArgumentException ex)
        {
            logger.LogError(
                ex,
                "webhook {WebhookId} 的 Body 模板渲染失败，跳过这次投递",
                webhook.Id);

            return null;
        }
    }
}
