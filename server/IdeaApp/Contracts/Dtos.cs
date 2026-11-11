namespace IdeaApp.Contracts;

/// <summary>一条想法的对外表示。</summary>
public sealed record IdeaDto(string Id, string ProjectId, string? Title, string Content,
                             string[] Tags, bool Done,
                             DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

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
