namespace IdeaApp.Domain;

/// <summary>
/// 事件发布接口。服务层只认这个接口，不认 <c>EventBus</c> 具体类 ——
/// 这样测试可以换成记录型的 fake，直接断言「发了什么事件」。
/// </summary>
public interface IEventPublisher
{
    /// <summary>发布一个事件（同步返回，不等订阅者处理完）。</summary>
    void Publish(IdeaEvent evt);
}

/// <summary>
/// 事件订阅接口。实现方由组合根注册进事件总线。
/// </summary>
public interface IEventHandler
{
    /// <summary>处理一个事件。抛异常不影响其他订阅者（总线会兜住并记日志）。</summary>
    ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct);
}

/// <summary>项目的对外快照：事件里只需要 id 与名字。</summary>
public sealed record ProjectSnapshot(string Id, string Name);

/// <summary>
/// 想法的对外快照：想法去掉 <c>DeletedAt</c>、标签转成字符串数组。
/// </summary>
public sealed record IdeaSnapshot(string Id, string ProjectId, string? Title, string Content,
                                  string[] Tags, bool Done,
                                  DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// 一个领域事件。
///
/// <para>
/// <see cref="Id"/> 是单调 UUIDv7（32 位无连字符小写 hex）：SSE 补发时
/// 客户端拿它当 <c>Last-Event-ID</c>，「我漏了哪些事件」靠字典序比较就能算出来。
/// </para>
/// <para>
/// <see cref="Changed"/> 只列**真正改了**的字段名（如 <c>title</c>、<c>done</c>）；
/// 该事件类型没有「改了什么」的概念时为 null；写了但值没变时是空数组。
/// </para>
/// <para>
/// <see cref="MergedFrom"/> 只在合并事件里有值：被硬删的源想法 id 列表。
/// </para>
/// </summary>
public sealed record IdeaEvent(string Id, string Type, DateTimeOffset Timestamp,
                               ProjectSnapshot? Project, IdeaSnapshot? Idea,
                               string[]? Changed, string[]? MergedFrom);

/// <summary>事件类型常量。</summary>
public static class EventTypes
{
    /// <summary>新建想法。</summary>
    public const string IdeaCreated = "idea.created";

    /// <summary>想法被修改（改正文/标题、换标签、标记完成都算）。</summary>
    public const string IdeaUpdated = "idea.updated";

    /// <summary>想法被移进回收站。</summary>
    public const string IdeaDeleted = "idea.deleted";

    /// <summary>想法从回收站恢复。</summary>
    public const string IdeaRestored = "idea.restored";

    /// <summary>多条想法合并成一条（源想法被硬删）。</summary>
    public const string IdeaMerged = "idea.merged";

    /// <summary>新建项目。</summary>
    public const string ProjectCreated = "project.created";

    /// <summary>项目被修改（改名、重排）。</summary>
    public const string ProjectUpdated = "project.updated";

    /// <summary>项目被移进回收站。</summary>
    public const string ProjectDeleted = "project.deleted";

    /// <summary>全部事件类型（订阅端按这个清单筛选）。</summary>
    public static readonly string[] All =
    [
        IdeaCreated,
        IdeaUpdated,
        IdeaDeleted,
        IdeaRestored,
        IdeaMerged,
        ProjectCreated,
        ProjectUpdated,
        ProjectDeleted
    ];
}

/// <summary>
/// 事件工厂：统一生成 id 与时间戳，避免每个调用点各写一套。
/// </summary>
public static class EventFactory
{
    /// <summary>
    /// 造一个事件。id 用单调 UUIDv7（不是 <c>Guid.CreateVersion7()</c>：
    /// 那个在同一毫秒内不单调），时间戳取当前 UTC。
    /// </summary>
    public static IdeaEvent Create(
        string type,
        ProjectSnapshot? project,
        IdeaSnapshot? idea,
        string[]? changed = null,
        string[]? mergedFrom = null)
    {
        return new IdeaEvent(
            Id: MonotonicGuid.NewUuid7().ToString("N"),
            Type: type,
            Timestamp: DateTimeOffset.UtcNow,
            Project: project,
            Idea: idea,
            Changed: changed,
            MergedFrom: mergedFrom);
    }
}
