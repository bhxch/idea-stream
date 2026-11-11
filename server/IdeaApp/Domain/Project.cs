namespace IdeaApp.Domain;

/// <summary>
/// 一个项目的领域记录。<see cref="DeletedAt"/> 非空表示该项目在回收站里。
/// </summary>
public sealed record Project(string Id, string Name, int SortOrder,
                             DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                             DateTimeOffset? DeletedAt);
