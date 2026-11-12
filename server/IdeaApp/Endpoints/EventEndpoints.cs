using System.Text;
using System.Text.Json;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace IdeaApp.Endpoints;

/// <summary>
/// SSE 端点：<c>GET /api/events</c>。
///
/// <para>
/// 恒定全量推送，**不接受任何过滤参数** —— 每台设备的筛选偏好存在各自的
/// <c>localStorage</c> 里，服务端无从得知，也不该替客户端猜（spec §10.2）。
/// </para>
/// <para>
/// 唯一的入参是「续发位置」：<c>Last-Event-ID</c> 请求头优先，
/// <c>?lastEventId=</c> 查询参数兜底（原生 <c>EventSource</c> 不能自定义请求头，
/// 浏览器端重建连接时只能把 id 放进查询参数）。
/// </para>
/// <para>
/// 帧格式：
/// <code>
/// id: &lt;事件 id&gt;
/// event: &lt;事件类型&gt;
/// data: &lt;单行 JSON&gt;
///
/// </code>
/// 空闲时每隔心跳间隔发一行 <c>: ping</c> 注释，防止中间代理掐断连接。
/// </para>
/// </summary>
public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events",
            async (HttpContext context, string? lastEventId, SseBroadcaster broadcaster, ILoggerFactory loggerFactory) =>
            {
                // 端点类是静态类，不能当日志的泛型参数：用分类名建日志器。
                var logger = loggerFactory.CreateLogger("IdeaApp.Endpoints.EventEndpoints");

                try
                {
                    await StreamAsync(context, broadcaster, lastEventId);
                }
                catch (OperationCanceledException)
                {
                    // 客户端断开或应用关闭：正常收工，不是错误。
                    logger.LogDebug("SSE 连接结束（客户端断开或应用关闭）");
                }
                catch (IOException ex)
                {
                    // 往已经断开的连接上写：正常现象。
                    logger.LogDebug(ex, "SSE 写入失败（客户端已断开）");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SSE 事件流异常结束");

                    if (!context.Response.HasStarted)
                    {
                        // 响应还没开始：还能按规范返回 ProblemDetails + code。
                        var result = ApiResults.ProblemResponse(
                            StatusCodes.Status500InternalServerError,
                            "服务器内部错误",
                            "内部错误，请查看服务端日志",
                            "internal_error");

                        await result.ExecuteAsync(context);
                    }
                }
            })
            // 响应不是 JSON，是 SSE 帧流：内容类型写 text/event-stream。
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream")
            // 响应已经开始之后就没法再改成 ProblemDetails 了 —— 只有「还没开始」
            // 的那一次失败会走 ProblemDetails 分支，所以错误响应只在这里成立。
            .ProducesApiProblem(StatusCodes.Status500InternalServerError, "internal_error");

        return app;
    }

    /// <summary>推流：补发漏掉的事件，然后一直推新事件（外加心跳）。</summary>
    private static async Task StreamAsync(
        HttpContext context,
        SseBroadcaster broadcaster,
        string? queryLastEventId)
    {
        var response = context.Response;
        var ct = context.RequestAborted;

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";

        // 不缓存：事件流是实时的，缓存住就等于没有推送。
        response.Headers.CacheControl = "no-cache";

        // nginx 侧也要关缓冲（双保险）：应用侧设了这个头，nginx 就不再攒着不发。
        response.Headers["X-Accel-Buffering"] = "no";

        // 先订阅再发响应头：客户端一看到响应头就可能马上写东西，
        // 订阅必须已经在那儿，否则那一下写产生的事件会漏掉。
        var subscription = broadcaster.Subscribe();

        try
        {
            // 立刻把响应头发出去：客户端据此知道连上了，
            // 中间代理也不会把「还没出帧」的连接当成超时。
            //
            // StartAsync 之后**还要空冲刷一次**：有些宿主（比如集成测试用的
            // TestHost）不把空冲刷当成「头已经发出」，
            // 客户端要等到第一帧数据才拿到响应头 —— 那样客户端就没法在
            // 连上之后立刻发起写操作了。
            await response.StartAsync(ct);
            await response.Body.FlushAsync(ct);

            var lastSentId = await WriteReplayAsync(context, broadcaster, queryLastEventId, ct);
            await PumpAsync(context, broadcaster, subscription, lastSentId, ct);
        }
        finally
        {
            // 客户端断开时也要摘掉订阅：否则订阅列表会越积越多。
            broadcaster.TryCompleteSubscription(subscription);
        }
    }

    /// <summary>
    /// 补发客户端漏掉的事件。返回最后发出的事件 id（用来去重）。
    /// </summary>
    private static async Task<string?> WriteReplayAsync(
        HttpContext context,
        SseBroadcaster broadcaster,
        string? queryLastEventId,
        CancellationToken ct)
    {
        var lastEventId = ResolveLastEventId(context, queryLastEventId);

        if (string.IsNullOrEmpty(lastEventId))
        {
            // 客户端没带续发位置：它是自己刚打开页面的，没有漏掉的东西。
            return null;
        }

        var missed = broadcaster.Since(lastEventId);

        if (missed is null)
        {
            // 缓冲里没有这个 id（太旧，被挤出去了；或者服务端重启过）：
            // 补不了，只能让客户端重新拉完整列表。
            //
            // resync 是**控制帧**，不是业务事件：它没有 id，客户端不能拿它继续补发。
            await WriteResyncAsync(context, ct);
            return null;
        }

        string? lastSentId = null;

        foreach (var evt in missed)
        {
            await WriteEventAsync(context, evt, ct);
            lastSentId = evt.Id;
        }

        return lastSentId;
    }

    /// <summary>
    /// 解析「客户端最后收到的事件 id」：<c>Last-Event-ID</c> 请求头优先，
    /// <c>?lastEventId=</c> 查询参数兜底。两个都没有就是「没有续发位置」。
    /// </summary>
    /// <remarks>
    /// 为什么要有查询参数这条兜底：浏览器的原生 <c>EventSource</c>
    /// **不能自定义请求头**。它自己重连时会自动带上 <c>Last-Event-ID</c> 头，
    /// 但前端重建连接（例如换了地址、主动重连）时只能把 id 塞进 URL ——
    /// 也就是 <c>?lastEventId=&lt;id&gt;</c>。后端只认头的话，这条路径上的
    /// 续发请求会被当成「没有续发位置」，断线期间的事件就静默丢了
    /// （服务端既不补发，也不发 resync 提醒客户端重拉）。
    /// <para>
    /// 头优先：两个都带时以头为准（它是 SSE 规范里约定的那个）。
    /// 空值按「没带」处理 —— 空的续发位置没有任何信息量，
    /// 不该把一个真的查询参数也一起挡掉。
    /// </para>
    /// </remarks>
    private static string? ResolveLastEventId(HttpContext context, string? queryLastEventId)
    {
        var fromHeader = context.Request.Headers["Last-Event-ID"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            return fromHeader.Trim();
        }

        // 查询参数由框架绑定（参数名 lastEventId），这里拿到的就是 ?lastEventId= 的值。
        if (!string.IsNullOrWhiteSpace(queryLastEventId))
        {
            return queryLastEventId.Trim();
        }

        return null;
    }

    /// <summary>推新事件 + 心跳，直到客户端断开或订阅被关掉。</summary>
    private static async Task PumpAsync(
        HttpContext context,
        SseBroadcaster broadcaster,
        SseSubscription subscription,
        string? lastSentId,
        CancellationToken ct)
    {
        var channel = subscription.Channel;

        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(broadcaster.HeartbeatSeconds));

        // 「等事件」与「等心跳」赛跑：各自只留一个未完成的等待，
        // 下一轮接着用同一个任务（不新开等待，免得越积越多）。
        Task<bool> readable = channel.Reader.WaitToReadAsync(ct).AsTask();
        Task<bool> tick = heartbeat.WaitForNextTickAsync(ct).AsTask();

        while (!ct.IsCancellationRequested)
        {
            var finished = await Task.WhenAny(readable, tick);

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (finished == tick)
            {
                // 心跳：一行注释，浏览器会忽略它，但中间的代理看到流量就不会掐连接。
                await WriteRawAsync(context, ": ping\n\n", ct);
                tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                continue;
            }

            bool hasMore;

            try
            {
                hasMore = await readable;
            }
            catch (OperationCanceledException)
            {
                // 客户端断开或应用关闭。
                break;
            }

            if (!hasMore)
            {
                // 通道被关闭（订阅被摘掉）：收工。
                break;
            }

            while (channel.Reader.TryRead(out var message))
            {
                if (message.IsResync)
                {
                    // 控制帧：这个订阅者漏了事件（它太慢，通道被挤掉了东西）。
                    await WriteResyncAsync(context, ct);
                    continue;
                }

                var evt = message.Event!;

                // 补发与实时推送之间有一个重叠窗口（订阅之后、补发之前到达的事件
                // 既在补发列表里、也在通道里）。事件 id 单调递增，
                // 按 id 去重即可 —— 客户端本来也要按 id 去重。
                if (lastSentId is not null && string.CompareOrdinal(evt.Id, lastSentId) <= 0)
                {
                    continue;
                }

                await WriteEventAsync(context, evt, ct);
                lastSentId = evt.Id;
            }

            readable = channel.Reader.WaitToReadAsync(ct).AsTask();
        }
    }

    /// <summary>
    /// 写一帧 resync 控制帧：告诉客户端「你漏了事件，重新拉完整列表」。
    /// </summary>
    /// <remarks>
    /// 这帧**不带 id 行**：客户端不能拿它当 <c>Last-Event-ID</c> 继续补发。
    /// 载荷是空对象 <c>{}</c> —— 它是控制帧，不是业务事件。
    /// </remarks>
    private static Task WriteResyncAsync(HttpContext context, CancellationToken ct)
    {
        return WriteRawAsync(context, "event: resync\ndata: {}\n\n", ct);
    }

    /// <summary>写一帧事件。</summary>
    private static async Task WriteEventAsync(HttpContext context, IdeaEvent evt, CancellationToken ct)
    {
        // data 必须是**单行** JSON：多行会被浏览器当成多段数据拼起来。
        // 序列化走源生成的上下文（Native AOT 下没有反射式序列化）。
        var json = JsonSerializer.Serialize(evt, IdeaJsonContext.Default.IdeaEvent);

        await WriteRawAsync(context, $"id: {evt.Id}\nevent: {evt.Type}\ndata: {json}\n\n", ct);
    }

    /// <summary>写一段原始帧并立刻冲刷（不冲刷的话数据会攒在缓冲区里）。</summary>
    private static async Task WriteRawAsync(HttpContext context, string frame, CancellationToken ct)
    {
        await context.Response.WriteAsync(frame, Encoding.UTF8, ct);
        await context.Response.Body.FlushAsync(ct);
    }
}
