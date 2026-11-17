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

/// <summary>
/// 项目的对外快照：id、名字与排序位置。
/// </summary>
/// <remarks>
/// <c>SortOrder</c> 必须在快照里：<c>project.updated</c> 的 <c>changed</c> 含
/// <c>sortOrder</c> 时，订阅端要拿到**新值**才能更新本地顺序，
/// 缺了它就只能整表重拉。
/// </remarks>
public sealed record ProjectSnapshot(string Id, string Name, int SortOrder)
{
    /// <summary>从领域记录造快照。</summary>
    public static ProjectSnapshot From(Project project) => new(project.Id, project.Name, project.SortOrder);
}

/// <summary>
/// 想法的对外快照：想法去掉 <c>DeletedAt</c>、标签转成字符串数组。
/// **没有标题** —— 想法就是一段文字。
/// </summary>
public sealed record IdeaSnapshot(string Id, string ProjectId, string Content,
                                  string[] Tags, bool Done,
                                  DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>从领域记录造快照（标签转成数组）。</summary>
    public static IdeaSnapshot From(Idea idea) => new(
        idea.Id,
        idea.ProjectId,
        idea.Content,
        idea.Tags.ToArray(),
        idea.Done,
        idea.CreatedAt,
        idea.UpdatedAt);
}

/// <summary>
/// 一个领域事件。
///
/// <para>
/// <see cref="Id"/> 是单调 UUIDv7（32 位无连字符小写 hex）：SSE 补发时
/// 客户端拿它当 <c>Last-Event-ID</c>，「我漏了哪些事件」靠字典序比较就能算出来。
/// </para>
/// <para>
/// <see cref="Changed"/> 是**三态**，订阅端必须分别处理：
/// <list type="bullet">
/// <item><c>null</c>：该事件类型没有「改了什么」的概念（如 <c>idea.created</c>），
/// 订阅端按「整条对象是新数据」处理；</item>
/// <item>空数组：写了但值没变（如 <c>Update</c> 传了同样的正文），
/// 数据本身仍然是最新的，别把这次更新跳过；</item>
/// <item>非空数组：<b>这次操作写过的字段名</b>（如 <c>done</c>、<c>tags</c>），
/// 订阅端按这些字段更新本地数据。</item>
/// </list>
/// </para>
/// <para>
/// 非空数组里的字段名有<b>四种口径</b>，订阅端不要假设「列出来的就一定变了」：
/// <list type="bullet">
/// <item><c>Update</c>（改正文）在事务里跟改前的值比对，
/// <b>只列真正变了</b>的字段（没变 → 空数组）；</item>
/// <item><c>SetTags</c> / <c>SetDone</c>（含它们的批量版本）<b>一律列该字段</b>
/// （<c>["tags"]</c> / <c>["done"]</c>）—— 这两个操作本来就要重写数据，
/// 值是否变化不影响「这个字段被写过」的事实；</item>
/// <item>项目改名（<c>project.updated</c>）也是<b>一律列 <c>["name"]</c></b>，
/// 不跟旧名比对。</item>
/// <item>项目<b>重排</b>（同样是 <c>project.updated</c>）是<b>有条件地</b>列
/// <c>["sortOrder"]</c>：只有 <c>sort_order</c> 真变了的项目才发这条事件，
/// 事件里的 <c>changed</c> 就带 <c>sortOrder</c> —— 没有它表示「这个项目的位置没变」。</item>
/// <item>项目<b>从回收站恢复</b>（也是 <c>project.updated</c>，因为事件类型只有 8 种，
/// 没有 <c>project.restored</c>）一律列 <c>["deletedAt"]</c>。</item>
/// </list>
/// </para>
/// <para>
/// <see cref="MergedFrom"/> 只在合并事件里有值：被硬删的源想法 id 列表。
/// </para>
/// <para>
/// 重排（<c>project.updated</c>）的事件条数：<b>N = <c>sort_order</c> 值真的变了的项目数</b>，
/// 不是请求里的 id 数。排序会重写全表的 <c>sort_order</c>（没被列出的接在后面），
/// 只按请求里的 id 发事件的话，被挤到后面的项目位置变了却没人知道，
/// 订阅端的列表顺序会错。每条事件的项目快照里带着该项目的**新** <c>sortOrder</c>。
/// </para>
/// <para>
/// <b>重排</b>的事件只覆盖<b>回收站之外</b>的项目：前后对比只看活跃项目，
/// 回收站里项目的 <c>sort_order</c> 变了也不会有事件（回收站的视图归 Task 10）。
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

    /// <summary>想法被修改（改正文、换标签、标记完成都算）。</summary>
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
