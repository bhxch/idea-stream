using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Tests;

/// <summary>
/// SSE 广播器的单元测试：缓冲、补发与慢订阅者的处理。
///
/// 端到端的行为（帧格式、心跳、断线补发、resync）在
/// <see cref="SseTests"/> 里；这里只测广播器自己。
/// </summary>
public sealed class SseBroadcasterTests
{
    /// <summary>
    /// 慢订阅者被挤掉事件时，要给它补一帧 resync。
    /// </summary>
    /// <remarks>
    /// 事件 id 是单调 UUIDv7，**不是连续序号** —— 客户端没法自己比对出
    /// 「我少了第 N 条」。不告诉它的话，它会一直带着一份缺了数据的列表用下去，
    /// 而且完全没有症状（静默空洞）。
    /// </remarks>
    [Fact]
    public void Slow_subscriber_gets_a_resync_when_an_event_is_dropped()
    {
        var broadcaster = new SseBroadcaster(bufferSize: 10);
        var subscription = broadcaster.Subscribe();

        // 订阅者不读：它的通道很快就被塞满（模拟慢客户端）。
        var filler = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);
        for (var i = 0; i < 1500; i++)
        {
            broadcaster.Append(filler);
        }

        var messages = new List<SseMessage>();
        while (subscription.Channel.Reader.TryRead(out var message))
        {
            messages.Add(message);
        }

        Assert.NotEmpty(messages);

        // 最后一帧就是 resync：它是在丢事件的那一刻塞进去的（顺序不能乱）。
        Assert.True(
            messages[^1].IsResync,
            "通道满丢掉事件时，订阅者应当收到一帧 resync（它发现不了这个空洞）");

        // 业务事件照旧推 —— resync 不是把流掐断。
        Assert.Contains(messages, message => !message.IsResync);
    }
}
