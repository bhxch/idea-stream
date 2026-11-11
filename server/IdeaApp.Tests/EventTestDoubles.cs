using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IdeaApp.Tests;

/// <summary>事件总线测试的公共超时上限。</summary>
internal static class EventTestTimeouts
{
    /// <summary>等事件到达的上限。超时即测试失败。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
}

/// <summary>把收到的事件记下来的处理器。</summary>
internal sealed class RecordingHandler : IEventHandler
{
    private readonly TaskCompletionSource<IdeaEvent> _received =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>收到事件后完成的任务（超时就失败）。</summary>
    public Task<IdeaEvent> Received => _received.Task.WaitAsync(EventTestTimeouts.Timeout);

    public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
    {
        _received.TrySetResult(evt);
        return ValueTask.CompletedTask;
    }
}

/// <summary>故意慢的处理器。</summary>
internal sealed class SlowHandler(TimeSpan delay) : IEventHandler
{
    private readonly TaskCompletionSource _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>处理完成后完成的任务（超时就失败）。</summary>
    public Task Done => _done.Task.WaitAsync(EventTestTimeouts.Timeout);

    public async ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
    {
        await Task.Delay(delay, ct);
        _done.TrySetResult();
    }
}

/// <summary>故意抛异常的处理器。</summary>
internal sealed class ThrowingHandler : IEventHandler
{
    public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
    {
        throw new InvalidOperationException("这个处理器故意失败");
    }
}

/// <summary>记下自己拿到的 token 的处理器。</summary>
internal sealed class TokenCapturingHandler : IEventHandler
{
    private readonly TaskCompletionSource _handled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>处理完第一个事件后完成的任务（超时就失败）。</summary>
    public Task Handled => _handled.Task.WaitAsync(EventTestTimeouts.Timeout);

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
internal sealed class CollectingHandler : IEventHandler
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
internal sealed class FailOnceHandler : IEventHandler
{
    private readonly object _gate = new();
    private readonly List<IdeaEvent> _received = new();
    private readonly TaskCompletionSource _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>收到第二个事件后完成的任务（超时就失败）。</summary>
    public Task Done => _done.Task.WaitAsync(EventTestTimeouts.Timeout);

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
/// <param name="throwAfterRelease">
/// true 时：放行之后抛异常（用来制造「消费循环以异常结束」）。
/// </param>
internal sealed class StuckHandler(bool throwAfterRelease = false) : IEventHandler
{
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>开始处理第一个事件后完成的任务（超时就失败）。</summary>
    public Task Started => _started.Task.WaitAsync(EventTestTimeouts.Timeout);

    /// <summary>放行：让卡住的处理返回。</summary>
    public void Release() => _release.TrySetResult();

    public async ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
    {
        _started.TrySetResult();

        // 不看 token：模拟不响应收工信号的订阅者。
        await _release.Task;

        if (throwAfterRelease)
        {
            throw new InvalidOperationException("这个处理器卡住之后故意失败");
        }
    }
}

/// <summary>
/// 第一次写错误日志时抛异常的日志器：
/// 用来制造「消费循环以异常结束」（记日志这一步自己炸了，异常就没人兜）。
/// </summary>
internal sealed class ThrowOnceOnErrorLogger : ILogger<EventBus>
{
    private readonly object _gate = new();
    private readonly List<string> _messages = new();
    private bool _thrown;

    /// <summary>已记录的日志（按记录顺序）。</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
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
            _messages.Add(formatter(state, exception));

            if (logLevel == LogLevel.Error && !_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("这个日志器故意在写错误日志时抛异常");
            }
        }
    }
}

/// <summary>
/// 把日志记下来的日志器（不往控制台写，避免测试输出噪音）。
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
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
