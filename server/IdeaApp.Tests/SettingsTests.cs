using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 设置 API 与项目排序方式的集成测试。每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </summary>
public sealed class SettingsTests
{
    /// <summary>
    /// 刚启动时没有设置项，但响应里一定有 <c>project.sort_mode</c>（读不到就回落默认值）。
    /// </summary>
    [Fact]
    public async Task Get_settings_returns_empty_or_defaults()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await ReadSettingsAsync(response);

        Assert.Equal("recent", settings["project.sort_mode"]);

        // 其它未知键不出现在响应里。
        Assert.Single(settings);

        // 默认值不播种到库里：这一行本来就不存在。
        Assert.Null(QuerySingleString(factory.DatabasePath,
            "SELECT value FROM setting WHERE key = $key;", "project.sort_mode"));
    }

    [Fact]
    public async Task Put_settings_changes_project_sort_mode()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings/project.sort_mode", new { value = "manual" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await ReadSettingsAsync(response);
        Assert.Equal("manual", settings["project.sort_mode"]);

        // 再读一次还是 manual：真的落库了。
        var reloaded = await ReadSettingsAsync(await client.GetAsync("/api/settings"));
        Assert.Equal("manual", reloaded["project.sort_mode"]);

        Assert.Equal("manual", QuerySingleString(factory.DatabasePath,
            "SELECT value FROM setting WHERE key = $key;", "project.sort_mode"));
    }

    [Fact]
    public async Task Put_settings_rejects_unknown_key_with_code_setting_key_not_writable()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings/Trash.RetentionDays", new { value = "30" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("setting_key_not_writable", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_settings_rejects_illegal_sort_mode_value()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/settings/project.sort_mode", new { value = "whatever" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_sort_mode", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// recent 模式（默认）：项目下**未删**想法最近更新的那个项目排在前面。
    /// </summary>
    [Fact]
    public async Task Recent_sort_mode_puts_most_recently_active_project_first()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");

        var firstIdea = await CreateIdeaAsync(client, first);
        var secondIdea = await CreateIdeaAsync(client, second);

        // 把两条想法的更新时间钉死：两次请求可能落在同一时刻，那样就分不出先后。
        SetIdeaUpdatedAt(factory.DatabasePath, firstIdea, "2026-01-01T00:00:00.0000000Z");
        SetIdeaUpdatedAt(factory.DatabasePath, secondIdea, "2026-02-01T00:00:00.0000000Z");

        var projects = await ReadProjectsAsync(await client.GetAsync("/api/projects"));

        Assert.True(IndexOf(projects, second) < IndexOf(projects, first),
            "最近活动的项目应该排在前面");
    }

    /// <summary>
    /// manual 模式：列表顺序按 <c>sort_order</c>，与 <c>recent</c> 的活动时间无关。
    /// </summary>
    [Fact]
    public async Task Manual_sort_mode_uses_sort_order()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");

        var switched = await client.PutAsJsonAsync("/api/settings/project.sort_mode", new { value = "manual" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        var reordered = await client.PostAsJsonAsync("/api/projects/reorder",
            new { ids = new[] { second, first, "inbox" } });
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);

        var projects = await ReadProjectsAsync(await client.GetAsync("/api/projects"));

        Assert.Equal(new[] { second, first, "inbox" }, projects.Select(p => p.Id).ToArray());
    }

    /// <summary>
    /// 从 recent 切到 manual 时用**当前显示顺序**初始化 <c>sort_order</c>：
    /// 否则用户切过去会看到顺序乱跳。初始化要落库，不是只在内存里排一次。
    /// </summary>
    [Fact]
    public async Task Switching_to_manual_keeps_the_current_display_order()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");

        var firstIdea = await CreateIdeaAsync(client, first);
        var secondIdea = await CreateIdeaAsync(client, second);

        SetIdeaUpdatedAt(factory.DatabasePath, firstIdea, "2026-01-01T00:00:00.0000000Z");
        SetIdeaUpdatedAt(factory.DatabasePath, secondIdea, "2026-02-01T00:00:00.0000000Z");

        var before = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        var beforeOrder = before.Select(p => p.Id).ToArray();

        var response = await client.PutAsJsonAsync("/api/settings/project.sort_mode", new { value = "manual" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await ReadProjectsAsync(await client.GetAsync("/api/projects"));

        Assert.Equal(beforeOrder, after.Select(p => p.Id).ToArray());

        // 初始化写进了库里：sort_order 反映了当前的显示顺序。
        for (var i = 1; i < after.Count; i++)
        {
            Assert.True(QuerySortOrder(factory.DatabasePath, after[i - 1].Id)
                        < QuerySortOrder(factory.DatabasePath, after[i].Id),
                $"{after[i - 1].Id} 的 sort_order 应该小于 {after[i].Id} 的");
        }
    }

    /// <summary>
    /// 切回 recent：列表立刻恢复按最近活动排序（设置是每次请求现读的，不是启动时读一次）。
    /// </summary>
    [Fact]
    public async Task Switching_back_to_recent_restores_the_activity_order()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateProjectAsync(client, "项目A");
        var second = await CreateProjectAsync(client, "项目B");

        // 项目A下面有想法：它的最近活动是最新的。
        await CreateIdeaAsync(client, first);

        var switched = await client.PutAsJsonAsync("/api/settings/project.sort_mode", new { value = "manual" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        // 手动排序：A 排到最后。
        var reordered = await client.PostAsJsonAsync("/api/projects/reorder",
            new { ids = new[] { second, first, "inbox" } });
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        Assert.Equal(new[] { second, first, "inbox" },
            (await ReadProjectsAsync(reordered)).Select(p => p.Id).ToArray());

        // 切回 recent：A 因为刚有想法活动，排到最前。
        var back = await client.PutAsJsonAsync("/api/settings/project.sort_mode", new { value = "recent" });
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);

        var projects = await ReadProjectsAsync(await client.GetAsync("/api/projects"));
        Assert.Equal(new[] { first, second, "inbox" }, projects.Select(p => p.Id).ToArray());
    }

    /// <summary>一个项目在列表里的位置（找不到返回 -1）。</summary>
    private static int IndexOf(List<ProjectSummary> projects, string id) =>
        projects.FindIndex(p => p.Id == id);

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>在项目下建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(HttpClient client, string projectId)
    {
        var response = await client.PostAsJsonAsync("/api/ideas",
            new { projectId, content = "想法" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读设置响应（扁平的键值字典）。</summary>
    private static async Task<Dictionary<string, string>> ReadSettingsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        return root.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);
    }

    /// <summary>读项目列表（裸数组）。</summary>
    private static async Task<List<ProjectSummary>> ReadProjectsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        return root.EnumerateArray()
            .Select(element => new ProjectSummary(
                Id: element.GetProperty("id").GetString()!,
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

    /// <summary>直接改想法的更新时间（时间戳格式与库里的一致）。</summary>
    private static void SetIdeaUpdatedAt(string databasePath, string ideaId, string updatedAt)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE idea SET updated_at = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$updatedAt", updatedAt);
        command.Parameters.AddWithValue("$id", ideaId);
        command.ExecuteNonQuery();
    }

    private static int QuerySortOrder(string databasePath, string projectId)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sort_order FROM project WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? QuerySingleString(string databasePath, string sql, string key)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
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

    private sealed record ProjectSummary(string Id, int SortOrder);
}
