using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdeaApp.Tests;

/// <summary>
/// Webhook 投递的集成测试：事件 → 匹配 → 渲染 → 异步投递 → 重试 → 死信 → 手动重投。
///
/// <para>
/// 目标端点用 <see cref="HttpListener"/> 起在 127.0.0.1 的随机端口上：
/// 投递走的是真的 HTTP 客户端（请求头、请求体、超时都按真实语义来），
/// 而不是把「投递」抽象成假的函数。
/// </para>
///
/// <para>
/// 断言一律走 **HTTP 契约**（端点 + 响应体）与假端点收到的请求，
/// 不引用后端的新类型 —— 这样测试只依赖对外行为，
/// 「端点还没实现」表现为真实的 404 而不是编译错误。
/// </para>
/// </summary>
public sealed class WebhookDeliveryTests
{
    /// <summary>默认的 Body 模板：不依赖项目名，测试只关心事件与正文。</summary>
    private const string DefaultBodyTemplate = """{"text":"{{event}} {{idea.content}}"}""";

    /// <summary>投递成功的判定：状态是 delivered。</summary>
    private static bool IsDelivered(JsonElement delivery) =>
        delivery.GetProperty("status").GetString() == "delivered";

    /// <summary>投递失败的判定：状态是 failed。</summary>
    private static bool IsFailed(JsonElement delivery) =>
        delivery.GetProperty("status").GetString() == "failed";

