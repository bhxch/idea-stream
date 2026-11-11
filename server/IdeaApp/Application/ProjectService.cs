using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Application;

/// <summary>
/// 项目领域服务。业务规则在这里，仓储只管存。
/// 所有 4xx 业务错误都通过 <see cref="ApiException"/> 抛出。
/// </summary>
public sealed class ProjectService(ProjectRepository repo)
{
    /// <summary>内置收件箱的 id。它不能被删除，但可以改名、可以参与排序。</summary>
    public const string InboxId = "inbox";

    /// <summary>列出未删除的项目。</summary>
    public IReadOnlyList<ProjectDto> List()
    {
        var projects = repo.ListActive();
        var result = new List<ProjectDto>(projects.Count);

        foreach (var project in projects)
        {
            result.Add(ToDto(project));
        }

        return result;
    }

    /// <summary>新建项目，新项目排在最后。</summary>
    public ProjectDto Create(string name)
    {
        var trimmed = NormalizeName(name);

        // sort_order 的作用域是全表（含回收站里的项目），不能只看活跃项目：
        // 否则删掉一个项目再新建，会和回收站里那个项目撞 sort_order。
        var sortOrder = repo.MaxSortOrder() + 1;

        return ToDto(repo.Insert(trimmed, sortOrder));
    }

    /// <summary>改项目名。</summary>
    public ProjectDto Rename(string id, string name)
    {
        EnsureExists(id);

        return ToDto(repo.Rename(id, NormalizeName(name)));
    }

    /// <summary>把项目移进回收站（软删）。</summary>
    public void Delete(string id)
    {
        if (id == InboxId)
        {
            throw new ApiException(400, "inbox_not_deletable", "内置收件箱不能删除");
        }

        EnsureExists(id);
        repo.SoftDelete(id, DateTimeOffset.UtcNow);
    }

    /// <summary>从回收站恢复项目。</summary>
    public ProjectDto Restore(string id)
    {
        EnsureExists(id);
        repo.Restore(id);

        return ToDto(repo.Get(id)!);
    }

    /// <summary>
    /// 手动排序，返回排好序之后的项目列表。
    /// </summary>
    /// <remarks>
    /// 请求体里缺 <c>ids</c> 或显式给 <c>null</c> 时，反序列化出来是 null
    /// （.NET 10 默认不校验非空 ctor 参数）。这里收敛成 400，
    /// 不能让它一路走到仓储里变成 NullReferenceException → 500。
    /// </remarks>
    public IReadOnlyList<ProjectDto> Reorder(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            throw new ApiException(400, "invalid_reorder_ids", "重排请求必须给出至少一个项目 id");
        }

        repo.Reorder(ids);

        return List();
    }

    private void EnsureExists(string id)
    {
        if (repo.Get(id) is null)
        {
            throw new ApiException(404, "project_not_found", $"项目不存在：{id}");
        }
    }

    /// <summary>项目名去掉首尾空白；空名字不算合法项目名。</summary>
    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ApiException(400, "invalid_project_name", "项目名称不能为空");
        }

        return trimmed;
    }

    private ProjectDto ToDto(Project project) => new(
        Id: project.Id,
        Name: project.Name,
        SortOrder: project.SortOrder,
        IdeaCount: repo.CountActiveIdeas(project.Id),
        LastActivityAt: repo.LastActivityAt(project.Id),
        CreatedAt: project.CreatedAt,
        UpdatedAt: project.UpdatedAt);
}
