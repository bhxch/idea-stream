using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Application;

/// <summary>
/// webhook 配置服务：业务规则在这里，仓储只管存。
/// 所有 4xx 业务错误都通过 <see cref="ApiException"/> 抛出。
///
/// <para>
/// 这一层只管**配置面**：webhook 的投递（异步、重试、死信）不在这里，
/// 那是投递端点那轮的事。
/// </para>
/// </summary>
public sealed class WebhookService(WebhookRepository repo, ProjectRepository projects)
{
    /// <summary>全局作用域：不属于任何项目。</summary>
    public const string ScopeGlobal = "global";

    /// <summary>项目作用域：绑定到某个项目，只收该项目的想法事件。</summary>
    public const string ScopeProject = "project";

    /// <summary>
    /// 列出 webhook 配置。
    /// </summary>
    /// <param name="projectId">
    /// 空 = 只列全局作用域的；给了项目 id = 只列绑定到该项目的。
    /// </param>
    public IReadOnlyList<WebhookDto> List(string? projectId)
    {
        var webhooks = repo.List(projectId);
        var result = new List<WebhookDto>(webhooks.Count);

        foreach (var webhook in webhooks)
        {
            result.Add(ToDto(webhook));
        }

        return result;
    }

    /// <summary>取一个 webhook 配置，不存在时 404。</summary>
    public WebhookDto Get(string id)
    {
        var webhook = repo.Get(id)
                      ?? throw new ApiException(404, "webhook_not_found", $"webhook 不存在：{id}");

        return ToDto(webhook);
    }

    /// <summary>
    /// 新建一个 webhook 配置。
    /// </summary>
    /// <remarks>
    /// <b>默认启用规则</b>：全局作用域的 webhook **创建时就是禁用的**（要手动开），
    /// 项目作用域的**默认启用**。请求体里没有 <c>enabled</c> 字段 ——
    /// 启用与否由作用域决定，不给用户「创建时随手开一个全局钩子」的机会。
    /// <para>
    /// 密钥只进不出：存进库里，出参只有 <c>HasSecret</c> 布尔值。
    /// </para>
    /// </remarks>
    public WebhookDto Create(WebhookCreateRequest request)
    {
        var name = NormalizeName(request.Name);
        var url = NormalizeUrl(request.Url);
        var method = NormalizeMethod(request.Method);
        var scope = NormalizeScope(request.Scope);
        var projectId = ResolveProjectId(scope, request.ProjectId);
        var events = ValidateEvents(request.Events);
        var bodyTemplate = ValidateBodyTemplate(request.BodyTemplate);
        var headers = request.Headers ?? new Dictionary<string, string>();
        var secret = NormalizeSecret(request.Secret);

        // 全局默认禁用、项目级默认启用。
        var enabled = scope == ScopeProject;

        var now = DateTimeOffset.UtcNow;
        var webhook = new Webhook(
            Id: MonotonicGuid.NewUuid7().ToString("N"),
            Name: name,
            Url: url,
            Method: method,
            Headers: headers,
            BodyTemplate: bodyTemplate,
            Events: events,
            Scope: scope,
            ProjectId: projectId,
            Secret: secret,
            Enabled: enabled,
            CreatedAt: now,
            UpdatedAt: now);

        return ToDto(repo.Insert(webhook));
    }

