using IdeaApp.Domain;

namespace IdeaApp.Application;

/// <summary>
/// 想法列表的查询条件：项目 + 状态三态 + 标签多选。
/// </summary>
/// <param name="ProjectId">项目 id；null 或空串表示不限项目。</param>
/// <param name="Status">状态过滤（全部 / 未完成 / 已完成）。</param>
/// <param name="Tags">选中的标签；空集合表示不按标签过滤。</param>
/// <param name="TagMode">多个标签的匹配方式（任一命中 / 全部命中）。</param>
public sealed record IdeaQuery(string? ProjectId, IdeaStatusFilter Status,
                                IReadOnlyList<string> Tags, TagMatchMode TagMode)
{
    /// <summary>不过滤任何条件的查询。</summary>
    public static IdeaQuery Everything { get; } =
        new(null, IdeaStatusFilter.All, Array.Empty<string>(), TagMatchMode.Or);

    /// <summary>
    /// 从 HTTP 查询参数构造查询条件。
    /// </summary>
    /// <remarks>
    /// 缺省或空串表示「不按这一项过滤」；但给了不认识的值（<c>status=archived</c>）
    /// 一律 400 —— 默默当成「不过滤」的话，用户以为筛了却拿到全量结果，
    /// 比报错更难查。错误码与 spec 里列的 <c>invalid_tag_mode</c> 同一口径。
    /// </remarks>
    public static IdeaQuery FromParameters(string? projectId, string? status, string? tags, string? tagMode)
    {
        return new IdeaQuery(
            Normalize(projectId),
            ParseStatus(status),
            ParseTags(tags),
            ParseTagMode(tagMode));
    }

    /// <summary>去掉首尾空白；空串一律当成「没给」。</summary>
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static IdeaStatusFilter ParseStatus(string? value)
    {
        var normalized = Normalize(value);

        if (normalized is null
            || string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase))
        {
            return IdeaStatusFilter.All;
        }

        if (string.Equals(normalized, "open", StringComparison.OrdinalIgnoreCase))
        {
            return IdeaStatusFilter.Open;
        }

        if (string.Equals(normalized, "done", StringComparison.OrdinalIgnoreCase))
        {
            return IdeaStatusFilter.Done;
        }

        throw new ApiException(400, "invalid_status", $"状态过滤只支持 all / open / done，收到：{normalized}");
    }

    private static TagMatchMode ParseTagMode(string? value)
    {
        var normalized = Normalize(value);

        if (normalized is null
            || string.Equals(normalized, "or", StringComparison.OrdinalIgnoreCase))
        {
            return TagMatchMode.Or;
        }

        if (string.Equals(normalized, "and", StringComparison.OrdinalIgnoreCase))
        {
            return TagMatchMode.And;
        }

        throw new ApiException(400, "invalid_tag_mode", $"标签匹配方式只支持 or / and，收到：{normalized}");
    }

    /// <summary>
    /// 逗号分隔的标签列表。去掉首尾空白、丢掉空项，
    /// 按大小写不敏感去重（保留首次出现的写法）。
    /// </summary>
    private static IReadOnlyList<string> ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in value.Split(','))
        {
            var name = part.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }
}

/// <summary>
/// 想法与查询条件的匹配判定。纯函数，不碰数据库，方便单测。
/// </summary>
/// <remarks>
/// 判定顺序：项目 → 状态 → 标签。标签比较用 <see cref="StringComparer.OrdinalIgnoreCase"/>，
/// 因为标签字典本身就是按大小写不敏感去重的（<c>tag.name UNIQUE COLLATE NOCASE</c>）。
/// 软删不在这里判：候选集由 SQL 过滤（<c>deleted_at IS NULL</c>）。
/// </remarks>
public static class IdeaFilter
{
    /// <summary>这条想法是否命中查询条件。</summary>
    public static bool Matches(Idea idea, IdeaQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.ProjectId)
            && !string.Equals(idea.ProjectId, query.ProjectId, StringComparison.Ordinal))
        {
            return false;
        }

        if (query.Status == IdeaStatusFilter.Open && idea.Done)
        {
            return false;
        }

        if (query.Status == IdeaStatusFilter.Done && !idea.Done)
        {
            return false;
        }

        // 没选标签 = 不按标签过滤。
        if (query.Tags.Count == 0)
        {
            return true;
        }

        var selected = new HashSet<string>(query.Tags, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(idea.Tags, StringComparer.OrdinalIgnoreCase);

        // OR：有交集即命中；AND：选中的标签必须全都在想法上。
        return query.TagMode == TagMatchMode.And
            ? selected.IsSubsetOf(actual)
            : selected.Overlaps(actual);
    }
}
