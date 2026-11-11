using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 批量操作与合并的集成测试。每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
///
/// 需要确定 <c>updated_at</c> 顺序的场景（合并的拼接顺序、标题取第一条）
/// 直接往库里写行并指定时间戳：不依赖时钟，测试结果可复现。
/// </summary>
public sealed class BulkAndMergeTests
{
    /// <summary>直接写库时用的固定时间戳（早于任何真实写入）。</summary>
    private const string OldStamp = "2026-01-01T00:00:00.0000000Z";

    /// <summary>库里不存在的想法 id（模拟客户端手里过期的 id）。</summary>
    private const string MissingId = "0198f0a1b2c3d4e5f60718293a4b5cff";

    [Fact]
    public async Task Bulk_complete_marks_all_and_bumps_updated_at()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        const string first = "0198f0a1b2c3d4e5f60718293a4b5c01";
        const string second = "0198f0a1b2c3d4e5f60718293a4b5c02";
        InsertIdea(factory.DatabasePath, first, "第一条");
        InsertIdea(factory.DatabasePath, second, "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/complete",
            new { ids = new[] { first, second }, done = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 批量返回数组：每条都是受影响想法的完整表示。
        var ideas = await ReadIdeasAsync(response);
        Assert.Equal(2, ideas.Count);
        Assert.All(ideas, idea => Assert.True(idea.Done));

        // 批量完成也是写操作 → updated_at 刷新（created_at 还是写库时那个旧时间戳）。
        Assert.All(ideas, idea => Assert.True(idea.UpdatedAt > idea.CreatedAt));
    }

    [Fact]
    public async Task Bulk_tags_appends_by_default_and_replaces_when_flagged()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "带标签的想法", tags: new[] { "a" });

