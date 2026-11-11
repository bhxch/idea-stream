using System.Collections.Concurrent;
using IdeaApp.Domain;

namespace IdeaApp.Tests;

/// <summary>
/// 单调 UUIDv7 生成器的契约测试。
///
/// 为什么必须自己实现：.NET 的 <c>Guid.CreateVersion7()</c> 把 74 个随机位放在时间戳后面，
/// **同一毫秒内完全不单调** —— 同刻写入的条目排序会随机跳动，
/// 直接破坏「最近更新的在最下边」这个核心承诺。
///
/// 注意：注入固定时间戳的重载共享同一个计数器状态，所以**不同测试必须用互不相同的
/// 注入时间戳** —— 否则「同一毫秒连调」会把计数器推到后面，已知答案向量就地对不上了。
/// </summary>
public sealed class MonotonicGuidTests
{
    /// <summary>已知答案向量用的时间戳（毫秒）。</summary>
    private const long ZeroUnixMs = 0;

    /// <summary>已知答案向量用的另一个时间戳（毫秒）。</summary>
    private const long SampleUnixMs = 0x0123456789AB;

    /// <summary>「同一毫秒连调」用的时间戳（毫秒）。</summary>
    private const long SameMillisecondUnixMs = 0x0000_0000_0000_1234;

    /// <summary>「毫秒递增」用的起始时间戳（毫秒）。</summary>
    private const long IncreasingUnixMs = 0x0000_0000_0000_0200;

    [Fact]
    public void NewUuid7_with_injected_timestamp_matches_known_vectors()
    {
        // RFC 9562 布局：48 位时间戳（大端）+ 版本位 7 + 12 位 rand_a
        // + 变体位 0b10 + 62 位 rand_b。计数器为 0 时低位全是 0。
        Assert.Equal("00000000-0000-7000-8000-000000000000",
            MonotonicGuid.NewUuid7(ZeroUnixMs).ToString());

        Assert.Equal("01234567-89ab-7000-8000-000000000000",
            MonotonicGuid.NewUuid7(SampleUnixMs).ToString());
    }

    [Fact]
    public void NewUuid7_within_the_same_millisecond_increases_strictly()
    {
        var previous = MonotonicGuid.NewUuid7(SameMillisecondUnixMs).ToString("N");

        for (var i = 0; i < 10_000; i++)
        {
            var current = MonotonicGuid.NewUuid7(SameMillisecondUnixMs).ToString("N");

            Assert.True(string.CompareOrdinal(current, previous) > 0,
                $"同一毫秒内必须严格递增：{previous} → {current}");

            previous = current;
        }
    }

    [Fact]
    public void NewUuid7_with_increasing_timestamps_increases_strictly()
    {
        var previous = MonotonicGuid.NewUuid7(IncreasingUnixMs).ToString("N");

        for (var offset = 1; offset <= 100; offset++)
        {
            var current = MonotonicGuid.NewUuid7(IncreasingUnixMs + offset).ToString("N");

            Assert.True(string.CompareOrdinal(current, previous) > 0,
                $"毫秒递增时必须严格递增：{previous} → {current}");

            previous = current;
        }
    }

    /// <summary>
    /// 生产路径（真实时钟）也必须严格递增 —— 跨毫秒靠时间戳，同毫秒靠计数器。
    /// </summary>
    [Fact]
    public void NewUuid7_with_system_clock_increases_strictly()
    {
        var previous = MonotonicGuid.NewUuid7().ToString("N");

        for (var i = 0; i < 10_000; i++)
        {
            var current = MonotonicGuid.NewUuid7().ToString("N");

            Assert.True(string.CompareOrdinal(current, previous) > 0,
                $"生产路径必须严格递增：{previous} → {current}");

            previous = current;
        }
    }

    [Fact]
    public void NewUuid7_is_thread_safe()
    {
        const int threadCount = 8;
        const int perThread = 500;

        var ids = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, _ =>
        {
            try
            {
                for (var i = 0; i < perThread; i++)
                {
                    ids.Add(MonotonicGuid.NewUuid7().ToString("N"));
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        });

        Assert.Empty(failures);
        Assert.Equal(threadCount * perThread, ids.Count);

        // 并发下也不能撞号：时间戳 + 计数器保证每个 id 唯一。
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
