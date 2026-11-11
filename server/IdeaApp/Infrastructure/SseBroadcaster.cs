using System.Threading.Channels;
using IdeaApp.Domain;
using Microsoft.Extensions.Logging;

namespace IdeaApp.Infrastructure;

/// <summary>
/// SSE 广播器：保留最近若干条事件的**内存缓冲**，并把新事件推给所有订阅者。
///
/// <para>
/// 缓冲是断线补发的依据：客户端重连时带上 <c>Last-Event-ID</c>，
/// 服务端从缓冲里找出它之后的事件补发过去。
/// </para>
/// <para>
/// 缓冲**只在内存**：进程重启后必然补发不到 ——
/// 那时服务端发一帧 <c>resync</c>，让客户端重新拉完整列表。
/// </para>
/// </summary>
public sealed class SseBroadcaster
{
    /// <summary>每个订阅者的通道容量。</summary>
    private const int SubscriberChannelCapacity = 1000;

    private readonly object _gate = new();

    /// <summary>最近的事件（超出容量时从最旧的开始丢）。</summary>
    private readonly List<IdeaEvent> _buffer = new();

    /// <summary>当前的订阅者。</summary>
    private readonly List<SseSubscription> _subscriptions = new();

    private readonly ILogger? _logger;

    /// <param name="bufferSize">缓冲条数（补发用）。</param>
    /// <param name="heartbeatSeconds">心跳间隔（秒）。</param>
    /// <param name="logger">
    /// 日志器（不给就不记日志 —— 单元测试里不需要）。
    /// </param>
    public SseBroadcaster(
        int bufferSize,
        int heartbeatSeconds = 25,
        ILogger<SseBroadcaster>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(heartbeatSeconds, 1);

        BufferSize = bufferSize;
        HeartbeatSeconds = heartbeatSeconds;
        _logger = logger;
    }

    /// <summary>缓冲条数（补发时最多能回放多少条）。</summary>
    public int BufferSize { get; }

    /// <summary>心跳间隔（秒）：空闲连接上每隔这么久发一行注释。</summary>
    public int HeartbeatSeconds { get; }

    /// <summary>
    /// 把事件放进缓冲，并推给每个订阅者。
    /// </summary>
    /// <remarks>
    /// 订阅者的通道满了就丢这一条并记一条警告：宁可丢事件，
    /// 也不能让写接口卡在某个慢客户端上。
    /// </remarks>
    public void Append(IdeaEvent evt)
    {
        lock (_gate)
        {
            _buffer.Add(evt);

            if (_buffer.Count > BufferSize)
            {
                _buffer.RemoveRange(0, _buffer.Count - BufferSize);
            }

            foreach (var subscription in _subscriptions)
            {
                if (!subscription.Channel.Writer.TryWrite(evt))
                {
                    _logger?.LogWarning(
                        "SSE 订阅 {SubscriptionId} 的通道已满，丢弃事件 {EventId}（类型 {EventType}）",
                        subscription.Id,
                        evt.Id,
                        evt.Type);
                }
            }
        }
    }

    /// <summary>
    /// 取某个事件**之后**的事件（断线补发用）。
    /// </summary>
    /// <param name="lastEventId">客户端最后收到的事件 id。</param>
    /// <returns>
    /// 要补发的事件；<paramref name="lastEventId"/> 为空时返回空列表（客户端刚连上，
    /// 没什么可补）；它**不在缓冲里**时返回 <c>null</c> ——
    /// 调用方据此发 <c>resync</c> 帧（服务端补不了，只能让客户端重新拉完整列表）。
    /// </returns>
    public IReadOnlyList<IdeaEvent>? Since(string? lastEventId)
    {
        if (string.IsNullOrEmpty(lastEventId))
        {
            return Array.Empty<IdeaEvent>();
        }

        lock (_gate)
        {
            var index = _buffer.FindIndex(
                evt => string.Equals(evt.Id, lastEventId, StringComparison.Ordinal));

            if (index < 0)
            {
                return null;
            }

            return _buffer.Skip(index + 1).ToArray();
        }
    }

    /// <summary>开一个新订阅：它有自己的通道，事件只推给它自己。</summary>
    public SseSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<IdeaEvent>(new BoundedChannelOptions(SubscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        var subscription = new SseSubscription(Guid.NewGuid(), channel);

        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>
    /// 结束一个订阅：把它从订阅列表里摘掉并关闭它的通道。
    /// </summary>
    /// <returns>这个订阅确实存在并被摘掉了（false = 已经被摘掉了）。</returns>
    public bool TryCompleteSubscription(SseSubscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(subscription))
            {
                return false;
            }
        }

        // 通道关掉之后，订阅端正在等的读操作会正常结束（不是异常）。
        subscription.Channel.Writer.TryComplete();

        return true;
    }
}

/// <summary>一个 SSE 订阅：自己的通道 + 一个 id。</summary>
public sealed record SseSubscription(Guid Id, Channel<IdeaEvent> Channel);
