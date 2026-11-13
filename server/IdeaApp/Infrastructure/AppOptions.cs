namespace IdeaApp.Infrastructure;

/// <summary>
/// 应用配置。配置键按层级映射到嵌套的选项类，
/// 例如 <c>Database:Path</c> 绑定到 <see cref="DatabaseOptions.Path"/>。
/// </summary>
public sealed class AppOptions
{
    public DatabaseOptions Database { get; set; } = new();
    public TrashOptions Trash { get; set; } = new();
    public ApiOptions Api { get; set; } = new();
    public WebhookOptions Webhook { get; set; } = new();
    public SseOptions Sse { get; set; } = new();
    public SkillOptions Skill { get; set; } = new();
    public string? PublicBaseUrl { get; set; }
}

/// <summary>SQLite 数据库位置。</summary>
public sealed class DatabaseOptions
{
    public string Path { get; set; } = "data/idea.db";
}

/// <summary>回收站保留策略。</summary>
public sealed class TrashOptions
{
    public int RetentionDays { get; set; } = 30;
}

/// <summary>API 行为限制。</summary>
public sealed class ApiOptions
{
    public int MaxIdeasPerResponse { get; set; } = 2000;
}

/// <summary>Webhook 投递参数。</summary>
public sealed class WebhookOptions
{
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// 最多投递尝试次数（**含首次尝试**）。
    /// </summary>
    /// <remarks>
    /// 默认 4 = 首次 + 3 次重试，正好把 1s / 5s / 30s 三段退避都用上
    /// （默认是 3 的话，退避表里的 30 秒永远用不上）。
    /// </remarks>
    public int MaxAttempts { get; set; } = 4;

    public int MaxDeliveryRecords { get; set; } = 500;
}

/// <summary>服务端事件（SSE）缓冲参数。</summary>
public sealed class SseOptions
{
    /// <summary>补发缓冲的条数（<c>Last-Event-ID</c> 补发时最多回放多少条）。</summary>
    public int BufferSize { get; set; } = 500;

    /// <summary>
    /// 心跳间隔（秒）：空闲连接上每隔这么久发一行 <c>: ping</c> 注释。
    /// </summary>
    /// <remarks>
    /// 做成配置项是为了能测：默认的 25 秒没法写测试。
    /// </remarks>
    public int HeartbeatSeconds { get; set; } = 25;
}

/// <summary>skill 分发副本的位置。</summary>
public sealed class SkillOptions
{
    /// <summary>分发副本的根目录。相对路径按应用目录解析。</summary>
    public string Path { get; set; } = "skill/idea-manager";
}
