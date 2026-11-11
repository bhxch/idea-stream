using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Application;

/// <summary>
/// 项目领域服务。业务规则在这里，仓储只管存。
/// 所有 4xx 业务错误都通过 <see cref="ApiException"/> 抛出。
/// </summary>
/// <remarks>
/// 每个写方法在写库成功之后发事件（SSE 推送、webhook 投递都靠它）。
/// 恢复项目的语义（连带恢复想法、恢复事件）归 <see cref="TrashService"/>。
/// </remarks>
public sealed class ProjectService(
    ProjectRepository repo,
    SettingRepository settings,
    SqliteDatabase db,
    IEventPublisher publisher)
{
    /// <summary>内置收件箱的 id。它不能被删除，但可以改名、可以参与排序。</summary>
    public const string InboxId = "inbox";

    /// <summary>
    /// 列出未删除的项目。
    /// </summary>
    /// <remarks>
    /// 顺序由设置 <c>project.sort_mode</c> 决定：
    /// <list type="bullet">
    /// <item><c>recent</c>（默认）：按最近活动时间倒序，同值按 id 升序兜底；</item>
    /// <item><c>manual</c>：按 <c>sort_order</c> 升序（同值按 id 升序）。</item>
    /// </list>
    /// 活动时间取 DTO 自带的 <see cref="ProjectDto.LastActivityAt"/> ——
    /// 前端显示的「最近活动」与这里的排序用的是同一个值，两边各算一份会对不上。
    /// </remarks>
    public IReadOnlyList<ProjectDto> List()
    {
        var projects = repo.ListActive();
        var result = new List<ProjectDto>(projects.Count);

        foreach (var project in projects)
        {
            result.Add(ToDto(project));
        }

        if (CurrentSortMode() == ProjectSortMode.Manual)
        {
            // repo.ListActive() 已经按 sort_order ASC, id ASC 排好。
            return result;
        }

        // recent：最近活动的在前；活动时间相同时按 id 升序兜底（顺序稳定，不跳）。
        result.Sort((left, right) =>
        {
            var byActivity = CompareByActivityDesc(left, right);
            return byActivity != 0 ? byActivity : string.CompareOrdinal(left.Id, right.Id);
        });

        return result;
    }

    /// <summary>
    /// 切换项目排序方式。
    /// </summary>
    /// <remarks>
    /// 切到 <c>manual</c> 时用**当前显示顺序**初始化 <c>sort_order</c>：
    /// recent 排出来的顺序与库里原来的 <c>sort_order</c> 没关系，
    /// 不初始化的话用户切过去会看到顺序乱跳。
    /// <para>
    /// 初始化必须落库（不能只在内存里排一次）：下次读设置、换设备、
    /// 重启进程都还要看到同一个顺序。
    /// </para>
    /// </remarks>
    public void SetSortMode(string mode)
    {
        if (mode is not (ProjectSortMode.Recent or ProjectSortMode.Manual))
        {
            throw new ApiException(400, "invalid_sort_mode", "排序方式只能是 recent 或 manual");
        }

        if (mode == ProjectSortMode.Manual)
        {
            // 当前显示顺序 = 此刻 List() 给出的顺序（设置还没改，仍是旧模式）。
            var ids = List().Select(project => project.Id).ToArray();
            if (ids.Length > 0)
            {
                Reorder(ids);
            }
        }

        settings.Set(SettingKeys.ProjectSortMode, mode);
    }

    /// <summary>当前的排序方式（存的值非法或没存时回落 <c>recent</c>）。</summary>
    private string CurrentSortMode() =>
        ProjectSortMode.Resolve(settings.Get(SettingKeys.ProjectSortMode));

    /// <summary>按最近活动时间比较（新的在前）。</summary>
    /// <remarks>
    /// 活动时间直接取 DTO 自带的 <see cref="ProjectDto.LastActivityAt"/>：
    /// 它由 <see cref="ProjectRepository.LastActivityAt"/> 给出，契约上**非空**
    /// （没有未删想法时已在仓储里回落到项目自身的 <c>updated_at</c>）。
    /// 服务层不再写第二份回落规则 —— 两处各写一遍就会分叉，
    /// 前端显示的「最近活动」和列表顺序会对不上。
    /// </remarks>
    private static int CompareByActivityDesc(ProjectDto left, ProjectDto right)
    {
        // DTO 上这个字段声明成可空（对外协议如此），但唯一的取值路径是仓储，
        // 它保证非空；这里用 ! 表明「依赖仓储契约」，不做兜底。
        var leftActivity = left.LastActivityAt!.Value;
        var rightActivity = right.LastActivityAt!.Value;

        // left 更新时返回负数（left 排前面）。
        return rightActivity.CompareTo(leftActivity);
    }

    /// <summary>新建项目。</summary>
    /// <remarks>
    /// 新项目的 <c>sort_order</c> 取全表最大值 +1，所以 <c>manual</c>（手动排序）
    /// 模式下它排在最后；默认的 <c>recent</c> 模式按最近活动时间排，
    /// 新项目的活动时间最新，它排到最前。
    /// </remarks>
    public ProjectDto Create(string name)
    {
        var trimmed = NormalizeName(name);

        // sort_order 的作用域是全表（含回收站里的项目），不能只看活跃项目：
        // 否则删掉一个项目再新建，会和回收站里那个项目撞 sort_order。
        var sortOrder = repo.MaxSortOrder() + 1;

        var project = repo.Insert(trimmed, sortOrder);
        var dto = ToDto(project);

        publisher.Publish(EventFactory.Create(
            EventTypes.ProjectCreated,
            project: ProjectSnapshot.From(project),
            idea: null));

        return dto;
    }

    /// <summary>改项目名。</summary>
    public ProjectDto Rename(string id, string name)
    {
        EnsureExists(id);

        var project = repo.Rename(id, NormalizeName(name));
        var dto = ToDto(project);

        publisher.Publish(EventFactory.Create(
            EventTypes.ProjectUpdated,
            project: ProjectSnapshot.From(project),
            idea: null,
            changed: new[] { "name" }));

        return dto;
    }

    /// <summary>
    /// 把项目移进回收站（软删），**连带**把它下面未删的想法一起移进去。
    /// </summary>
    /// <remarks>
    /// 项目的 <c>deleted_at</c> 与它下面想法的 <c>deleted_at</c> 是**同一时刻**：
    /// 恢复项目时靠这个相等关系区分「是不是随项目一起删的」
    /// （见 <see cref="TrashService.RestoreProject"/>）。
    /// <para>
    /// 项目行与想法行必须在**同一个事务**里改：中途失败会留下
    /// 「项目没了、想法还在」的半截状态 —— 那条想法会继续出现在
    /// <c>GET /api/ideas</c> 里，而它的项目已经不在了。
    /// </para>
    /// <para>
    /// 重复删除是**空操作**：项目已经在回收站里时再删一次不改任何状态、不发任何事件。
    /// 再写一次 <c>deleted_at</c> 会破坏上面那个「同一时刻」的不变量 ——
    /// 恢复项目时那些想法就回不来了，只能以顶层条目留在回收站里。
    /// </para>
    /// </remarks>
    public void Delete(string id)
    {
        if (id == InboxId)
        {
            throw new ApiException(400, "inbox_not_deletable", "内置收件箱不能删除");
        }

        var (project, deletedIdeas, changed) = db.InTransaction((boundIdeas, boundProjects) =>
        {
            var project = boundProjects.Get(id)
                          ?? throw new ApiException(404, "project_not_found", $"项目不存在：{id}");

            // 已经在回收站里：空操作。
            if (project.DeletedAt is not null)
            {
                return (project, (IReadOnlyList<Idea>)Array.Empty<Idea>(), false);
            }

            var now = DateTimeOffset.UtcNow;

            boundProjects.SoftDelete(id, now);
            var deleted = boundIdeas.SoftDeleteByProject(id, now);

            return (project, deleted, true);
        });

        if (!changed)
        {
            // 已经在回收站里的项目：不发事件 —— 它早就被删了，
            // 再发一次「项目被删了」会误导客户端。
            return;
        }

        publisher.Publish(EventFactory.Create(
            EventTypes.ProjectDeleted,
            project: ProjectSnapshot.From(project),
            idea: null));

        // 项目下面的想法也进了回收站：订阅端（浏览器、SSE）要知道它们也不在了，
        // 否则本地列表里会留着这些条目。
        foreach (var idea in deletedIdeas)
        {
            publisher.Publish(EventFactory.Create(
                EventTypes.IdeaDeleted,
                project: null,
                idea: IdeaSnapshot.From(idea)));
        }
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

        // 重排会重写所有项目的 sort_order（没被列出的接在后面），
        // 所以先记下原来的顺序、改完再对比：只有顺序真的变了才发事件。
        var before = repo.ListActive().ToDictionary(
            project => project.Id,
            project => project.SortOrder,
            StringComparer.Ordinal);

        repo.Reorder(ids);

        foreach (var project in repo.ListActive())
        {
            if (!before.TryGetValue(project.Id, out var previous) || previous != project.SortOrder)
            {
                publisher.Publish(EventFactory.Create(
                    EventTypes.ProjectUpdated,
                    project: ProjectSnapshot.From(project),
                    idea: null,
                    changed: new[] { "sortOrder" }));
            }
        }

        return List();
    }

    /// <summary>取一条存在的项目，否则 404。</summary>
    private Project EnsureExists(string id)
    {
        return repo.Get(id) ?? throw new ApiException(404, "project_not_found", $"项目不存在：{id}");
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

    private ProjectDto ToDto(Project project) => ToDto(project, repo);

    /// <summary>
    /// 项目 → DTO（含未删想法数与最近活动时间）。
    /// </summary>
    /// <remarks>
    /// 静态的：回收站视图也要用同一份口径（想法计数、最近活动时间），
    /// 两边各写一份的话，前端显示的「最近活动」和列表里的会对不上。
    /// </remarks>
    internal static ProjectDto ToDto(Project project, ProjectRepository projects) => new(
        Id: project.Id,
        Name: project.Name,
        SortOrder: project.SortOrder,
        IdeaCount: projects.CountActiveIdeas(project.Id),
        LastActivityAt: projects.LastActivityAt(project.Id),
        CreatedAt: project.CreatedAt,
        UpdatedAt: project.UpdatedAt);
}
