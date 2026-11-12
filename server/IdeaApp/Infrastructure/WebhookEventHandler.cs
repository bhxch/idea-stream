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
/// <item>再叠加：<b>事件所属项目在回收站里 → 整个事件不投递</b>，
/// 全局与项目级 webhook 一视同仁。</item>
/// </list>
/// </para>
///
/// <para>
/// 第 4 条的<b>唯一例外</b>是 <c>project.deleted</c>：照投。
/// 删项目会连带软删它下面的 N 条想法，产生 N 条 <c>idea.deleted</c>；
/// 照投的话一次删除会往同一个 webhook 里灌「1 条 project.deleted + N 条 idea.deleted」，
/// 对 Telegram 这类目标会撞发送频率限制，剩下的一批变成失败与死信。
/// 而「项目没了（连带它的所有想法）」这件事本身值得通知一次 ——
/// 否则用户永远不知道自己的项目没了。
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

        var candidates = webhooks.ListEnabledFor(projectId);
        if (candidates.Count == 0)
        {
            // 没人要收这个事件：不投递，也就不必再查库。
            return ValueTask.CompletedTask;
        }

        // 事件所属的项目在回收站里 → 这个事件**整条不投**，
        // 全局与项目级 webhook 一视同仁。
        // 例外：project.deleted（「项目被删了」这件事本身要通知一次）。
        if (projectId is not null && evt.Type != EventTypes.ProjectDeleted && IsProjectInTrash(projectId))
        {
            return ValueTask.CompletedTask;
        }

        foreach (var webhook in candidates)
        {
            if (!Matches(webhook, evt, projectId))
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
    /// 这个项目当前是不是在回收站里。
    /// </summary>
    /// <param name="projectId">项目 id。</param>
    /// <remarks>
    /// 判的是**当前**状态：事件是在事务提交之后才 publish 的，
    /// 所以匹配到 <c>project.deleted</c> 时项目已经在回收站里了 ——
    /// 不需要给事件加字段，也不需要改 publish 侧。
    /// <para>
    /// 项目行整个没了（回收站到期清理把它彻底删掉）也算「不在活跃状态」。
    /// </para>
    /// </remarks>
    private bool IsProjectInTrash(string projectId)
    {
        var project = projects.Get(projectId);
        return project is null || project.DeletedAt is not null;
    }

    /// <summary>这个 webhook 该不该收这个事件。</summary>
    private static bool Matches(Webhook webhook, IdeaEvent evt, string? projectId)
    {
        if (!webhook.Enabled)
        {
            return false;
        }

        if (webhook.Scope == ProjectScope)
        {
            // 项目级 webhook 却没绑项目（库里被手改过的行）：不投递。
            if (string.IsNullOrEmpty(webhook.ProjectId))
            {
                return false;
            }

            if (webhook.ProjectId != projectId)
            {
                return false;
            }
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
