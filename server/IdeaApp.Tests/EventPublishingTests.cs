using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 服务侧事件发布接线的集成测试。
///
/// 这里把组合根注册的 <see cref="IEventPublisher"/> 换成记录型的 fake：
/// 断言「哪个写方法发了什么事件、载荷长什么样」。
///
/// 最重要的两条：
/// <list type="bullet">
/// <item>每个写方法都要发对应类型的事件（事件总线本身没人 publish 就是死的）；</item>
/// <item>事件必须在**事务提交之后**才发 —— 回滚了就不能有幽灵事件。</item>
/// </list>
/// </summary>
public sealed class EventPublishingTests
{
    [Fact]
    public async Task Create_idea_publishes_idea_created_after_commit()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ideas", new
        {
            content = "第一条想法",
            tags = new[] { "灵感" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var createdId = await ReadIdAsync(response);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaCreated, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(createdId, idea.Id);
        Assert.Equal("inbox", idea.ProjectId);
        Assert.Equal("第一条想法", idea.Content);
        Assert.Equal(new[] { "灵感" }, idea.Tags);
        Assert.False(idea.Done);

        // 新建/删除这类事件没有「改了哪些字段」的概念。
        Assert.Null(evt.Changed);
        Assert.Null(evt.MergedFrom);

        // 想法事件**也带项目快照**（spec §11.2）：没它的话项目级 webhook
        // 匹配不到想法事件，{{project}} 占位符也是空的。
        var project = Assert.IsType<ProjectSnapshot>(evt.Project);
        Assert.Equal("inbox", project.Id);
        Assert.Equal("收件箱", project.Name);
    }

    /// <summary>
    /// 想法事件**必须带项目快照**：建、改、设标签、标记完成、删 —— 每一条都要带，
    /// 带的是这条想法**所属**的项目。
    /// </summary>
    /// <remarks>
    /// 没有项目快照的两处后果：项目级 webhook 按项目匹配时收不到想法事件；
    /// <c>{{project}}</c> 占位符渲染成空串（Telegram 模板首行就没有项目名）。
    /// </remarks>
    [Fact]
    public async Task Idea_events_carry_the_project_snapshot()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var projectId = await ReadIdAsync(
            await client.PostAsJsonAsync("/api/projects", new { name = "带快照的项目" }));

        // 准备阶段（建项目）的事件不算。
        publisher.Clear();

        var ideaId = await CreateIdeaAsync(client, "第一条", projectId: projectId);

        await PatchJsonAsync(client, $"/api/ideas/{ideaId}", new { content = "改过的正文" });
        await client.PutAsJsonAsync($"/api/ideas/{ideaId}/tags", new { tags = new[] { "标签" } });
        await client.PostAsJsonAsync($"/api/ideas/{ideaId}/complete", new { done = true });
        await client.DeleteAsync($"/api/ideas/{ideaId}");

        Assert.Equal(5, publisher.Events.Count);

        Assert.All(publisher.Events, evt =>
        {
            Assert.StartsWith("idea.", evt.Type, StringComparison.Ordinal);

            var project = Assert.IsType<ProjectSnapshot>(evt.Project);
            Assert.Equal(projectId, project.Id);
            Assert.Equal("带快照的项目", project.Name);
        });
    }

