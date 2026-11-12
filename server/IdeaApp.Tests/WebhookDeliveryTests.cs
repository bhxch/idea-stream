using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

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
    /// 目标端点一直返回 500 → 重试到上限后记 failed：attempts == 4（含首次），last_error 非空。
    /// </summary>
    /// <remarks>
    /// 默认 <c>Webhook:MaxAttempts = 4</c>（首次 + 3 次重试），
    /// 退避 1s / 5s / 30s —— 这条测试要等满 36 秒才等到死信，是全套里最慢的一条。
    /// <para>
    /// 它顺带把「三段退避一一对应」钉住：默认值得是 4 的话，
    /// 退避表里的 30 秒那一段才用得上（默认值若是 3，它永远用不上）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Failed_delivery_retries_three_times_then_records_failed()
    {
        using var endpoint = new FakeEndpoint { StatusCodeProvider = _ => 500 };
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        await client.PostAsJsonAsync("/api/ideas", new { content = "会失败的想法" });

        var delivery = await WaitForDeliveryAsync(client, webhookId, IsFailed, TimeSpan.FromSeconds(90));

        // attempts 的口径是**总尝试次数（含首次）**：首次 + 三次重试 = 4。
        Assert.Equal(4, delivery.GetProperty("attempts").GetInt32());
        Assert.False(string.IsNullOrEmpty(delivery.GetProperty("lastError").GetString()));

        // 目标端点确实被打了 4 次。
        await endpoint.WaitForRequestsAsync(4, TimeSpan.FromSeconds(5));

        // 三段退避一一对应：1 秒 / 5 秒 / 30 秒。
        var requests = endpoint.Requests;
        Assert.True(
            requests[1].ReceivedAt - requests[0].ReceivedAt >= TimeSpan.FromSeconds(1),
            $"第 1 次退避不到 1 秒：{requests[1].ReceivedAt - requests[0].ReceivedAt}");
        Assert.True(
            requests[2].ReceivedAt - requests[1].ReceivedAt >= TimeSpan.FromSeconds(5),
            $"第 2 次退避不到 5 秒：{requests[2].ReceivedAt - requests[1].ReceivedAt}");
        Assert.True(
            requests[3].ReceivedAt - requests[2].ReceivedAt >= TimeSpan.FromSeconds(30),
            $"第 3 次退避不到 30 秒：{requests[3].ReceivedAt - requests[2].ReceivedAt}");
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

    /// <summary>
    /// 手动重投一条**失败**的投递：那一行回到「投递中」，且**没有上次那个错误**。
    ///
    /// <remarks>
    /// 「投递中」却挂着上次那个错，看的人会以为「还没重投成功、又要再点一次」。
    /// 所以重投时要把错误清掉。
    /// <para>
    /// 这条**不赌时序**：目标端点把**重投那次**的请求卡住不放
    /// （同时把请求超时调大，卡住的状态不会因为超时被解除）——
    /// 重投的这次投递一直在途，那一行必然停在「投递中」+ 没有错误。
    /// </para>
    /// <para>
    /// 旧写法（不卡端点）会**间歇失败**：worker 可能在这条请求返回之前就把重投做完
    /// （那一行变成 attempts=2 的新失败），这时响应报的是**当前**的真实状态（failed），
    /// 而不是「投递中」—— 那不是 bug，是「响应说真话」。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Manual_retry_clears_the_previous_error()
    {
        // 重投那次请求的「放行」开关：卡住 → 在途；放行 → 目标端点回 500。
        var releaseRetryRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 第 1 次请求（worker 发的那次）立刻回 500 → 死信；
        // 第 2 次（手动重投）卡住不放 —— 重投一直在途。
        using var endpoint = new FakeEndpoint
        {
            StatusCodeProvider = _ => 500,
            OnRequestAsync = async index =>
            {
                if (index == 1)
                {
                    return;
                }

                await releaseRetryRequest.Task;
            }
        };
        using var factory = new TestAppFactory(webhookMaxAttempts: 1, webhookTimeoutSeconds: 3600);
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        await client.PostAsJsonAsync("/api/ideas", new { content = "会失败的想法" });

        var failed = await WaitForDeliveryAsync(client, webhookId, IsFailed, TimeSpan.FromSeconds(20));
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("lastError").GetString()));
        var deliveryId = failed.GetProperty("id").GetString()!;

        var retry = await client.PostAsync($"/api/webhooks/{webhookId}/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        // 响应里就是「投递中」+ 没有上次那个错误。
        using var document = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal("pending", document.RootElement.GetProperty("status").GetString());
        Assert.Null(document.RootElement.GetProperty("lastError").GetString());

        // 库里也是「投递中」+ 没有上次那个错误。
        var row = Assert.Single(await ReadDeliveriesAsync(client, webhookId));
        Assert.Equal("pending", row.GetProperty("status").GetString());
        Assert.Null(row.GetProperty("lastError").GetString());

        // 重投**不重置** attempts：它接着上次的次数继续累加（口径 = 总尝试次数）。
        // 重投那次还在途 → 还没记账 → attempts 仍然是 1。
        Assert.Equal(1, row.GetProperty("attempts").GetInt32());

        // 重投确实发出去了（第 2 个请求到了目标端点），而且被卡在那里。
        await endpoint.WaitForRequestsAsync(2, TimeSpan.FromSeconds(20));

        // 放行重投那次请求：它回 500 → 这次尝试记在**同一行**上 → attempts 变成 2。
        releaseRetryRequest.TrySetResult(true);

        var afterRetry = await WaitForDeliveryAsync(
            client,
            webhookId,
            d => d.GetProperty("attempts").GetInt32() == 2 && d.GetProperty("status").GetString() == "failed",
            TimeSpan.FromSeconds(20));
        Assert.Equal("failed", afterRetry.GetProperty("status").GetString());
        Assert.Equal(2, afterRetry.GetProperty("attempts").GetInt32());
    }

    /// <summary>
    /// 手动重投**排不进队列**（队列满）时：那一行**原封不动** ——
    /// 还是 <c>failed</c>，而且**保留原来的 <c>last_error</c></c>。
    /// </summary>
    /// <remarks>
    /// 这条钉的是「入队失败不改那一行」：
    /// 用户看到的仍然是**上次失败的原因**，而不是一条没有理由的 failed。
    /// <para>
    /// 队列怎么塞满的：worker 的队列容量是
    /// <see cref="WebhookDeliveryWorker.QueueCapacity"/>，并发位是 8。
    /// 这里直接往 worker 的队列里投够任务（目标端点把请求全卡住 → 并发位全占住
    /// → 队列只进不出），下一次入队就必然失败。
    /// <b>断言仍然走 HTTP 契约</b>；用到的后台类型只用来准备这个前置条件。
    /// </para>
    /// <para>
    /// 「卡住」是**稳的**，不靠真实超时：请求超时被调到 3600 秒 ——
    /// 卡住的请求在整条测试期间都不会超时，8 个并发位就一直占着，
    /// 队列就一直满着（默认 10 秒超时的话，机器一慢并发位就被腾出来，队列被排空）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Manual_retry_that_cannot_be_queued_keeps_the_failed_record_and_its_error()
    {
        using var failingEndpoint = new FakeEndpoint { StatusCodeProvider = _ => 500 };
        using var blockingEndpoint = new FakeEndpoint
        {
            // 收到请求就不回：worker 的并发位全卡在这儿，队列只进不出。
            OnRequestAsync = _ => Task.Delay(Timeout.Infinite)
        };
        using var factory = new TestAppFactory(webhookMaxAttempts: 1, webhookTimeoutSeconds: 3600);
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, failingEndpoint.Url, ["idea.created"]);
        var fillerWebhookId = await CreateWebhookAsync(client, blockingEndpoint.Url, ["idea.updated"]);

        // 第 1 次投递失败 → 死信：failed + last_error。
        await client.PostAsJsonAsync("/api/ideas", new { content = "会失败的想法" });

        var failed = await WaitForDeliveryAsync(client, webhookId, IsFailed, TimeSpan.FromSeconds(20));
        var deliveryId = failed.GetProperty("id").GetString()!;
        var originalError = failed.GetProperty("lastError").GetString();
        Assert.False(string.IsNullOrEmpty(originalError));

        // 把投递队列塞满。
        var worker = factory.Services.GetRequiredService<WebhookDeliveryWorker>();
        var filler = factory.Services.GetRequiredService<WebhookRepository>().Get(fillerWebhookId)!;
        FillDeliveryQueue(worker, filler);

        // 等 worker 的 8 个并发位全卡在目标端点上：队列不会再被消费，「满」是稳的。
        await blockingEndpoint.WaitForRequestsAsync(8, TimeSpan.FromSeconds(30));

        // 再塞一次：这一次队列确实是满的。
        FillDeliveryQueue(worker, filler);

        // 重投 → 排不进队列 → 503 + code=delivery_queue_full。
        var retry = await client.PostAsync(
            $"/api/webhooks/{webhookId}/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);

        using var document = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal("delivery_queue_full", document.RootElement.GetProperty("code").GetString());

        // 那一行原封不动：还是 failed，last_error 还是原来那个，attempts 也没动。
        var after = Assert.Single(await ReadDeliveriesAsync(client, webhookId));
        Assert.Equal(deliveryId, after.GetProperty("id").GetString());
        Assert.Equal("failed", after.GetProperty("status").GetString());
        Assert.Equal(originalError, after.GetProperty("lastError").GetString());
        Assert.Equal(1, after.GetProperty("attempts").GetInt32());
    }

    /// <summary>
    /// 一次**成功**的投递能把死信改成 <c>delivered</c>（并清掉那个错误）。
    /// </summary>
    /// <remarks>
    /// 手动重投一条死信时，这一行在重投那一刻可能**还是 <c>failed</c>**：
    /// 重投要先入队、入队之后才置 pending，而 worker 可能已经把这次投递投完了。
    /// 这次投递**确实成功了**，记录就该是 delivered ——
    /// 否则用户重投成功了，看到的还是一条 failed。
    /// <para>
    /// 「worker 的这一次写入落在死信上」这个瞬间没法从 HTTP 那一侧排出来
    /// （入队与置 pending 之间只隔一条语句），所以这里直接调仓储的方法；
    /// <b>断言仍然走 HTTP 契约</b>：读回来看列表里那一条。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_successful_attempt_turns_a_dead_letter_into_delivered()
    {
        using var endpoint = new FakeEndpoint { StatusCodeProvider = _ => 500 };
        using var factory = new TestAppFactory(webhookMaxAttempts: 1);
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        await client.PostAsJsonAsync("/api/ideas", new { content = "会失败的想法" });

        var failed = await WaitForDeliveryAsync(client, webhookId, IsFailed, TimeSpan.FromSeconds(20));
        var deliveryId = failed.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("lastError").GetString()));

        // 模拟「worker 把这次投递投完了，而这一行还挂着死信」：记一次成功的尝试。
        var deliveries = factory.Services.GetRequiredService<DeliveryRepository>();
        deliveries.RecordAttempt(deliveryId, succeeded: true, lastError: null);

        // 那一行现在是 delivered，上次那个错也没了。
        var after = Assert.Single(await ReadDeliveriesAsync(client, webhookId));
        Assert.Equal("delivered", after.GetProperty("status").GetString());
        Assert.Null(after.GetProperty("lastError").GetString());
        Assert.Equal(2, after.GetProperty("attempts").GetInt32());
    }

    /// <summary>
    /// 手动重投与**在途投递**重叠时：<c>attempts</c> 不丢、终态不被改回。
    /// </summary>
    /// <remarks>
    /// 这是投递记录并发安全的核心断言：
    /// <list type="bullet">
    /// <item>worker 的第一次投递**还在途**（请求卡在目标端点上）时，用户点了重投；</item>
    /// <item>重投那条投递**成功**了（记录：attempts=1、delivered）；</item>
    /// <item>在途的那次投递随后**失败**了。</item>
    /// </list>
    /// 这时那一行上已经有两次真实的 HTTP 请求，<c>attempts</c> 就该是 2；
    /// 而且终态不能被后到的那次失败改回去（重投确实成功了）。
    /// <para>
    /// 旧实现（读出快照 → 整行覆盖写回）在这里会：
    /// 用内存里的旧快照把记录写回 <c>attempts=1 / pending</c>，最后再标成 <c>failed</c> ——
    /// 少记一次尝试，还把一次成功的投递判成了失败。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Manual_retry_during_an_in_flight_delivery_keeps_attempts_and_terminal_status()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory(webhookMaxAttempts: 1);
        using var client = factory.CreateClient();

        // 第 1 次请求（worker 发的那次）的**响应**卡住不放 ——
        // 逼出「投递在途」这个状态：worker 还在等这次请求的结果。
        var firstRequestArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        endpoint.OnRequestAsync = async index =>
        {
            if (index != 1)
            {
                return;
            }

            firstRequestArrived.TrySetResult(true);
            await releaseFirstRequest.Task;
        };

        // 第 1 次（worker 的那次）失败；第 2 次（手动重投）成功。
        endpoint.StatusCodeProvider = index => index == 1 ? 500 : 200;

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        var created = await client.PostAsJsonAsync("/api/ideas", new { content = "并发投递的想法" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // 等 worker 把第一次请求发出去（响应还卡着）：此刻投递在途。
        await firstRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // 投递记录已经落库了（worker 先插记录、再发请求）。
        var record = Assert.Single(await ReadDeliveriesAsync(client, webhookId));
        var deliveryId = record.GetProperty("id").GetString()!;
        Assert.Equal("pending", record.GetProperty("status").GetString());
        Assert.Equal(0, record.GetProperty("attempts").GetInt32());

        // 用户在「第一次投递还在途」的时候点了重投。
        var retry = await client.PostAsync($"/api/webhooks/{webhookId}/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        // 重投那条投递成功了：记录变成 attempts=1 / delivered。
        // （重投不会插出第二行 —— 一个 (webhook, 事件) 只有一行。）
        var afterRetry = await WaitForDeliveryAsync(
            client, webhookId, d => d.GetProperty("status").GetString() == "delivered");
        Assert.Equal(1, afterRetry.GetProperty("attempts").GetInt32());
        Assert.Single(await ReadDeliveriesAsync(client, webhookId));

        // 放行 worker 那次卡住的请求：它会失败。
        releaseFirstRequest.TrySetResult(true);

        // 等 worker 把那次失败的尝试写回去。
        // （新实现：attempts 变成 2；旧实现：状态被改回 failed —— 两种都算「写完了」，
        //   后面的断言会把旧实现顶出去。）
        var settled = await WaitForDeliveryAsync(
            client,
            webhookId,
            d => d.GetProperty("attempts").GetInt32() >= 2 || d.GetProperty("status").GetString() == "failed",
            TimeSpan.FromSeconds(20));

        // 两次真实的 HTTP 请求 → attempts 就是 2（一次都不能少记）。
        Assert.Equal(2, settled.GetProperty("attempts").GetInt32());

        // 终态不被后到的那次失败改回去：重投确实成功了。
        Assert.Equal("delivered", settled.GetProperty("status").GetString());

        // 已经 delivered 的记录不该挂上后来那次失败的错误。
        Assert.Null(settled.GetProperty("lastError").GetString());

        // 目标端点确实被打 2 次（不多不少）。
        await endpoint.WaitForRequestsAsync(2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, endpoint.Requests.Count);
    }

    /// <summary>
    /// 一个 (webhook, 事件) 在库里**只允许有一行**。
    /// </summary>
    /// <remarks>
    /// 这条不能只靠代码里的「先查后插」：手动重投与在途投递撞在一起时，
    /// 两边都可能在对方插之前查完（查的时候都还没插上）→ 插出两行同一事件的记录。
    /// 库里有唯一索引才兜得住，所以这里直接往库里插一行重复的，验证它被挡下来。
    /// </remarks>
    [Fact]
    public async Task One_delivery_row_per_webhook_and_event_is_enforced_by_the_database()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var webhookId = await CreateWebhookAsync(client, endpoint.Url, ["idea.created"]);

        await client.PostAsJsonAsync("/api/ideas", new { content = "唯一性检查的想法" });

        var delivery = await WaitForDeliveryAsync(client, webhookId, IsDelivered);
        var eventId = delivery.GetProperty("eventId").GetString()!;

        // 直接往库里插第二行同样的 (webhook, 事件) → 唯一索引必须把它挡下来。
        var error = Assert.Throws<SqliteException>(
            () => InsertDuplicateDelivery(factory.DatabasePath, webhookId, eventId));
        Assert.Contains("UNIQUE", error.Message, StringComparison.OrdinalIgnoreCase);

        // 库里还是只有一行。
        Assert.Single(await ReadDeliveriesAsync(client, webhookId));
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
    /// 删项目 → <b>全局</b> webhook 只收到 <b>1 条</b> <c>project.deleted</c>，
    /// 连带的那 N 条 <c>idea.deleted</c> 一条都收不到。
    /// </summary>
    /// <remarks>
    /// 规则：<b>事件所属项目在回收站里 → 跳过该事件</b>，全局与项目级一视同仁；
    /// 唯一例外是 <c>project.deleted</c>（「项目被删了」这件事本身要通知一次）。
    /// <para>
    /// 没有这条规则时，一次删项目会往全局 webhook 里灌
    /// 「1 条 project.deleted + N 条 idea.deleted」：
    /// 对 Telegram 这类目标会撞发送频率限制（约 30 条/秒），
    /// 剩下的一批变成失败与死信。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Deleted_project_global_webhook_receives_only_project_deleted()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        // 全局 webhook：订阅删除相关的事件。
        var globalWebhookId = await CreateWebhookAsync(
            client, endpoint.Url, ["project.deleted", "idea.deleted"]);

        // 项目A下建三条想法：删项目时会被连带删掉，各发一条 idea.deleted。
        foreach (var content in new[] { "想法1", "想法2", "想法3" })
        {
            var created = await client.PostAsJsonAsync("/api/ideas", new { content, projectId });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var deleted = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 只有 project.deleted 那 1 条：等它落地，再等它稳定下来确认没有后来者。
        var deliveries = await WaitForSettledDeliveriesAsync(client, globalWebhookId, 1);

        Assert.Single(deliveries);
        Assert.Equal("project.deleted", deliveries[0].GetProperty("eventType").GetString());

        var request = Assert.Single(endpoint.Requests);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("project.deleted ", payload.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 删项目 → <b>项目级</b> webhook 也只收到 <b>1 条</b> <c>project.deleted</c>，
    /// 连带的那 N 条 <c>idea.deleted</c> 一条都收不到。
    /// </summary>
    ///
    /// <remarks>
    /// <c>project.deleted</c> 是规则里的<b>唯一例外</b>：照投。
    /// 项目级 webhook 与全局 webhook 受<b>同一条</b>规则约束，
    /// 不做「全局的是例外」这种特例：
    /// 删项目时它收到 1 条，而不是 0 条（项目没了用户得知道），也不是 N+1 条。
    /// </remarks>
    [Fact]
    public async Task Deleted_project_project_webhook_receives_only_project_deleted()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        // 绑在项目A上的 webhook：订阅删除相关的事件。
        var projectWebhookId = await CreateWebhookAsync(
            client,
            endpoint.Url,
            ["project.deleted", "idea.deleted"],
            scope: "project",
            projectId: projectId);

        // 项目A下建三条想法：删项目时会被连带删掉，各发一条 idea.deleted。
        foreach (var content in new[] { "想法1", "想法2", "想法3" })
        {
            var created = await client.PostAsJsonAsync("/api/ideas", new { content, projectId });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var deleted = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var deliveries = await WaitForSettledDeliveriesAsync(client, projectWebhookId, 1);

        Assert.Single(deliveries);
        Assert.Equal("project.deleted", deliveries[0].GetProperty("eventType").GetString());

        var request = Assert.Single(endpoint.Requests);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("project.deleted ", payload.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 单独删一条想法（<b>项目还活着</b>）→ 全局与项目级 webhook <b>都</b>照收
    /// 那条 <c>idea.deleted</c>。
    /// </summary>
    ///
    /// <remarks>
    /// 这条守「别把规则写过头」：触发条件是「所属项目在回收站里」，
    /// 不是「事件是删除类事件」。项目还活着的时候，删想法就该照常通知。
    /// </remarks>
    [Fact]
    public async Task Deleting_one_idea_while_its_project_is_alive_reaches_global_and_project_webhooks()
    {
        using var globalEndpoint = new FakeEndpoint();
        using var projectEndpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        var globalWebhookId = await CreateWebhookAsync(
            client, globalEndpoint.Url, ["idea.deleted"]);

        var projectWebhookId = await CreateWebhookAsync(
            client, projectEndpoint.Url, ["idea.deleted"], scope: "project", projectId: projectId);

        var created = await client.PostAsJsonAsync(
            "/api/ideas", new { content = "单独删的想法", projectId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ideaId = await ReadIdAsync(created);

        var deleted = await client.DeleteAsync($"/api/ideas/{ideaId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 两条都照收。
        var globalDeliveries = await WaitForSettledDeliveriesAsync(client, globalWebhookId, 1);
        var projectDeliveries = await WaitForSettledDeliveriesAsync(client, projectWebhookId, 1);

        Assert.Single(globalDeliveries);
        Assert.Equal("idea.deleted", globalDeliveries[0].GetProperty("eventType").GetString());

        Assert.Single(projectDeliveries);
        Assert.Equal("idea.deleted", projectDeliveries[0].GetProperty("eventType").GetString());

        Assert.Single(globalEndpoint.Requests);
        Assert.Single(projectEndpoint.Requests);
    }

    /// <summary>
    /// 项目从回收站恢复后，它自己的 webhook **重新开始收**这个项目的事件。
    /// </summary>
    /// <remarks>
    /// 项目级 webhook 随项目隐式失效，<b>项目恢复后自动恢复投递</b>。
    /// 上一条守的是「回收站期间不投递」，这条守的是「恢复后要能恢复投递」：
    /// 两头都守住，这条规则才不是「把 webhook 永久关掉」。
    /// <para>
    /// 删除阶段收到的那 1 条 <c>project.deleted</c> 是规则里的例外（照投），
    /// 所以这里断言的是「一共 2 条：project.deleted + project.updated」。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Restored_project_webhook_receives_its_project_events_again()
    {
        using var endpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        // 订阅两种事件：删除时收到 project.deleted（规则的例外，照投），
        // 恢复时收到 project.updated（恢复后自动恢复投递）。
        var webhookId = await CreateWebhookAsync(
            client, endpoint.Url, ["project.deleted", "project.updated"],
            scope: "project", projectId: projectId);

        var deleted = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 删除阶段：只有 project.deleted 那 1 条（连带的 idea.deleted 被规则挡掉）。
        var afterDelete = await WaitForSettledDeliveriesAsync(client, webhookId, 1);
        Assert.Single(afterDelete);
        Assert.Equal("project.deleted", afterDelete[0].GetProperty("eventType").GetString());

        // 恢复项目 → 发 project.updated（changed = ["deletedAt"]）。
        var restored = await client.PostAsync($"/api/projects/{projectId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        await WaitForDeliveryAsync(client, webhookId, IsDelivered);

        var afterRestore = await WaitForSettledDeliveriesAsync(client, webhookId, 2);
        Assert.Equal(2, afterRestore.Count);

        var types = afterRestore
            .Select(delivery => delivery.GetProperty("eventType").GetString())
            .ToArray();
        Assert.Contains("project.deleted", types);
        Assert.Contains("project.updated", types);

        // 恢复后那条是 project.updated。
        var last = endpoint.Requests[^1];
        using var payload = JsonDocument.Parse(last.Body);
        Assert.Equal("project.updated ", payload.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 项目还在回收站时，单独恢复它下面的某条想法 → 那条 <c>idea.restored</c>
    /// <b>哪个 webhook 都收不到</b>。
    /// </summary>
    /// <remarks>
    /// 这是「所属项目在回收站期间不投递」这条规则的**连带效果**，不是另一个规则：
    /// 想法事件带的是所属项目，那个项目在回收站里，所以整条事件被跳过：
    /// 全局与项目级一视同仁。
    /// 断言写在这里，是为了让这个效果是「有意为之」而不是「碰巧」。
    /// </remarks>
    [Fact]
    public async Task Idea_restored_while_its_project_is_in_trash_reaches_no_webhook()
    {
        using var projectEndpoint = new FakeEndpoint();
        using var globalEndpoint = new FakeEndpoint();
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "项目A");

        var projectWebhookId = await CreateWebhookAsync(
            client, projectEndpoint.Url, ["idea.restored"], scope: "project", projectId: projectId);

        var globalWebhookId = await CreateWebhookAsync(
            client, globalEndpoint.Url, ["idea.restored"]);

        var created = await client.PostAsJsonAsync(
            "/api/ideas", new { content = "会被连带删掉的想法", projectId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ideaId = await ReadIdAsync(created);

        // 删项目：想法被连带软删（同一个 deleted_at）。
        var deleted = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 单独恢复这条想法：项目仍在回收站里。
        var restored = await client.PostAsync($"/api/ideas/{ideaId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        // 恢复确实生效了（想法回到活跃列表）：免得断言「没收到」是因为根本没恢复。
        var active = await client.GetAsync($"/api/ideas?projectId={projectId}");
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        using (var activeDocument = JsonDocument.Parse(await active.Content.ReadAsStringAsync()))
        {
            Assert.Contains(
                activeDocument.RootElement.EnumerateArray(),
                element => element.GetProperty("id").GetString() == ideaId);
        }

        // 所属项目还在回收站里 → 整条事件被跳过，两个作用域都收不到。
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Empty(projectEndpoint.Requests);
        Assert.Empty(globalEndpoint.Requests);
        Assert.Empty(await ReadDeliveriesAsync(client, projectWebhookId));
        Assert.Empty(await ReadDeliveriesAsync(client, globalWebhookId));
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

    /// <summary>从一个响应里读出资源的 id。</summary>
    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
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

    /// <summary>
    /// 等到某个 webhook 的投递记录**至少**有指定条数为止。
    /// </summary>
    /// <remarks>
    /// 用来当「事件确实走完了投递链路」的证据：
    /// 等对照组（全局 webhook）收齐了 N 条，才轮到断言「项目级那条一条都没有」。
    /// </remarks>
    private static async Task WaitForDeliveryCountAsync(
        HttpClient client,
        string webhookId,
        int count,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ReadDeliveriesAsync(client, webhookId)).Count >= count)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"等 webhook {webhookId} 的 {count} 条投递记录超时");
    }

    /// <summary>
    /// 等到某个 webhook 的投递记录**至少**有指定条数，再等一小段时间让它稳定下来，
    /// 然后把当前的投递记录返回。
    /// </summary>
    /// <remarks>
    /// 「等稳定」这一步是断言「**只有** N 条」的前提：
    /// 投递是异步的，刚看到第 1 条时后面几条可能还在路上，
    /// 只等「第 1 条出现」就断言「只有 1 条」会把还没到的投递算漏。
    /// </remarks>
    private static async Task<List<JsonElement>> WaitForSettledDeliveriesAsync(
        HttpClient client,
        string webhookId,
        int count,
        TimeSpan? timeout = null,
        TimeSpan? settle = null)
    {
        await WaitForDeliveryCountAsync(client, webhookId, count, timeout);

        await Task.Delay(settle ?? TimeSpan.FromSeconds(2));

        return await ReadDeliveriesAsync(client, webhookId);
    }

    /// <summary>独立重算一遍 HMAC-SHA256 签名（不调用后端的实现）。</summary>
    private static string ExpectedSignature(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>直接往应用的库里插一行重复的 (webhook, 事件)（用来撞唯一索引）。</summary>
    private static void InsertDuplicateDelivery(string databasePath, string webhookId, string eventId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO webhook_delivery(id, webhook_id, event_id, event_type, payload, status, " +
            "attempts, last_error, created_at, updated_at) " +
            "VALUES ('duplicate-delivery-row', $webhookId, $eventId, 'idea.created', '{}', 'pending', " +
            "0, NULL, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');";
        command.Parameters.AddWithValue("$webhookId", webhookId);
        command.Parameters.AddWithValue("$eventId", eventId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 把投递队列塞满，让下一次入队必然失败。
    /// </summary>
    /// <remarks>
    /// 一直塞到塞不进去为止（队列容量是 <see cref="WebhookDeliveryWorker.QueueCapacity"/>）。
    /// 目标端点把请求全卡住 → worker 的 8 个并发位全被占住 → 队列不会再被消费，
    /// 「满」这个状态是稳的。
    /// </remarks>
    private static void FillDeliveryQueue(WebhookDeliveryWorker worker, Webhook webhook)
    {
        while (worker.Enqueue(NewFillerJob(webhook)))
        {
            // 一直塞到塞不进去为止。
        }
    }

    /// <summary>造一个「占位」的投递任务（只用来把队列塞满）。</summary>
    private static WebhookDeliveryJob NewFillerJob(Webhook webhook)
    {
        var evt = new IdeaEvent(
            Id: Guid.NewGuid().ToString("N"),
            Type: "idea.updated",
            Timestamp: DateTimeOffset.UtcNow,
            Project: null,
            Idea: null,
            Changed: null,
            MergedFrom: null);

        return new WebhookDeliveryJob(webhook, evt, "{}");
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

        /// <summary>
        /// 每次请求返回的状态码（默认 200）。
        /// </summary>
        /// <param name="requestIndex">这是第几个请求（从 1 开始）。</param>
        /// <remarks>
        /// 带上序号才能写出「第 1 次失败、第 2 次成功」这种按次变化的响应，
        /// 也才能把「投递在途」与「重投」这两条路径区分开。
        /// </remarks>
        public Func<int, int> StatusCodeProvider { get; set; } = _ => 200;

        /// <summary>
        /// 收到请求后、回应之前要跑的钩子（参数是第几个请求，从 1 开始）。
        /// </summary>
        /// <remarks>
        /// 用来在测试里**卡住**某一次请求，逼出并发时序
        /// （比如「投递还在途，用户就点了重投」）。
        /// </remarks>
        public Func<int, Task>? OnRequestAsync { get; set; }

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

                // 每个请求单独一个任务：真实的 HTTP 服务端本来就是并发的。
                // 串行处理的话，「上一次请求还在途」时新来的请求会被堵在后面 ——
                // 而「投递在途时用户点了重投」这个场景正需要它们同时在场。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleAsync(context);
                    }
                    catch (Exception)
                    {
                        // 客户端提前断开之类：忽略这一条，继续接下一条。
                    }
                });
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
                headers,
                DateTimeOffset.UtcNow);

            int index;
            lock (_gate)
            {
                _requests.Add(request);
                index = _requests.Count;
            }

            // 钩子先跑：测试要在这里卡住某一次请求，逼出并发时序。
            var hook = OnRequestAsync;
            if (hook is not null)
            {
                await hook(index);
            }

            if (ResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(ResponseDelay);
            }

            var bytes = Encoding.UTF8.GetBytes("{}");
            context.Response.StatusCode = StatusCodeProvider(index);
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
        Dictionary<string, string> Headers,
        DateTimeOffset ReceivedAt)
    {
        /// <summary>取一个请求头（大小写不敏感）。</summary>
        public string? Header(string name) =>
            Headers.TryGetValue(name, out var value) ? value : null;
    }
}