    /// <summary>
    /// 投递把渲染后的模板 POST 到目标端点：body 是渲染结果，Content-Type 是 application/json。
    /// </summary>
    [Fact]
    public async Task Delivery_posts_rendered_template_to_target()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        var created = await client.PostAsJsonAsync("/api/ideas", new { content = "投递正文" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        var request = Assert.Single(endpoint.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.ContentType);

        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("idea.created 投递正文", payload.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 配了自定义请求头与密钥 → 目标端点收到该请求头与 HMAC 签名头。
    /// </summary>
    /// <remarks>
    /// 签名在这里**独立重算**一遍（不调用后端的实现）：
    /// 否则「实现算错也照样通过」，这条断言就白写了。
    /// </remarks>
    [Fact]
    public async Task Delivery_sends_custom_headers_and_hmac_signature()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(
            client,
            endpoint.Url,
            ["idea.created"],
            headers: new Dictionary<string, string> { ["X-Token"] = "value" },
            secret: "s3cret");

        await client.PostAsJsonAsync("/api/ideas", new { content = "带签名的投递" });

        await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        var request = Assert.Single(endpoint.Requests);
        Assert.Equal("value", request.Header("X-Token"));

        var signature = request.Header("X-Idea-Signature");
        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
        Assert.Equal(ExpectedSignature("s3cret", request.Body), signature);
    }

    /// <summary>
    /// 目标端点一直返回 500 → 重试到上限后记 failed：attempts == 3（含首次），last_error 非空。
    /// </summary>
    [Fact]
    public async Task Failed_delivery_retries_three_times_then_records_failed()
    {
        using var endpoint = new FakeEndpoint { StatusCodeProvider = () => 500 };
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        await client.PostAsJsonAsync("/api/ideas", new { content = "会失败的想法" });

        var delivery = await WaitForDeliveryAsync(client, webhookId, IsFailed, TimeSpan.FromSeconds(30));

        // attempts 的口径是**总尝试次数（含首次）**：首次 + 两次重试 = 3。
        Assert.Equal(3, delivery.GetProperty("attempts").GetInt32());
        Assert.False(string.IsNullOrEmpty(delivery.GetProperty("lastError").GetString()));

        // 目标端点确实被打了 3 次。
        await endpoint.WaitForRequestsAsync(3, TimeSpan.FromSeconds(5));
    }

    /// <summary>投递成功 → 记 delivered，attempts == 1（只试了一次就成功）。</summary>
    [Fact]
    public async Task Successful_delivery_records_delivered_with_attempts_one()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        await client.PostAsJsonAsync("/api/ideas", new { content = "成功的想法" });

        var delivery = await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        Assert.Equal(webhookId, delivery.GetProperty("webhookId").GetString());
        Assert.Equal("idea.created", delivery.GetProperty("eventType").GetString());
        Assert.Equal(1, delivery.GetProperty("attempts").GetInt32());
        Assert.Null(delivery.GetProperty("lastError").GetString());

        // 记录里存的就是发出去的那份请求体（手动重投靠它重发）。
        var request = Assert.Single(endpoint.Requests);
        Assert.Equal(request.Body, delivery.GetProperty("payload").GetString());
    }

    /// <summary>
    /// 手动重投**重发已渲染的 payload**：改了模板之后重投，收到的仍是旧 payload。
    /// </summary>
    /// <remarks>
    /// 重渲染的输入（事件的全量数据）根本没被存下来 ——
    /// 表里只有渲染好的 payload，所以重投只能重发它。
    /// </remarks>
    [Fact]
    public async Task Manual_retry_resends_the_stored_payload()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(
            client,
            endpoint.Url,
            ["idea.created"],
            bodyTemplate: """{"text":"旧模板 {{idea.content}}"}""");

        await client.PostAsJsonAsync("/api/ideas", new { content = "第一条想法" });

        var first = await WaitForDeliveryAsync(client, webhookId, IsDelivered);
        var deliveryId = first.GetProperty("id").GetString()!;
        var originalPayload = Assert.Single(endpoint.Requests).Body;

        // 改模板：改成新模板之后重投，收到的**仍然**是旧 payload。
        var patched = await PatchJsonAsync(client, $"/api/webhooks/{webhookId}",
            new { bodyTemplate = """{"text":"新模板 {{idea.content}}"}""" });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var retry = await client.PostAsync($"/api/webhooks/{webhookId}/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        await endpoint.WaitForRequestsAsync(2, TimeSpan.FromSeconds(10));

        var resent = endpoint.Requests[1];
        Assert.Equal(originalPayload, resent.Body);
        Assert.DoesNotContain("新模板", resent.Body, StringComparison.Ordinal);

        // attempts 在**同一行**上继续累加：1（首次） + 1（重投） = 2。
        var after = await WaitForDeliveryAsync(
            client, webhookId, d => d.GetProperty("attempts").GetInt32() == 2);

        Assert.Equal("delivered", after.GetProperty("status").GetString());
        Assert.Equal(deliveryId, after.GetProperty("id").GetString());
    }

    /// <summary>只订阅 idea.created 的 webhook 收不到 idea.updated。</summary>
    [Fact]
    public async Task Webhook_receives_only_subscribed_events()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        var ideaId = await CreateIdeaAsync(client, "第一条想法");
        await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        // 改想法 → idea.updated：没订阅，不投递。
        var updated = await PatchJsonAsync(client, $"/api/ideas/{ideaId}", new { content = "改过的正文" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Single(endpoint.Requests);
        Assert.Single(await ReadDeliveriesAsync(client, webhookId));
    }

    /// <summary>全局且 disabled 的 webhook 什么都收不到。</summary>
    [Fact]
    public async Task Global_webhook_disabled_receives_nothing()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 全局作用域的 webhook 创建时默认禁用。
        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"], enabled: false);

        await client.PostAsJsonAsync("/api/ideas", new { content = "不该被投递的想法" });

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Empty(endpoint.Requests);
        Assert.Empty(await ReadDeliveriesAsync(client, webhookId));
    }

    /// <summary>绑定项目 A 的 webhook 只收项目 A 的事件，不收项目 B 的。</summary>
    [Fact]
    public async Task Project_webhook_receives_only_its_project_events()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectA = await CreateProjectAsync(client, "项目A");
        var projectB = await CreateProjectAsync(client, "项目B");

        var webhookId = await CreateWebhookAsync(
            client, endpoint.Url, ["idea.created"], scope: "project", projectId: projectA);

        // 项目 B 的想法：这个 webhook 收不到。
        await client.PostAsJsonAsync("/api/ideas", new { content = "B 的想法", projectId = projectB });
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Empty(endpoint.Requests);
        Assert.Empty(await ReadDeliveriesAsync(client, webhookId));

        // 项目 A 的想法：收得到。
        await client.PostAsJsonAsync("/api/ideas", new { content = "A 的想法", projectId = projectA });

        await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        var request = Assert.Single(endpoint.Requests);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("idea.created A 的想法", payload.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 项目级 webhook 收得到**本项目**的想法事件，载荷里还带得出项目名
    /// （<c>{{project}}</c> 渲染成项目名，不是空串）。
    /// </summary>
    /// <remarks>
    /// 想法事件带项目快照（spec §11.2）是这条成立的前提：匹配按
    /// <c>evt.Project?.Id == webhook.ProjectId</c> 判定，占位符也读的是同一个快照。
    /// </remarks>
    [Fact]
    public async Task Project_webhook_receives_its_project_idea_events_with_project_name()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        var webhookId = await CreateWebhookAsync(
            client,
            endpoint.Url,
            ["idea.created"],
            scope: "project",
            projectId: projectId,
            bodyTemplate: """{"text":"[{{project}}] {{event}}"}""");

        var created = await client.PostAsJsonAsync(
            "/api/ideas", new { content = "A 的想法", projectId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        var request = Assert.Single(endpoint.Requests);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("[项目A] idea.created", payload.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 目标端点慢（5 秒）也不拖慢写接口：POST /api/ideas 照样 1 秒内返回 201。
    /// </summary>
    [Fact]
    public async Task Slow_or_unreachable_target_does_not_block_the_api_response()
    {
        using var endpoint = new FakeEndpoint { ResponseDelay = TimeSpan.FromSeconds(5) };
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        var stopwatch = Stopwatch.StartNew();
        var created = await client.PostAsJsonAsync("/api/ideas", new { content = "慢目标的想法" });
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"写路径被慢目标拖住了，耗时 {stopwatch.Elapsed}");
    }

    /// <summary>
    /// 手动重投一条不存在的投递记录 → 404 + code=delivery_not_found。
    /// </summary>
    [Fact]
    public async Task Manual_retry_of_unknown_delivery_returns_404_code_delivery_not_found()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, "https://example.com/hook", ["idea.created"]);

        var response = await client.PostAsync(
            $"/api/webhooks/{webhookId}/deliveries/0198f0a1b2c3d4e5f60718293a4b5c6d/retry", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("delivery_not_found", document.RootElement.GetProperty("code").GetString());
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(HttpClient client, string content)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// 建一个 webhook 并返回它的 id。
    ///
    /// <para>
    /// 启用状态一律用 PATCH 显式设置：全局作用域的 webhook 创建时默认禁用，
    /// 项目作用域的默认启用 —— 这里不依赖那个默认值。
    /// </para>
    /// </summary>
    private static async Task<string> CreateWebhookAsync(
        HttpClient client,
        string url,
        string[] events,
        bool enabled = true,
        string scope = "global",
        string? projectId = null,
        string bodyTemplate = DefaultBodyTemplate,
        Dictionary<string, string>? headers = null,
        string? secret = null)
    {
        var response = await client.PostAsJsonAsync("/api/webhooks", new
        {
            name = "投递钩子",
            url,
            method = "POST",
            headers = headers ?? new Dictionary<string, string>(),
            bodyTemplate,
            events,
            scope,
            projectId,
            secret
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;

        var patched = await PatchJsonAsync(client, $"/api/webhooks/{id}", new { enabled });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        return id;
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

    /// <summary>读某个 webhook 的投递记录列表。</summary>
    private static async Task<List<JsonElement>> ReadDeliveriesAsync(HttpClient client, string webhookId)
    {
        var response = await client.GetAsync($"/api/webhooks/{webhookId}/deliveries");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    /// <summary>
    /// 等到出现满足条件的投递记录为止。
    /// </summary>
    /// <remarks>
    /// 投递是异步的：写接口返回时投递还没发生，所以只能轮询等它落地。
    /// </remarks>
    private static async Task<JsonElement> WaitForDeliveryAsync(
        HttpClient client,
        string webhookId,
        Func<JsonElement, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var delivery in await ReadDeliveriesAsync(client, webhookId))
            {
                if (predicate(delivery))
                {
                    return delivery;
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"等 webhook {webhookId} 的投递记录超时");
    }

    /// <summary>独立重算一遍 HMAC-SHA256 签名（不调用后端的实现）。</summary>
    private static string ExpectedSignature(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>假的目标端点：一个真的 HTTP 监听器，记录收到的请求。</summary>
    private sealed class FakeEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<ReceivedRequest> _requests = new();
        private readonly object _gate = new();

        public FakeEndpoint()
        {
            Port = GetFreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            _ = Task.Run(ListenAsync);
        }

        /// <summary>监听端口。</summary>
        public int Port { get; }

        /// <summary>webhook 该填的 URL。</summary>
        public string Url => $"http://127.0.0.1:{Port}/hook";

        /// <summary>每次请求返回的状态码（默认 200）。</summary>
        public Func<int> StatusCodeProvider { get; set; } = () => 200;

        /// <summary>响应前的延迟（用来模拟慢目标端点）。</summary>
        public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

        /// <summary>收到的请求（快照）。</summary>
        public IReadOnlyList<ReceivedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        /// <summary>等收到指定数量的请求。</summary>
        public async Task WaitForRequestsAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Requests.Count >= count)
                {
                    return;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"等 {count} 个请求超时，实际收到 {Requests.Count} 个");
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // 已经关过了。
            }
        }

        private async Task ListenAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    // 监听器被关掉了。
                    return;
                }

                try
                {
                    await HandleAsync(context);
                }
                catch (Exception)
                {
                    // 客户端提前断开之类：忽略这一条，继续接下一条。
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is not null)
                {
                    headers[key] = context.Request.Headers[key] ?? string.Empty;
                }
            }

            var request = new ReceivedRequest(
                context.Request.HttpMethod,
                body,
                context.Request.ContentType,
                headers);

            lock (_gate)
            {
                _requests.Add(request);
            }

            if (ResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(ResponseDelay);
            }

            var bytes = Encoding.UTF8.GetBytes("{}");
            context.Response.StatusCode = StatusCodeProvider();
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        /// <summary>取一个空闲端口。</summary>
        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }
    }

    /// <summary>假端点收到的一个请求。</summary>
    private sealed record ReceivedRequest(
        string Method,
        string Body,
        string? ContentType,
        Dictionary<string, string> Headers)
    {
        /// <summary>取一个请求头（大小写不敏感）。</summary>
        public string? Header(string name) =>
            Headers.TryGetValue(name, out var value) ? value : null;
    }
}
