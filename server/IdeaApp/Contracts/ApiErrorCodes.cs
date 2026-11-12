namespace IdeaApp.Contracts;

/// <summary>
/// 一个稳定的机器可读错误码。
/// </summary>
/// <param name="Code">
/// 错误码本身，如 <c>project_not_found</c>。
/// 它出现在错误响应的 ProblemDetails 扩展字段 <c>code</c> 里。
/// </param>
/// <param name="Status">出现这个错误码时的 HTTP 状态码。</param>
/// <param name="Meaning">给人看的含义（也进 OpenAPI 契约，供 agent 读）。</param>
public sealed record ApiErrorCode(string Code, int Status, string Meaning);

/// <summary>
/// 全部业务错误码的**权威清单**。
///
/// <para>
/// 这份清单有两个用处：
/// <list type="bullet">
/// <item>OpenAPI 契约里的 <c>ProblemDetails</c> schema 描述与每个错误响应的
/// 描述都从这里取 —— 契约文件里写的错误码与代码里抛的因此不会各说各话。</item>
/// <item>skill 计划的错误码对照表（<c>docs/superpowers/plans/2026-11-11-idea-skill.md</c>）
/// 以它为准。改错误码时两边都要改。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>这份清单必须与实际抛出的错误码一致</b>：守卫测试
/// <c>ApiErrorCodeTests.Documented_codes_match_the_ones_actually_thrown</c>
/// 会扫源码里的 <c>ApiException(...)</c> 调用点来核对，漏登记就会测试失败。
/// </para>
/// </remarks>
public static class ApiErrorCodes
{
    /// <summary>
    /// 未预期的异常用的错误码。
    /// </summary>
    /// <remarks>
    /// 它不是 <c>ApiException</c>：由 <c>ApiResults</c> 在兜底时直接产出，
    /// 因此不在 <see cref="All"/> 里（那份清单只登记业务错误码）。
    /// </remarks>
    public static readonly ApiErrorCode InternalError = new(
        "internal_error",
        500,
        "未预期的异常；真实原因只进服务端日志，不进响应体");

    /// <summary>
    /// 全部业务错误码（按状态码、再按码名排序）。
    /// </summary>
    public static readonly IReadOnlyList<ApiErrorCode> All =
    [
        // 400：请求本身不合法。
        new("inbox_not_deletable", 400, "内置收件箱不能删除"),
        new("invalid_body_template", 400, "Body 模板不是合法的 JSON 文档"),
        new("invalid_content", 400, "想法正文为空或全是空白"),
        new("invalid_event_type", 400, "订阅的事件名不在已知事件类型里"),
        new("invalid_merge_selection", 400, "合并至少需要两条想法"),
        new("invalid_project_name", 400, "项目名称为空或全是空白"),
        new("invalid_reorder_ids", 400, "重排请求没有给出任何项目 id"),
        new("invalid_sort_mode", 400, "排序方式只能是 recent 或 manual"),
        new("invalid_status", 400, "status 参数只支持 all / open / done"),
        new("invalid_tag_mode", 400, "tagMode 参数只支持 or / and"),
        new("invalid_webhook_name", 400, "webhook 名称为空"),
        new("invalid_webhook_project", 400, "项目级 webhook 必须指定一个存在的项目，全局 webhook 不能绑定项目"),
        new("invalid_webhook_scope", 400, "webhook 的作用域只能是 global 或 project"),
        new("invalid_webhook_url", 400, "webhook 的 URL 为空或不是 http(s) 绝对地址"),
        new("merge_cross_project", 400, "不能跨项目合并想法"),
        new("not_in_trash", 400, "要恢复的东西不在回收站里"),
        new("setting_key_not_writable", 400, "该设置项不可经 API 修改（只有 project.sort_mode 可以）"),
        new("too_many_ideas", 400, "命中数超过单次返回上限，请收窄过滤条件"),

        // 404：东西不存在。
        new("delivery_not_found", 404, "投递记录不存在"),
        new("idea_not_found", 404, "想法不存在"),
        new("project_not_found", 404, "项目不存在"),
        new("webhook_not_found", 404, "webhook 不存在"),

        // 503：服务暂时接不住。
        new("delivery_queue_full", 503, "投递队列已满，请稍后重试"),
    ];

    /// <summary>某个状态码下的全部错误码。</summary>
    public static IEnumerable<ApiErrorCode> ForStatus(int status) =>
        All.Where(code => code.Status == status);

    /// <summary>契约里登记的全部错误码（业务错误码 + 内部错误码）。</summary>
    public static IEnumerable<ApiErrorCode> Documented => All.Append(InternalError);

    /// <summary>按码名查错误码；查不到返回 null。</summary>
    public static ApiErrorCode? Find(string code) =>
        Documented.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.Ordinal));
}
