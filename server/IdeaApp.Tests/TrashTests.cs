using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IdeaApp.Tests;

/// <summary>
/// 回收站集成测试：回收站视图、恢复语义与到期清理。
///
/// 回收站装两类东西：被单独删除的想法（顶层条目）与被删除的项目（容器条目，
/// 下挂它所属的所有想法）。同一个想法不会在两处同时出现。
///
/// 每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </summary>
public sealed class TrashTests
{
    [Fact]
    public async Task Trash_lists_deleted_ideas_and_deleted_projects()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 顶层想法：它所属的项目还活着。
        var inboxIdea = await CreateIdeaAsync(client, "收件箱里的想法");

        // 容器项目：项目自己被删了，它下面的想法跟着进回收站。
        var projectId = await CreateProjectAsync(client, "要删的项目");
        var projectIdea = await CreateIdeaAsync(client, "项目里的想法", projectId);

        await client.DeleteAsync($"/api/ideas/{inboxIdea}");
        await client.DeleteAsync($"/api/projects/{projectId}");

        var entries = await ReadTrashAsync(client);

        Assert.Equal(2, entries.Count);

        // 容器排在顶层之前。
        Assert.Equal("project", entries[0].Kind);
        Assert.Equal(projectId, entries[0].ProjectId);
        Assert.Equal(new[] { projectIdea }, entries[0].IdeaIds);

