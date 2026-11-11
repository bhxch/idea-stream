namespace IdeaApp.Contracts;

/// <summary>创建项目的请求体。</summary>
public sealed record CreateProjectRequest(string Name);

/// <summary>项目改名的请求体。</summary>
public sealed record RenameProjectRequest(string Name);

/// <summary>
/// 项目重排的请求体：按给定顺序排列的项目 id。
/// </summary>
/// <remarks>
/// <c>Ids</c> 可空：JSON 里缺这个字段、或显式给 <c>null</c>，反序列化出来都是 null
/// （.NET 10 默认不校验非空 ctor 参数），所以这里如实标成可空，由服务层收敛成 400。
/// </remarks>
public sealed record ReorderProjectsRequest(string[]? Ids);
