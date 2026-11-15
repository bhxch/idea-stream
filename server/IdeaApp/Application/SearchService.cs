using System.Globalization;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Application;

/// <summary>
/// 全库搜索的服务层：校验参数、编排三组查询、组装响应。
///
/// <para>
/// 关键词的口径与 <c>GET /api/ideas?q=</c> **一致**（规格 §9.2）：
/// 切不出词元（全是标点）时当作「没搜」—— 不过滤、返回全部。
/// 两处口径分叉的话，agent 在一个端点上看到「搜得到」、在另一个端点上看到
/// 「搜不到」，那种偏差极难查。
/// </para>
///
/// <para>
/// 它要 <see cref="ProjectRepository"/>：项目 DTO 里的「未删想法数」与
/// 「最近活动时间」由那一份口径给出（见 <see cref="ProjectService.ToDto"/>），
/// 服务层不再写第二份 —— 两份口径迟早对不上。
/// </para>
/// </summary>
public sealed class SearchService(SearchRepository repository, ProjectRepository projects)
{
    /// <summary>单页最多几条（没给 <c>limit</c> 时的默认值）。</summary>
    /// <remarks>
    /// 默认给上限是为了**省 agent 的上下文**：一次请求塞进 100 条正文就把窗口吃满了。
    /// 要更多就显式调大 <c>limit</c>。
    /// </remarks>
    public const int DefaultLimit = 20;

    /// <summary>单页最多几条（的上限）。</summary>
    public const int MaxLimit = 100;

    /// <summary>
    /// 全库搜索。返回想法、项目、标签三组命中，每组自带分页信息。
    /// </summary>
    /// <param name="query">搜索词。trim 后为空 → 400 <c>invalid_search_query</c>。</param>
    /// <param name="limit">每组最多几条（1..100）。</param>
    /// <param name="offset">跳过最相关的前几条（三组统一用它）。</param>
    /// <param name="includeTrash">是否包含回收站里的条目（想法与项目）。</param>
    /// <exception cref="ApiException">关键词为空时抛 400 <c>invalid_search_query</c>。</exception>
    public SearchResponse Search(string query, int limit, int offset, bool includeTrash)
    {
        var trimmed = query?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ApiException(400, "invalid_search_query", "搜索关键词不能为空（q 必填）");
        }

        // 切不出词元 = 没有关键词 = 不过滤（返回全部）。
        var match = TextTokenizer.BuildMatchExpression(trimmed);

        var ideas = repository.Ideas(match, trimmed, includeTrash, limit, offset);
        var projectHits = repository.Projects(match, includeTrash, limit, offset);
        var tags = repository.Tags(match, limit, offset);

        return new SearchResponse(
            trimmed,
            new SearchPage<IdeaDto>(
                ideas.Items.Select(ToIdeaDto).ToArray(),
                ideas.Total,
                offset,
                limit,
                offset + ideas.Items.Count < ideas.Total),
            new SearchPage<ProjectDto>(
                projectHits.Items.Select(ToProjectDto).ToArray(),
                projectHits.Total,
                offset,
                limit,
                offset + projectHits.Items.Count < projectHits.Total),
            new SearchPage<TagDto>(
                tags.Items.ToArray(),
                tags.Total,
                offset,
                limit,
                offset + tags.Items.Count < tags.Total));
    }

    /// <summary>
    /// 解析并校验分页参数。
    /// </summary>
    /// <param name="limit">单页最多几条（字符串）；不给 = <see cref="DefaultLimit"/>。</param>
    /// <param name="offset">跳过最相关的前几条（字符串）；不给 = 0。</param>
    /// <returns>解析后的 (limit, offset)。</returns>
    /// <exception cref="ApiException">
    /// 不是整数或越界时抛 400 <c>invalid_pagination</c>（detail 说清楚收到的是什么）。
    /// </exception>
    /// <remarks>
    /// 参数绑成 <c>string?</c>、由这里自己解析：绑成 <c>int?</c> 时
    /// <c>?limit=abc</c> 在参数绑定阶段就失败，框架**只设一个 400 状态码就返回**
    /// —— 空 body、没有 code，进不了 <see cref="ApiException"/> 的统一出口，
    /// agent 拿到的就是一个空响应，连「是哪儿错了」都读不出来。
    /// <para>
    /// <b>只给 offset 不给 limit 是合法的</b>（与 <c>/api/ideas</c> 不同）：
    /// 这里的 limit 有默认值（<see cref="DefaultLimit"/>），翻页宽度本来就定死了。
    /// </para>
    /// </remarks>
    public static (int Limit, int Offset) ParsePagination(string? limit, string? offset)
    {
        var pageLimit = ParsePageNumber(limit, "limit") ?? DefaultLimit;
        var pageOffset = ParsePageNumber(offset, "offset") ?? 0;

        if (pageLimit is < 1 or > MaxLimit)
        {
            throw new ApiException(400, "invalid_pagination",
                $"limit 必须在 1..{MaxLimit} 之间，收到：{limit}");
        }

        if (pageOffset < 0)
        {
            throw new ApiException(400, "invalid_pagination",
                $"offset 不能是负数，收到：{offset}");
        }

        return (pageLimit, pageOffset);
    }

    /// <summary>
    /// 解析一个分页数字。空（null / 空串 / 全是空白）= 没给；不是数字 → 400。
    /// </summary>
    /// <param name="value">请求里的原始字符串。</param>
    /// <param name="name">参数名（写进 detail，让调用方知道是哪个参数错了）。</param>
    private static int? ParsePageNumber(string? value, string name)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ApiException(400, "invalid_pagination",
                $"{name} 必须是整数，收到：{value}");
        }

        // long 装得下的都先收下，范围判断交给调用方（limit 1..100 / offset >= 0）。
        if (parsed > int.MaxValue || parsed < int.MinValue)
        {
            throw new ApiException(400, "invalid_pagination", $"{name} 太大了，收到：{value}");
        }

        return (int)parsed;
    }

    /// <summary>
    /// 命中的想法 → DTO（带命中位置与是否在回收站）。
    /// </summary>
    private static IdeaDto ToIdeaDto(IdeaHit hit)
    {
        var dto = IdeaService.ToDto(hit.Idea);

        // 没有命中位置时**不写** matches 键：那个键的存在与否是
        // 「这次是不是带关键词」的信号（与 /api/ideas 同一口径）。
        var matches = hit.Hits.Count == 0 ? null : hit.Hits.ToArray();

        return dto with { Matches = matches, Trashed = hit.Trashed };
    }

    /// <summary>
    /// 命中的项目 → DTO（带是否在回收站）。
    /// </summary>
    /// <remarks>
    /// 想法计数与最近活动时间走 <see cref="ProjectService.ToDto"/>：
    /// 那份口径（只数未删想法、没有未删想法时回落到项目自身的更新时间）
    /// 只有一个写处，两边各写一份的话迟早对不上。
    /// </remarks>
    private ProjectDto ToProjectDto(ProjectHit hit) =>
        ProjectService.ToDto(hit.Project, projects) with { Trashed = hit.Trashed };
}
