using System.Globalization;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Options;

namespace IdeaApp.Application;

/// <summary>
/// 想法领域服务。业务规则在这里，仓储只管存。
/// 所有 4xx 业务错误都通过 <see cref="ApiException"/> 抛出。
/// </summary>
/// <remarks>
/// 每个写方法在**事务提交之后**发一条事件（SSE 推送、webhook 投递都靠它）。
/// 绝不能在事务回调里发：事务回滚了却把事件发出去，订阅端就会收到幽灵事件。
/// <para>
/// **想法事件也带项目快照**（spec §11.2）：没有它的话，项目级 webhook 匹配不到想法事件，
/// <c>{{project}}</c> 占位符也是空的。取项目快照的那次查询放在事务之外、发事件之前。
/// </para>
/// </remarks>
public sealed class IdeaService(
    IdeaRepository repo,
    ProjectRepository projects,
    IOptions<AppOptions> options,
    IEventPublisher publisher)
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
        return List(query, null, null, null);
    }

    /// <summary>
    /// 按查询条件列出想法。给了 <paramref name="limit"/> 就只返回那一页（往回翻历史）。
    /// </summary>
    /// <param name="query">项目 / 状态 / 标签的过滤条件。</param>
    /// <param name="keyword">
    /// 关键词（null = 不过滤）。走全文索引：语义是「词元相邻」——
    /// 中文等价于子串（标点与空白不挡路），英文是词级。
    /// 切不出词元的串按「没有关键词」处理（见 <see cref="ParseKeyword"/>）。
    /// </param>
    /// <param name="limit">单页最多几条（字符串）；null = 不限。</param>
    /// <param name="offset">已经翻过多少条（从**最新**一端数，字符串）。</param>
    /// <remarks>
    /// <para>
    /// 分页参数是**字符串**：自己解析、自己判范围，非法一律 400
    /// <c>invalid_pagination</c>。不能绑成 <c>int?</c> —— 实测（跑真实应用）
    /// 绑 <c>int?</c> 时 <c>?limit=abc</c> 在参数绑定阶段失败后，
    /// 框架**只设一个 400 状态码就返回**：空 body、没有 code，
    /// 进不了 <see cref="ApiException"/> 的统一出口。
    /// </para>
    /// <para>
    /// 给了 <paramref name="limit"/> 就**不再报 <c>too_many_ideas</c>**：
    /// 明确要了一页，不该被顶回来。没给 limit 才是「给我全部」，
    /// 超上限就该明说。
    /// </para>
    /// <para>
    /// 切片在**内存标签过滤之后**：SQL 层切出来的一页在内存过滤后可能只剩零星几条，
    /// 前端按「返回条数 == limit+1」判定没更多，后面的命中就永远看不到了。
    /// </para>
    /// </remarks>
    public IReadOnlyList<IdeaDto> List(IdeaQuery query, string? keyword, string? limit, string? offset)
    {
        // 关键词在这里再归一化一次：直接调本方法的人（不经 ParseKeyword）
        // 传的可能是「切不出词元」的串 —— 那不是关键词。
        // 归一化只有一个口径（ParseKeyword），null 一路传下去：
        // 服务层与仓储层各判一次的话，纯标点查询的响应里每条想法都会多出
        // 一个空的 "matches": []，而不带 q 的响应没有这个键。
        var effectiveKeyword = ParseKeyword(keyword);

        // 分页参数在这里也要解析一次（切片要的是数字）：规则只有一份，
        // 见 ParsePagination。
        var (pageLimit, pageOffset) = ParsePagination(limit, offset);

        var ideas = repo.List(query, effectiveKeyword);

        IReadOnlyList<Idea> page;

        if (pageLimit is null)
        {
            var max = options.Value.Api.MaxIdeasPerResponse;

            if (ideas.Count > max)
            {
                throw new ApiException(400, "too_many_ideas",
                    $"命中 {ideas.Count} 条想法，超过单次返回上限 {max}，请收窄过滤条件");
            }

            page = ideas;
        }
        else
        {
            // 往回翻历史：offset 从**最新**一端数。
            // ideas 是 updated_at ASC —— 最老的在 [0]，最新的在 [Count-1]。
            var end = Math.Max(0, ideas.Count - pageOffset);
            var start = Math.Max(0, end - pageLimit.Value);
            page = ideas.Skip(start).Take(end - start).ToList();
        }

        // 命中位置只在这一条路径上算：别的路径（创建/更新/合并……）共用 ToDto，
        // 它们不带 matches。
        var matches = effectiveKeyword is null ? null : BuildMatches(page, effectiveKeyword);

        var result = new List<IdeaDto>(page.Count);
        foreach (var idea in page)
        {
            var dto = ToDto(idea);

            result.Add(matches is null ? dto : dto with { Matches = matches[idea.Id] });
        }

        return result;
    }

    /// <summary>
    /// 把 HTTP 查询参数翻译成查询条件。
    /// </summary>
    /// <param name="projectId">项目 id；空串 = 不限项目。</param>
    /// <param name="status">all / open / done；不认识的值 400。</param>
    /// <param name="tags">逗号分隔的标签名。</param>
    /// <param name="tagMode">or / and；不认识的值 400。</param>
    /// <param name="q">
    /// 关键词。它**不进** <see cref="IdeaQuery"/>：关键词走全文索引，
    /// 语义与 <see cref="IdeaFilter"/> 的集合判定不是一回事，混在一起会互相误杀。
    /// <para>
    /// 归一化（trim 后为空 = 没有关键词）由 <see cref="ParseKeyword"/> 负责：
    /// 本方法只返回**查询条件**，带不出关键词，所以归一化与取值分成两步。
    /// </para>
    /// </param>
    /// <param name="limit">单页最多几条（字符串）；范围 1..1000，非法 → 400。</param>
    /// <param name="offset">已经翻过多少条（从最新一端数，字符串）。</param>
    /// <remarks>
    /// 放在服务层而不是 <see cref="IdeaQuery"/> 那边：查询规格（<see cref="IdeaQuery"/>）
    /// 与匹配判定（<see cref="IdeaFilter"/>）是纯领域概念，留在 <c>Domain</c> 层，
    /// 不依赖应用层；而「不认识的过滤值要收敛成 400」这件事要用
    /// <see cref="ApiException"/>（应用层），因此解析留在这一层。
    /// </remarks>
    public static IdeaQuery ParseQuery(
        string? projectId,
        string? status,
        string? tags,
        string? tagMode,
        string? q,
        string? limit,
        string? offset)
    {
        // 分页参数先判：不是数字、越界、offset 单独给 —— 一律 400 invalid_pagination。
        // 「没给 limit」与「limit 给错了」是两件事，不能混成一件。
        ParsePagination(limit, offset);

        return new IdeaQuery(
            NormalizeParameter(projectId),
            ParseStatus(status),
            ParseTags(tags),
            ParseTagMode(tagMode));
    }

    /// <summary>
    /// 关键词归一化：没有「可搜的内容」就是没有关键词。
    /// </summary>
    /// <param name="q">用户给的查询串。</param>
    /// <returns>归一化后的关键词；没有关键词时返回 null。</returns>
    /// <remarks>
    /// 两种情况算「没有关键词」：trim 后为空；切不出词元（全是标点或空白 ——
    /// 索引里存的就是词元串，切不出词元的查询搜不到任何东西）。
    /// <para>
    /// 归一化只在这里做一次：服务层与仓储层各判一次的话，纯标点查询的响应里
    /// 每条想法都会多出一个空的 <c>"matches": []</c>，而不带 q 的响应没有这个键 ——
    /// 同一语义两种响应形状，而键的存在与否本应是「这次是不是搜索」的信号。
    /// </para>
    /// <para>
    /// **不做大小写折叠**：切词时自己转小写（见 <see cref="TextTokenizer"/>），
    /// 索引侧与查询侧的口径才一致。
    /// </para>
    /// </remarks>
    public static string? ParseKeyword(string? q)
    {
        var trimmed = q?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        // 切不出词元 = 搜不到任何东西 = 等于没给关键词。
        return TextTokenizer.BuildMatchExpression(trimmed) is null ? null : trimmed;
    }

    /// <summary>新建想法。<paramref name="projectId"/> 为空时落到内置收件箱。</summary>
    public IdeaDto Create(string projectId, string content, string? title, IReadOnlyList<string> tags)
    {
        var body = ValidateContent(content);
        var targetProjectId = ResolveProjectId(projectId);
        var normalizedTags = NormalizeTags(tags);

        // 项目在这一步就查出来：想法事件要带项目快照（spec §11.2），
        // 顺带把「项目必须存在且不在回收站里」这条校验做掉 —— 不用查第二次。
        var project = RequireProject(targetProjectId);

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

        var dto = ToDto(created);

        // 事件在事务提交之后才发。
        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaCreated,
            project: ProjectSnapshot.From(project),
            idea: ToSnapshot(dto)));

        return dto;
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
        // changed 只列**真正改了**的字段，所以要在事务里跟改之前的值比。
        var (dto, changed) = repo.InTransaction(r =>
        {
            var existing = RequireExisting(r, id);

            var nextTitle = title is null ? existing.Title : NormalizeTitle(title);
            var nextContent = content is null ? existing.Content : ValidateContent(content);

            var changed = new List<string>(2);
            if (!string.Equals(existing.Title, nextTitle, StringComparison.Ordinal))
            {
                changed.Add("title");
            }

            if (!string.Equals(existing.Content, nextContent, StringComparison.Ordinal))
            {
                changed.Add("content");
            }

            return (ToDto(r.UpdateContent(id, nextTitle, nextContent)), changed.ToArray());
        });

        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaUpdated,
            project: ProjectSnapshotOf(dto.ProjectId),
            idea: ToSnapshot(dto),
            changed: changed));

        return dto;
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

        var dto = ToDto(updated);

        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaUpdated,
            project: ProjectSnapshotOf(dto.ProjectId),
            idea: ToSnapshot(dto),
            changed: new[] { "tags" }));

        return dto;
    }

    /// <summary>设置完成标记。</summary>
    public IdeaDto SetDone(string id, bool done)
    {
        var updated = repo.InTransaction(r =>
        {
            RequireExisting(r, id);

            return r.SetDone(id, done);
        });

        var dto = ToDto(updated);

        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaUpdated,
            project: ProjectSnapshotOf(dto.ProjectId),
            idea: ToSnapshot(dto),
            changed: new[] { "done" }));

        return dto;
    }

    /// <summary>
    /// 把想法移进回收站（软删）。
    /// </summary>
    /// <remarks>
    /// 重复删除是**空操作**：想法已经在回收站里时再删一次不改任何状态、不发任何事件。
    /// <para>
    /// 404 在这里语义不准：那条想法**确实存在**，只是进了回收站 ——
    /// 跟 <c>not_in_trash</c> 的判断依据是同一个事实（存在但不在回收站），
    /// 错误码不该一个说「不存在」一个说「不在回收站」。
    /// </para>
    /// <para>
    /// 再写一次 <c>deleted_at</c> 会把保留期**重新开始**（用户点两下删除，
    /// 那条想法就多留一个月）；重复发 <c>idea.deleted</c> 会让其它设备
    /// 收到一条「它被删了」的假消息。两条都是幂等性该挡掉的。
    /// </para>
    /// <para>
    /// 但**根本不存在**的 id 仍然报 404（<c>idea_not_found</c>）——
    /// 「压根没有这条」与「它在回收站里」是两回事。
    /// </para>
    /// </remarks>
    public void Delete(string id)
    {
        var (idea, changed) = repo.InTransaction(r =>
        {
            // 仓储的 Get 含回收站里的：null = 压根没有这条。
            var existing = r.Get(id)
                            ?? throw new ApiException(404, "idea_not_found", $"想法不存在：{id}");

            // 已经在回收站里：空操作。
            if (existing.DeletedAt is not null)
            {
                return (existing, false);
            }

            r.SoftDelete(id, DateTimeOffset.UtcNow);

            // 回读一次：updated_at 被刷新过了，载荷是删除之后的样子。
            return (r.Get(id)!, true);
        });

        if (!changed)
        {
            // 已经在回收站里的想法：不发事件 —— 它早就被删了，
            // 再发一次「想法被删了」会误导客户端。
            return;
        }

        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaDeleted,
            project: ProjectSnapshotOf(idea.ProjectId),
            idea: ToSnapshot(ToDto(idea))));
    }

    /// <summary>
    /// 批量标记完成/取消完成。返回**实际被处理到的**想法（按请求里 id 的顺序）。
    /// </summary>
    /// <remarks>
    /// 批量也是写操作：每条被改的想法都刷新 <c>updated_at</c>，因此挪到列表最下面。
    /// <para>
    /// 不存在的 id（或已在回收站的）直接跳过，不报错：这工具是多端实时同步的，
    /// 「你在电脑上选中的 5 条，手机上刚被删掉 1 条」是常态，不是异常。
    /// 但传的 id 一个都不存在时报 404 —— 「全选错」与「部分对」要能区分开。
    /// </para>
    /// </remarks>
    public IReadOnlyList<IdeaDto> BulkSetDone(IReadOnlyList<string> ids, bool done)
    {
        var targets = NormalizeIds(ids);

        var updated = repo.InTransaction(r =>
        {
            var existing = ResolveExisting(r, targets);
            EnsureSomethingProcessed(targets, existing.Count);

            var results = new List<IdeaDto>(existing.Count);
            foreach (var idea in existing)
            {
                results.Add(ToDto(r.SetDone(idea.Id, done)));
            }

            return (IReadOnlyList<IdeaDto>)results;
        });

        // 批量是「每条想法一个事件」：订阅端逐条更新，不用重新拉整个列表。
        // 项目快照按项目 id 缓存：同一批想法往往在同一个项目里，项目只查一次。
        var projectCache = new Dictionary<string, ProjectSnapshot?>(StringComparer.Ordinal);
        foreach (var dto in updated)
        {
            publisher.Publish(EventFactory.Create(
                EventTypes.IdeaUpdated,
                project: ProjectSnapshotOf(dto.ProjectId, projectCache),
                idea: ToSnapshot(dto),
                changed: new[] { "done" }));
        }

        return updated;
    }

    /// <summary>
    /// 批量设置标签。<paramref name="replace"/> 为 false（默认）时是**并集式追加**
    /// （保留每条想法原有的标签），为 true 时覆盖成给定的标签集。
    /// </summary>
    /// <remarks>
    /// 返回**实际被处理到的**想法（按请求里 id 的顺序）；不存在的 id 跳过，
    /// 一个都不存在时报 404（见 <see cref="BulkSetDone"/>）。
    /// 换标签也是写操作，同样刷新 <c>updated_at</c>。
    /// </remarks>
    public IReadOnlyList<IdeaDto> BulkSetTags(IReadOnlyList<string> ids, IReadOnlyList<string> tags, bool replace)
    {
        var targets = NormalizeIds(ids);
        var requested = NormalizeTags(tags);

        var updated = repo.InTransaction(r =>
        {
            var existing = ResolveExisting(r, targets);
            EnsureSomethingProcessed(targets, existing.Count);

            var results = new List<IdeaDto>(existing.Count);
            foreach (var idea in existing)
            {
                // 追加模式下先算并集再整体写入：原有标签的写法优先保留
                //（标签字典按大小写不敏感唯一，写法以最早那次为准）。
                var next = replace
                    ? requested
                    : NormalizeTags(idea.Tags.Concat(requested).ToArray());

                r.ReplaceTags(idea.Id, next);
                results.Add(ToDto(r.Get(idea.Id)!));
            }

            return (IReadOnlyList<IdeaDto>)results;
        });

        var projectCache = new Dictionary<string, ProjectSnapshot?>(StringComparer.Ordinal);
        foreach (var dto in updated)
        {
            publisher.Publish(EventFactory.Create(
                EventTypes.IdeaUpdated,
                project: ProjectSnapshotOf(dto.ProjectId, projectCache),
                idea: ToSnapshot(dto),
                changed: new[] { "tags" }));
        }

        return updated;
    }

    /// <summary>
    /// 批量删除想法（软删，移进回收站）。返回被软删的想法（按请求里 id 的顺序）。
    /// </summary>
    /// <remarks>
    /// 与合并不同：批量删除是**软删**，源想法还在库里（在回收站里）。
    /// 只有合并的源想法才硬删。
    /// <para>
    /// 返回完整表示而不是空响应：前端能立刻把这些条目从本地列表里移除，
    /// 也能显示「已删除 N 条」，不必等重新拉列表。
    /// </para>
    /// <para>
    /// <b>已在回收站里的 id 算「已处理」（幂等）</b>：与单条删除
    /// （<c>DELETE /api/ideas/{id}</c> 对已软删的想法回 204）一个口径 ——
    /// 不重复写 <c>deleted_at</c>（那会把保留期重新开始）、不发
    /// <c>idea.deleted</c> 事件（那会让其它设备收到一条假消息），
    /// 也不把它算进「压根不存在」那 404 里。
    /// </para>
    /// </remarks>
    public IReadOnlyList<IdeaDto> BulkDelete(IReadOnlyList<string> ids)
    {
        var targets = NormalizeIds(ids);

        var deleted = repo.InTransaction(r =>
        {
            var (active, alreadyDeleted) = ResolveForDelete(r, targets);

            // 「已在回收站」也算处理过了：只有压根不存在的才算没处理。
            EnsureSomethingProcessed(
                targets,
                active.Count + alreadyDeleted,
                "选中的想法都不存在了（可能已被合并）");

            // 同一批用同一时刻进回收站。
            var now = DateTimeOffset.UtcNow;
            var results = new List<IdeaDto>(active.Count);
            foreach (var idea in active)
            {
                r.SoftDelete(idea.Id, now);

                // 回读一次：updated_at 被刷新过了，返回的是删除之后的样子。
                results.Add(ToDto(r.Get(idea.Id)!));
            }

            return (IReadOnlyList<IdeaDto>)results;
        });

        var projectCache = new Dictionary<string, ProjectSnapshot?>(StringComparer.Ordinal);
        foreach (var dto in deleted)
        {
            publisher.Publish(EventFactory.Create(
                EventTypes.IdeaDeleted,
                project: ProjectSnapshotOf(dto.ProjectId, projectCache),
                idea: ToSnapshot(dto)));
        }

        return deleted;
    }

    /// <summary>
    /// 把多条想法合并成一条**全新**的想法：正文按源想法的 <c>updated_at</c> 升序以空行拼接，
    /// 标签取所有源标签的并集，标题取第一条源的标题。源想法**硬删除**（不进回收站）。
    /// </summary>
    /// <param name="ids">要合并的想法 id，至少两条。</param>
    /// <param name="content">给字符串则用它当正文；null 表示用自动拼接的结果。</param>
    /// <param name="title">给字符串则用它当标题；null 表示用第一条源的标题。</param>
    /// <remarks>
    /// 跨项目合并不允许（400 <c>merge_cross_project</c>）：把一个项目的想法拼到
    /// 另一个项目下，源与结果之间的归属关系就断了。
    /// <para>
    /// 与批量操作不同，合并**不跳过**不存在的 id：语义是「这 N 条合成一条」，
    /// 少一条就合成不了，所以整体拒绝（404 <c>idea_not_found</c>），一条都不改。
    /// </para>
    /// <para>
    /// 整件事在一个事务里：插新想法 → 写并集标签 → 硬删源想法，
    /// 要么全成要么全不成，不会出现「源想法没了、合并结果也没建」的中间态。
    /// </para>
    /// </remarks>
    public IdeaDto Merge(IReadOnlyList<string> ids, string? content, string? title)
    {
        var targets = NormalizeIds(ids);
        if (targets.Count < 2)
        {
            throw new ApiException(400, "invalid_merge_selection", "合并至少需要两条想法");
        }

        var (merged, mergedFrom) = repo.InTransaction(r =>
        {
            // 源排序：updated_at ASC, id ASC（全序，顺序可复现）。
            var sources = targets
                .Select(id => RequireExisting(r, id))
                .OrderBy(idea => idea.UpdatedAt)
                .ThenBy(idea => idea.Id, StringComparer.Ordinal)
                .ToList();

            var projectIds = sources
                .Select(idea => idea.ProjectId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (projectIds.Count > 1)
            {
                throw new ApiException(400, "merge_cross_project",
                    "不能跨项目合并想法：源想法分属不同项目");
            }

            var now = DateTimeOffset.UtcNow;
            var idea = new Idea(
                Id: MonotonicGuid.NewUuid7().ToString("N"),
                ProjectId: projectIds[0],
                Title: title is not null ? NormalizeTitle(title) : sources[0].Title,
                Content: content is not null
                    ? ValidateContent(content)
                    : string.Join("\n\n", sources.Select(source => source.Content)),
                Tags: NormalizeTags(sources.SelectMany(source => source.Tags).ToArray()),
                Done: false,
                CreatedAt: now,
                UpdatedAt: now,
                DeletedAt: null);

            // Insert 会一并写入标签链接（并集就是标签集）。
            var created = r.Insert(idea);

            // 源想法硬删：idea_tag 靠外键 CASCADE 一起清掉。
            foreach (var source in sources)
            {
                r.HardDelete(source.Id);
            }

            return (ToDto(created), sources.Select(source => source.Id).ToArray());
        });

        // 载荷带上被硬删的源 id：订阅端据此把源条目从本地列表里去掉。
        publisher.Publish(EventFactory.Create(
            EventTypes.IdeaMerged,
            project: ProjectSnapshotOf(merged.ProjectId),
            idea: ToSnapshot(merged),
            mergedFrom: mergedFrom));

        return merged;
    }

    /// <summary>
    /// 请求里的 id 列表：去掉首尾空白、丢掉空项、按原样去重（保序）。
    /// </summary>
    /// <remarks>
    /// 去重是为了「同一个 id 传两次」不被当成两条想法：合并只有一条源想法时
    /// 应当按非法选择拒绝，而不是凭空造出一条重复内容的想法。
    /// </remarks>
    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string?> ids)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in ids)
        {
            var id = raw?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    /// <summary>
    /// 挑出这批 id 里**存在且未删**的想法（不存在的直接跳过），保持请求里的顺序。
    /// </summary>
    /// <remarks>
    /// 批量操作对「陈旧 id」宽容：多端实时同步下，客户端手里的 id 随时可能失效。
    /// 注意这与合并相反 —— 合并的语义是「这 N 条合成一条」，少一条就没法合，
    /// 所以合并必须全部存在。
    /// </remarks>
    private static List<Idea> ResolveExisting(IdeaRepository repo, IReadOnlyList<string> ids)
    {
        var result = new List<Idea>();

        foreach (var id in ids)
        {
            var idea = repo.Get(id);
            if (idea is not null && idea.DeletedAt is null)
            {
                result.Add(idea);
            }
        }

        return result;
    }

    /// <summary>
    /// 批量删除用的三态分辨：压根不存在 / 已在回收站 / 还活着。
    /// </summary>
    /// <remarks>
    /// 删除要区分「已在回收站」与「压根不存在」，所以不能直接用
    /// <see cref="ResolveExisting"/>（它把已软删的与不存在的混在一起，都跳过）。
    /// <para>
    /// 「已在回收站」算**已处理**（幂等，与单条删除同口径）：不重复软删、
    /// 不重复发事件。「压根不存在」才算没处理。
    /// </para>
    /// </remarks>
    private static (List<Idea> Active, int AlreadyDeleted) ResolveForDelete(
        IdeaRepository repo,
        IReadOnlyList<string> ids)
    {
        var active = new List<Idea>();
        var alreadyDeleted = 0;

        foreach (var id in ids)
        {
            var idea = repo.Get(id);

            if (idea is null)
            {
                // 压根没有这条：不算已处理。
                continue;
            }

            if (idea.DeletedAt is not null)
            {
                // 已经在回收站里：算已处理（幂等）。
                alreadyDeleted++;
                continue;
            }

            active.Add(idea);
        }

        return (active, alreadyDeleted);
    }

    /// <summary>
    /// 传的 id 一个都没处理到时报错：「全选错」与「部分对」要能区分开。
    /// </summary>
    /// <remarks>
    /// 传空列表不算「全选错」：那是「没选东西」，空操作即可。
    /// </remarks>
    /// <param name="requested">请求里的 id 列表（去重去空之后）。</param>
    /// <param name="processed">其中被处理到的条数。</param>
    /// <param name="detail">报错时给调用方看的说明。</param>
    private static void EnsureSomethingProcessed(
        IReadOnlyList<string> requested,
        int processed,
        string detail = "选中的想法都不存在了（可能已被删除或合并）")
    {
        if (requested.Count > 0 && processed == 0)
        {
            throw new ApiException(404, "idea_not_found", detail);
        }
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

    /// <summary>项目必须存在且不在回收站里，否则 404。返回那条项目（给事件的项目快照用）。</summary>
    private Project RequireProject(string projectId)
    {
        var project = projects.Get(projectId);
        if (project is null || project.DeletedAt is not null)
        {
            throw new ApiException(404, "project_not_found", $"项目不存在：{projectId}");
        }

        return project;
    }

    /// <summary>
    /// 想法所属项目的快照 —— 想法事件要带上它（spec §11.2：想法事件的结构里也有 project）。
    /// </summary>
    /// <remarks>
    /// 查询在**事务之外**、发事件之前：写事务已经提交了，这里只是多读一行项目，
    /// 不会把事务范围扩大。
    /// <para>
    /// 项目行一定还在（想法外键指着它，项目到期清理时想法跟着级联删），
    /// 万一取不到就返回 null：事件照发，只是载荷里没有项目名。
    /// </para>
    /// </remarks>
    private ProjectSnapshot? ProjectSnapshotOf(string projectId) =>
        ProjectSnapshotOf(projectId, new Dictionary<string, ProjectSnapshot?>(StringComparer.Ordinal));

    /// <summary>
    /// 同上，但带一个**按项目 id 的缓存**：一次批量操作可能涉及多个项目，
    /// 同一个项目只查一次库。
    /// </summary>
    private ProjectSnapshot? ProjectSnapshotOf(
        string projectId,
        Dictionary<string, ProjectSnapshot?> cache)
    {
        if (cache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var project = projects.Get(projectId);
        var snapshot = project is null ? null : ProjectSnapshot.From(project);
        cache[projectId] = snapshot;

        return snapshot;
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

    /// <summary>查询参数去掉首尾空白；空串一律当成「没给」。</summary>
    private static string? NormalizeParameter(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// 状态过滤参数。缺省或空串 = 不过滤；不认识的值一律 400 ——
    /// 默默当成「不过滤」的话，用户以为筛了却拿到全量结果，比报错更难查。
    /// </summary>
    private static IdeaStatusFilter ParseStatus(string? value)
    {
        var normalized = NormalizeParameter(value);

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

    /// <summary>标签匹配方式参数。缺省或空串 = OR；不认识的值一律 400。</summary>
    private static TagMatchMode ParseTagMode(string? value)
    {
        var normalized = NormalizeParameter(value);

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

    /// <summary>
    /// 解析并校验分页参数。规则**只有这一份**。
    /// </summary>
    /// <param name="limit">单页最多几条（字符串）；null / 空串 = 不限。</param>
    /// <param name="offset">已经翻过多少条（从最新一端数，字符串）；null / 空串 = 0。</param>
    /// <returns>解析后的 (limit, offset)。</returns>
    /// <remarks>
    /// 三种非法情形一律 400 <c>invalid_pagination</c>，detail 说清楚收到的是什么：
    /// 不是数字、<c>limit</c> 不在 1..1000、<c>offset</c> 为负、
    /// 以及 <c>offset &gt; 0</c> 而 <c>limit</c> 缺省（翻历史必须知道翻多宽）。
    /// <para>
    /// 为什么自己解析而不绑成 <c>int?</c>：绑 <c>int?</c> 时 <c>?limit=abc</c>
    /// 在参数绑定阶段失败，框架**只设一个 400 状态码就返回** ——
    /// 空 body、没有 code，进不了统一出口（<see cref="ApiException"/>）。
    /// agent 拿到的就是一个空响应，连「是哪儿错了」都读不出来。
    /// 这个应用里既有的查询参数（<c>status</c> / <c>tagMode</c>）也都是
    /// <c>string?</c> + 自己解析 + 自己的错误码 —— 跟它们一致。
    /// </para>
    /// <para>
    /// 「没给 limit」与「limit 给错了」是两件事：没给是「给我全部」
    /// （超上限报 <c>too_many_ideas</c>），给错了就得明说，
    /// 不能默默当成「没给」。
    /// </para>
    /// </remarks>
    private static (int? Limit, int Offset) ParsePagination(string? limit, string? offset)
    {
        var parsedLimit = ParsePageNumber(limit, "limit");
        var parsedOffset = ParsePageNumber(offset, "offset") ?? 0;

        if (parsedLimit is < 1 or > 1000)
        {
            throw new ApiException(400, "invalid_pagination",
                $"limit 必须在 1..1000 之间，收到：{limit}");
        }

        if (parsedOffset < 0)
        {
            throw new ApiException(400, "invalid_pagination",
                $"offset 不能是负数，收到：{offset}");
        }

        if (parsedOffset > 0 && parsedLimit is null)
        {
            throw new ApiException(400, "invalid_pagination",
                "offset 必须与 limit 一起给（offset 单独给没有意义）");
        }

        return (parsedLimit, parsedOffset);
    }

    /// <summary>
    /// 解析一个分页数字。空（null / 空串 / 全是空白）= 没给；
    /// 不是数字 → 400 <c>invalid_pagination</c>。
    /// </summary>
    /// <param name="value">请求里的原始字符串。</param>
    /// <param name="name">参数名（写进 detail，让调用方知道是哪个参数错了）。</param>
    private static int? ParsePageNumber(string? value, string name)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ApiException(400, "invalid_pagination",
                $"{name} 必须是整数，收到：{value}");
        }

        // long 装得下的都先收下，范围判断交给调用方（limit 1..1000 / offset >= 0）。
        if (parsed > int.MaxValue || parsed < int.MinValue)
        {
            throw new ApiException(400, "invalid_pagination", $"{name} 太大了，收到：{value}");
        }

        return (int)parsed;
    }

    /// <summary>
    /// 算这一页每条想法的命中位置（想法 id → 命中区间）。
    /// </summary>
    /// <param name="page">这一页的想法（已经过滤过、切片过）。</param>
    /// <param name="keyword">归一化后的关键词。</param>
    /// <remarks>
    /// 四个字段各算一遍（标题、正文、标签名、项目名 —— 它们都是索引里的列，
    /// 命中了任何一列都算命中）。这段逻辑**只有一份**：
    /// <see cref="Infrastructure.IdeaMatches"/> —— <c>/api/search</c> 的想法组也用它，
    /// 两处各写一份的话，改了字段名或偏移口径只有一处会跟着改。
    /// </remarks>
    private Dictionary<string, SearchMatch[]> BuildMatches(IReadOnlyList<Idea> page, string keyword)
    {
        // 项目名按项目 id 缓存：同一页里通常只有 1~5 个不同项目，
        // 逐条查库的话一页就是几十次往返。
        var projectNames = new Dictionary<string, string>(StringComparer.Ordinal);

        var result = new Dictionary<string, SearchMatch[]>(StringComparer.Ordinal);

        foreach (var idea in page)
        {
            var matches = IdeaMatches.For(idea, ProjectNameOf(idea.ProjectId, projectNames), keyword);

            result[idea.Id] = matches.ToArray();
        }

        return result;
    }

    /// <summary>
    /// 项目名（按项目 id 缓存）。项目不存在时返回空串 ——
    /// 与索引里那一列的写法一致（见 <see cref="Infrastructure.SearchIndex"/>）。
    /// </summary>
    private string ProjectNameOf(string projectId, Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var name = projects.Get(projectId)?.Name ?? string.Empty;
        cache[projectId] = name;

        return name;
    }

    /// <summary>
    /// 想法 → DTO。
    /// </summary>
    /// <remarks>
    /// 静态的：回收站视图也要用同一份口径，两边各写一份的话，
    /// 列表里的想法与回收站里的同一条想法会长得不一样。
    /// </remarks>
    internal static IdeaDto ToDto(Idea idea) => new(
        Id: idea.Id,
        ProjectId: idea.ProjectId,
        Title: idea.Title,
        Content: idea.Content,
        Tags: idea.Tags.ToArray(),
        Done: idea.Done,
        CreatedAt: idea.CreatedAt,
        UpdatedAt: idea.UpdatedAt);

    /// <summary>DTO → 事件载荷里的想法快照。</summary>
    private static IdeaSnapshot ToSnapshot(IdeaDto dto) => new(
        Id: dto.Id,
        ProjectId: dto.ProjectId,
        Title: dto.Title,
        Content: dto.Content,
        Tags: dto.Tags,
        Done: dto.Done,
        CreatedAt: dto.CreatedAt,
        UpdatedAt: dto.UpdatedAt);
}
