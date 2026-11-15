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
/// 队列满了就**丢掉新任务**（<see cref="Enqueue"/> 返回 false），
/// 但**要留下痕迹**：给这次投递记一条 failed 的投递记录，
/// 用户在页面的投递记录里看得到这次事件没投出去。
/// 写接口不会因为 webhook 目标挂掉而卡住。
/// </para>
///
/// <para>
/// <b>退避不占并发位</b>：重试的退避（1 秒 / 5 秒 / 30 秒）在**消费槽之外**等待。
/// 退避期间占着槽的话，一批指向不可达地址的 webhook（退避合计约 36 秒）
/// 就能把 8 个并发位全占住 ——
/// 后面那个指向正常地址的 webhook 会被饿死（实测 90 秒都没落地）。
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
    /// 队列满了时写进投递记录的错误原因。
    /// </summary>
    /// <remarks>
    /// 公开出来是为了让测试断言**页面上的那句话**，
    /// 而不是断言某个实现细节（少写一句「队列已满」，用户就看不出这次事件没投出去）。
    /// </remarks>
    public const string QueueFullError = "投递队列已满，这次投递被丢弃（队列容量 1000）";

    /// <summary>
    /// 重试之间的退避：第 1 次重试等 1 秒，第 2 次等 5 秒，第 3 次等 30 秒。
    /// </summary>
    /// <remarks>
    /// 表长 3 段，正好对应默认的 <c>MaxAttempts = 4</c>（含首次 → 首次 + 3 次重试）：
    /// 默认值下三段退避都用得上。段数更多时按最后一段兜底。
    /// </remarks>
    private static readonly TimeSpan[] DefaultBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30)
    ];

    /// <summary>
    /// 重试之间的退避表：第 1 次重试等 <c>Backoff[0]</c>，第 2 次等 <c>Backoff[1]</c>……
    /// </summary>
    /// <remarks>
    /// <para>
    /// **可改是为了能测**：默认的 1s / 5s / 30s 对测试来说太长
    /// （要么等 36 秒，要么分不清「退避占了槽」和「投递本来就慢」）。
    /// 生产的代码不改它。
    /// </para>
    /// <para>
    /// 读的时候每次都重新取这个属性（不是构造时拷一份），
    /// 这样测试在 worker 跑起来之后改它也来得及。
    /// </para>
    /// </remarks>
    public IReadOnlyList<TimeSpan> Backoff { get; set; } = DefaultBackoff;

    private readonly Channel<QueuedDelivery> _queue = Channel.CreateBounded<QueuedDelivery>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    /// <summary>是否已经释放过（容器会释放两次：hosted service 与单例各一次）。</summary>
    private int _disposed;

    /// <summary>
    /// 把一次**新投递**放进队列。
    /// </summary>
    /// <returns>
    /// true = 已入队；false = 队列满了，这次投递**没排上**。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <c>TryWrite</c> 不管队列满不满都**不会阻塞调用方**：
    /// 满了就返回 false，写路径宁可丢一次投递，也不能被慢目标拖住。
    /// </para>
    /// <para>
    /// <b>丢了也要有痕迹</b>：队列满时给这次投递记一条 failed 的投递记录
    /// （<see cref="QueueFullError"/>）——
    /// 否则用户在页面上看到的仍然是「还没有投递记录。」，
    /// 完全看不出这次事件压根没投出去。
    /// </para>
    /// <para>
    /// 这一步是**本地库的一次写**（毫秒级），而且跑在事件总线的消费线程上，
    /// 不在写接口的线程上 —— 写路径不会被目标端点拖住。
    /// </para>
    /// </remarks>
    public bool Enqueue(WebhookDeliveryJob job)
    {
        if (_queue.Writer.TryWrite(new QueuedDelivery(job, Attempt: 1)))
        {
            return true;
        }

        logger.LogWarning(
            "投递队列已满，丢弃这次投递（webhook {WebhookId}，事件 {EventId}）",
            job.Webhook.Id,
            job.Event.Id);

        RecordQueueOverflow(job);

        return false;
    }

    /// <summary>
    /// 把一次**手动重投**放进队列。
    /// </summary>
    /// <returns>
    /// true = 已入队；false = 队列满了，这次重投没排上。
    /// </returns>
    /// <remarks>
    /// 与 <see cref="Enqueue"/> 的区别只有一处：<b>队列满时那一行原封不动</b>。
    /// 手动重投的那一行**已经存在**了，它上面有上次的失败原因 ——
    /// 把它改成「队列已满」等于把上次失败的原因抹掉，
    /// 用户看到的是一条没有理由的 failed。
    /// 所以这里只记日志，不动那一行。
    /// </remarks>
    public bool EnqueueRedelivery(WebhookDeliveryJob job)
    {
        if (_queue.Writer.TryWrite(new QueuedDelivery(job, Attempt: 1)))
        {
            return true;
        }

        logger.LogWarning(
            "投递队列已满，这次手动重投没排上（webhook {WebhookId}，事件 {EventId}）",
            job.Webhook.Id,
            job.Event.Id);

        return false;
    }

    /// <summary>
    /// 队列满时给这次投递留一条 failed 的记录。
    /// </summary>
    /// <remarks>
    /// 记失败这一条本身也可能失败（库写不进去）——
    /// 那也不能把调用方带崩：这里吞掉异常，只记日志。
    /// </remarks>
    private void RecordQueueOverflow(WebhookDeliveryJob job)
    {
        try
        {
            deliveries.InsertFailed(job, QueueFullError);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "记录「投递队列已满」这条投递记录时出错（webhook {WebhookId}，事件 {EventId}）",
                job.Webhook.Id,
                job.Event.Id);
        }
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
                async (item, ct) => await DeliverAsyncSafelyAsync(item, ct));
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
    private async Task DeliverAsyncSafelyAsync(QueuedDelivery item, CancellationToken ct)
    {
        try
        {
            await DeliverAttemptAsync(item, ct);
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
                item.Job.Webhook.Id,
                item.Job.Event.Id);
        }
    }

    /// <summary>
    /// 投递**一次**（只发一次 HTTP）：成功就收工，
    /// 失败还有次数就把「过一会儿再投」交给延时任务，没次数了就记死信。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>投递记录只往里「加」，不整行覆盖</b>：计数与状态跃迁都在
    /// <see cref="DeliveryRepository"/> 那边原子完成。
    /// 这一行同时可能被用户的手动重投改着 —— 拿内存里的旧快照整行写回去，
    /// 会把旁边那次写入抹掉（尝试次数少记、已经成功的投递被改回失败）。
    /// </para>
    /// <para>
    /// <b>这里只发一次请求</b>：退避在消费槽之外等（见 <see cref="ScheduleRetry"/>），
    /// 消费槽不能被退避占着。
    /// </para>
    /// </remarks>
    private async Task DeliverAttemptAsync(QueuedDelivery item, CancellationToken ct)
    {
        var job = item.Job;

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
        // 裁剪只在**首次尝试**时做一次：重试不再裁（那一行已经是最新的一条了）。
        if (item.Attempt == 1)
        {
            deliveries.Trim(job.Webhook.Id, Math.Max(1, options.Value.Webhook.MaxDeliveryRecords));
        }

        var maxAttempts = Math.Max(1, options.Value.Webhook.MaxAttempts);

        var error = await PostAsync(webhook, job.Payload, ct);

        // 记这一次尝试：attempts 加 1 与状态跃迁在 SQL 侧原子完成。
        // 成功 → 这一行还是 pending / failed 就落 delivered；
        // 失败 → 记下错误、留在 pending（等下一段退避）。
        deliveries.RecordAttempt(record.Id, error is null, error);

        if (error is null)
        {
            return;
        }

        if (item.Attempt < maxAttempts)
        {
            // 还有重试次数：**不在这里等**退避。
            // 等退避的延时任务独立于消费循环 —— 消费槽立刻释放，
            // 后面来的投递不用排队等一个指向不可达地址的 webhook 退完避。
            //
            // 重投的那一项尝试次数 +1：这条链走到 maxAttempts 就收手（记死信）。
            ScheduleRetry(item with { Attempt = item.Attempt + 1 }, BackoffDelay(item.Attempt), ct);
            return;
        }

        // 重试到上限仍然失败：记死信。
        // 只在这一行还是「投递中」时才改 —— 旁边那次手动重投可能已经成功了，
        // 后到的这次失败不能把它改判成失败。
        deliveries.MarkFailed(record.Id);

        logger.LogWarning(
            "webhook {WebhookId} 投递失败（事件 {EventId}）：{Error}",
            webhook.Id,
            job.Event.Id,
            error);
    }

    /// <summary>
    /// 这次失败之后要等多久再重投。
    /// </summary>
    /// <param name="attempt">刚做完的是第几次尝试（从 1 开始）。</param>
    private TimeSpan BackoffDelay(int attempt)
    {
        var backoff = Backoff;

        return backoff.Count == 0
            ? TimeSpan.Zero
            : backoff[Math.Min(attempt - 1, backoff.Count - 1)];
    }

    /// <summary>
    /// 把「过一会儿再投」交给一个**独立于消费循环**的延时任务。
    /// </summary>
    /// <param name="item">要重投的任务。</param>
    /// <param name="delay">等多久。</param>
    /// <param name="ct">停机信号。</param>
    /// <remarks>
    /// <para>
    /// 为什么不在消费槽里 await 退避：退避最长 30 秒（三段加起来约 36 秒），
    /// 占着槽的话，一批指向不可达地址的 webhook 就能把 8 个并发位全占住 ——
    /// 后面那个指向正常地址的 webhook 会被**饿死**（实测 90 秒都没落地，
    /// 清掉那批失败 webhook 后 5 秒内就 delivered 了）。
    /// </para>
    /// <para>
    /// 延时任务到点后把任务**塞回队列**（<c>WriteAsync</c>，不是 <c>TryWrite</c>）：
    /// 重试是已经在途的工作，不能因为队列满了就把它丢掉 ——
    /// 丢掉的话那一行会永远停在「投递中」，用户看不出它到底怎么了。
    /// 这里阻塞的是**后台的延时任务**，不是写路径（写路径那边才是「宁可丢也不拖住」）。
    /// </para>
    /// </remarks>
    private void ScheduleRetry(QueuedDelivery item, TimeSpan delay, CancellationToken ct)
    {
        _ = RetryLaterAsync(item, delay, ct);
    }

    /// <summary>
    /// 延时任务：等够时间，把任务塞回队列。
    /// </summary>
    /// <remarks>
    /// 这是个「发射后不管」的任务：它的异常自己吞掉 ——
    /// 没人 await 它，异常逃出去只会变成没人看见的未观察异常。
    /// </remarks>
    private async Task RetryLaterAsync(QueuedDelivery item, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            await _queue.Writer.WriteAsync(item, ct);
        }
        catch (OperationCanceledException)
        {
            // 应用在收工：这次重投就不投了。
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "webhook {WebhookId} 的重试没能重新入队（事件 {EventId}）",
                item.Job.Webhook.Id,
                item.Job.Event.Id);
        }
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

    /// <summary>
    /// 队列里的一项：一次投递 + 它**已经试过几次**。
    /// </summary>
    /// <param name="Job">要投的东西（目标 webhook + 事件 + 渲染好的请求体）。</param>
    /// <param name="Attempt">
    /// 这是这条链上的第几次尝试（从 1 开始）。
    /// 它是**这条链**的计数，不是记录行上的总尝试次数：
    /// 手动重投是**新的一条链**（从 1 开始），但记录行上的 attempts 继续累加。
    /// </param>
    private sealed record QueuedDelivery(WebhookDeliveryJob Job, int Attempt);
}
