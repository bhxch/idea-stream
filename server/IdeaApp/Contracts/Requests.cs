namespace IdeaApp.Contracts;

/// <summary>创建项目的请求体。</summary>
public sealed record CreateProjectRequest(string Name);

/// <summary>项目改名的请求体。</summary>
public sealed record RenameProjectRequest(string Name);

/// <summary>项目重排的请求体：按给定顺序排列的项目 id。</summary>
public sealed record ReorderProjectsRequest(string[] Ids);
