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
/// <para>
/// 释放（<see cref="DisposeAsync"/>）分三步：先取消订阅者的 token（阻塞在长连接上的
/// 订阅者靠它退出），再完成通道（读循环把已入队的事件处理完），
/// 最后等排空 —— 但排空有上限，订阅者不收工时不能把应用关闭卡死。
/// </para>
/// </summary>
public sealed class EventBus : IEventPublisher, IAsyncDisposable
{
    /// <summary>每个订阅者的通道容量。</summary>
    private const int ChannelCapacity = 1000;

    /// <summary>默认的排空上限。</summary>
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>排空超时后，再给读循环收尾的时间。</summary>
    private static readonly TimeSpan ForceStopGrace = TimeSpan.FromMilliseconds(250);

    private readonly List<Channel<IdeaEvent>> _channels = new();
    private readonly List<Task> _consumers = new();
    private readonly ILogger<EventBus> _logger;
    private readonly TimeSpan _drainTimeout;

    /// <summary>
    /// 把「往通道里写」与「关闭通道」互斥起来：
    /// 否则并发下可能在通道已经关闭之后还 TryWrite，
    /// 刷出一条「通道已关闭」的假警告。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>通知订阅者收工的信号（订阅者拿到的就是这个 token）。</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>排空超时后强制结束读循环的信号。</summary>
    private readonly CancellationTokenSource _abort = new();

    private bool _disposed;

    /// <summary>
    /// 构造事件总线：每个订阅者一条独立通道。
    /// </summary>
    /// <param name="handlers">订阅者（可以为空 —— 没有订阅者时 publish 是空操作）。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="drainTimeout">
    /// 释放时排空的上限：超过这个时间还没处理完就强制结束并记一条警告。
    /// 默认 5 秒。
    /// </param>
    public EventBus(
        IEnumerable<IEventHandler> handlers,
        ILogger<EventBus> logger,
        TimeSpan? drainTimeout = null)
    {
        _logger = logger;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;

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
        lock (_gate)
        {
            if (_disposed)
            {
                // 已经释放：直接返回。再往已关闭的通道里写只会写失败，
                // 还会刷出一条「通道已关闭」的假警告。
                return;
            }

            foreach (var channel in _channels)
            {
                if (!channel.Writer.TryWrite(evt))
                {
                    // 通道满了：丢掉这一条，但不能拖住写接口。
                    _logger.LogWarning(
                        "事件通道已满，丢弃事件 {EventId}（类型 {EventType}）",
                        evt.Id,
                        evt.Type);
                }
            }
        }
    }

    /// <summary>
    /// 释放：先通知订阅者收工，再完成通道，最后把已入队的事件排空（有上限）。
    /// </summary>
    /// <remarks>
    /// 排空必须处理完已入队的事件：已经写进通道的事件不能凭空消失。
    /// 但排空不能没有上限 —— Task 9 的 SSE 订阅者阻塞在长连接上，
    /// 没有上限的话应用关闭会一直挂着。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // 1) 先喊收工：订阅者拿到的 token 在这里被取消。阻塞在长连接上的
        //    订阅者靠它退出，否则应用关闭会一直挂着。
        try
        {
            _shutdown.Cancel();
        }
        catch (Exception ex)
        {
            // 收工信号里注册的回调抛异常也不能让释放失败。
            _logger.LogError(ex, "通知订阅者收工时出错");
        }

        // 2) 再完成通道：不再接受新事件，读循环把已入队的事件处理完。
        foreach (var channel in _channels)
        {
            channel.Writer.TryComplete();
        }

        // 3) 排空，但有上限：订阅者不收工时不能把应用关闭卡死。
        var consumers = Task.WhenAll(_consumers);

        if (await Task.WhenAny(consumers, Task.Delay(_drainTimeout)) == consumers)
        {
            try
            {
                await consumers;
            }
            catch (Exception ex)
            {
                // 消费循环自己兜住了异常，这里只是兜底：释放失败不该把应用带崩。
                _logger.LogError(ex, "事件总线排空时消费循环异常");
            }

            _shutdown.Dispose();
            _abort.Dispose();
            return;
        }

        _logger.LogWarning(
            "事件总线排空超时（{DrainTimeout}），强制结束剩余的消费循环",
            _drainTimeout);

        // 强制结束读循环；已经在跑的 handler 由它自己收尾。
        _abort.Cancel();

        await Task.WhenAny(consumers, Task.Delay(ForceStopGrace));

        // 不释放 token 源：可能还有 handler 在跑，它还要用这个 token。
    }

    /// <summary>
    /// 一条通道的消费循环：取出事件交给订阅者，订阅者抛异常只记日志，
    /// 不影响这条通道上的后续事件。
    /// </summary>
    private async Task ConsumeAsync(IEventHandler handler, Channel<IdeaEvent> channel)
    {
        try
        {
            // 读循环用独立的 token：收工信号（_shutdown）只通知订阅者，
            // 读循环还要把已入队的事件处理完。
            await foreach (var evt in channel.Reader.ReadAllAsync(_abort.Token))
            {
                try
                {
                    await handler.HandleAsync(evt, _shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    // 订阅者响应了收工信号：不记错误，继续把队列里剩下的交给它。
                    _logger.LogDebug(
                        "订阅者 {Handler} 处理事件 {EventId} 时响应了收工信号",
                        handler.GetType().Name,
                        evt.Id);
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
            // 排空超时后被强制结束读循环。
        }
    }
}
