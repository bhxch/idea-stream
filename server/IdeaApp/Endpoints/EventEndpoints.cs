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
            async (HttpContext context, SseBroadcaster broadcaster, ILoggerFactory loggerFactory) =>
            {
                // 端点类是静态类，不能当日志的泛型参数：用分类名建日志器。
                var logger = loggerFactory.CreateLogger("IdeaApp.Endpoints.EventEndpoints");

                try
                {
                    await StreamAsync(context, broadcaster);
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
            });

        return app;
    }

    /// <summary>推流：补发漏掉的事件，然后一直推新事件（外加心跳）。</summary>
    private static async Task StreamAsync(HttpContext context, SseBroadcaster broadcaster)
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

            var lastSentId = await WriteReplayAsync(context, broadcaster, ct);
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
        CancellationToken ct)
    {
        var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();

        if (string.IsNullOrEmpty(lastEventId))
        {
            // 客户端没带 Last-Event-ID：它是自己刚打开页面的，没有漏掉的东西。
            return null;
        }

        var missed = broadcaster.Since(lastEventId);

        if (missed is null)
        {
            // 缓冲里没有这个 id（太旧，被挤出去了；或者服务端重启过）：
            // 补不了，只能让客户端重新拉完整列表。
            //
            // resync 是**控制帧**，不是业务事件：它没有 id，客户端不能拿它继续补发。
            await WriteRawAsync(context, "event: resync\ndata: {}\n\n", ct);
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

            while (channel.Reader.TryRead(out var evt))
            {
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
