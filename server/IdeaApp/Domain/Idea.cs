namespace IdeaApp.Domain;

/// <summary>
/// 一条想法的领域记录。<see cref="DeletedAt"/> 非空表示该想法在回收站里。
/// </summary>
/// <remarks>
/// <strong>没有标题</strong> —— 想法就是一段文字。
/// </remarks>
public sealed record Idea(string Id, string ProjectId, string Content,
                          IReadOnlyList<string> Tags, bool Done,
                          DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                          DateTimeOffset? DeletedAt);

/// <summary>想法列表的状态过滤条件。</summary>
public enum IdeaStatusFilter
{
    /// <summary>不过滤状态。</summary>
    All,

    /// <summary>只看未完成。</summary>
    Open,

    /// <summary>只看已完成。</summary>
    Done
}

/// <summary>多标签筛选时的匹配方式。</summary>
public enum TagMatchMode
{
    /// <summary>命中任意一个标签即可。</summary>
    Or,

    /// <summary>必须同时命中全部标签。</summary>
    And
}