    /// <summary>
    /// 修改一个 webhook 配置。请求里没给的字段保持原样。
    /// </summary>
    /// <remarks>
    /// 密钥三态：<c>null</c> = 不改、<c>""</c> = 清空、非空 = 替换。
    /// <para>
    /// 作用域与所属项目不可改。
    /// </para>
    /// </remarks>
    public WebhookDto Update(string id, WebhookUpdateRequest request)
    {
        var existing = repo.Get(id)
                        ?? throw new ApiException(404, "webhook_not_found", $"webhook 不存在：{id}");

        var updated = existing with
        {
            Name = request.Name is null ? existing.Name : NormalizeName(request.Name),
            Url = request.Url is null ? existing.Url : NormalizeUrl(request.Url),
            Method = request.Method is null ? existing.Method : NormalizeMethod(request.Method),
            Headers = request.Headers ?? existing.Headers,
            BodyTemplate = request.BodyTemplate is null
                ? existing.BodyTemplate
                : ValidateBodyTemplate(request.BodyTemplate),
            Events = request.Events is null ? existing.Events : ValidateEvents(request.Events),
            Enabled = request.Enabled ?? existing.Enabled,
            Secret = request.Secret is null ? existing.Secret : NormalizeSecret(request.Secret),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return ToDto(repo.Update(updated));
    }

    /// <summary>删掉一个 webhook 配置。</summary>
    public void Delete(string id)
    {
        if (repo.Get(id) is null)
        {
            throw new ApiException(404, "webhook_not_found", $"webhook 不存在：{id}");
        }

        repo.Delete(id);
    }

    /// <summary>
    /// webhook → DTO。
    /// </summary>
    /// <remarks>
    /// 只回传 <c>HasSecret</c>：密钥明文不出现在任何响应里。
    /// </remarks>
    private static WebhookDto ToDto(Webhook webhook) => new(
        Id: webhook.Id,
        Name: webhook.Name,
        Url: webhook.Url,
        Method: webhook.Method,
        Headers: webhook.Headers,
        BodyTemplate: webhook.BodyTemplate,
        Events: webhook.Events,
        Scope: webhook.Scope,
        ProjectId: webhook.ProjectId,
        Enabled: webhook.Enabled,
        HasSecret: !string.IsNullOrEmpty(webhook.Secret),
        CreatedAt: webhook.CreatedAt,
        UpdatedAt: webhook.UpdatedAt);

    /// <summary>名称去掉首尾空白；空名字不是合法配置。</summary>
    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ApiException(400, "invalid_webhook_name", "webhook 名称不能为空");
        }

