using System.Diagnostics;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdeaApp.Tests;

/// <summary>
/// 事件总线的单元测试。
///
/// 重点是**扇出**：每个处理器有自己的通道，事件投递给**每一个**处理器。
/// 共用一条通道就成了竞争消费 —— 两个处理器各拿一半事件，
/// 「手机收到了、电脑没收到」这种 bug 从此没法排查。
/// </summary>
public sealed class EventBusTests
{
    /// <summary>等事件到达的上限（秒）。超时即测试失败。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Publish_delivers_to_every_handler()
    {
        var first = new RecordingHandler();
        var second = new RecordingHandler();

        await using var bus = new EventBus(
            new IEventHandler[] { first, second },
            NullLogger<EventBus>.Instance);

        var evt = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);
        bus.Publish(evt);

        var delivered = await Task.WhenAll(first.Received, second.Received);

        // 每个处理器都收到**同一个实例**（同一个 Id），不是各拿一半。
        Assert.Same(evt, delivered[0]);
        Assert.Same(evt, delivered[1]);
        Assert.Equal(evt.Id, delivered[0].Id);
        Assert.Equal(evt.Id, delivered[1].Id);
    }

    /// <summary>
    /// 慢处理器不能拖住发布方：写接口不能因为某个订阅者卡住就跟着卡住。
    /// </summary>
    [Fact]
    public async Task Publish_does_not_block_when_handler_is_slow()
    {
        var slow = new SlowHandler(TimeSpan.FromMilliseconds(200));

        await using var bus = new EventBus(
            new IEventHandler[] { slow },
            NullLogger<EventBus>.Instance);

        var evt = EventFactory.Create(EventTypes.IdeaUpdated, project: null, idea: null);

        var stopwatch = Stopwatch.StartNew();
        bus.Publish(evt);
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 50,
            $"Publish 用了 {stopwatch.ElapsedMilliseconds}ms，应当立即返回（不能等处理器处理完）");

        // 事件确实在后台送到了慢处理器（不是被丢掉了）。
        await slow.Done;
    }

    /// <summary>
    /// 一个处理器抛异常，不能影响发布方与其他处理器。
    /// </summary>
    [Fact]
    public async Task Publish_is_silent_when_a_handler_throws()
    {
        var throwing = new ThrowingHandler();
        var healthy = new RecordingHandler();

        await using var bus = new EventBus(
            new IEventHandler[] { throwing, healthy },
            NullLogger<EventBus>.Instance);

        var evt = EventFactory.Create(EventTypes.IdeaDeleted, project: null, idea: null);

        var exception = Record.Exception(() => bus.Publish(evt));
        Assert.Null(exception);

        // 出错的处理器没有拖垮另一个处理器。
        Assert.Same(evt, await healthy.Received);
    }

    /// <summary>
    /// 事件 id 用单调 UUIDv7：字典序与生成顺序同向，
    /// SSE 补发时才能按 id 判断「哪些事件已经发过了」。
    /// </summary>
    [Fact]
    public void EventFactory_creates_monotonic_ids()
    {
        var ids = new List<string>();

        for (var i = 0; i < 100; i++)
        {
            var evt = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);
            ids.Add(evt.Id);

            // 32 位无连字符的小写 hex。
            Assert.Equal(32, evt.Id.Length);
            Assert.Matches("^[0-9a-f]{32}$", evt.Id);
        }

        for (var i = 1; i < ids.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(ids[i - 1], ids[i]) < 0,
                $"事件 id 必须严格递增：{ids[i - 1]} -> {ids[i]}");
        }
    }

    /// <summary>把收到的事件记下来的处理器。</summary>
    private sealed class RecordingHandler : IEventHandler
    {
        private readonly TaskCompletionSource<IdeaEvent> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>收到事件后完成的任务（超时就失败）。</summary>
        public Task<IdeaEvent> Received => _received.Task.WaitAsync(Timeout);

        public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            _received.TrySetResult(evt);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>故意慢的处理器。</summary>
    private sealed class SlowHandler(TimeSpan delay) : IEventHandler
    {
        private readonly TaskCompletionSource _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>处理完成后完成的任务（超时就失败）。</summary>
        public Task Done => _done.Task.WaitAsync(Timeout);

        public async ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            _done.TrySetResult();
        }
    }

    /// <summary>故意抛异常的处理器。</summary>
    private sealed class ThrowingHandler : IEventHandler
    {
        public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            throw new InvalidOperationException("这个处理器故意失败");
        }
    }
}
