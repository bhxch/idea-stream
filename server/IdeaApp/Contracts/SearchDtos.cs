namespace IdeaApp.Contracts;

/// <summary>
/// 一类命中结果的一页。
/// </summary>
/// <param name="Items">这一页的条目（按该类的相关度排序）。</param>
/// <param name="Total">该类的**命中总数**，不受分页影响。</param>
/// <param name="Offset">这一页从第几条开始（从最相关一端数）。</param>
/// <param name="Limit">这一页最多几条。</param>
/// <param name="HasMore">后面还有没有（<c>offset + items.Length &lt; total</c>）。</param>
/// <remarks>
/// 为什么要带 <paramref name="Total"/> 与 <paramref name="HasMore"/>：
/// 搜索结果是**按相关度排**的，agent 想知道「还有没有别的命中」就得靠这两个数 ——
/// 只给这一页的话，agent 分不清「到底了」与「这一页刚好满了」。
/// <para>
/// <paramref name="Total"/> 不受分页影响，所以它要另跑一次计数查询：
/// 按相关度排序的计数必须把所有命中项都算一遍（切页是切完之后的事）。
/// </para>
/// </remarks>
public sealed record SearchPage<T>(T[] Items, int Total, int Offset, int Limit, bool HasMore);

/// <summary>
/// 全库搜索的响应：想法、项目、标签三组。
/// </summary>
/// <param name="Query">这次搜的关键词（回显，让 agent 知道搜的是什么）。</param>
/// <param name="Ideas">命中的想法（按相关度排，每条带命中位置与是否在回收站）。</param>
/// <param name="Projects">命中的项目（按相关度排，每条带是否在回收站）。</param>
/// <param name="Tags">命中的标签（按相关度排，每条带使用次数）。</param>
/// <remarks>
/// 三组**各自**分页、共用同一个 <c>offset</c> / <c>limit</c>：
/// 三类是不同实体，「第 41 条想法」与「第 41 个项目」之间没有共同的序，
/// 统一 offset 已经是妥协 —— agent 想要更多想法就把 offset 调大，
/// 多出来的项目与标签被跳过也无所谓。
/// </remarks>
public sealed record SearchResponse(
    string Query,
    SearchPage<IdeaDto> Ideas,
    SearchPage<ProjectDto> Projects,
    SearchPage<TagDto> Tags);
