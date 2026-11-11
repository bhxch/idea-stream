using System.Threading.Channels;
using IdeaApp.Domain;
using Microsoft.Extensions.Logging;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 事件总线：把一个事件**扇出**给每一个订阅者。
///
/// <para>
/// 每个 <see cref="IEventHandler"/> 有自己的一条通道和一个后台消费循环 ——
/// 共用一条通道就成了竞争消费，两个订阅者各拿一半事件，
/// 「手机收到了、电脑没收到」这种问题从此没法排查。
/// </para>
/// <para>
/// <see cref="Publish"/> 只往通道里写（不等待），所以写接口不会被慢订阅者拖住；
/// 通道满了（默认 1000 条）就丢掉这条事件并记一条警告 ——
/// 宁可丢事件，也不能让写接口卡住。
/// </para>
/// </summary>
public sealed class EventBus : IEventPublisher, IAsyncDisposable
{
    /// <summary>每个订阅者的通道容量。</summary>
    private const int ChannelCapacity = 1000;

    private readonly List<Channel<IdeaEvent>> _channels = new();
    private readonly List<Task> _consumers = new();
    private readonly ILogger<EventBus> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    /// <summary>
    /// 构造事件总线：每个订阅者一条独立通道。
    /// </summary>
    /// <param name="handlers">订阅者（可以为空 —— 没有订阅者时 publish 是空操作）。</param>
    /// <param name="logger">日志器。</param>
    public EventBus(IEnumerable<IEventHandler> handlers, ILogger<EventBus> logger)
    {
        _logger = logger;

        foreach (var handler in handlers)
        {
            var channel = Channel.CreateBounded<IdeaEvent>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

            _channels.Add(channel);
            _consumers.Add(Task.Run(() => ConsumeAsync(handler, channel)));
        }
    }

    /// <summary>
    /// 发布一个事件：写进每一条通道就立刻返回，不等订阅者处理。
    /// </summary>
    public void Publish(IdeaEvent evt)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var channel in _channels)
        {
            if (!channel.Writer.TryWrite(evt))
            {
                // 通道满了（或已关闭）：丢掉这一条，但不能拖住写接口。
                _logger.LogWarning(
                    "事件通道已满或已关闭，丢弃事件 {EventId}（类型 {EventType}）",
                    evt.Id,
                    evt.Type);
            }
        }
    }

    /// <summary>释放：完成所有通道，等后台消费循环把剩下的事件处理完。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var channel in _channels)
        {
            channel.Writer.TryComplete();
        }

        await Task.WhenAll(_consumers);

        _shutdown.Dispose();
    }

    /// <summary>
    /// 一条通道的消费循环：取出事件交给订阅者，订阅者抛异常只记日志，
    /// 不影响这条通道上的后续事件。
    /// </summary>
    private async Task ConsumeAsync(IEventHandler handler, Channel<IdeaEvent> channel)
    {
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    await handler.HandleAsync(evt, _shutdown.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "订阅者 {Handler} 处理事件 {EventId}（类型 {EventType}）失败",
                        handler.GetType().Name,
                        evt.Id,
                        evt.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 释放期间的取消：正常退出。
        }
    }
}