        return trimmed;
    }

    /// <summary>URL 去掉首尾空白；必须是 http(s) 绝对地址。</summary>
    /// <remarks>
    /// 相对地址、别的协议都投不出去（投递是 HTTP 请求），这类配置在保存时就该拒掉 ——
    /// 留到投递时才炸的话，炸的是「投递」而不是「配置」。
    /// <para>
    /// Telegram 预设的 URL 带 <c>&lt;BOT_TOKEN&gt;</c> 占位符：占位符不是合法的 URI 字符，
    /// 但整体仍是 http(s) 绝对地址（<c>Uri</c> 解析时把它转义成 <c>%3CBOT_TOKEN%3E</c>），
    /// 所以页面给的预设自己照样能用。
    /// </para>
    /// <para>
    /// 错误信息里**不回显 URL**：Telegram 的 bot token 就在 URL 路径里，
    /// 回显出去等于把它写进了日志与 skill 的 stderr。
    /// </para>
    /// </remarks>
    private static string NormalizeUrl(string? url)
    {
        var trimmed = url?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ApiException(400, "invalid_webhook_url", "webhook 的 URL 不能为空");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ApiException(400, "invalid_webhook_url",
                "webhook 的 URL 必须是 http(s) 绝对地址");
        }

        return trimmed;
    }

    /// <summary>方法名去掉首尾空白并转成大写；留空取默认的 POST。</summary>
    private static string NormalizeMethod(string? method)
    {
        var trimmed = method?.Trim() ?? string.Empty;

        return trimmed.Length == 0 ? "POST" : trimmed.ToUpperInvariant();
    }

    /// <summary>作用域只有 <c>global</c> 与 <c>project</c> 两种。</summary>
    private static string NormalizeScope(string? scope)
    {
        var trimmed = scope?.Trim().ToLowerInvariant() ?? string.Empty;

        if (trimmed != ScopeGlobal && trimmed != ScopeProject)
        {
            throw new ApiException(400, "invalid_webhook_scope",
                $"webhook 的作用域只能是 {ScopeGlobal} 或 {ScopeProject}");
        }

        return trimmed;
    }

    /// <summary>
    /// 作用域与所属项目必须一致：项目级必须给一个**存在的**项目，全局级必须不给。
    /// </summary>
    private string? ResolveProjectId(string scope, string? projectId)
    {
        var trimmed = projectId?.Trim();

        if (scope == ScopeProject)
        {
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new ApiException(400, "invalid_webhook_project",
                    "项目级 webhook 必须指定所属项目");
            }

            if (projects.Get(trimmed) is null)
            {
                throw new ApiException(400, "invalid_webhook_project", $"项目不存在：{trimmed}");
            }

            return trimmed;
        }

        if (!string.IsNullOrEmpty(trimmed))
        {
            throw new ApiException(400, "invalid_webhook_project",
                $"全局 webhook 不能绑定项目（scope = {ScopeGlobal} 时 projectId 必须为空）");
        }

        return null;
    }

    /// <summary>
    /// 订阅的事件类型必须在 <see cref="EventTypes.All"/> 里。
    /// </summary>
    private static string[] ValidateEvents(string[]? events)
    {
        if (events is null || events.Length == 0)
        {
            // 空数组 = 不订阅任何事件（合法的配置，不是错误）。
            return [];
        }

        foreach (var eventType in events)
        {
            if (string.IsNullOrEmpty(eventType) || !EventTypes.All.Contains(eventType, StringComparer.Ordinal))
            {
                throw new ApiException(400, "invalid_event_type", $"未知的事件类型：{eventType}");
            }
        }

        return events;
    }

    /// <summary>
    /// Body 模板必须是一个**合法的 JSON 对象**，且占位符只允许出现在字符串值节点里。
    /// </summary>
    /// <remarks>
    /// 规则只有一份，在 <see cref="TemplateRenderer.Validate"/> 里：渲染器只处理字符串值节点里的
    /// 占位符，所以根节点不是对象、或者占位符写在对象的键里，模板在投递时就用不了 ——
    /// 这种事要在保存时说清楚，而不是留到投递时才炸（炸的是「投递」而不是「配置」）。
    /// </remarks>
    private static string ValidateBodyTemplate(string? template)
    {
        try
        {
            return TemplateRenderer.Validate(template);
        }
        catch (ArgumentException ex)
        {
            throw new ApiException(400, "invalid_body_template", ex.Message);
        }
    }

    /// <summary>密钥去掉首尾空白；空串与 null 都表示「没有密钥」。</summary>
    private static string? NormalizeSecret(string? secret)
    {
        var trimmed = secret?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// Telegram webhook 模板预设：页面点「添加 Telegram webhook」时用它预填表单。
/// </summary>
/// <remarks>
/// Body 模板是一个 JSON 文档：<c>chat_id</c> 与 <c>message_thread_id</c> 是数字，
/// <c>text</c> 里放占位符。约定（对齐既有通知脚本）：不带 <c>parse_mode</c>、纯文本；
/// 正文首行 <c>[项目] 事件</c>，末行 <c>#标签</c>。
/// <para>
/// bot token 是秘密：只写成占位符 <c>&lt;BOT_TOKEN&gt;</c>，由用户自己在页面上填。
/// </para>
/// </remarks>
public static class TelegramTemplate
{
    /// <summary>Telegram 发送消息的地址（token 由用户自己填）。</summary>
    public const string Url = "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage";

    /// <summary>请求体模板（JSON 文档）。</summary>
    public const string BodyTemplate =
        """{"chat_id":-1003800671233,"message_thread_id":89,"text":"[{{project}}] {{event}}\n{{idea.content}}\n#{{idea.tags}}"}""";
}
