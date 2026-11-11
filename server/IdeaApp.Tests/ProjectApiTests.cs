using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 项目 API 集成测试。每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </summary>
public sealed class ProjectApiTests
{
    [Fact]
    public async Task Create_project_returns_201_and_persists()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/projects", new { name = "项目A" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("项目A", root.GetProperty("name").GetString());

        var id = root.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.True(IsLowerHex(c), $"id 不是 32 位小写 hex：{id}"));

        // 不只是回显：行确实落库了。
        Assert.Equal("项目A",
            QuerySingleString(factory.DatabasePath, "SELECT name FROM project WHERE id = $id;", id));
    }

    [Fact]
    public async Task List_projects_includes_inbox_and_active_projects()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");

        var response = await client.GetAsync("/api/projects");
        var projects = await ReadProjectsAsync(response);

        Assert.Equal(3, projects.Count);

        // 默认排序是 recent（最近活动在前）：收件箱没有想法，回落到它自己的 updated_at
        // （建库时写的，最早），所以排在最后 —— 收件箱不置顶，按同一规则参与排序。
        Assert.Equal("inbox", projects[^1].Id);
        Assert.Equal("收件箱", projects[^1].Name);

        Assert.Contains(projects, p => p.Id == first && p.Name == "项目A");
        Assert.Contains(projects, p => p.Id == second && p.Name == "项目B");
    }

    [Fact]
    public async Task Rename_project_updates_name()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateProjectAsync(client, "旧名字");

        var response = await client.PatchAsJsonAsync($"/api/projects/{id}", new { name = "改名" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var projects = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        var renamed = Assert.Single(projects, p => p.Id == id);
        Assert.Equal("改名", renamed.Name);
    }

    [Fact]
    public async Task Delete_project_soft_deletes_and_hides_from_list()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateProjectAsync(client, "待删除");

        var response = await client.DeleteAsync($"/api/projects/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // 列表里不再出现。
        var projects = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        Assert.DoesNotContain(projects, p => p.Id == id);

        // 软删：行还在，deleted_at 非空。
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM project WHERE id = $id;", id));
        Assert.NotNull(QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM project WHERE id = $id;", id));
    }

    [Fact]
    public async Task Delete_inbox_returns_400_with_code_inbox_not_deletable()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/projects/inbox");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("inbox_not_deletable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reorder_persists_sort_order()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");

        var response = await client.PostAsJsonAsync("/api/projects/reorder",
            new { ids = new[] { second, first, "inbox" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var expected = new[] { second, first, "inbox" };
        var reordered = await ReadProjectsAsync(response);
        Assert.Equal(expected, reordered.Select(p => p.Id).ToArray());

        // 顺序落库了：重新拉一次还是这个顺序。
        var reloaded = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        Assert.Equal(expected, reloaded.Select(p => p.Id).ToArray());
    }

    /// <summary>
    /// 重排时没被列出的项目要接在列出来的项目后面，且保持它们原来的相对顺序。
    /// </summary>
    [Fact]
    public async Task Reorder_keeps_unlisted_projects_after_the_listed_ones()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");
        var third = await CreateProjectAsync(client, "项目C");

        // 手动排序：列表顺序按 sort_order 排，重排的效果才看得见。
        await SwitchToManualAsync(client);

        var response = await client.PostAsJsonAsync("/api/projects/reorder",
            new { ids = new[] { third, first } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 列出来的按给定顺序在前（第三、第一）；没列出来的按原来的相对顺序接在后面
        // （切到手动排序时的显示顺序是「最近活动在前」：第三、第二、第一、收件箱，
        //  所以没列出的第二、收件箱就按这个相对顺序接在后面）。
        var reordered = await ReadProjectsAsync(response);
        Assert.Equal(new[] { third, first, second, "inbox" }, reordered.Select(p => p.Id).ToArray());
    }

    /// <summary>
    /// 重排请求没给 ids（缺字段、显式 null、空数组）时必须是 400，不能是 500。
    /// 反序列化出来的 <c>Ids</c> 是 null 时，仓储里的 foreach 会抛 NRE。
    /// </summary>
    [Fact]
    public async Task Reorder_without_ids_returns_400_with_code_invalid_reorder_ids()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        foreach (var payload in new[] { "{}", """{"ids":null}""", """{"ids":[]}""" })
        {
            var response = await PostJsonAsync(client, "/api/projects/reorder", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_reorder_ids", problem.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// 重排请求里的未知 id 被忽略，而且不占用 sort_order 索引 ——
    /// 已知 id 拿到的索引不能因为前面那个未知 id 而往后挪。
    /// </summary>
    [Fact]
    public async Task Reorder_ignores_unknown_ids_without_consuming_an_index()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");
        const string unknown = "0198f0a1b2c3d4e5f60718293a4b5c6d";

        // 手动排序：列表顺序按 sort_order 排。
        await SwitchToManualAsync(client);

        var response = await client.PostAsJsonAsync("/api/projects/reorder",
            new { ids = new[] { unknown, second } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reordered = await ReadProjectsAsync(response);

        Assert.DoesNotContain(reordered, p => p.Id == unknown);

        // 未知 id 没占索引：第二个项目拿到 sort_order 0。
        Assert.Equal(second, reordered[0].Id);
        Assert.Equal(0, reordered[0].SortOrder);

        // 其余项目保持原来的相对顺序接在后面。
        Assert.Equal(new[] { second, first, "inbox" }, reordered.Select(p => p.Id).ToArray());
    }

    /// <summary>
    /// 项目名去掉首尾空白后为空时必须 400（空名字的项目在界面上没法用）。
    /// </summary>
    [Fact]
    public async Task Create_project_with_blank_name_returns_400_with_code_invalid_project_name()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        foreach (var name in new[] { "", "   ", (string?)null })
        {
            var response = await client.PostAsJsonAsync("/api/projects", new { name });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_project_name", problem.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// 没有想法的项目，最近活动时间回落到项目自身的 updated_at，不返回 null ——
    /// DTO 展示与 Task 13 的 recent 排序要用同一个值。
    /// </summary>
    [Fact]
    public async Task List_projects_returns_last_activity_at_for_project_without_ideas()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateProjectAsync(client, "空项目");

        var projects = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        var project = Assert.Single(projects, p => p.Id == id);

        Assert.NotNull(project.LastActivityAt);
        Assert.Equal(project.UpdatedAt, project.LastActivityAt);
    }

    /// <summary>软删之后 restore，项目回到活跃列表，deleted_at 被置空。</summary>
    [Fact]
    public async Task Restore_project_brings_it_back_to_the_active_list()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateProjectAsync(client, "待恢复");

        var deleted = await client.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var hidden = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        Assert.DoesNotContain(hidden, p => p.Id == id);

        var response = await client.PostAsync($"/api/projects/{id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var restored = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        Assert.Contains(restored, p => p.Id == id);

        // deleted_at 被清空了。
        Assert.Null(QuerySingleString(factory.DatabasePath,
            "SELECT deleted_at FROM project WHERE id = $id;", id));
    }

    [Fact]
    public async Task Rename_unknown_project_returns_404_with_code_project_not_found()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/projects/0198f0a1b2c3d4e5f60718293a4b5c6d", new { name = "改名" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("project_not_found", problem.GetProperty("code").GetString());
    }

    private static bool IsLowerHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

    /// <summary>切到手动排序（列表顺序按 sort_order 排）。</summary>
    private static async Task SwitchToManualAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/settings/project.sort_mode",
            new { value = "manual" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读项目列表。返回的是裸数组（RFC 9457 之外的成功响应不带信封）。</summary>
    private static async Task<List<ProjectSummary>> ReadProjectsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        return root.EnumerateArray()
            .Select(element => new ProjectSummary(
                Id: element.GetProperty("id").GetString()!,
                Name: element.GetProperty("name").GetString()!,
                SortOrder: element.GetProperty("sortOrder").GetInt32(),
                LastActivityAt: element.GetProperty("lastActivityAt").GetString(),
                UpdatedAt: element.GetProperty("updatedAt").GetString()))
            .ToList();
    }

    /// <summary>按原始 JSON 文本发 POST（用来构造缺字段 / 显式 null 的请求体）。</summary>
    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json) =>
        await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>读 ProblemDetails 响应体。</summary>
    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // JsonElement 在文档释放后失效，复制一份出去。
        return document.RootElement.Clone();
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

    private sealed record ProjectSummary(string Id, string Name, int SortOrder,
                                          string? LastActivityAt, string? UpdatedAt);
}
