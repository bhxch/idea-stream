namespace IdeaApp.Domain;

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
