namespace IdeaApp.Domain;

/// <summary>
/// 一个 webhook 配置的领域记录。
///
/// <para>
/// <see cref="Scope"/> 只有两个取值：<c>global</c>（全局）与 <c>project</c>（绑定某个项目）。
/// <see cref="ProjectId"/> 在 <c>global</c> 下必须为 null，在 <c>project</c> 下必须指向一个存在的项目
/// —— 两者不一致是非法配置（服务层收敛成 400 <c>invalid_webhook_project</c>）。
/// </para>
///
/// <para>
/// <see cref="Headers"/> 与 <see cref="Events"/> 在库里以 JSON 字符串存放，
/// 映射时用 <see cref="IdeaApp.Contracts.IdeaJsonContext"/> 序列化（Native AOT 下没有反射式序列化）。
/// </para>
///
/// <para>
/// <see cref="Secret"/> 只进不出：对外的 DTO 只有 <c>HasSecret</c> 布尔值，
/// 密钥明文既不出现在响应里，也不进日志。
/// </para>
/// </summary>
public sealed record Webhook(string Id, string Name, string Url, string Method,
                             Dictionary<string, string> Headers, string BodyTemplate,
                             string[] Events, string Scope, string? ProjectId,
                             string? Secret, bool Enabled,
                             DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
