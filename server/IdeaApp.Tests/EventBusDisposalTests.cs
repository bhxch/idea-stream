using System.Diagnostics;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdeaApp.Tests;

/// <summary>
/// 事件总线的**释放语义**测试：收工信号、排空与排空超时。
///
/// 与总线机制（扇出、不阻塞、异常隔离）分开一个文件：
/// 两组测试关注的是不同的性质，混在一个文件里会互相淹没。
/// </summary>
public sealed class EventBusDisposalTests
{
    /// <summary>
    /// 释放时要把已入队的事件处理完：写进通道的事件不能凭空消失。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_drains_the_events_that_were_already_queued()
    {
        var collector = new CollectingHandler();

        await using var bus = new EventBus(
            new IEventHandler[] { collector },
            NullLogger<EventBus>.Instance);

        const int count = 50;
        var evt = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);
        for (var i = 0; i < count; i++)
        {
            bus.Publish(evt);
        }

        await bus.DisposeAsync();

        Assert.Equal(count, collector.Received.Count);
    }

    /// <summary>
    /// 订阅者不收工（handler 永不返回）时，释放不能把应用关闭卡死：
    /// Task 9 的 SSE 订阅者就阻塞在长连接上。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_gives_up_when_a_handler_never_returns()
    {
        var stuck = new StuckHandler();

        await using var bus = new EventBus(
            new IEventHandler[] { stuck },
            NullLogger<EventBus>.Instance,
            TimeSpan.FromMilliseconds(200));

        bus.Publish(EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null));
        await stuck.Started;

        var stopwatch = Stopwatch.StartNew();
        await bus.DisposeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 5000,
            $"释放用时 {stopwatch.ElapsedMilliseconds}ms，订阅者不收工时也要有上限");

        stuck.Release();
    }

    /// <summary>
    /// 释放时要通知订阅者收工：handler 拿到的 token 必须被取消，
    /// 否则阻塞在长连接上的订阅者（如 Task 9 的 SSE）永远退不出来。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_cancels_the_token_given_to_handlers()
    {
        var handler = new TokenCapturingHandler();

        await using var bus = new EventBus(
            new IEventHandler[] { handler },
            NullLogger<EventBus>.Instance);

        bus.Publish(EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null));
        await handler.Handled;

        await bus.DisposeAsync();

        Assert.True(
            handler.CapturedToken.IsCancellationRequested,
            "释放时必须取消订阅者的 token，否则收不了工");
    }

    /// <summary>
    /// 排空超时之后才以异常结束的消费循环，异常也要被观察到：
    /// 没人 await 的 Task 异常会静默丢失（默认被吞掉）。
    /// </summary>
    [Fact]
    public async Task DisposeAsync_observes_a_consumer_loop_that_faults_after_the_drain_timeout()
    {
        var handler = new StuckHandler(throwAfterRelease: true);
        var logger = new ThrowOnceOnErrorLogger();

        await using var bus = new EventBus(
            new IEventHandler[] { handler },
            logger,
            TimeSpan.FromMilliseconds(100));

        bus.Publish(EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null));
        await handler.Started;

        // handler 卡着不放 → 排空超时，释放在上限内返回。
        await bus.DisposeAsync();

        // 之后 handler 抛异常 → 消费循环以异常结束（连同日志一起炸）。
        handler.Release();

        // 这个异常必须被记进日志，而不是变成没人观察的未观察异常。
        await WaitUntilAsync(
            () => logger.Messages.Any(message => message.Contains("消费循环异常结束")),
            "消费循环的异常应当被记进日志");
    }

    /// <summary>等某个条件成立（超时即测试失败）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.Add(EventTestTimeouts.Timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), $"等待超时：{what}");
    }
}