        // 默认追加（并集）。
        var appended = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = new[] { id }, tags = new[] { "b" } });

        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
        Assert.Equal(new[] { "a", "b" }, Assert.Single(await ReadIdeasAsync(appended)).Tags);

        // replace: true → 覆盖成给定的标签集。
        var replaced = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = new[] { id }, tags = new[] { "c" }, replace = true });

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(new[] { "c" }, Assert.Single(await ReadIdeasAsync(replaced)).Tags);
    }

    [Fact]
    public async Task Bulk_delete_soft_deletes_all()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/delete",
            new { ids = new[] { first, second } });

        // 批量删除是 POST（动作），不是 DELETE：spec §13.5 里「删除返回 204」
        // 只适用于 HTTP 方法 DELETE，批量与合并一样返回 200 + 受影响实体的数组。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deleted = await ReadIdeasAsync(response);
        Assert.Equal(new[] { first, second }, deleted.Select(idea => idea.Id).ToArray());

        // 列表里没有了。
        Assert.Empty(await ReadIdeasAsync(await client.GetAsync("/api/ideas")));

        // 软删（不是硬删）：行还在，只是 deleted_at 非空。
        foreach (var id in new[] { first, second })
        {
            Assert.Equal(1, QuerySingleInt(factory.DatabasePath,
                "SELECT COUNT(*) FROM idea WHERE id = $id0;", id));
            Assert.NotNull(QuerySingleString(factory.DatabasePath,
                "SELECT deleted_at FROM idea WHERE id = $id0;", id));
        }
    }

    /// <summary>
    /// 批量操作**跳过**不存在的 id：多端实时同步下客户端手里的 id 随时可能失效，
    /// 「你在电脑上选中的 5 条，手机上刚被删掉 1 条」是常态，不是异常。
    /// </summary>
    [Fact]
    public async Task Bulk_complete_skips_unknown_ids()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        // 中间夹一个不存在的 id：处理掉两条有效的，不报错。
        var response = await client.PostAsJsonAsync("/api/ideas/bulk/complete",
            new { ids = new[] { first, MissingId, second }, done = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 返回体是**实际被处理到的**那些想法（跳过的不在里面），顺序按请求。
        var ideas = await ReadIdeasAsync(response);
        Assert.Equal(new[] { first, second }, ideas.Select(idea => idea.Id).ToArray());
        Assert.All(ideas, idea => Assert.True(idea.Done));
    }

    [Fact]
    public async Task Bulk_tags_skips_unknown_ids()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = new[] { first, MissingId, second }, tags = new[] { "x" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ideas = await ReadIdeasAsync(response);
        Assert.Equal(new[] { first, second }, ideas.Select(idea => idea.Id).ToArray());
        Assert.All(ideas, idea => Assert.Equal(new[] { "x" }, idea.Tags));

        // 被跳过的那条当然没被动过 —— 它本来就不存在。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea WHERE id = $id0;", MissingId));
    }

    [Fact]
    public async Task Bulk_delete_skips_unknown_ids()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/delete",
            new { ids = new[] { first, MissingId, second } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 返回体是**实际被处理到的**那些想法（跳过的不在里面）。
        var deleted = await ReadIdeasAsync(response);
        Assert.Equal(new[] { first, second }, deleted.Select(idea => idea.Id).ToArray());

        // 两条有效的都进回收站了。
        Assert.Empty(await ReadIdeasAsync(await client.GetAsync("/api/ideas")));

        foreach (var id in new[] { first, second })
        {
            Assert.NotNull(QuerySingleString(factory.DatabasePath,
                "SELECT deleted_at FROM idea WHERE id = $id0;", id));
        }
    }

    /// <summary>
    /// 换标签也是写操作 → 必须刷新 <c>updated_at</c>（挪到列表最下面）。
    /// </summary>
    [Fact]
    public async Task Bulk_tags_bumps_updated_at()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 写库时指定旧时间戳：批量换标签后 updated_at 必须被刷新。
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", "第一条");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = new[] { "0198f0a1b2c3d4e5f60718293a4b5c01", "0198f0a1b2c3d4e5f60718293a4b5c02" },
                tags = new[] { "x" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ideas = await ReadIdeasAsync(response);
        Assert.Equal(2, ideas.Count);

        // created_at 还是写库时那个旧时间戳 → updated_at 必须更大。
        Assert.All(ideas, idea => Assert.True(idea.UpdatedAt > idea.CreatedAt));
    }

    /// <summary>
    /// 删除也是写操作 → 必须刷新 <c>updated_at</c>。
    /// </summary>
    [Fact]
    public async Task Bulk_delete_bumps_updated_at()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", "第一条");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/delete",
            new { ids = new[] { "0198f0a1b2c3d4e5f60718293a4b5c01", "0198f0a1b2c3d4e5f60718293a4b5c02" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var deleted = await ReadIdeasAsync(response);
        Assert.Equal(2, deleted.Count);

        // created_at 还是写库时那个旧时间戳 → updated_at 必须更大。
        Assert.All(deleted, idea => Assert.True(idea.UpdatedAt > idea.CreatedAt));
    }

    /// <summary>
    /// 空 ids 列表是空操作（不是「全选错」）：200 + 空数组，什么都不改。
    /// </summary>
    [Fact]
    public async Task Bulk_with_empty_ids_is_a_no_op()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var existing = await CreateIdeaAsync(client, "不该被动的一条");

        var complete = await client.PostAsJsonAsync("/api/ideas/bulk/complete",
            new { ids = Array.Empty<string>(), done = true });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Empty(await ReadIdeasAsync(complete));

        var tags = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = Array.Empty<string>(), tags = new[] { "x" } });
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        Assert.Empty(await ReadIdeasAsync(tags));

        var delete = await client.PostAsJsonAsync("/api/ideas/bulk/delete",
            new { ids = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Empty(await ReadIdeasAsync(delete));

        // 那条想法一点没被动过：没被标记完成、没加标签、没进回收站。
        var idea = Assert.Single(await ReadIdeasAsync(await client.GetAsync("/api/ideas")));
        Assert.Equal(existing, idea.Id);
        Assert.False(idea.Done);
        Assert.Empty(idea.Tags);
    }

    /// <summary>
    /// 传的 id 一个都不存在时报 404 <c>idea_not_found</c>：
    /// 「全选错」与「部分对」要能区分开。
    /// </summary>
    [Fact]
    public async Task Bulk_with_only_unknown_ids_returns_404_code_idea_not_found()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var complete = await client.PostAsJsonAsync("/api/ideas/bulk/complete",
            new { ids = new[] { MissingId }, done = true });
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
        Assert.Equal("idea_not_found",
            (await ReadProblemAsync(complete)).GetProperty("code").GetString());

        var tags = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = new[] { MissingId }, tags = new[] { "x" } });
        Assert.Equal(HttpStatusCode.NotFound, tags.StatusCode);
        Assert.Equal("idea_not_found",
            (await ReadProblemAsync(tags)).GetProperty("code").GetString());

        var delete = await client.PostAsJsonAsync("/api/ideas/bulk/delete",
            new { ids = new[] { MissingId } });
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("idea_not_found",
            (await ReadProblemAsync(delete)).GetProperty("code").GetString());
    }

    /// <summary>
    /// 合并**不跳过**：少一条就合成不了，整体拒绝且一条都不改。
    /// </summary>
    [Fact]
    public async Task Merge_with_unknown_id_returns_404_and_changes_nothing()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");

        var response = await client.PostAsJsonAsync("/api/ideas/merge",
            new { ids = new[] { first, MissingId } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("idea_not_found",
            (await ReadProblemAsync(response)).GetProperty("code").GetString());

        // 源想法还在，也没有多出合并结果。
        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(first, Assert.Single(ideas).Id);
    }

    [Fact]
    public async Task Merge_creates_new_idea_with_concatenated_content()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 三条想法的 updated_at 依次递增 → 拼接顺序是 A、B、C（与请求里的顺序无关）。
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", "A",
            stamp: "2026-01-01T10:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "B",
            stamp: "2026-01-01T11:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c03", "C",
            stamp: "2026-01-01T12:00:00.0000000Z");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new
        {
            ids = new[]
            {
                "0198f0a1b2c3d4e5f60718293a4b5c03",
                "0198f0a1b2c3d4e5f60718293a4b5c01",
                "0198f0a1b2c3d4e5f60718293a4b5c02"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var merged = ReadIdea(await response.Content.ReadAsStringAsync());

        // 按 updated_at 升序以空行拼接。
        Assert.Equal("A\n\nB\n\nC", merged.Content);

        // 合并结果是全新的一条：未完成、落在源项目里。
        Assert.False(merged.Done);
        Assert.Equal("inbox", merged.ProjectId);
        Assert.DoesNotContain(merged.Id, new[]
        {
            "0198f0a1b2c3d4e5f60718293a4b5c01",
            "0198f0a1b2c3d4e5f60718293a4b5c02",
            "0198f0a1b2c3d4e5f60718293a4b5c03"
        });
    }

    [Fact]
    public async Task Merge_result_takes_union_of_tags()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", "第一条",
            tags: new[] { "a" }, stamp: "2026-01-01T10:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "第二条",
            tags: new[] { "b" }, stamp: "2026-01-01T11:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c03", "第三条",
            tags: new[] { "a" }, stamp: "2026-01-01T12:00:00.0000000Z");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new
        {
            ids = new[]
            {
                "0198f0a1b2c3d4e5f60718293a4b5c01",
                "0198f0a1b2c3d4e5f60718293a4b5c02",
                "0198f0a1b2c3d4e5f60718293a4b5c03"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var merged = ReadIdea(await response.Content.ReadAsStringAsync());

        // 并集：{a} ∪ {b} ∪ {a} = {a, b}。
        Assert.Equal(new[] { "a", "b" }, merged.Tags);
    }

    [Fact]
    public async Task Merge_result_takes_title_of_first_source()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 「第一条」= updated_at 最早的那条。
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", "正文一",
            title: "第一条的标题", stamp: "2026-01-01T10:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "正文二",
            title: "第二条的标题", stamp: "2026-01-01T11:00:00.0000000Z");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new
        {
            ids = new[]
            {
                "0198f0a1b2c3d4e5f60718293a4b5c01",
                "0198f0a1b2c3d4e5f60718293a4b5c02"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var merged = ReadIdea(await response.Content.ReadAsStringAsync());

        Assert.Equal("第一条的标题", merged.Title);
    }

    [Fact]
    public async Task Merge_hard_deletes_sources()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        const string first = "0198f0a1b2c3d4e5f60718293a4b5c01";
        const string second = "0198f0a1b2c3d4e5f60718293a4b5c02";
        InsertIdea(factory.DatabasePath, first, "第一条", tags: new[] { "a" });
        InsertIdea(factory.DatabasePath, second, "第二条", tags: new[] { "b" });

        var response = await client.PostAsJsonAsync("/api/ideas/merge",
            new { ids = new[] { first, second } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var merged = ReadIdea(await response.Content.ReadAsStringAsync());

        // 列表里只剩合并结果。
        var remaining = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(merged.Id, Assert.Single(remaining).Id);

        // 源想法硬删除（不进回收站）：库里查不到。
        foreach (var id in new[] { first, second })
        {
            Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
                "SELECT COUNT(*) FROM idea WHERE id = $id0;", id));
        }

        // idea_tag 随源想法一起级联清掉。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea_tag WHERE idea_id IN ($id0, $id1);", first, second));
    }

    [Fact]
    public async Task Merge_across_projects_returns_400_code_merge_cross_project()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "另一个项目");
        var first = await CreateIdeaAsync(client, "收件箱里的想法");
        var second = await CreateIdeaAsync(client, "另一个项目里的想法", projectId: projectId);

        var response = await client.PostAsJsonAsync("/api/ideas/merge",
            new { ids = new[] { first, second } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("merge_cross_project",
            (await ReadProblemAsync(response)).GetProperty("code").GetString());

        // 不产生任何改动：两条源想法都还在，也没有多出新想法。
        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(2, ideas.Count);
        Assert.Contains(ideas, idea => idea.Id == first);
        Assert.Contains(ideas, idea => idea.Id == second);
    }

    [Fact]
    public async Task Merge_with_fewer_than_two_ids_returns_400_code_invalid_merge_selection()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "只有一条");

        var one = await client.PostAsJsonAsync("/api/ideas/merge", new { ids = new[] { id } });

        Assert.Equal(HttpStatusCode.BadRequest, one.StatusCode);
        Assert.Equal("invalid_merge_selection",
            (await ReadProblemAsync(one)).GetProperty("code").GetString());

        // 一条都没有同样是非法选择。
        var none = await client.PostAsJsonAsync("/api/ideas/merge",
            new { ids = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("invalid_merge_selection",
            (await ReadProblemAsync(none)).GetProperty("code").GetString());

        // 什么都没改。
        Assert.Single(await ReadIdeasAsync(await client.GetAsync("/api/ideas")));
    }

    [Fact]
    public async Task Merge_preserves_verbatim_special_characters()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        const string special = "带\"引号\"\n换行\\反斜杠 <b>标签</b>";

        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", special,
            stamp: "2026-01-01T10:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "普通正文",
            stamp: "2026-01-01T11:00:00.0000000Z");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new
        {
            ids = new[]
            {
                "0198f0a1b2c3d4e5f60718293a4b5c01",
                "0198f0a1b2c3d4e5f60718293a4b5c02"
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var merged = ReadIdea(await response.Content.ReadAsStringAsync());

        // 逐字保持：引号、换行、反斜杠、尖括号都不被转义或吞掉。
        Assert.Equal(special + "\n\n" + "普通正文", merged.Content);

        // 库里也是逐字相同。
        Assert.Equal(special + "\n\n" + "普通正文", QuerySingleString(factory.DatabasePath,
            "SELECT content FROM idea WHERE id = $id0;", merged.Id));
    }

    /// <summary>
    /// 请求体带了 content / title 时用请求体的（覆盖自动拼接与首条源的标题）。
    /// </summary>
    /// <remarks>
    /// 补充测试：简报 Step 3 明确要求这个行为，但列出的 10 条测试没有覆盖它。
    /// </remarks>
    [Fact]
    public async Task Merge_request_body_overrides_content_and_title()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c01", "第一条",
            title: "第一条的标题", stamp: "2026-01-01T10:00:00.0000000Z");
        InsertIdea(factory.DatabasePath, "0198f0a1b2c3d4e5f60718293a4b5c02", "第二条",
            title: "第二条的标题", stamp: "2026-01-01T11:00:00.0000000Z");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new
        {
            ids = new[]
            {
                "0198f0a1b2c3d4e5f60718293a4b5c01",
                "0198f0a1b2c3d4e5f60718293a4b5c02"
            },
            content = "自己写的合并正文",
            title = "自己写的标题"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var merged = ReadIdea(await response.Content.ReadAsStringAsync());

        Assert.Equal("自己写的合并正文", merged.Content);
        Assert.Equal("自己写的标题", merged.Title);
    }

    /// <summary>
    /// 合并必须原子：插新想法、写并集标签、硬删源想法在**同一个事务**里。
    /// </summary>
    /// <remarks>
    /// 用一个 SQLite 触发器在删除第二条源想法时制造失败：
    /// 这几步若不在同一个事务里，已经插进去的新想法会留在库里，
    /// 第一条源想法也会被删掉 —— 源没了、结果也没建成的中间态。
    /// <para>
    /// 失败时的响应是 500 + ProblemDetails + <c>code=internal_error</c>：
    /// DB 层异常不能裸着逃到客户端（那样 agent 消费方拿不到稳定的错误码），
    /// 但真实异常信息只进日志，不进响应体。
    /// </para>
    /// <para>
    /// 补充测试：简报把「合并必须原子」列为硬要求，但列出的 10 条测试没有覆盖它。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Merge_is_atomic_when_a_step_fails()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        const string first = "0198f0a1b2c3d4e5f60718293a4b5c01";
        const string second = "0198f0a1b2c3d4e5f60718293a4b5c02";
        InsertIdea(factory.DatabasePath, first, "第一条");
        InsertIdea(factory.DatabasePath, second, "第二条");

        // 触发器里不能用绑定变量，id 直接写死在 DDL 里。
        Execute(factory.DatabasePath,
            $"CREATE TRIGGER block_delete BEFORE DELETE ON idea WHEN OLD.id = '{second}' " +
            "BEGIN SELECT RAISE(ABORT, 'blocked'); END;");

        // 合并在硬删第二条源想法时失败。
        var response = await client.PostAsJsonAsync("/api/ideas/merge",
            new { ids = new[] { first, second } });

        // 失败也要有稳定的机器可读错误码。
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("internal_error", problem.GetProperty("code").GetString());

        // 真实异常信息不泄露给外部。
        Assert.DoesNotContain("blocked", problem.GetProperty("detail").GetString() ?? string.Empty);

        // 整件事回滚：两条源想法都还在（第一条的删除也被撤销），也没有留下合并结果。
        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(new[] { first, second }, ideas.Select(idea => idea.Id).ToArray());

        Assert.Equal(2, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea;"));
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(
        HttpClient client, string content, string? projectId = null, string[]? tags = null)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content, projectId, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读想法列表（裸数组）。</summary>
    private static async Task<List<IdeaSummary>> ReadIdeasAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.EnumerateArray()
            .Select(element => new IdeaSummary(
                Id: element.GetProperty("id").GetString()!,
                ProjectId: element.GetProperty("projectId").GetString()!,
                Title: element.GetProperty("title").GetString(),
                Content: element.GetProperty("content").GetString()!,
                Tags: element.GetProperty("tags").EnumerateArray()
                    .Select(tag => tag.GetString()!).ToArray(),
                Done: element.GetProperty("done").GetBoolean(),
                CreatedAt: element.GetProperty("createdAt").GetDateTimeOffset(),
                UpdatedAt: element.GetProperty("updatedAt").GetDateTimeOffset()))
            .ToList();
    }

    /// <summary>读单条想法的响应体。</summary>
    private static IdeaSummary ReadIdea(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new IdeaSummary(
            Id: root.GetProperty("id").GetString()!,
            ProjectId: root.GetProperty("projectId").GetString()!,
            Title: root.GetProperty("title").GetString(),
            Content: root.GetProperty("content").GetString()!,
            Tags: root.GetProperty("tags").EnumerateArray()
                .Select(tag => tag.GetString()!).ToArray(),
            Done: root.GetProperty("done").GetBoolean(),
            CreatedAt: root.GetProperty("createdAt").GetDateTimeOffset(),
            UpdatedAt: root.GetProperty("updatedAt").GetDateTimeOffset());
    }

    /// <summary>读 ProblemDetails 响应体。</summary>
    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // JsonElement 在文档释放后失效，复制一份出去。
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 直接往库里写一条想法（绕过 API）：让 <c>updated_at</c> 完全确定，
    /// 合并的拼接顺序与「第一条」不依赖时钟。
    /// </summary>
    private static void InsertIdea(
        string databasePath,
        string id,
        string content,
        string? title = null,
        string[]? tags = null,
        string projectId = "inbox",
        string? stamp = null)
    {
        var createdAt = stamp ?? OldStamp;
        var updatedAt = stamp ?? OldStamp;

        using var connection = OpenDatabase(databasePath);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO idea(id, project_id, title, content, done, deleted_at, created_at, updated_at) " +
                "VALUES ($id, $projectId, $title, $content, 0, NULL, $createdAt, $updatedAt);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$title", title is null ? (object)DBNull.Value : title);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$createdAt", createdAt);
            command.Parameters.AddWithValue("$updatedAt", updatedAt);
            command.ExecuteNonQuery();
        }

        foreach (var tag in tags ?? Array.Empty<string>())
        {
            using (var insertTag = connection.CreateCommand())
            {
                insertTag.CommandText = "INSERT OR IGNORE INTO tag(name) VALUES ($name);";
                insertTag.Parameters.AddWithValue("$name", tag);
                insertTag.ExecuteNonQuery();
            }

            using var link = connection.CreateCommand();
            link.CommandText =
                "INSERT OR IGNORE INTO idea_tag(idea_id, tag_id) " +
                "SELECT $ideaId, id FROM tag WHERE name = $name COLLATE NOCASE;";
            link.Parameters.AddWithValue("$ideaId", id);
            link.Parameters.AddWithValue("$name", tag);
            link.ExecuteNonQuery();
        }
    }

    private static string? QuerySingleString(string databasePath, string sql, params string[] ids)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < ids.Length; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", ids[i]);
        }

        return command.ExecuteScalar() as string;
    }

    private static long QuerySingleInt(string databasePath, string sql, params string[] ids)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < ids.Length; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", ids[i]);
        }

        var value = command.ExecuteScalar();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>在测试库上执行一条不带参数的语句（建触发器之类）。</summary>
    private static void Execute(string databasePath, string sql)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record IdeaSummary(string Id, string ProjectId, string? Title, string Content,
                                     string[] Tags, bool Done,
                                     DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