    [Fact]
    public async Task Update_idea_publishes_idea_updated_with_changed_fields()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "原来的正文");

        // 准备阶段的事件不算（这里只关心这次修改发的）。
        publisher.Clear();

        var response = await PatchJsonAsync(client, $"/api/ideas/{id}", new { content = "新的正文" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaUpdated, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(id, idea.Id);
        Assert.Equal("新的正文", idea.Content);

        // 只列**真正改了**的字段：这次只改了正文。
        // 「标题」这个字段名已经不存在了，changed 里不可能有它。
        Assert.Equal(new[] { "content" }, evt.Changed);
    }

    [Fact]
    public async Task Set_tags_publishes_idea_updated_with_tags()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "正文");

        publisher.Clear();

        var response = await client.PutAsJsonAsync($"/api/ideas/{id}/tags",
            new { tags = new[] { "甲", "乙" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaUpdated, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(id, idea.Id);

        // 标签按名字排序（与接口返回的口径一致）。
        Assert.Equal(new[] { "乙", "甲" }, idea.Tags);

        Assert.Equal(new[] { "tags" }, evt.Changed);
    }

    [Fact]
    public async Task Set_done_publishes_idea_updated_with_done()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "正文");

        publisher.Clear();

        var response = await client.PostAsJsonAsync($"/api/ideas/{id}/complete", new { done = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaUpdated, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(id, idea.Id);
        Assert.True(idea.Done);

        Assert.Equal(new[] { "done" }, evt.Changed);
    }

    [Fact]
    public async Task Delete_idea_publishes_idea_deleted()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "正文");

        publisher.Clear();

        var response = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaDeleted, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(id, idea.Id);
    }

    [Fact]
    public async Task Restore_idea_publishes_idea_restored()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "正文");

        await client.DeleteAsync($"/api/ideas/{id}");

        // 只关心恢复这一步发的事件。
        publisher.Clear();

        var response = await client.PostAsync($"/api/ideas/{id}/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaRestored, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(id, idea.Id);
    }

    [Fact]
    public async Task Merge_publishes_idea_merged_with_merged_from()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        publisher.Clear();

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new { ids = new[] { first, second } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var mergedId = await ReadIdAsync(response);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaMerged, evt.Type);

        var idea = Assert.IsType<IdeaSnapshot>(evt.Idea);
        Assert.Equal(mergedId, idea.Id);

        // 被硬删的源想法 id 在载荷里：客户端据此把源条目从本地列表里去掉。
        Assert.NotNull(evt.MergedFrom);
        Assert.Equal(2, evt.MergedFrom.Length);
        Assert.Contains(first, evt.MergedFrom);
        Assert.Contains(second, evt.MergedFrom);
    }

    [Fact]
    public async Task Create_project_publishes_project_created()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/projects", new { name = "新项目" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var createdId = await ReadIdAsync(response);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.ProjectCreated, evt.Type);

        var project = Assert.IsType<ProjectSnapshot>(evt.Project);
        Assert.Equal(createdId, project.Id);
        Assert.Equal("新项目", project.Name);

        // 快照里带着排序位置：订阅端据此摆项目，不用整表重拉。
        Assert.Equal(1, project.SortOrder);

        Assert.Null(evt.Idea);
    }

    [Fact]
    public async Task Rename_project_publishes_project_updated_with_name()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/projects", new { name = "旧名字" });
        var id = await ReadIdAsync(response);

        publisher.Clear();

        var renamed = await PatchJsonAsync(client, $"/api/projects/{id}", new { name = "新名字" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.ProjectUpdated, evt.Type);

        var project = Assert.IsType<ProjectSnapshot>(evt.Project);
        Assert.Equal(id, project.Id);
        Assert.Equal("新名字", project.Name);

        // 改名不动排序位置。
        Assert.Equal(1, project.SortOrder);

        Assert.Equal(new[] { "name" }, evt.Changed);
    }

    [Fact]
    public async Task Delete_project_publishes_project_deleted()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/projects", new { name = "要删的项目" });
        var id = await ReadIdAsync(response);

        publisher.Clear();

        var deleted = await client.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var evt = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.ProjectDeleted, evt.Type);

        var project = Assert.IsType<ProjectSnapshot>(evt.Project);
        Assert.Equal(id, project.Id);
        Assert.Equal("要删的项目", project.Name);
        Assert.Equal(1, project.SortOrder);
    }

    /// <summary>
    /// 批量操作是**每条想法一个事件**：客户端按事件逐条更新，不用重新拉整个列表。
    /// </summary>
    [Fact]
    public async Task Bulk_set_done_publishes_one_event_per_idea()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        publisher.Clear();

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/complete",
            new { ids = new[] { first, second }, done = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(2, publisher.Events.Count);
        Assert.All(publisher.Events, evt =>
        {
            Assert.Equal(EventTypes.IdeaUpdated, evt.Type);
            Assert.Equal(new[] { "done" }, evt.Changed);
        });
        Assert.Equal(new[] { first, second },
            publisher.Events.Select(evt => Assert.IsType<IdeaSnapshot>(evt.Idea).Id).ToArray());
    }

    [Fact]
    public async Task Bulk_set_tags_publishes_one_event_per_idea()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        publisher.Clear();

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/tags",
            new { ids = new[] { first, second }, tags = new[] { "批量" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(2, publisher.Events.Count);
        Assert.All(publisher.Events, evt =>
        {
            Assert.Equal(EventTypes.IdeaUpdated, evt.Type);
            Assert.Equal(new[] { "tags" }, evt.Changed);
            Assert.Equal(new[] { "批量" }, Assert.IsType<IdeaSnapshot>(evt.Idea).Tags);
        });
    }

    [Fact]
    public async Task Bulk_delete_publishes_one_idea_deleted_event_per_idea()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        publisher.Clear();

        var response = await client.PostAsJsonAsync("/api/ideas/bulk/delete",
            new { ids = new[] { first, second } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(2, publisher.Events.Count);
        Assert.All(publisher.Events, evt => Assert.Equal(EventTypes.IdeaDeleted, evt.Type));
        Assert.Equal(new[] { first, second },
            publisher.Events.Select(evt => Assert.IsType<IdeaSnapshot>(evt.Idea).Id).ToArray());
    }

    /// <summary>
    /// 重排：顺序真的变了的项目各发一条 <c>project.updated</c>。
    /// </summary>
    /// <remarks>
    /// 重排会重写所有项目的 sort_order（没被列出的接在后面），
    /// 所以位置没动的项目不该发事件 —— 订阅端按事件更新顺序才是准的。
    /// </remarks>
    [Fact]
    public async Task Reorder_projects_publishes_project_updated_per_reordered_project()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var first = await ReadIdAsync(await client.PostAsJsonAsync("/api/projects", new { name = "第一个" }));
        var second = await ReadIdAsync(await client.PostAsJsonAsync("/api/projects", new { name = "第二个" }));

        publisher.Clear();

        var response = await client.PostAsJsonAsync("/api/projects/reorder", new { ids = new[] { second, first } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.All(publisher.Events, evt =>
        {
            Assert.Equal(EventTypes.ProjectUpdated, evt.Type);
            Assert.Equal(new[] { "sortOrder" }, evt.Changed);
        });

        // 排完是：第二个(0) → 第一个(1) → 收件箱(2)。
        // 「第二个」与「收件箱」的位置变了，「第一个」没动。
        var changed = publisher.Events.Select(evt => Assert.IsType<ProjectSnapshot>(evt.Project).Id).ToArray();
        Assert.Equal(2, changed.Length);
        Assert.Contains(second, changed);
        Assert.Contains("inbox", changed);
        Assert.DoesNotContain(first, changed);

        // 载荷里必须带**新的**位置：changed 说「顺序变了」，订阅端要靠这个值
        // 更新本地顺序，缺了它就只能整表重拉。
        var positions = publisher.Events.ToDictionary(
            evt => Assert.IsType<ProjectSnapshot>(evt.Project).Id,
            evt => Assert.IsType<ProjectSnapshot>(evt.Project).SortOrder);

        Assert.Equal(0, positions[second]);
        Assert.Equal(2, positions["inbox"]);
    }

    /// <summary>
    /// 防幽灵事件：事务失败并回滚时，**一个事件都不能发出去**。
    /// 事件必须在事务提交之后才 publish。
    /// </summary>
    /// <remarks>
    /// 失败点选在**提交那一刻**（延迟外键约束要到 COMMIT 才检查）：
    /// 于是整个事务体都跑完了 —— 包括任何写在回调末尾的 publish ——
    /// 提交时才失败回滚。这样把 publish 挪进事务回调（哪怕挪到最后一句）
    /// 也会被这条测试抓住。
    /// <para>
    /// 用「硬删第二条源想法时失败」那种触发器则不行：失败发生在 publish 之前，
    /// publish 根本执行不到，测试照样绿 —— 证伪力不够。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Events_are_published_only_after_the_transaction_commits()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        // 准备阶段的事件不算（下面只关心这次失败的事务有没有留下幽灵事件）。
        publisher.Clear();

        // 延迟外键：违规要到 COMMIT 时才报。
        // 触发器在合并结果插进 idea 表的那一刻，往这张表里塞一个指向不存在
        // 想法的行 —— 事务体照常跑完，提交时才失败。
        Execute(factory.DatabasePath,
            "CREATE TABLE commit_guard(id INTEGER PRIMARY KEY, " +
            "idea_id TEXT REFERENCES idea(id) DEFERRABLE INITIALLY DEFERRED);");
        Execute(factory.DatabasePath,
            "CREATE TRIGGER poison_commit AFTER INSERT ON idea BEGIN " +
            "INSERT INTO commit_guard(idea_id) VALUES('00000000000000000000000000000000'); END;");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new { ids = new[] { first, second } });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 事务回滚了 → 不能有任何事件被发出去。
        Assert.Empty(publisher.Events);

        // 回滚是真的回滚了：两条源想法都还在（第一条的硬删也被撤销），
        // 也没有留下合并结果。
        var listed = await client.GetAsync("/api/ideas");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        using var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var ids = document.RootElement.EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);

        Assert.Equal(2, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea;"));
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(
        HttpClient client,
        string content,
        string[]? tags = null,
        string? projectId = null)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content, tags, projectId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    /// <summary>读响应体里的 id。</summary>
    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>发一个 PATCH 请求（HttpClient 没有 PatchAsJsonAsync）。</summary>
    private static Task<HttpResponseMessage> PatchJsonAsync(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body)
        };

        return client.SendAsync(request);
    }

    /// <summary>在测试库上执行一条不带参数的语句（建触发器之类）。</summary>
    private static void Execute(string databasePath, string sql)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());

        connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>在测试库里查一个整数（断言回滚有没有真的发生）。</summary>
    private static long QuerySingleInt(string databasePath, string sql)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());

        connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        finally
        {
            connection.Dispose();
        }
    }

}
