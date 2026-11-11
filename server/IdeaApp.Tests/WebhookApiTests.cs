using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IdeaApp.Tests;

/// <summary>
/// Webhook 配置 API 集成测试。每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </summary>
/// <remarks>
/// 请求体一律用**匿名对象 / 原始 JSON**发出去，不引用后端的请求体 record：
/// 这样测试只依赖 HTTP 契约（字段名、语义），不依赖实现细节，
/// 也让「端点还没实现」这件事表现为真实的 404 而不是编译错误。
/// </remarks>
public sealed class WebhookApiTests
{
    /// <summary>合法的 Body 模板：本身是一个 JSON 文档，占位符在字符串值里面。</summary>
    private const string ValidBodyTemplate = """{"text":"[{{project}}] {{event}} {{idea.content}}"}""";

    [Fact]
    public async Task Create_global_webhook_is_disabled_by_default()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "全局钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string> { ["X-Token"] = "value" },
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null,
            secret = "s3cret"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("global", root.GetProperty("scope").GetString());
        Assert.Null(root.GetProperty("projectId").GetString());

        // 库里显式写的是 0（不是靠列默认值 1）：全局作用域默认禁用。
        var id = root.GetProperty("id").GetString()!;
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath, "SELECT enabled FROM webhook WHERE id = $id;", id));

        // id 是单调 UUIDv7：32 位无连字符小写 hex。
        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.True(IsLowerHex(c), $"id 不是 32 位小写 hex：{id}"));
    }

    [Fact]
    public async Task Create_project_webhook_is_enabled_by_default()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "项目钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created", "idea.updated" },
            scope = "project",
            projectId,
            secret = (string?)null
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("project", root.GetProperty("scope").GetString());
        Assert.Equal(projectId, root.GetProperty("projectId").GetString());

        var id = root.GetProperty("id").GetString()!;
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath, "SELECT enabled FROM webhook WHERE id = $id;", id));
    }

    [Fact]
    public async Task Create_webhook_never_returns_secret_plaintext()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "带密钥的钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null,
            secret = "s3cret-value"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.True(root.GetProperty("hasSecret").GetBoolean());

        // 响应里既不能有 secret 明文，也不能有 secret 字段本身。
        Assert.DoesNotContain("s3cret-value", body, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("secret", out _));

        // 但库里确实存了（投递时要拿它算签名）。
        var id = root.GetProperty("id").GetString()!;
        Assert.Equal("s3cret-value",
            QuerySingleString(factory.DatabasePath, "SELECT secret FROM webhook WHERE id = $id;", id));
    }

    [Fact]
    public async Task List_webhooks_filters_by_project_id()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectA = await CreateProjectAsync(client, "项目A");
        var projectB = await CreateProjectAsync(client, "项目B");

        var globalId = await CreateWebhookAsync(client, scope: "global", projectId: null);
        var projectAId = await CreateWebhookAsync(client, scope: "project", projectId: projectA);
        var projectBId = await CreateWebhookAsync(client, scope: "project", projectId: projectB);

        // 不带 projectId：只列全局作用域的 webhook。
        var globals = await ReadWebhooksAsync(await client.GetAsync("/api/webhooks"));
        Assert.Equal(new[] { globalId }, globals.Select(w => w.Id).ToArray());

        // 带 projectId：只列绑定到该项目的 webhook。
        var forA = await ReadWebhooksAsync(await client.GetAsync($"/api/webhooks?projectId={projectA}"));
        Assert.Equal(new[] { projectAId }, forA.Select(w => w.Id).ToArray());

        var forB = await ReadWebhooksAsync(await client.GetAsync($"/api/webhooks?projectId={projectB}"));
        Assert.Equal(new[] { projectBId }, forB.Select(w => w.Id).ToArray());
    }

    [Fact]
    public async Task Get_telegram_template_returns_preset_with_message_thread_id()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/webhooks/templates/telegram");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("https://api.telegram.org/bot<BOT_TOKEN>/sendMessage",
            root.GetProperty("url").GetString());

        var bodyTemplate = root.GetProperty("bodyTemplate").GetString()!;

        Assert.Contains("message_thread_id", bodyTemplate, StringComparison.Ordinal);
        Assert.Contains("{{idea.content}}", bodyTemplate, StringComparison.Ordinal);

        // 预设本身必须是一个合法的 JSON 文档（占位符在字符串值里面）——
        // 页面拿它直接当 Body 模板用，非法 JSON 的话渲染器会直接抛。
        using var template = JsonDocument.Parse(bodyTemplate);
        Assert.Equal(JsonValueKind.Object, template.RootElement.ValueKind);
        Assert.Equal("{{idea.content}}",
            template.RootElement.GetProperty("text").GetString()!.Split('\n')[2]);
    }

    [Fact]
    public async Task Create_webhook_with_unknown_event_type_returns_400_code_invalid_event_type()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created", "idea.exploded" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_event_type", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Update_webhook_can_change_enabled_and_events()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");
        var id = await CreateWebhookAsync(client, scope: "project", projectId: projectId);

        var response = await client.PatchAsJsonAsync($"/api/webhooks/{id}", new
        {
            enabled = false,
            events = new[] { "idea.updated" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { "idea.updated" },
            root.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ToArray());

        // 改动落库了。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath, "SELECT enabled FROM webhook WHERE id = $id;", id));
        Assert.Equal("""["idea.updated"]""",
            QuerySingleString(factory.DatabasePath, "SELECT events_json FROM webhook WHERE id = $id;", id));
    }

    [Fact]
    public async Task Create_webhook_with_invalid_body_template_returns_400_code_invalid_body_template()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = "这不是 JSON",
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_body_template", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 作用域与 projectId 必须一致：项目级必须给一个存在的项目，全局级必须不给。
    /// </summary>
    [Fact]
    public async Task Create_webhook_with_inconsistent_scope_and_project_returns_400_code_invalid_webhook_project()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        var payloads = new[]
        {
            // 项目级但没给 projectId
            """{"name":"钩子","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"project"}""",
            // 项目级但项目不存在
            """{"name":"钩子","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"project","projectId":"0198f0a1b2c3d4e5f60718293a4b5c6d"}""",
            // 全局级却给了 projectId
            $$"""{"name":"钩子","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"global","projectId":"{{projectId}}"}"""
        };

        foreach (var payload in payloads)
        {
            var response = await PostJsonAsync(client, "/api/webhooks", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_webhook_project", problem.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// PATCH 里的 secret 三态：null = 不改、空串 = 清空、非空 = 替换。
    /// 响应里同样不能出现 secret 明文。
    /// </summary>
    [Fact]
    public async Task Update_webhook_secret_null_keeps_it_and_empty_string_clears_it()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null,
            secret = "first-secret"
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;

        // 传 null：密钥不变。
        var keep = await client.PatchAsJsonAsync($"/api/webhooks/{id}", new { secret = (string?)null });

        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
        Assert.Equal("first-secret",
            QuerySingleString(factory.DatabasePath, "SELECT secret FROM webhook WHERE id = $id;", id));

        // 传空串：清空密钥。
        var clear = await client.PatchAsJsonAsync($"/api/webhooks/{id}", new { secret = "" });

        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null(QuerySingleString(factory.DatabasePath, "SELECT secret FROM webhook WHERE id = $id;", id));

        var body = await clear.Content.ReadAsStringAsync();
        using var cleared = JsonDocument.Parse(body);
        Assert.False(cleared.RootElement.GetProperty("hasSecret").GetBoolean());
        Assert.DoesNotContain("first-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_unknown_webhook_returns_404_with_code_webhook_not_found()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/webhooks/0198f0a1b2c3d4e5f60718293a4b5c6d");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("webhook_not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_webhook_removes_it()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateWebhookAsync(client, scope: "global", projectId: null);

        var response = await client.DeleteAsync($"/api/webhooks/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // 行没了。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM webhook WHERE id = $id;", id));

        // 再取就是 404。
        var get = await client.GetAsync($"/api/webhooks/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    /// <summary>
    /// 投递命中规则（投递那轮要用的）：全局且启用的 webhook 全部命中，
    /// 外加绑定到该事件所属项目且启用的 webhook；别的项目的 webhook 不命中。
    /// </summary>
    [Fact]
    public async Task List_enabled_webhooks_returns_global_and_that_project_only()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectA = await CreateProjectAsync(client, "项目A");
        var projectB = await CreateProjectAsync(client, "项目B");

        var globalId = await CreateWebhookAsync(client, scope: "global", projectId: null);
        var projectAId = await CreateWebhookAsync(client, scope: "project", projectId: projectA);
        var projectBId = await CreateWebhookAsync(client, scope: "project", projectId: projectB);

        var repository = factory.Services.GetRequiredService<WebhookRepository>();

        // 全局的那个默认禁用 → 不在命中名单里。
        Assert.Equal(new[] { projectAId },
            repository.ListEnabledFor(projectA).Select(w => w.Id).ToArray());

        // 把它打开：全局 + 该项目的都命中，别的不命中。
        var enabled = await client.PatchAsJsonAsync($"/api/webhooks/{globalId}", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        var forA = repository.ListEnabledFor(projectA).Select(w => w.Id).ToArray();
        Assert.Equal(new[] { globalId, projectAId }, forA);

        var forB = repository.ListEnabledFor(projectB).Select(w => w.Id).ToArray();
        Assert.Equal(new[] { globalId, projectBId }, forB);
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>建一个 webhook 并返回它的 id。</summary>
    private static async Task<string> CreateWebhookAsync(HttpClient client, string scope, string? projectId)
    {
        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope,
            projectId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读 webhook 列表（裸数组）。</summary>
    private static async Task<List<WebhookSummary>> ReadWebhooksAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        return root.EnumerateArray()
            .Select(element => new WebhookSummary(
                Id: element.GetProperty("id").GetString()!,
                Scope: element.GetProperty("scope").GetString()!,
                ProjectId: element.GetProperty("projectId").GetString(),
                Enabled: element.GetProperty("enabled").GetBoolean(),
                HasSecret: element.GetProperty("hasSecret").GetBoolean()))
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

    private static bool IsLowerHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

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

    private sealed record WebhookSummary(string Id, string Scope, string? ProjectId,
                                        bool Enabled, bool HasSecret);
}
