using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 想法 API 集成测试。每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </summary>
public sealed class IdeaApiTests
{
    [Fact]
    public async Task Create_idea_defaults_to_inbox_and_returns_201()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ideas", new { content = "第一条" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // 没给 projectId 就落到内置收件箱。
        Assert.Equal("inbox", root.GetProperty("projectId").GetString());
        Assert.Equal("第一条", root.GetProperty("content").GetString());

        // 想法没有「标题」这个概念了：响应里没有 title 键（不是 null，是没有）。
        Assert.False(root.TryGetProperty("title", out _));

        // 没给 tags 就是空数组，不是 null。
        var tags = root.GetProperty("tags");
        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Empty(tags.EnumerateArray());

        Assert.False(root.GetProperty("done").GetBoolean());

        var id = root.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.True(IsLowerHex(c), $"id 不是 32 位小写 hex：{id}"));
    }

    /// <summary>
    /// 响应里不能有 <c>title</c> 键 —— 想法就是一段文字。
    /// </summary>
    [Fact]
    public async Task Create_idea_returns_no_title_field()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ideas", new { content = "只有正文" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(document.RootElement.TryGetProperty("title", out _));
    }

    /// <summary>
    /// 老客户端仍然会发 <c>title</c>：System.Text.Json 静默忽略未知成员，
    /// 内容照常保存，响应里也不该冒出 title 键（不为单个字段加特例）。
    /// </summary>
    [Fact]
    public async Task Create_idea_ignores_a_legacy_title_field()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ideas", new
        {
            content = "正文",
            title = "老客户端的标题"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("正文", root.GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Create_idea_with_empty_content_returns_400_code_invalid_content()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        foreach (var content in new[] { "", "   ", "\t\n" })
        {
            var response = await client.PostAsJsonAsync("/api/ideas", new { content });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_content", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Create_idea_with_unknown_project_returns_404_code_project_not_found()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ideas", new
        {
            projectId = "0198f0a1b2c3d4e5f60718293a4b5c6d",
            content = "挂错项目"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("project_not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Update_idea_bumps_updated_at_and_moves_to_end_of_list()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.PatchAsJsonAsync($"/api/ideas/{first}", new { content = "改过的正文" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // updated_at 被刷新 → 排序（updated_at ASC, id ASC）里挪到最后。
        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(new[] { second, first }, ideas.Select(idea => idea.Id).ToArray());
    }

    [Fact]
    public async Task Same_timestamp_ideas_keep_insertion_order()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 两条 updated_at 完全相同的想法：顺序必须由 id ASC 决定，不许随机跳动。
        const string stamp = "2026-01-01T00:00:00.0000000Z";
        const string earlier = "0198f0a1b2c3d4e5f60718293a4b5c01";
        const string later = "0198f0a1b2c3d4e5f60718293a4b5c02";

        InsertIdeaRow(factory.DatabasePath, earlier, stamp);
        InsertIdeaRow(factory.DatabasePath, later, stamp);

        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));

        Assert.Equal(new[] { earlier, later }, ideas.Select(idea => idea.Id).ToArray());
    }

    [Fact]
    public async Task SetTags_replaces_existing_tags_case_insensitively()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "带标签的想法");

        var response = await client.PutAsJsonAsync($"/api/ideas/{id}/tags",
            new { tags = new[] { "A", "a", "B" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var idea = ReadIdea(await response.Content.ReadAsStringAsync());

        // 大小写不敏感去重，保留首次出现的写法。
        Assert.Equal(new[] { "A", "B" }, idea.Tags);

        // 关联行也只有两条（不是三条）。
        Assert.Equal(2, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea_tag WHERE idea_id = $id;", id));

        // 覆盖式：再设一次，旧标签被换掉。
        var replaced = await client.PutAsJsonAsync($"/api/ideas/{id}/tags", new { tags = new[] { "C" } });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        var afterReplace = ReadIdea(await replaced.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "C" }, afterReplace.Tags);
    }

    [Fact]
    public async Task SetDone_marks_done_and_bumps_updated_at()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "待办完了");

        var response = await client.PostAsJsonAsync($"/api/ideas/{id}/complete", new { done = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var idea = ReadIdea(await response.Content.ReadAsStringAsync());
        Assert.True(idea.Done);
        Assert.True(idea.UpdatedAt > idea.CreatedAt);

        // 取消完成。
        var undone = await client.PostAsJsonAsync($"/api/ideas/{id}/complete", new { done = false });
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        Assert.False(ReadIdea(await undone.Content.ReadAsStringAsync()).Done);
    }

    [Fact]
    public async Task Delete_idea_soft_deletes_it()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "待删除");

        var response = await client.DeleteAsync($"/api/ideas/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.DoesNotContain(ideas, idea => idea.Id == id);

        // 软删：行还在，deleted_at 非空。
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea WHERE id = $id;", id));
        Assert.NotNull(QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM idea WHERE id = $id;", id));
    }

    /// <summary>
    /// 重复删除已在回收站里的想法是**空操作**：不改状态、不发事件。
    /// </summary>
    /// <remarks>
    /// 与项目删除同一个口径：那条想法**确实存在**，只是进了回收站 ——
    /// 404 在这里语义不准（跟 <c>not_in_trash</c> 的判断依据是同一个事实）。
    /// <para>
    /// 再写一次 <c>deleted_at</c> 会把保留期**重新开始**（用户点两下删除，
    /// 那条想法就多留一个月）；重复发 <c>idea.deleted</c> 会让其它设备
    /// 收到一条「它被删了」的假消息。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Delete_idea_twice_is_idempotent()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "待删除");

        // 把建想法时那条 idea.created 排除掉。
        publisher.Clear();

        var first = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var deletedAt = QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM idea WHERE id = $id;", id);
        Assert.NotNull(deletedAt);

        var again = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        // 状态没变：deleted_at 没被推到更晚（保留期没被重新开始）。
        Assert.Equal(deletedAt, QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM idea WHERE id = $id;", id));

        // 行还在，也还在回收站里。
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea WHERE id = $id;", id));

        // 事件只发一条：第一次删除发的那条就是全部，第二次一条都不发。
        var deleted = Assert.Single(publisher.Events);
        Assert.Equal(EventTypes.IdeaDeleted, deleted.Type);
    }

    [Fact]
    public async Task Restore_idea_brings_it_back_and_puts_it_at_end()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        await client.DeleteAsync($"/api/ideas/{first}");

        var response = await client.PostAsync($"/api/ideas/{first}/restore", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 恢复是一次写操作 → updated_at 刷新 → 回到列表最下面。
        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(new[] { second, first }, ideas.Select(idea => idea.Id).ToArray());

        Assert.Null(QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM idea WHERE id = $id;", first));
    }

    [Fact]
    public async Task Idea_content_with_quotes_newlines_is_stored_verbatim()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        const string content = "带\"引号\"\n换行\\反斜杠 <b>标签</b>";

        var id = await CreateIdeaAsync(client, content);

        var ideas = await ReadIdeasAsync(await client.GetAsync("/api/ideas"));
        var idea = Assert.Single(ideas);

        Assert.Equal(id, idea.Id);
        Assert.Equal(content, idea.Content);

        // 库里也是逐字相同。
        Assert.Equal(content, QuerySingleString(factory.DatabasePath,
            "SELECT content FROM idea WHERE id = $id;", id));
    }

    [Fact]
    public async Task Update_unknown_idea_returns_404_code_idea_not_found()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/ideas/0198f0a1b2c3d4e5f60718293a4b5c6d", new { content = "改谁" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("idea_not_found", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 正文不能被清空：<c>content = ""</c> 是 400（不是「改成空正文」）。
    /// </summary>
    [Fact]
    public async Task Update_idea_with_empty_content_is_rejected()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "原正文");

        var response = await client.PatchAsJsonAsync($"/api/ideas/{id}", new { content = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_content", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 入参 <c>content = null</c> 表示「不改正文」，正文原样保留。
    /// </summary>
    [Fact]
    public async Task Update_idea_with_null_content_keeps_existing_content()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "原正文");

        var response = await client.PatchAsJsonAsync($"/api/ideas/{id}", new { content = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var idea = ReadIdea(await response.Content.ReadAsStringAsync());
        Assert.Equal("原正文", idea.Content);
    }

    /// <summary>
    /// 合并时请求体给了正文就用请求体的；响应里没有 title 键。
    /// </summary>
    [Fact]
    public async Task Merge_request_body_overrides_content()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.PostAsJsonAsync("/api/ideas/merge", new
        {
            ids = new[] { first, second },
            content = "自己写的正文"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("自己写的正文", root.GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("title", out _));
    }

    private static bool IsLowerHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(HttpClient client, string content)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读想法列表（裸数组，排序恒为 updated_at ASC, id ASC）。</summary>
    private static async Task<List<IdeaSummary>> ReadIdeasAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        return root.EnumerateArray()
            .Select(element => new IdeaSummary(
                Id: element.GetProperty("id").GetString()!,
                ProjectId: element.GetProperty("projectId").GetString()!,
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

    /// <summary>直接往库里插一条想法（绕过 API），用来构造 updated_at 完全相同的场景。</summary>
    private static void InsertIdeaRow(string databasePath, string id, string stamp)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO idea(id, project_id, title, content, done, deleted_at, created_at, updated_at) " +
            "VALUES ($id, 'inbox', NULL, '直接插入', 0, NULL, $stamp, $stamp);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$stamp", stamp);
        command.ExecuteNonQuery();
    }

    private static string? QuerySingleString(string databasePath, string sql, string id)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static long QuerySingleInt(string databasePath, string sql, string id)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
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

    private sealed record IdeaSummary(string Id, string ProjectId, string Content,
                                     string[] Tags, bool Done,
                                     DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
