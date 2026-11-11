using System.Diagnostics;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Logging;
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
    /// 通道满了以后，发布方仍然要**立刻**返回：
    /// 宁可丢事件，也不能让写接口卡在订阅者的背压上。
    /// </summary>
    [Fact]
    public async Task Publish_returns_immediately_when_a_channel_is_full()
    {
        var stuck = new StuckHandler();
        var logger = new CapturingLogger<EventBus>();

        await using var bus = new EventBus(
            new IEventHandler[] { stuck },
            logger,
            TimeSpan.FromSeconds(1));

        // 订阅者卡住不动 → 通道被灌满（总线的通道容量是 1000）。
        var evt = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 1010; i++)
        {
            bus.Publish(evt);
        }
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 500,
            $"发布 1010 条事件用了 {stopwatch.ElapsedMilliseconds}ms，通道满了也不能拖住发布方");

        // 丢事件要留痕，不能静默丢。
        Assert.True(logger.WarningCount > 0, "通道满时应当记一条警告");

        stuck.Release();
    }

    /// <summary>
    /// 某个订阅者抛异常后，**它那条通道**的后续事件仍然要送达：
    /// 一次失败不能把这个订阅者的通道废掉。
    /// </summary>
    [Fact]
    public async Task A_failing_handler_still_receives_later_events()
    {
        var flaky = new FailOnceHandler();

        await using var bus = new EventBus(
            new IEventHandler[] { flaky },
            NullLogger<EventBus>.Instance);

        var first = EventFactory.Create(EventTypes.IdeaUpdated, project: null, idea: null);
        var second = EventFactory.Create(EventTypes.IdeaUpdated, project: null, idea: null);

        bus.Publish(first);
        bus.Publish(second);

        await flaky.Done;

        // 第一次抛了异常，第二次照常送达（同一个订阅者、同一条通道）。
        Assert.Equal(2, flaky.Received.Count);
        Assert.Same(first, flaky.Received[0]);
        Assert.Same(second, flaky.Received[1]);
    }

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

    /// <summary>记下自己拿到的 token 的处理器。</summary>
    private sealed class TokenCapturingHandler : IEventHandler
    {
        private readonly TaskCompletionSource _handled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>处理完第一个事件后完成的任务（超时就失败）。</summary>
        public Task Handled => _handled.Task.WaitAsync(Timeout);

        /// <summary>处理事件时拿到的 token。</summary>
        public CancellationToken CapturedToken { get; private set; }

        public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            CapturedToken = ct;
            _handled.TrySetResult();

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>把收到的事件全都记下来的处理器。</summary>
    private sealed class CollectingHandler : IEventHandler
    {
        private readonly object _gate = new();
        private readonly List<IdeaEvent> _received = new();

        /// <summary>已收到的事件（按到达顺序）。</summary>
        public IReadOnlyList<IdeaEvent> Received
        {
            get
            {
                lock (_gate)
                {
                    return _received.ToArray();
                }
            }
        }

        public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            lock (_gate)
            {
                _received.Add(evt);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>第一次抛异常、之后照常处理的处理器。</summary>
    private sealed class FailOnceHandler : IEventHandler
    {
        private readonly object _gate = new();
        private readonly List<IdeaEvent> _received = new();
        private readonly TaskCompletionSource _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>收到第二个事件后完成的任务（超时就失败）。</summary>
        public Task Done => _done.Task.WaitAsync(Timeout);

        /// <summary>已收到的事件（按到达顺序）。</summary>
        public IReadOnlyList<IdeaEvent> Received
        {
            get
            {
                lock (_gate)
                {
                    return _received.ToArray();
                }
            }
        }

        public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            lock (_gate)
            {
                _received.Add(evt);

                if (_received.Count == 1)
                {
                    throw new InvalidOperationException("这个处理器故意失败一次");
                }

                if (_received.Count == 2)
                {
                    _done.TrySetResult();
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>卡住不返回的处理器（模拟「怎么喊都不收工」的订阅者）。</summary>
    private sealed class StuckHandler : IEventHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>开始处理第一个事件后完成的任务（超时就失败）。</summary>
        public Task Started => _started.Task.WaitAsync(Timeout);

        /// <summary>放行：让卡住的处理返回。</summary>
        public void Release() => _release.TrySetResult();

        public async ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
        {
            _started.TrySetResult();

            // 不看 token：模拟不响应收工信号的订阅者。
            await _release.Task;
        }
    }

    /// <summary>
    /// 把日志记下来的日志器（不往控制台写，避免测试输出噪音）。
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<LogLevel> _levels = new();

        /// <summary>警告条数。</summary>
        public int WarningCount
        {
            get
            {
                lock (_gate)
                {
                    return _levels.Count(level => level == LogLevel.Warning);
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _levels.Add(logLevel);
            }
        }
    }
}
