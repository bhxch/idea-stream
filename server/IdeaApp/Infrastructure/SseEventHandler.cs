using IdeaApp.Domain;

namespace IdeaApp.Infrastructure;

/// <summary>
/// SSE 订阅者：把事件总线上的事件塞进 <see cref="SseBroadcaster"/>。
///
/// <para>
/// <b>必须注册成 Singleton</b>：事件总线在**根** provider 里用
/// <c>sp.GetServices&lt;IEventHandler&gt;()</c> 解析订阅者，
/// 注册成 Scoped 会在开发期（<c>ValidateScopes</c>）抛
/// <see cref="InvalidOperationException"/>，在生产期则泄漏作用域。
/// </para>
/// </summary>
public sealed class SseEventHandler(SseBroadcaster broadcaster) : IEventHandler
{
    /// <summary>
    /// 处理一个事件：进缓冲 + 推给所有订阅者。
    /// </summary>
    /// <remarks>
    /// 总线在收工（<c>DisposeAsync</c>）时会先取消订阅者的 token 再排空 ——
    /// 这时 token 已经是取消态，**放弃剩下的事件是正确的**：
    /// 长连接本来就要断了，为它继续排队没有意义。这不是异常，不要往外抛。
    /// </remarks>
    public ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        broadcaster.Append(evt);

        return ValueTask.CompletedTask;
    }
}
