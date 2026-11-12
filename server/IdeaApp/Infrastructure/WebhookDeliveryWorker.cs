using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using IdeaApp.Domain;
using Microsoft.Extensions.Options;

namespace IdeaApp.Infrastructure;

/// <summary>
/// webhook 投递的后台消费者：把队列里的任务真的 POST 出去，失败重试，超限记死信。
///
/// <para>
/// <b>投递完全异步，绝不阻塞写路径</b>：事件 → 队列（容量 1000）→ 本 worker 消费 → HTTP POST。
/// 队列满了就**丢掉新任务并记日志**（<see cref="Enqueue"/> 返回 false），
/// 写接口不会因为 webhook 目标挂掉而卡住。
/// </para>
///
/// <para>
/// <b>投递不产生事件</b>：投递失败 → 发事件 → 触发投递 → 再失败……那是死循环。
/// 所以本 worker 不持有事件总线，也不发布任何事件。
/// </para>
///
/// <para>
/// 重试退避是 1 秒 / 5 秒 / 30 秒；总尝试次数是 <c>Webhook:MaxAttempts</c>（默认 4，<b>含首次</b>），
/// 也就是「首次 + 3 次重试」—— 三段退避正好一一对应。
/// </para>
/// </summary>
public sealed class WebhookDeliveryWorker(
    WebhookRepository webhooks,
    DeliveryRepository deliveries,
    IHttpClientFactory httpClientFactory,
    IOptions<AppOptions> options,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    /// <summary>投递队列的容量。满了就丢新任务（写路径不能等）。</summary>
    /// <remarks>公开出来是为了让测试能算出「塞多少任务才满得下」。</remarks>
    public const int QueueCapacity = 1000;

    /// <summary>同时投递的最大条数。</summary>
    private const int MaxParallelism = 8;

    /// <summary>签名头：配了密钥就有。</summary>
    public const string SignatureHeader = "X-Idea-Signature";

    /// <summary>
    /// 重试之间的退避：第 1 次重试等 1 秒，第 2 次等 5 秒，第 3 次等 30 秒。
    /// </summary>
    /// <remarks>
    /// 表长 3 段，正好对应默认的 <c>MaxAttempts = 4</c>（含首次 → 首次 + 3 次重试）：
    /// 默认值下三段退避都用得上。段数更多时按最后一段兜底。
    /// </remarks>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30)
    ];

    private readonly Channel<WebhookDeliveryJob> _queue = Channel.CreateBounded<WebhookDeliveryJob>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    /// <summary>是否已经释放过（容器会释放两次：hosted service 与单例各一次）。</summary>
    private int _disposed;

    /// <summary>
    /// 把一次投递放进队列。
    /// </summary>
    /// <returns>
    /// true = 已入队；false = 队列满了，这次投递被**丢弃**（已记日志）。
    /// </returns>
    /// <remarks>
    /// <c>TryWrite</c> 不管队列满不满都**不会阻塞调用方**：
    /// 满了就返回 false，写路径宁可丢一次投递，也不能被慢目标拖住。
    /// </remarks>
    public bool Enqueue(WebhookDeliveryJob job)
    {
        if (_queue.Writer.TryWrite(job))
        {
            return true;
        }

        logger.LogWarning(
            "投递队列已满，丢弃这次投递（webhook {WebhookId}，事件 {EventId}）",
            job.Webhook.Id,
            job.Event.Id);

        return false;
    }

    /// <summary>
    /// 消费队列：把每个任务真的投递出去（失败按退避重试）。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                _queue.Reader.ReadAllAsync(stoppingToken),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelism,
                    CancellationToken = stoppingToken
                },
                async (job, ct) => await DeliverAsyncSafelyAsync(job, ct));
        }
        catch (OperationCanceledException)
        {
            // 应用要停了：正常收工。
        }
    }

    /// <summary>
    /// 投递一次，但**不让异常逃出去**。
    /// </summary>
    /// <remarks>
    /// 一个 job 失败（库写不进去、配置被删了……）不能把整个消费循环带崩 ——
    /// 循环一死，后面的投递就再也不会发生；异常一路冒到宿主更是会让整个应用停掉。
    /// </remarks>
    private async Task DeliverAsyncSafelyAsync(WebhookDeliveryJob job, CancellationToken ct)
    {
        try
        {
            await DeliverAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用在收工：正常退出。
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "webhook {WebhookId} 的这次投递出错（事件 {EventId}）",
                job.Webhook.Id,
                job.Event.Id);
        }
    }

    /// <summary>
    /// 投递一次：取（或记）投递记录 → 尝试 POST → 成功记 delivered，
    /// 失败按退避重试到上限 → 仍失败记 failed 与 last_error。
    /// </summary>
    /// <remarks>
    /// 公开出来是为了能直接调用它做测试（不必先经过队列）。
    /// <para>
    /// <b>投递记录只往里「加」，不整行覆盖</b>：计数与状态跃迁都在
    /// <see cref="DeliveryRepository"/> 那边原子完成。
    /// 这一行同时可能被用户的手动重投改着 —— 拿内存里的旧快照整行写回去，
    /// 会把旁边那次写入抹掉（尝试次数少记、已经成功的投递被改回失败）。
    /// </para>
    /// </remarks>
    public async ValueTask DeliverAsync(WebhookDeliveryJob job, CancellationToken ct)
    {
        // 投递前重新取一次配置：排队期间改过的 URL / 密钥立刻生效。
        // 配置没了（webhook 被删）就跳过 —— 投递记录也跟着外键级联删了，
        // 这时再插记录会撞外键约束。
        var webhook = webhooks.Get(job.Webhook.Id);
        if (webhook is null)
        {
            logger.LogWarning(
                "webhook {WebhookId} 已不存在，丢弃这次投递（事件 {EventId}）",
                job.Webhook.Id,
                job.Event.Id);

            return;
        }

        var record = deliveries.Insert(job);

        // 每个 webhook 只留最近若干条记录，多余的删最旧的。
        deliveries.Trim(job.Webhook.Id, Math.Max(1, options.Value.Webhook.MaxDeliveryRecords));

        var maxAttempts = Math.Max(1, options.Value.Webhook.MaxAttempts);

        // 最近一次尝试的错误（记日志用）。库里那一行的 last_error 由仓储管。
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var error = await PostAsync(webhook, job.Payload, ct);
            lastError = error;

            // 记这一次尝试：attempts 加 1 与状态跃迁在 SQL 侧原子完成。
            // 成功 → 这一行还是 pending / failed 就落 delivered；
            // 失败 → 记下错误、留在 pending（等下一段退避）。
            deliveries.RecordAttempt(record.Id, error is null, error);

            if (error is null)
            {
                return;
            }

            if (attempt < maxAttempts)
            {
                // 退避：第 1 次重试等 1 秒，第 2 次等 5 秒，第 3 次等 30 秒。
                await Task.Delay(Backoff[Math.Min(attempt - 1, Backoff.Length - 1)], ct);
            }
        }

        // 重试到上限仍然失败：记死信。
        // 只在这一行还是「投递中」时才改 —— 旁边那次手动重投可能已经成功了，
        // 后到的这次失败不能把它改判成失败。
        deliveries.MarkFailed(record.Id);

        logger.LogWarning(
            "webhook {WebhookId} 投递失败（事件 {EventId}）：{Error}",
            webhook.Id,
            job.Event.Id,
            lastError);
    }

    /// <summary>
    /// 发一次 HTTP 请求。返回 <c>null</c> 表示成功，否则是错误原因。
    /// </summary>
    private async Task<string?> PostAsync(Webhook webhook, string payload, CancellationToken ct)
    {
        var timeoutSeconds = Math.Max(1, options.Value.Webhook.TimeoutSeconds);

        // 工厂给的客户端可以随手释放：底层处理器由工厂池化，不会被一起关掉。
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);


        try
        {
            // Content-Type 就是 "application/json"，**不带 charset 参数**：
            // JSON 的字符集隐含就是 UTF-8（RFC 8259），带上 charset 反而会让
            // 一些严格的接收端不认这个 Content-Type。
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var request = new HttpRequestMessage(new HttpMethod(webhook.Method), webhook.Url)
            {
                Content = content
            };

            foreach (var (name, value) in webhook.Headers)
            {
                AddHeader(request, content, name, value);
            }

            // 签名用**当前**密钥算：接收方拿同一个密钥重算一遍就能确认请求没被改过。
            if (!string.IsNullOrEmpty(webhook.Secret))
            {
                request.Headers.Remove(SignatureHeader);
                request.Headers.TryAddWithoutValidation(
                    SignatureHeader, HmacSigner.Sign(webhook.Secret, payload));
            }

            using var response = await client.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 应用在收工：让异常上去，整个投递随之中止。
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"请求超时（超过 {timeoutSeconds} 秒）";
        }
        catch (Exception ex)
        {
            return Sanitize(ex.Message, webhook.Url);
        }
    }

    /// <summary>
    /// 加一个自定义请求头。
    /// </summary>
    /// <remarks>
    /// 内容头（如 <c>Content-Type</c>）必须加在内容上，加在请求头上会被忽略。
    /// 头名非法时只记日志（**不记头值** —— 头里可能有凭据）。
    /// </remarks>
    private void AddHeader(HttpRequestMessage request, HttpContent content, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", value);
            return;
        }

        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            if (!content.Headers.TryAddWithoutValidation(name, value))
            {
                logger.LogWarning("webhook 的请求头名非法，已忽略：{HeaderName}", name);
            }
        }
    }

    /// <summary>
    /// 把错误消息里的 webhook URL 抹掉。
    /// </summary>
    /// <remarks>
    /// Telegram 的 bot token 就在 URL 路径里，而错误消息会进投递记录、也会进日志 ——
    /// 不能让它从那里漏出去。
    /// </remarks>
    private static string Sanitize(string message, string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return message;
        }

        return message.Replace(url, "[webhook url]", StringComparison.Ordinal);
    }

    /// <summary>
    /// 释放：只放行第一次。
    /// </summary>
    /// <remarks>
    /// 同一个实例会被容器释放两次（hosted service 的注册与单例的注册各一次），
    /// 重复释放会碰到已经释放的 token 源。
    /// </remarks>
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose();
    }
}
