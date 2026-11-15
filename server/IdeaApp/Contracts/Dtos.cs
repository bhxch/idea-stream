using System.Text.Json.Serialization;

namespace IdeaApp.Contracts;

/// <summary>
/// 一处命中：某个字段里的字符区间（UTF-16 code unit）。
/// </summary>
/// <param name="Field">命中的字段：<c>"title"</c> | <c>"content"</c> | <c>"tags"</c> | <c>"project"</c>。</param>
/// <param name="Start">命中区间的起始偏移，单位是 UTF-16 code unit。</param>
/// <param name="Length">命中区间的长度，单位是 UTF-16 code unit。</param>
/// <remarks>
/// 偏移用 UTF-16 code unit 而不是「字符序号」，是为了和前端的
/// <c>String.prototype.slice</c> 对齐 —— 前端拿到的偏移可以直接切字符串。
/// </remarks>
public sealed record SearchMatch(string Field, int Start, int Length);

/// <summary>一条想法的对外表示。</summary>
/// <param name="Id">想法 id。</param>
/// <param name="ProjectId">所属项目 id。</param>
/// <param name="Title">标题；null 表示「无标题」。</param>
/// <param name="Content">正文。</param>
/// <param name="Tags">标签名（库里读回来的排序版）。</param>
/// <param name="Done">是否已完成。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">最近一次变化的时间。</param>
/// <param name="Matches">
/// 命中位置。仅在**带关键词过滤**的响应里出现；普通列表不带这个键。
/// </param>
/// <param name="Trashed">
/// 这条想法是不是在回收站里。仅在 <c>/api/search</c> 的响应里出现；
/// <c>/api/ideas</c> 只返回未删的想法，不填这个字段。
/// </param>
/// <remarks>
/// 两个可选字段都带**属性级**的 <c>[JsonIgnore(WhenWritingNull)]</c>：
/// <see cref="IdeaJsonContext"/> 没有设全局 <c>DefaultIgnoreCondition</c>，
/// 不加这个特性的话 null 字段会恒定写出，
/// 每一条想法的响应都会多出 <c>"matches": null, "trashed": null</c> 两个键 ——
/// 不带关键词、不带回收站标记的响应必须与今天逐字节一样。
/// </remarks>
public sealed record IdeaDto(string Id, string ProjectId, string? Title, string Content,
                             string[] Tags, bool Done,
                             DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                             [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                             SearchMatch[]? Matches = null,
                             [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                             bool? Trashed = null);

/// <summary>一个项目的对外表示，含想法计数与最近活动时间。</summary>
public sealed record ProjectDto(string Id, string Name, int SortOrder, int IdeaCount,
                                 DateTimeOffset? LastActivityAt,
                                 DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>一个标签及其关联的想法数量。</summary>
public sealed record TagDto(string Name, int Count);

/// <summary>
/// 一个 webhook 的对外表示。只暴露 <see cref="HasSecret"/> 布尔值，
/// 不回传 secret 明文。
/// </summary>
public sealed record WebhookDto(string Id, string Name, string Url, string Method,
                                Dictionary<string, string> Headers, string BodyTemplate,
                                string[] Events, string Scope, string? ProjectId,
                                bool Enabled, bool HasSecret,
                                DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// 一个 webhook 模板预设（当前只有 Telegram）：页面用它预填表单。
/// </summary>
public sealed record WebhookTemplateDto(string Url, string BodyTemplate);

/// <summary>一次 webhook 投递记录。</summary>
public sealed record DeliveryDto(string Id, string WebhookId, string EventId, string EventType,
                                 string Payload, string Status, int Attempts, string? LastError,
                                 DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>回收站中的一条条目（项目或想法）。</summary>
public sealed record TrashEntryDto(string Kind, ProjectDto? Project, IdeaDto[] Ideas, IdeaDto? Idea);

/// <summary>健康检查响应体。</summary>
public sealed record HealthResponse(string Status);
