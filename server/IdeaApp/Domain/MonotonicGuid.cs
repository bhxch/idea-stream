namespace IdeaApp.Domain;

/// <summary>
/// 单调 UUIDv7 生成器（RFC 9562 §6.2 的计数器法）。
///
/// 为什么不用 .NET 内置的 <c>Guid.CreateVersion7()</c>：它把 74 个随机位放在时间戳后面，
/// **同一毫秒内完全不单调** —— 同刻写入的条目顺序会随机跳动，
/// 直接破坏「最近更新的在最下边」这个核心承诺。这里按 RFC 9562 自己实现：
/// 时间戳占最高 48 位，后面的 74 位用计数器填，同一毫秒内计数器递增 → id 严格递增。
///
/// 字节布局（大端 16 字节）：
/// <code>
/// bytes[0..6]   unix_ts_ms（48 位，大端）
/// bytes[6]      0x70 | (rand_a >> 8)        // 版本位 = 7
/// bytes[7]      rand_a & 0xFF
/// bytes[8]      0x80 | (rand_b >> 56)       // 变体位 = 0b10
/// bytes[9..16]  rand_b 的低 56 位（大端）
/// </code>
///
/// 计数器 C 共 74 位：<c>rand_a = (C >> 62) & 0xFFF</c>（12 位）、
/// <c>rand_b = C & 0x3FFF_FFFF_FFFF_FFFF</c>（62 位）。
/// 用 <see cref="Guid.ToString(string)"/> 的 "N" 格式转出来就是 32 位无连字符小写 hex，
/// 字典序与生成顺序同向。
/// </summary>
public static class MonotonicGuid
{
    /// <summary>计数器低 62 位的掩码。</summary>
    private const ulong CounterLowMask = 0x3FFF_FFFF_FFFF_FFFFUL;

    /// <summary>计数器高 12 位的掩码。</summary>
    private const uint CounterHighMask = 0xFFFU;

    /// <summary>保护计数器状态的锁。</summary>
    private static readonly object Gate = new();

    /// <summary>生产用计数器：时间戳只进不退。</summary>
    private static readonly Counter SystemCounter = new();

    /// <summary>测试用计数器：注入的时间戳原样使用（与生产计数器分开，已知答案向量才确定）。</summary>
    private static readonly Counter InjectedCounter = new();

    /// <summary>生成一个单调的 UUIDv7（时间戳取当前 UTC 毫秒）。</summary>
    public static Guid NewUuid7()
    {
        return Next(SystemCounter, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), exactTimestamp: false);
    }

    /// <summary>
    /// 生成一个指定时间戳的 UUIDv7。测试用：注入固定毫秒，好把已知答案向量钉死。
    /// </summary>
    internal static Guid NewUuid7(long unixMs)
    {
        return Next(InjectedCounter, unixMs, exactTimestamp: true);
    }

    /// <summary>取下一个 id：先推进计数器，再按 RFC 9562 的布局拼字节。</summary>
    private static Guid Next(Counter counter, long unixMs, bool exactTimestamp)
    {
        long timestamp;
        uint randA;
        ulong randB;

        lock (Gate)
        {
            (timestamp, randA, randB) = counter.Next(unixMs, exactTimestamp);
        }

        return Build(timestamp, randA, randB);
    }

    /// <summary>按 RFC 9562 的布局把时间戳与计数器拼成 16 字节（大端）。</summary>
    private static Guid Build(long unixMs, uint randA, ulong randB)
    {
        Span<byte> bytes = stackalloc byte[16];

        // 48 位时间戳（大端）。
        for (var i = 0; i < 6; i++)
        {
            bytes[5 - i] = (byte)(unixMs >> (8 * i));
        }

        // 版本位 7，后面跟 rand_a 的高 4 位。
        bytes[6] = (byte)(0x70 | ((randA >> 8) & 0x0F));

        // rand_a 的低 8 位。
        bytes[7] = (byte)(randA & 0xFF);

        // 变体位 0b10，后面跟 rand_b 的高 6 位。
        bytes[8] = (byte)(0x80 | ((randB >> 56) & 0x3F));

        // rand_b 的低 56 位（大端）。
        for (var i = 0; i < 7; i++)
        {
            bytes[15 - i] = (byte)(randB >> (8 * i));
        }

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// UUIDv7 的 74 位计数器状态：记住上一个时间戳，同一毫秒内靠计数器递增保持严格递增。
    /// </summary>
    private sealed class Counter
    {
        /// <summary>上一个时间戳；<see cref="long.MinValue"/> 表示还没用过。</summary>
        private long _lastUnixMs = long.MinValue;

        /// <summary>计数器高 12 位（rand_a）。</summary>
        private uint _high;

        /// <summary>计数器低 62 位（rand_b）。</summary>
        private ulong _low;

        /// <summary>
        /// 取下一个计数值。
        /// </summary>
        /// <param name="unixMs">本次请求的时间戳（UTC 毫秒）。</param>
        /// <param name="exactTimestamp">
        /// true = 时间戳原样使用（注入固定时间戳的测试缝用：已知答案向量必须确定）；
        /// false = 时间戳只进不退（生产用：时钟回拨时保持上一个时间戳，否则回拨后
        /// 再回到同一毫秒会生成与先前重复的 id）。
        /// </param>
        /// <returns>写进 id 的时间戳，以及 74 位计数器（高 12 位 + 低 62 位）。</returns>
        public (long UnixMs, uint High, ulong Low) Next(long unixMs, bool exactTimestamp)
        {
            var advanced = exactTimestamp ? unixMs != _lastUnixMs : unixMs > _lastUnixMs;

            if (advanced)
            {
                // 时间戳变了：计数器归零。毫秒占最高位，生产路径上时间戳变大时 id 必然变大。
                _lastUnixMs = unixMs;
                _high = 0;
                _low = 0;
            }
            else
            {
                // 同一毫秒（或时钟回拨）：计数器加一。
                // 74 位计数器的上限是每毫秒 2^74 个 id，实际不可能触到。
                _low++;
                if (_low > CounterLowMask)
                {
                    _low = 0;
                    _high = (_high + 1) & CounterHighMask;
                }
            }

            return (_lastUnixMs, _high, _low);
        }
    }
}
