using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Options;

namespace IdeaApp.Application;

/// <summary>
/// 想法领域服务。业务规则在这里，仓储只管存。
/// 所有 4xx 业务错误都通过 <see cref="ApiException"/> 抛出。
/// </summary>
public sealed class IdeaService(IdeaRepository repo, ProjectRepository projects, IOptions<AppOptions> options)
{
    /// <summary>列出未删除的想法（不带过滤条件），按 <c>updated_at ASC, id ASC</c> 排序。</summary>
    public IReadOnlyList<IdeaDto> List()
    {
        return List(IdeaQuery.Everything);
    }

    /// <summary>
    /// 按查询条件列出想法，按 <c>updated_at ASC, id ASC</c> 排序。
    /// </summary>
    /// <remarks>
    /// 命中数超过 <see cref="ApiOptions.MaxIdeasPerResponse"/> 时抛 400
    /// （<c>too_many_ideas</c>）：这个接口不分页，静默截断的话用户根本不知道自己漏看了。
    /// </remarks>
    public IReadOnlyList<IdeaDto> List(IdeaQuery query)
    {
        var ideas = repo.List(query);

        var limit = options.Value.Api.MaxIdeasPerResponse;
        if (ideas.Count > limit)
        {
            throw new ApiException(400, "too_many_ideas",
                $"命中 {ideas.Count} 条想法，超过单次返回上限 {limit}，请收窄过滤条件");
        }

        var result = new List<IdeaDto>(ideas.Count);
        foreach (var idea in ideas)
        {
            result.Add(ToDto(idea));
        }

        return result;
    }

    /// <summary>新建想法。<paramref name="projectId"/> 为空时落到内置收件箱。</summary>
    public IdeaDto Create(string projectId, string content, string? title, IReadOnlyList<string> tags)
    {
        var body = ValidateContent(content);
        var targetProjectId = ResolveProjectId(projectId);
        var normalizedTags = NormalizeTags(tags);

        EnsureProjectExists(targetProjectId);

        var now = DateTimeOffset.UtcNow;
        var idea = new Idea(
            Id: MonotonicGuid.NewUuid7().ToString("N"),
            ProjectId: targetProjectId,
            Title: NormalizeTitle(title),
            Content: body,
            Tags: normalizedTags,
            Done: false,
            CreatedAt: now,
            UpdatedAt: now,
            DeletedAt: null);

        // 建想法与写标签必须同一个事务：标签写一半失败会留下没有标签的半成品。
        // Insert 返回的想法带库里实际的标签名（字典里若已有大小写不同的同一个标签，
        // 复用的是那一行，写法以字典里最早出现的那次为准）。
        var created = repo.InTransaction(r => r.Insert(idea));

        return ToDto(created);
    }

    /// <summary>
    /// 改正文与标题。
    /// <paramref name="title"/> 为 null 表示**不改标题**，给字符串（含空串）表示改成那个标题
    /// （空串 = 改成「无标题」）；<paramref name="content"/> 为 null 表示不改正文。
    /// </summary>
    /// <remarks>
    /// 入参的 null 与出参的 null 语义不同：DTO 里 <c>"title": null</c> 是「无标题」，
    /// 入参 <c>Title = null</c> 是「不修改」，前端容易误读。
    /// </remarks>
    public IdeaDto Update(string id, string? title, string? content)
    {
        var updated = repo.InTransaction(r =>
        {
            var existing = RequireExisting(r, id);

            var nextTitle = title is null ? existing.Title : NormalizeTitle(title);
            var nextContent = content is null ? existing.Content : ValidateContent(content);

            return r.UpdateContent(id, nextTitle, nextContent);
        });

        return ToDto(updated);
    }

    /// <summary>覆盖式设置标签：把标签集替换成给定的那批。</summary>
    public IdeaDto SetTags(string id, IReadOnlyList<string> tags)
    {
        var normalized = NormalizeTags(tags);

        var updated = repo.InTransaction(r =>
        {
            RequireExisting(r, id);

            r.ReplaceTags(id, normalized);
            return r.Get(id)!;
        });

        return ToDto(updated);
    }

    /// <summary>设置完成标记。</summary>
    public IdeaDto SetDone(string id, bool done)
    {
        var updated = repo.InTransaction(r =>
        {
            RequireExisting(r, id);

            return r.SetDone(id, done);
        });

        return ToDto(updated);
    }

    /// <summary>把想法移进回收站（软删）。</summary>
    public void Delete(string id)
    {
        repo.InTransaction(r =>
        {
            RequireExisting(r, id);

            r.SoftDelete(id, DateTimeOffset.UtcNow);
        });
    }

    /// <summary>从回收站恢复想法。</summary>
    public IdeaDto Restore(string id)
    {
        var restored = repo.InTransaction(r =>
        {
            var existing = r.Get(id);
            if (existing is null)
            {
                throw new ApiException(404, "idea_not_found", $"想法不存在：{id}");
            }

            r.Restore(id);
            return r.Get(id)!;
        });

        return ToDto(restored);
    }

    /// <summary>取一条存在且不在回收站的想法，否则 404。</summary>
    private static Idea RequireExisting(IdeaRepository repo, string id)
    {
        var idea = repo.Get(id);
        if (idea is null || idea.DeletedAt is not null)
        {
            throw new ApiException(404, "idea_not_found", $"想法不存在：{id}");
        }

        return idea;
    }

    /// <summary>项目必须存在且不在回收站里。</summary>
    private void EnsureProjectExists(string projectId)
    {
        var project = projects.Get(projectId);
        if (project is null || project.DeletedAt is not null)
        {
            throw new ApiException(404, "project_not_found", $"项目不存在：{projectId}");
        }
    }

    /// <summary>没给项目（或给了空串）时落到内置收件箱。</summary>
    private static string ResolveProjectId(string? projectId)
    {
        var trimmed = projectId?.Trim() ?? string.Empty;

        return trimmed.Length == 0 ? ProjectService.InboxId : trimmed;
    }

    /// <summary>正文不能为空也不能全是空白；正文本身逐字保留，不做裁剪。</summary>
    private static string ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ApiException(400, "invalid_content", "想法正文不能为空");
        }

        return content;
    }

    /// <summary>标题去掉首尾空白；空标题等于「无标题」（null）。</summary>
    private static string? NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// 标签按大小写不敏感去重，保留首次出现的原始写法；
    /// 去掉首尾空白，空标签丢弃。
    /// </summary>
    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = raw.Trim();
            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static IdeaDto ToDto(Idea idea) => new(
        Id: idea.Id,
        ProjectId: idea.ProjectId,
        Title: idea.Title,
        Content: idea.Content,
        Tags: idea.Tags.ToArray(),
        Done: idea.Done,
        CreatedAt: idea.CreatedAt,
        UpdatedAt: idea.UpdatedAt);
}
