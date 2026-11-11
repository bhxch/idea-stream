using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Application;

/// <summary>
/// 回收站服务：回收站视图与恢复语义。
///
/// <para>
/// 回收站装两类东西：
/// <list type="bullet">
/// <item><b>容器条目</b>：被删除的项目，下挂它所属的**全部**想法；</item>
/// <item><b>顶层条目</b>：被单独删除的想法（它的项目还活着）。</item>
/// </list>
/// 同一个想法不会在两处同时出现：项目被删时，它下面的想法一律只在容器里。
/// </para>
///
/// <para>
/// <b>不允许手动彻底删除</b>：到期自动清理是唯一出口（见 <see cref="TrashCleanupWorker"/>）。
/// </para>
/// </summary>
public sealed class TrashService(
    IdeaRepository ideas,
    ProjectRepository projects,
    SqliteDatabase db,
    IEventPublisher publisher)
{
    /// <summary>
    /// 回收站条目列表：容器（已删项目）优先，其次顶层想法。
    /// </summary>
    /// <remarks>
    /// 容器排在前面：顶层想法只有一条时列表也不会因为「想法先、项目后」而
    /// 让用户先看到一堆散条目。
    /// </remarks>
    public IReadOnlyList<TrashEntryDto> List()
    {
        var entries = new List<TrashEntryDto>();

        // 容器条目：已删的项目，它下面的**全部**想法都在里面
        // （无论它们是随项目一起删的，还是在项目被删之前就被单独删掉的）。
        foreach (var project in projects.ListDeleted())
        {
            var children = new List<IdeaDto>();
            foreach (var idea in ideas.ListByProject(project.Id))
            {
                children.Add(IdeaService.ToDto(idea));
            }

            entries.Add(new TrashEntryDto(
                Kind: "project",
                Project: ProjectService.ToDto(project, projects),
                Ideas: children.ToArray(),
                Idea: null));
        }

        // 顶层条目：被单独删掉的想法（它的项目没被删）。
        foreach (var idea in ideas.ListTopLevelTrash())
        {
            entries.Add(new TrashEntryDto(
                Kind: "idea",
                Project: null,
                Ideas: Array.Empty<IdeaDto>(),
                Idea: IdeaService.ToDto(idea)));
        }

        return entries;
    }

    /// <summary>
    /// 从回收站恢复一条想法。
    /// </summary>
    /// <remarks>
    /// 所属项目还在回收站时**允许**恢复：恢复后这条想法出现在所属项目的列表里，
    /// 但项目本身仍留在回收站，直到项目被恢复或到期清理。
    /// </remarks>
    public IdeaDto RestoreIdea(string id)
    {
        var restored = ideas.InTransaction(r =>
        {
            var existing = r.Get(id);
            if (existing is null)
            {
                throw new ApiException(404, "idea_not_found", $"想法不存在：{id}");
            }

            r.Restore(id);

            // 回读一次：updated_at 被刷新过了，载荷是恢复之后的样子。
            return r.Get(id)!;
        });

        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaRestored,
            project: null,
            idea: IdeaSnapshot.From(restored)));

        return IdeaService.ToDto(restored);
    }

    /// <summary>
    /// 从回收站恢复一个项目，**连带**恢复「因删项目而删」的想法。
    /// </summary>
    /// <remarks>
    /// 判定依据是想法的 <c>deleted_at</c> 与项目的 <c>deleted_at</c> 相同
    /// （两者在删除项目时被写成同一时刻）。
    /// 在项目被删**之前**就被单独删掉的想法不复活 —— 它们仍以顶层条目的形式
    /// 留在回收站里。
    /// <para>
    /// 项目行与想法行必须在**同一个事务**里改：中途失败会留下
    /// 「项目回来了、想法还在回收站」这种半截状态。
    /// </para>
    /// </remarks>
    public ProjectDto RestoreProject(string id)
    {
        var (project, restoredIdeas) = db.InTransaction((boundIdeas, boundProjects) =>
        {
            var existing = boundProjects.Get(id)
                            ?? throw new ApiException(404, "project_not_found", $"项目不存在：{id}");

            // 先恢复想法：判定「是不是因删项目而删的」靠的是想法的 deleted_at
            // 与项目的 deleted_at 相同 —— 项目行恢复之后这个依据就没了。
            var restored = boundIdeas.RestoreWithProject(id, DateTimeOffset.UtcNow);

            boundProjects.Restore(id);

            // 回读一次：updated_at 被刷新过了，载荷是恢复之后的样子。
            return (boundProjects.Get(id)!, restored);
        });

        var dto = ProjectService.ToDto(project, projects);

        // 事件类型只有 8 种，里面没有 project.restored —— 用 project.updated
        // 加 changed = ["deletedAt"] 表达「这个项目从回收站回来了」。
        publisher.Publish(EventFactory.Create(
            EventTypes.ProjectUpdated,
            project: new ProjectSnapshot(dto.Id, dto.Name, dto.SortOrder),
            idea: null,
            changed: new[] { "deletedAt" }));

        // 连带恢复的每条想法各发一条 idea.restored。
        foreach (var idea in restoredIdeas)
        {
            publisher.Publish(EventFactory.Create(
                EventTypes.IdeaRestored,
                project: null,
                idea: IdeaSnapshot.From(idea)));
        }

        return dto;
    }
}
