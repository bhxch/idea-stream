using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IdeaApp.Contracts;
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

        // 想法没有标题了，预设里不该再有 {{idea.title}}。
        Assert.DoesNotContain("{{idea.title}}", bodyTemplate, StringComparison.Ordinal);

        // 预设本身必须是一个合法的 JSON 文档（占位符在字符串值里面）——
        // 页面拿它直接当 Body 模板用，非法 JSON 的话渲染器会直接抛。
        using var template = JsonDocument.Parse(bodyTemplate);
        Assert.Equal(JsonValueKind.Object, template.RootElement.ValueKind);

        // 正文那一行的位置变了：模板少了一行（原来第 2 行是 {{idea.title}}，第 3 行才是正文）。
        // 结构变了，断言没写错 —— 现在第 1 行就是正文。
        Assert.Equal("{{idea.content}}",
            template.RootElement.GetProperty("text").GetString()!.Split('\n')[1]);
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
    /// Body 模板的根节点必须是 JSON 对象：数组、数字、字符串都要拒掉。
    /// </summary>
    /// <remarks>
    /// 渲染器只在**字符串值节点**里替换占位符，根节点不是对象的话，
    /// 模板在投递时根本没法按「JSON 模板」处理 —— 这种事要在保存时就说清楚。
    /// </remarks>
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("123")]
    [InlineData("\"x\"")]
    public async Task Create_webhook_with_non_object_body_template_returns_400_code_invalid_body_template(
        string bodyTemplate)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_body_template", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 占位符只允许出现在字符串值节点里：写在对象的键里要拒掉。
    /// </summary>
    [Fact]
    public async Task Create_webhook_with_placeholder_in_object_key_returns_400_code_invalid_body_template()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = """{"{{idea.id}}":"{{idea.title}}"}""",
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_body_template", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 同一个对象里出现重复的键要拒掉（400 <c>invalid_body_template</c>），
    /// 而且 <c>detail</c> 是**中文**的：内部实现抛的是英文消息，不能直接透给 API 用户。
    /// </summary>
    [Fact]
    public async Task Create_webhook_with_duplicate_keys_in_body_template_returns_400_code_invalid_body_template()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = """{"text":"a","text":"b"}""",
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_body_template", problem.GetProperty("code").GetString());

        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("重复", detail);
        Assert.DoesNotContain("same key", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 模板不是合法 JSON 时，400 的 <c>detail</c> 是**纯中文**的，而且**不回显模板内容**：
    /// BCL 的 JsonException 消息是英文的，而且会把读到的那个裸串原样带出来 ——
    /// 模板里可能有 bot token，不能进 API 响应。
    /// </summary>
    [Fact]
    public async Task Create_webhook_with_invalid_json_body_template_returns_400_with_chinese_detail_and_no_template_echo()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 以 n 开头的裸串：BCL 会把它整个回显进异常消息里。
        const string tokenLike = "not_a_json_BOT_TOKEN_1234567890:AAHfakefakefakefakefake";

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = tokenLike,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_body_template", problem.GetProperty("code").GetString());

        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Equal("Body 模板必须是合法的 JSON", detail);
        Assert.DoesNotContain(tokenLike, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid JSON literal", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 键名里有占位符时，400 的 <c>detail</c> **不回显键名**：
    /// 键名是用户写的模板内容，可能含凭据，而 <c>detail</c> 会原样发给调用方
    /// （含 agent 与 skill 的 stderr）。
    /// </summary>
    /// <remarks>
    /// 消息里保留了「第 N 个键」这种位置信息，定位能力没丢 ——
    /// 丢的只是键名的内容。
    /// </remarks>
    [Fact]
    public async Task Create_webhook_with_placeholder_in_object_key_returns_400_detail_without_echoing_key_name()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string>(),
            // 键名里既有占位符（触发这条分支），也有看起来像凭据的片段。
            bodyTemplate = """{"sk-live-SECRET-{{idea.id}}":"v"}""",
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_body_template", problem.GetProperty("code").GetString());

        var detail = problem.GetProperty("detail").GetString()!;

        // 键名的内容一个字都不带：整体不带，片段也不带。
        Assert.DoesNotContain("sk-live-SECRET-{{idea.id}}", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-SECRET", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("idea.id", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", detail, StringComparison.Ordinal);

        // 位置信息保留，而且不夹带键名内容。
        Assert.Contains("个键", detail, StringComparison.Ordinal);
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

    /// <summary>缺 name / 空 name → 400 invalid_webhook_name。</summary>
    [Fact]
    public async Task Create_webhook_with_blank_name_returns_400_code_invalid_webhook_name()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var payloads = new[]
        {
            // 缺 name
            """{"url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}""",
            """{"name":"","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}""",
            """{"name":"   ","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}"""
        };

        foreach (var payload in payloads)
        {
            var response = await PostJsonAsync(client, "/api/webhooks", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_webhook_name", problem.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// URL 为空或不是 http(s) 绝对地址 → 400 invalid_webhook_url。
    /// </summary>
    /// <remarks>
    /// 错误信息里**不回显 URL**：Telegram 的 bot token 就在 URL 路径里，
    /// 回显出去等于把 token 写进了日志与 skill 的 stderr。
    /// </remarks>
    [Fact]
    public async Task Create_webhook_with_invalid_url_returns_400_code_invalid_webhook_url()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var payloads = new[]
        {
            // 缺 url
            """{"name":"钩子","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}""",
            """{"name":"钩子","url":"","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}""",
            """{"name":"钩子","url":"   ","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}""",
            // 不是绝对地址
            """{"name":"钩子","url":"example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}""",
            // 不是 http(s)
            """{"name":"钩子","url":"ftp://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"global"}"""
        };

        foreach (var payload in payloads)
        {
            var response = await PostJsonAsync(client, "/api/webhooks", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_webhook_url", problem.GetProperty("code").GetString());

            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("example.com", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Telegram 预设的 URL 里带 <c>&lt;BOT_TOKEN&gt;</c> 占位符 ——
    /// 它不是合法的 URI 字符，但整体仍是 http(s) 绝对地址，
    /// 不能被 URL 校验挡掉（否则页面自己给的预设自己用不了）。
    /// </summary>
    [Fact]
    public async Task Create_webhook_accepts_telegram_preset_url_with_placeholder()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "Telegram",
            url = "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>scope 不是 global / project → 400 invalid_webhook_scope。</summary>
    [Fact]
    public async Task Create_webhook_with_unknown_scope_returns_400_code_invalid_webhook_scope()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var payloads = new[]
        {
            // 缺 scope
            """{"name":"钩子","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[]}""",
            """{"name":"钩子","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":""}""",
            """{"name":"钩子","url":"https://example.com/hook","method":"POST","bodyTemplate":"{}","events":[],"scope":"team"}"""
        };

        foreach (var payload in payloads)
        {
            var response = await PostJsonAsync(client, "/api/webhooks", payload);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var problem = await ReadProblemAsync(response);
            Assert.Equal("invalid_webhook_scope", problem.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// 单查响应体同样不带 secret 明文 —— 这是「secret 不回传」的关键路径之一：
    /// 单查会把整个配置面（URL、Body 模板、请求头）原样回传。
    /// </summary>
    /// <remarks>
    /// 这里显式要 <c>?unmask=true</c>：默认的响应是遮蔽版（URL 与请求头的值都换了）。
    /// 遮蔽归遮蔽，secret 仍然只进不出 —— 就算是明文的配置面，密钥也不在里面。
    /// </remarks>
    [Fact]
    public async Task Get_webhook_returns_config_without_secret_plaintext()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "带密钥的钩子",
            url = "https://example.com/hook",
            method = "POST",
            headers = new Dictionary<string, string> { ["X-Token"] = "value" },
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null,
            secret = "single-secret"
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;

        var response = await client.GetAsync($"/api/webhooks/{id}?unmask=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        using var single = JsonDocument.Parse(body);
        var root = single.RootElement;

        Assert.Equal(id, root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("hasSecret").GetBoolean());

        // 配置面确实返回了（不然「没返回」也算不上「没泄漏」）。
        Assert.Equal("https://example.com/hook", root.GetProperty("url").GetString());
        Assert.Equal(ValidBodyTemplate, root.GetProperty("bodyTemplate").GetString());

        // 但密钥明文一个字都不在里面。
        Assert.DoesNotContain("single-secret", body, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("secret", out _));
    }

    /// <summary>
    /// 列表默认返回遮蔽版：URL 只留 scheme 与 host，headers 只留键名。
    /// </summary>
    /// <remarks>
    /// 命令层删掉之后，脱敏的归处就是服务端：裸 curl 拿到的响应会原样进
    /// agent 的上下文与日志，而 Telegram 的 bot token 就在 URL 路径里。
    /// </remarks>
    [Fact]
    public async Task List_returns_masked_url_and_header_keys_by_default()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateWebhookAsync(
            client,
            scope: "global",
            projectId: null,
            url: "https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET2" });

        var response = await client.GetAsync("/api/webhooks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.EnumerateArray().Single();

        // URL：host 可读（知道打的是谁），凭据不可读（路径整段换成 ***）。
        var url = item.GetProperty("url").GetString()!;
        Assert.StartsWith("https://api.telegram.org/", url, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", url, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", url, StringComparison.Ordinal);

        // headers：键名留着（调用方要靠它认出这是哪个请求头），值一律 ***。
        var headers = item.GetProperty("headers");
        var header = headers.EnumerateObject().Single();
        Assert.Equal("Authorization", header.Name);
        Assert.Equal("***", header.Value.GetString());

        // 整份响应里都不该出现凭据。
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
    }

    /// <summary>要完整值得显式说一声：<c>?unmask=true</c> 返回原值。</summary>
    [Fact]
    public async Task List_returns_the_raw_values_when_unmask_is_true()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateWebhookAsync(
            client,
            scope: "global",
            projectId: null,
            url: "https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET2" });

        var response = await client.GetAsync("/api/webhooks?unmask=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = document.RootElement.EnumerateArray().Single();

        Assert.Equal("https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            item.GetProperty("url").GetString());
        Assert.Equal("Bearer SECRET2",
            item.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    /// <summary>单查同样默认遮蔽；<c>?unmask=true</c> 才给原值。</summary>
    [Fact]
    public async Task Get_returns_masked_url_by_default()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateWebhookAsync(
            client,
            scope: "global",
            projectId: null,
            url: "https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET2" });

        var masked = await client.GetAsync($"/api/webhooks/{id}");

        Assert.Equal(HttpStatusCode.OK, masked.StatusCode);

        var maskedBody = await masked.Content.ReadAsStringAsync();

        using (var document = JsonDocument.Parse(maskedBody))
        {
            var item = document.RootElement;
            Assert.Equal("https://api.telegram.org/***/***", item.GetProperty("url").GetString());
            Assert.Equal("***", item.GetProperty("headers").GetProperty("Authorization").GetString());
        }

        Assert.DoesNotContain("SECRET", maskedBody, StringComparison.Ordinal);

        var raw = await client.GetAsync($"/api/webhooks/{id}?unmask=true");

        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);

        using var rawDocument = JsonDocument.Parse(await raw.Content.ReadAsStringAsync());
        Assert.Equal("https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            rawDocument.RootElement.GetProperty("url").GetString());
        Assert.Equal("Bearer SECRET2",
            rawDocument.RootElement.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    /// <summary>写接口的响应一律遮蔽：新建的回执里不该带完整凭据。</summary>
    [Fact]
    public async Task Create_response_is_masked()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = "https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            method = "POST",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET2" },
            bodyTemplate = ValidBodyTemplate,
            events = new[] { "idea.created" },
            scope = "global",
            projectId = (string?)null
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var item = document.RootElement;

        Assert.Equal("https://api.telegram.org/***/***", item.GetProperty("url").GetString());
        Assert.Equal("***", item.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 改配置的响应也遮蔽。
    /// </summary>
    /// <remarks>
    /// 这里只改 name：更新接口的 url 是三态的（传 null = 不改），
    /// 所以「回执是遮蔽版」不会把库里的 URL 改坏 —— 遮蔽只发生在出参上。
    /// </remarks>
    [Fact]
    public async Task Update_response_is_masked()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateWebhookAsync(
            client,
            scope: "global",
            projectId: null,
            url: "https://api.telegram.org/botXXXXXX:SECRET/sendMessage",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET2" });

        var response = await client.PatchAsJsonAsync($"/api/webhooks/{id}", new { name = "改个名" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var item = document.RootElement;

        Assert.Equal("改个名", item.GetProperty("name").GetString());
        Assert.Equal("https://api.telegram.org/***/***", item.GetProperty("url").GetString());
        Assert.Equal("***", item.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.DoesNotContain("SECRET", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 遮蔽只动路径：scheme 与 host 留着，路径的段数也留着。
    /// </summary>
    /// <remarks>
    /// host 可读是刻意的：调用方要知道「打的是哪个服务」，
    /// 而路径段（token 在那里）一律换成 ***。
    /// </remarks>
    [Fact]
    public async Task Masked_url_keeps_scheme_and_host()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateWebhookAsync(
            client,
            scope: "global",
            projectId: null,
            url: "https://api.telegram.org/botX/sendMessage");

        var response = await client.GetAsync($"/api/webhooks/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("https://api.telegram.org/***/***",
            document.RootElement.GetProperty("url").GetString());
    }

    /// <summary>
    /// 请求体 record 的字段必须声明为**可空**。
    /// </summary>
    /// <remarks>
    /// 签名是给调用方看的契约：JSON 里缺字段时反序列化出来就是 null，
    /// 服务层据此返回 400（<c>invalid_webhook_name</c> / <c>invalid_webhook_url</c> 等）。
    /// 签名写成非空就是在骗调用方 —— 照着它写代码的人（比如 <c>req.Name.Trim()</c>）
    /// 会在「客户端漏传字段」这一种输入上踩到 NRE。
    /// <para>
    /// 这条断言是**编译期**的：把 null 传给非空参数是 CS8625，
    /// 而测试项目开着 <c>TreatWarningsAsErrors</c> —— 谁把字段改回非空，
    /// 测试项目直接编译不过，比运行期断言更早、更硬。
    /// </para>
    /// </remarks>
    [Fact]
    public void Webhook_request_records_accept_missing_fields_as_null()
    {
        var create = new WebhookCreateRequest(
            Name: null,
            Url: null,
            Method: null,
            Headers: null,
            BodyTemplate: null,
            Events: null,
            Scope: null,
            ProjectId: null,
            Secret: null);

        var update = new WebhookUpdateRequest(
            Name: null,
            Url: null,
            Method: null,
            Headers: null,
            BodyTemplate: null,
            Events: null,
            Enabled: null,
            Secret: null);

        Assert.Null(create.Name);
        Assert.Null(create.Url);
        Assert.Null(create.Method);
        Assert.Null(create.Headers);
        Assert.Null(create.BodyTemplate);
        Assert.Null(create.Events);
        Assert.Null(create.Scope);
        Assert.Null(create.ProjectId);
        Assert.Null(create.Secret);

        Assert.Null(update.Name);
        Assert.Null(update.Url);
        Assert.Null(update.Method);
        Assert.Null(update.Headers);
        Assert.Null(update.BodyTemplate);
        Assert.Null(update.Events);
        Assert.Null(update.Enabled);
        Assert.Null(update.Secret);
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
    /// <param name="url">URL；不给就用默认的。</param>
    /// <param name="headers">请求头；不给就用空字典。</param>
    private static async Task<string> CreateWebhookAsync(
        HttpClient client,
        string scope,
        string? projectId,
        string? url = null,
        Dictionary<string, string>? headers = null)
    {
        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "钩子",
            url = url ?? "https://example.com/hook",
            method = "POST",
            headers = headers ?? new Dictionary<string, string>(),
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