        var topLevel = Assert.Single(entries, entry => entry.Kind == "idea");
        Assert.Equal(inboxIdea, topLevel.IdeaId);
        Assert.Null(topLevel.ProjectId);
    }

    [Fact]
    public async Task Trash_project_container_lists_its_ideas()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var kept = await CreateIdeaAsync(client, "留在项目里的想法", projectId);
        var deletedEarlier = await CreateIdeaAsync(client, "先前单独删掉的想法", projectId);

        await client.DeleteAsync($"/api/ideas/{deletedEarlier}");
        await client.DeleteAsync($"/api/projects/{projectId}");

        var entries = await ReadTrashAsync(client);

        // 容器项的 Ideas 数组含该项目下**全部** idea（含先前单独删掉的那条）。
        var container = Assert.Single(entries);
        Assert.Equal("project", container.Kind);
        Assert.Equal(projectId, container.ProjectId);

        // 容器里的想法按 updated_at 升序：先前单独删掉的那条（更早）排前面，
        // 随项目一起删的那条 updated_at 被刷新过，排后面。
        Assert.Equal(new[] { deletedEarlier, kept }, container.IdeaIds);
    }

    [Fact]
    public async Task Trash_does_not_list_same_idea_twice()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var ideaId = await CreateIdeaAsync(client, "想法", projectId);

        await client.DeleteAsync($"/api/ideas/{ideaId}");
        await client.DeleteAsync($"/api/projects/{projectId}");

        var entries = await ReadTrashAsync(client);

        // 顶层 idea 条目里不含它，只在容器里出现一次。
        Assert.DoesNotContain(entries, entry => entry.Kind == "idea");

        var container = Assert.Single(entries);
        Assert.Equal("project", container.Kind);
        Assert.Equal(new[] { ideaId }, container.IdeaIds);
    }

    [Fact]
    public async Task Restore_project_does_not_revive_previously_deleted_idea()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var ideaId = await CreateIdeaAsync(client, "想法", projectId);

        await client.DeleteAsync($"/api/ideas/{ideaId}");
        await client.DeleteAsync($"/api/projects/{projectId}");

        var response = await client.PostAsync($"/api/projects/{projectId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 项目回来了，但它下面被单独删掉的想法没有复活：仍在回收站顶层。
        var entries = await ReadTrashAsync(client);

        var topLevel = Assert.Single(entries);
        Assert.Equal("idea", topLevel.Kind);
        Assert.Equal(ideaId, topLevel.IdeaId);
    }

    [Fact]
    public async Task Restore_project_revives_ideas_deleted_with_the_project()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var ideaId = await CreateIdeaAsync(client, "想法", projectId);

        await client.DeleteAsync($"/api/projects/{projectId}");

        // 删项目时它下面的想法跟着进了回收站。
        var container = Assert.Single(await ReadTrashAsync(client));
        Assert.Equal("project", container.Kind);
        Assert.Equal(new[] { ideaId }, container.IdeaIds);

        var response = await client.PostAsync($"/api/projects/{projectId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 容器消失，想法回到正常列表。
        Assert.Empty(await ReadTrashAsync(client));
        Assert.Equal(new[] { ideaId }, await ReadIdeaIdsAsync(client));
    }

    /// <summary>
    /// 删项目必须连带软删它下面的想法：想法的 <c>deleted_at</c> 与项目的
    /// <c>deleted_at</c> 是**同一时刻**（恢复项目时靠这个判定「是不是随项目一起删的」）。
    /// </summary>
    [Fact]
    public async Task Delete_project_soft_deletes_its_ideas_at_the_same_moment()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var ideaId = await CreateIdeaAsync(client, "想法", projectId);

        var response = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // 想法跟着进回收站：不在正常列表里了。
        Assert.Empty(await ReadIdeaIdsAsync(client));

        var projectDeletedAt = QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM project WHERE id = $id;", projectId);
        var ideaDeletedAt = QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM idea WHERE id = $id;", ideaId);

        Assert.NotNull(projectDeletedAt);
        Assert.Equal(projectDeletedAt, ideaDeletedAt);
    }

    /// <summary>
    /// 删项目时它下面的每条想法都要补发一条 <c>idea.deleted</c>：
    /// 否则订阅端只知道项目没了，前端列表里还留着那些想法。
    /// </summary>
    [Fact]
    public async Task Delete_project_publishes_idea_deleted_for_each_of_its_ideas()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var first = await CreateIdeaAsync(client, "第一条", projectId);
        var second = await CreateIdeaAsync(client, "第二条", projectId);

        publisher.Clear();

        await client.DeleteAsync($"/api/projects/{projectId}");

        // 项目一条 + 它下面的想法各一条。
        Assert.Equal(3, publisher.Events.Count);

        Assert.Equal(EventTypes.ProjectDeleted, publisher.Events[0].Type);
        Assert.Equal(projectId, Assert.IsType<ProjectSnapshot>(publisher.Events[0].Project).Id);

        Assert.Equal(new[] { EventTypes.IdeaDeleted, EventTypes.IdeaDeleted },
            publisher.Events.Skip(1).Select(evt => evt.Type).ToArray());

        Assert.Equal(new[] { first, second },
            publisher.Events.Skip(1)
                .Select(evt => Assert.IsType<IdeaSnapshot>(evt.Idea).Id)
                .ToArray());
    }

    /// <summary>
    /// 恢复项目发 <c>project.updated</c>（<c>changed = ["deletedAt"]</c>），
    /// 连带恢复的每条想法各发一条 <c>idea.restored</c>。
    /// </summary>
    [Fact]
    public async Task Restore_project_publishes_project_updated_and_idea_restored()
    {
        var publisher = new RecordingEventPublisher();
        using var factory = new TestAppFactory(publisher: publisher);
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var ideaId = await CreateIdeaAsync(client, "想法", projectId);

        await client.DeleteAsync($"/api/projects/{projectId}");

        publisher.Clear();

        var response = await client.PostAsync($"/api/projects/{projectId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(2, publisher.Events.Count);

        Assert.Equal(EventTypes.ProjectUpdated, publisher.Events[0].Type);
        Assert.Equal(new[] { "deletedAt" }, publisher.Events[0].Changed);
        Assert.Equal(projectId, Assert.IsType<ProjectSnapshot>(publisher.Events[0].Project).Id);

        Assert.Equal(EventTypes.IdeaRestored, publisher.Events[1].Type);
        Assert.Equal(ideaId, Assert.IsType<IdeaSnapshot>(publisher.Events[1].Idea).Id);
    }

    /// <summary>
    /// 所属项目还在回收站时，恢复一条想法是允许的：想法回到所属项目的列表里，
    /// 项目本身仍留在回收站，直到项目被恢复或到期清理。
    /// </summary>
    [Fact]
    public async Task Restore_idea_is_allowed_while_its_project_is_in_the_trash()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目");
        var ideaId = await CreateIdeaAsync(client, "想法", projectId);

        await client.DeleteAsync($"/api/projects/{projectId}");

        var response = await client.PostAsync($"/api/ideas/{ideaId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 想法活了，项目还在回收站里。
        Assert.Null(QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM idea WHERE id = $id;", ideaId));
        Assert.NotNull(QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM project WHERE id = $id;", projectId));
    }

    /// <summary>
    /// 过了保留期（默认 30 天）的条目被自动清理掉：想法行被删，回收站里也不再出现。
    /// </summary>
    [Fact]
    public async Task Cleanup_deletes_entries_older_than_retention_days()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var ideaId = await CreateIdeaAsync(client, "过期的想法");
        await client.DeleteAsync($"/api/ideas/{ideaId}");

        // 直接改库，把删除时刻挪到 39 天前（保留期 30 天）。
        SetDeletedAt(factory.DatabasePath, "idea", ideaId, DateTimeOffset.UtcNow.AddDays(-39));

        CreateWorker(factory).Cleanup();

        Assert.Empty(await ReadTrashAsync(client));

        // 行也被删了（不只是从视图里消失）。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea WHERE id = $id;", ideaId));
    }

    /// <summary>保留期内的条目不被清理。</summary>
    [Fact]
    public async Task Cleanup_keeps_entries_within_retention_days()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var ideaId = await CreateIdeaAsync(client, "还在保留期内的想法");
        await client.DeleteAsync($"/api/ideas/{ideaId}");

        // 29 天前：还在保留期内。
        SetDeletedAt(factory.DatabasePath, "idea", ideaId, DateTimeOffset.UtcNow.AddDays(-29));

        CreateWorker(factory).Cleanup();

        var entries = await ReadTrashAsync(client);

        var topLevel = Assert.Single(entries);
        Assert.Equal("idea", topLevel.Kind);
        Assert.Equal(ideaId, topLevel.IdeaId);
    }

    /// <summary>
    /// 过期的容器项目被清理时，它下面的想法与它自己的 webhook 一起被删
    /// （webhook 靠外键的 ON DELETE CASCADE 带走）。
    /// </summary>
    [Fact]
    public async Task Cleanup_deletes_project_together_with_its_ideas_and_webhooks()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "过期的项目");
        var ideaId = await CreateIdeaAsync(client, "过期项目的想法", projectId);
        var webhookId = InsertWebhook(factory.DatabasePath, projectId);

        await client.DeleteAsync($"/api/projects/{projectId}");

        // 容器项目过期了（39 天前）；它下面的想法是随项目一起进回收站的。
        SetDeletedAt(factory.DatabasePath, "project", projectId, DateTimeOffset.UtcNow.AddDays(-39));

        CreateWorker(factory).Cleanup();

        Assert.Empty(await ReadTrashAsync(client));

        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM project WHERE id = $id;", projectId));
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM idea WHERE id = $id;", ideaId));
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM webhook WHERE id = $id;", webhookId));
    }

    /// <summary>造一个清理任务（指向本次测试的临时库）。</summary>
    private static TrashCleanupWorker CreateWorker(TestAppFactory factory)
    {
        return new TrashCleanupWorker(
            new SqliteDatabase(factory.DatabasePath),
            Options.Create(new AppOptions()),
            NullLogger<TrashCleanupWorker>.Instance);
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(
        HttpClient client, string content, string? projectId = null, string? title = null)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { projectId, content, title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    /// <summary>读响应体里的 id。</summary>
    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读正常列表（<c>GET /api/ideas</c>）里的想法 id。</summary>
    private static async Task<IReadOnlyList<string>> ReadIdeaIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/ideas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();
    }

    /// <summary>
    /// 读回收站条目（<c>GET /api/trash</c>）。成功响应是裸数组，不套信封。
    /// </summary>
    private static async Task<List<TrashEntry>> ReadTrashAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        var entries = new List<TrashEntry>();
        foreach (var element in root.EnumerateArray())
        {
            var project = element.GetProperty("project");
            var idea = element.GetProperty("idea");

            entries.Add(new TrashEntry(
                Kind: element.GetProperty("kind").GetString()!,
                ProjectId: project.ValueKind == JsonValueKind.Null
                    ? null
                    : project.GetProperty("id").GetString(),
                IdeaIds: element.GetProperty("ideas").EnumerateArray()
                    .Select(child => child.GetProperty("id").GetString()!)
                    .ToArray(),
                IdeaId: idea.ValueKind == JsonValueKind.Null
                    ? null
                    : idea.GetProperty("id").GetString()));
        }

        return entries;
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
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>直接改库，把某条记录的删除时刻挪到指定时刻（用来造过期条目）。</summary>
    private static void SetDeletedAt(string databasePath, string table, string id, DateTimeOffset at)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET deleted_at = $at WHERE id = $id;";
        command.Parameters.AddWithValue("$at", SqliteDatabase.FormatTimestamp(at));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>插一条挂在项目下的 webhook（webhook API 还没有，这里直接写库）。</summary>
    private static string InsertWebhook(string databasePath, string projectId)
    {
        var id = "0198f0a1b2c3d4e5f60718293a4b5c7a";
        var now = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);

        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO webhook(id, name, url, method, headers_json, body_template, events_json, " +
            "scope, project_id, secret, enabled, created_at, updated_at) " +
            "VALUES($id, '钩子', 'https://example.invalid/hook', 'POST', '{}', '{}', '[]', " +
            "'project', $projectId, NULL, 1, $now, $now);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        return id;
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

    /// <summary>回收站条目（只取测试要断言的那几个字段）。</summary>
    private sealed record TrashEntry(string Kind, string? ProjectId, IReadOnlyList<string> IdeaIds, string? IdeaId);
}
