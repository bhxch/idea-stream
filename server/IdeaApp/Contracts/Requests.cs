namespace IdeaApp.Contracts;

/// <summary>创建项目的请求体。</summary>
public sealed record CreateProjectRequest(string Name);

/// <summary>项目改名的请求体。</summary>
public sealed record RenameProjectRequest(string Name);

/// <summary>
/// 项目重排的请求体：按给定顺序排列的项目 id。
/// </summary>
/// <remarks>
/// <c>Ids</c> 可空：JSON 里缺这个字段、或显式给 <c>null</c>，反序列化出来都是 null
/// （.NET 10 默认不校验非空 ctor 参数），所以这里如实标成可空，由服务层收敛成 400。
/// </remarks>
public sealed record ReorderProjectsRequest(string[]? Ids);

/// <summary>
/// 创建想法的请求体。
/// </summary>
/// <remarks>
/// <c>ProjectId</c> 缺省或为空串时落到内置收件箱；<c>Content</c> 缺省或全空白是非法正文
/// （400 <c>invalid_content</c>）；<c>Tags</c> 缺省表示不带标签。
/// </remarks>
public sealed record CreateIdeaRequest(string? ProjectId, string? Content, string? Title, string[]? Tags);

/// <summary>
/// 修改想法的请求体。
/// </summary>
/// <remarks>
/// <c>Title</c> 为 null 表示**不改标题**，给字符串（含空串）表示改成那个标题；
/// <c>Content</c> 为 null 表示不改正文。与出参语义不同：DTO 里 <c>"title": null</c> 是「无标题」。
/// </remarks>
public sealed record UpdateIdeaRequest(string? Title, string? Content);

/// <summary>覆盖式设置标签的请求体：<c>tags</c> 缺省或为空表示清空标签。</summary>
public sealed record SetTagsRequest(string[]? Tags);

/// <summary>标记完成/取消完成的请求体。</summary>
public sealed record SetDoneRequest(bool Done);

/// <summary>
/// 批量标记完成/取消完成的请求体：<c>ids</c> 是要改的想法 id。
/// </summary>
public sealed record BulkCompleteRequest(string[]? Ids, bool Done);

/// <summary>
/// 批量设置标签的请求体：<c>ids</c> 是要改的想法 id，<c>tags</c> 是要写的标签。
/// </summary>
/// <remarks>
/// <c>Replace</c> 缺省或为 false 时是**并集式追加**（保留原有标签），
/// 为 true 时覆盖成 <c>Tags</c> 给的那批。
/// </remarks>
public sealed record BulkTagsRequest(string[]? Ids, string[]? Tags, bool? Replace);

/// <summary>批量删除的请求体：<c>ids</c> 是要移进回收站的想法 id。</summary>
public sealed record BulkDeleteRequest(string[]? Ids);

/// <summary>
/// 合并想法的请求体：<c>ids</c> 是要合并的想法 id（至少两条）。
/// </summary>
/// <remarks>
/// <c>Content</c> 缺省时用源想法正文按 <c>updated_at</c> 升序拼接的结果；
/// <c>Title</c> 缺省时用第一条源想法的标题。
/// </remarks>
public sealed record MergeIdeasRequest(string[]? Ids, string? Content, string? Title);

/// <summary>
/// 修改单个设置项的请求体。
/// </summary>
/// <remarks>
/// <c>Value</c> 可空：JSON 里缺这个字段、或显式给 <c>null</c>，反序列化出来都是 null，
/// 由服务层收敛成 400（<c>invalid_sort_mode</c>）。
/// </remarks>
public sealed record SetSettingRequest(string? Value);

/// <summary>
/// 创建 webhook 的请求体。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：JSON 里缺字段时反序列化出来就是 null，
/// 服务层据此返回 400（<c>invalid_webhook_name</c> / <c>invalid_webhook_url</c> /
/// <c>invalid_webhook_scope</c> / <c>invalid_body_template</c>），而不是让模型绑定抛异常。
/// <para>
/// 签名要如实描述契约：照着非空签名写代码（比如 <c>req.Name.Trim()</c>）的人，
/// 会在「客户端漏传字段」这一种输入上踩到 NRE。
/// </para>
/// <para>
/// 没有 <c>Enabled</c> 字段：启用与否由作用域决定（全局默认禁用、项目级默认启用），
/// 创建完再改由 PATCH 负责。
/// </para>
/// <para>
/// <c>Secret</c> 给空串或不给都表示「不设密钥」。
/// </para>
/// </remarks>
public sealed record WebhookCreateRequest(string? Name, string? Url, string? Method,
    Dictionary<string, string>? Headers, string? BodyTemplate, string[]? Events,
    string? Scope, string? ProjectId, string? Secret);

/// <summary>
/// 修改 webhook 的请求体。字段一律可空：<c>null</c> = 不改。
/// </summary>
/// <remarks>
/// 全字段可空不只是为了「不改」这一种语义：JSON 里缺字段时反序列化出来同样是 null，
/// 签名写成非空就是在骗调用方（见 <see cref="WebhookCreateRequest"/> 的说明）。
/// <para>
/// <c>Secret</c> 的三态：<c>null</c> = 不改、<c>""</c> = 清空密钥、非空 = 替换成新密钥。
/// </para>
/// <para>
/// 作用域与所属项目不能改（改了就等于换了一个 webhook）。
/// </para>
/// </remarks>
public sealed record WebhookUpdateRequest(string? Name, string? Url, string? Method,
    Dictionary<string, string>? Headers, string? BodyTemplate, string[]? Events,
    bool? Enabled, string? Secret);
