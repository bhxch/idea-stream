using System.Net;
using System.Net.Http.Json;
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

        // 内置收件箱排在最前（sort_order 最小）。
        Assert.Equal("inbox", projects[0].Id);
        Assert.Equal("收件箱", projects[0].Name);

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

        var response = await client.PostAsJsonAsync("/api/projects/reorder",
            new { ids = new[] { third, first } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 列出来的按给定顺序在前（第三、第一）；没列出来的按原来的相对顺序接在后面
        // （收件箱原本排在最前，项目B 原本在收件箱之后）。
        var reordered = await ReadProjectsAsync(response);
        Assert.Equal(new[] { third, first, "inbox", second }, reordered.Select(p => p.Id).ToArray());
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
                SortOrder: element.GetProperty("sortOrder").GetInt32()))
            .ToList();
    }

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

    private sealed record ProjectSummary(string Id, string Name, int SortOrder);
}
