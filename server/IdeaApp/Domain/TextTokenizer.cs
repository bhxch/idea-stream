using System.Globalization;
using System.Text;

namespace IdeaApp.Domain;

/// <summary>
/// 一个词元及其在原文中的位置（UTF-16 code unit）。
/// </summary>
/// <param name="Token">词元文本（已转小写）。</param>
/// <param name="Start">词元在原文中的起始偏移，单位是 UTF-16 code unit。</param>
/// <param name="Length">词元在原文中的长度，单位是 UTF-16 code unit。</param>
public sealed record TextToken(string Token, int Start, int Length);

/// <summary>
/// 一处命中：在原文里的字符区间（UTF-16 code unit）。
/// </summary>
/// <param name="Start">命中区间的起始偏移，单位是 UTF-16 code unit。</param>
/// <param name="Length">命中区间的长度，单位是 UTF-16 code unit。</param>
public sealed record TextMatch(int Start, int Length);

/// <summary>
/// 中文友好的分词器：索引侧与查询侧共用的唯一一套切词规则。
///
/// 规则（规格 §7.1）：
/// <list type="number">
/// <item>连续的 ASCII 字母 / 数字合并成一个词元，并转小写。</item>
/// <item>其余词元字符逐字成一个词元。</item>
/// <item>非词元字符一律丢弃，只当分隔符（半角与全角一视同仁）。</item>
/// </list>
///
/// **词元字符 = Unicode 一般类别以 <c>L</c>（字母）或 <c>N</c>（数字）开头的字符**，
/// 其余（标点、符号、<c>Cf</c> 零宽连接符与变体选择符、<c>Cc</c> 控制字符、
/// <c>Mn</c> 组合记号……）全是分隔符。
///
/// 为什么这条边界要跟 FTS5 靠齐：FTS5 的 <c>unicode61</c> 划 token 字符的依据与
/// 「一般类别以 L 或 N 开头」**基本一致**（实测：<c>中</c>/<c>é</c>/<c>ʰ</c>/<c>0</c>/<c>①</c>/<c>〇</c>
/// 是 token 字符；<c>，</c>/<c>—</c>/<c>$</c>/<c>→</c>/<c>😀</c>/零宽连接符/变体选择符/C1 控制字符
/// 都是分隔符）。我们保留的字符集合**基本**是 unicode61 的子集 →
/// 「FTS 命中了却算不出高亮区间」这一类偏差**基本不会发生**。
///
/// **两处分叉（别写成「彻底不存在」）**：
/// <list type="bullet">
/// <item>
/// unicode61 把 <c>Mn</c>（组合记号）当**词内附加符**保留，而**我们丢弃** ——
/// 丢掉的字符 unicode61 根本看不到（索引文本里已经没有它），方向是安全的。
/// </item>
/// <item>
/// unicode61 把 **21 个 <c>Lo</c> 码点**当分隔符，而 .NET 判它们是 <c>Lo</c>（我们保留）：
/// New Tai Lue 元音符号 <c>U+19B0..U+19C0</c>、<c>U+19C8</c>、<c>U+19C9</c>，
/// Vedic 符号 <c>U+1CF2</c>、<c>U+1CF3</c>。**只有这 21 个码点上仍可能出现
/// 「FTS 命中却算不出高亮」**（冷门到不会有人输入，认了）。
/// </item>
/// </list>
///
/// 若我们多留一个 <c>Cf</c>（零宽连接符），正文 <c>a&lt;ZWJ&gt;b</c> 在索引里是
/// <c>[a, b]</c> 两个 token，而服务端算出的却是 <c>[a, ZWJ, b]</c> 三个 —— 两边对不上。
///
/// 为什么自己写而不用分词库：发布是 AOT 的，装不进原生扩展，也不接受运行期反射；
/// 而规则本身并不复杂 —— 中文正文里「逐字成词 + 短语查询」的语义已经等价于子串匹配。
///
/// 为什么索引侧与查询侧必须共用这一个实现：索引里存的是切分后的词元串，
/// 查询侧的 MATCH 串也是切分后的词元串。两边规则只要有一处不同，
/// 就会出现「搜得到却高亮不出来」这类没法自洽的偏差。
///
/// 遍历单位是 Unicode 标量值（rune）而不是 UTF-16 code unit：
/// emoji 是代理对，按 char 遍历会被切成两个半截词元。
/// 判定用 <see cref="Rune.GetUnicodeCategory"/> / <see cref="Rune.IsWhiteSpace"/> ——
/// 这些 API 在 <c>InvariantGlobalization=true</c> 下也可用，不依赖 ICU。
/// </summary>
public static class TextTokenizer
{
    /// <summary>
    /// 切词：ASCII 字母数字合并成词并转小写；其余词元字符逐字一个；
    /// 非词元字符（标点、符号、格式符、控制符等）丢弃。索引侧与查询侧共用这一个实现。
    /// </summary>
    /// <param name="text">待切分的原文。</param>
    /// <returns>词元序列，按出现顺序；没有词元时返回空序列。</returns>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        // 走的是同一个核心实现：切词语义必须一字不差，
        // 这里只是把词元投影成字符串，省下游的活。
        var tokens = TokenizeCore(text);
        var result = new string[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            result[i] = tokens[i].Token;
        }

        return result;
    }

    /// <summary>
    /// 同 <see cref="Tokenize"/>，但带上每个词元在原文里的 UTF-16 code unit 区间。
    /// </summary>
    /// <param name="text">待切分的原文。</param>
    /// <returns>词元序列，按出现顺序；没有词元时返回空序列。</returns>
    /// <remarks>
    /// 偏移用 UTF-16 code unit 而不是「字符序号」，是为了和前端的
    /// <c>String.prototype.slice</c> 对齐 —— 前端拿到的偏移可以直接切字符串。
    /// </remarks>
    public static IReadOnlyList<TextToken> Analyze(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<TextToken>();
        }

        return TokenizeCore(text);
    }

    /// <summary>
    /// 把查询串变成 FTS5 的 MATCH 表达式；全空返回 null（= 不过滤）。
    /// </summary>
    /// <param name="query">用户输入的查询串。</param>
    /// <returns>
    /// MATCH 表达式：段内是短语查询（<c>"tok1 tok2"</c>，要求词元相邻），
    /// 段间是 AND（<c>" AND "</c>，要求都出现但不限顺序与距离）；
    /// 查询里一个词元都没有时返回 null，表示「没有关键词」。
    /// </returns>
    /// <remarks>
    /// 词元里不会出现 FTS5 的语法字符（<c>"</c>、<c>*</c>、<c>(</c> 等都是标点，已被丢弃），
    /// 所以不需要转义。
    /// </remarks>
    public static string? BuildMatchExpression(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        var parts = new List<string>();

        // 按空白切段：段内要求相邻，段间只要求都出现。
        foreach (var segment in SplitSegments(query))
        {
            var tokens = Tokenize(segment);

            // 空段（全是标点）跳过 —— 它没有可搜的内容，也不该拖垮整条查询。
            if (tokens.Count == 0)
            {
                continue;
            }

            parts.Add($"\"{string.Join(" ", tokens)}\"");
        }

        // 一段都没剩下 = 没有关键词 = 不过滤。
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>
    /// 在原文里找出查询命中的位置。一个查询段 = 一处命中，
    /// 区间从首个命中词元的起点到末个命中词元的终点（跨过被丢弃的标点）。
    /// </summary>
    /// <param name="text">被搜索的原文。</param>
    /// <param name="query">用户输入的查询串。</param>
    /// <returns>
    /// 命中区间列表，按「查询段的先后」排列，同一段内的多处命中按在原文里的先后排列。
    ///
    /// ⚠️ **同一查询可能产出重复或重叠的区间**：查询「咖啡 咖啡」会产出两处一模一样的区间；
    /// 「哈哈哈哈」搜「哈哈」会产出两处自重叠的区间（滑动比较逐位推进）。
    /// **调用方需自行合并**（去重 + 合并重叠），否则前端会渲染出嵌套的高亮。
    /// </returns>
    /// <remarks>
    /// 为什么不用 FTS5 的 offsets()：FTS5 压根没有这个函数（那是 FTS3/4 的），
    /// 而 <c>highlight()</c> 输出的是切分后的词元串，跟原文对不上。
    /// 所以命中位置在原文上自己算 —— 用的是同一套分词规则，语义天然与 FTS 一致。
    ///
    /// 命中区间会跨过两者之间被丢弃的标点与空白：正文「咖啡，水温」搜「咖啡水温」，
    /// 区间是 0..5（含那个逗号），前端渲染出来是「&lt;mark&gt;咖啡，水温&lt;/mark&gt;」。
    /// </remarks>
    public static IReadOnlyList<TextMatch> FindMatches(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return Array.Empty<TextMatch>();
        }

        var tokens = TokenizeCore(text);
        var matches = new List<TextMatch>();

        foreach (var segment in SplitSegments(query))
        {
            var needle = Tokenize(segment);

            // 这一段在原文里找不到就跳过（不抛异常）—— 另一段可能命中。
            if (needle.Count == 0)
            {
                continue;
            }

            // 在词元序列上滑动比较：连续且完全相等的一段 = 一处命中。
            for (var i = 0; i + needle.Count <= tokens.Count; i++)
            {
                if (!MatchesAt(tokens, i, needle))
                {
                    continue;
                }

                var first = tokens[i];
                var last = tokens[i + needle.Count - 1];
                matches.Add(new TextMatch(first.Start, last.Start + last.Length - first.Start));
            }
        }

        return matches;
    }

    /// <summary>
    /// 切词的核心实现：一次遍历同时产出词元与它在原文里的偏移。
    /// </summary>
    /// <param name="text">待切分的原文。</param>
    /// <returns>词元序列，按出现顺序。</returns>
    private static List<TextToken> TokenizeCore(string text)
    {
        var tokens = new List<TextToken>();
        var word = new StringBuilder();
        var wordStart = 0;
        var offset = 0;

        // offset 一直跟着原文的 UTF-16 code unit 走：被丢弃的字符也占位置，
        // 否则后面的偏移会全部前移。
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.Utf16SequenceLength;

            if (IsAsciiLetterOrDigit(rune))
            {
                // ASCII 字母数字攒成一个词，遇到分隔符才结算。
                if (word.Length == 0)
                {
                    wordStart = offset;
                }

                // 逐个 rune 转小写：不能用 (char)rune —— 那样 emoji 会被截断成半个字符。
                word.Append(rune.ToString().ToLowerInvariant());
                offset += length;
                continue;
            }

            // 词元与词元之间要靠分隔符断开，先把攒着的 ASCII 词结算掉。
            FlushWord();

            // 只有词元字符（L*/N*）才成词元，其余（标点、符号、Cf、Cc、Mn……）一律丢弃。
            if (IsTokenChar(rune))
            {
                tokens.Add(new TextToken(rune.ToString().ToLowerInvariant(), offset, length));
            }

            offset += length;
        }

        FlushWord();

        return tokens;

        // 结算攒着的 ASCII 词：作为一个词元。
        void FlushWord()
        {
            if (word.Length == 0)
            {
                return;
            }

            tokens.Add(new TextToken(word.ToString(), wordStart, offset - wordStart));
            word.Clear();
        }
    }

    /// <summary>
    /// 判断一个字符是不是「词元字符」。
    /// </summary>
    /// <param name="rune">待判断的字符。</param>
    /// <returns>一般类别以 L（字母）或 N（数字）开头则返回 true。</returns>
    /// <remarks>
    /// 这条边界是**量出来的，不是猜的**：FTS5 的 <c>unicode61</c> 划 token 字符的依据与
    /// 「一般类别以 L 或 N 开头」**基本一致**（实测：<c>中</c>/<c>é</c>/<c>ʰ</c>/<c>0</c>/<c>①</c>/<c>〇</c>
    /// 是 token 字符；<c>，</c>/<c>—</c>/<c>$</c>/<c>→</c>/<c>😀</c>/零宽连接符/变体选择符/C1 控制字符
    /// 都是分隔符）。我们保留的集合**基本**是它的子集 → 这类偏差基本不会发生。
    ///
    /// 两处分叉：① unicode61 把 <c>Mn</c>（组合记号）当词内附加符**保留**，我们**丢弃** ——
    /// 丢掉的字符 unicode61 看不到（索引文本里已经没有），方向是安全的；
    /// ② unicode61 把 **21 个 <c>Lo</c> 码点**（New Tai Lue 元音符号
    /// <c>U+19B0..U+19C0</c>/<c>U+19C8</c>/<c>U+19C9</c>、Vedic 符号 <c>U+1CF2</c>/<c>U+1CF3</c>）
    /// 当分隔符而我们保留 —— **只有这 21 个码点上仍可能出现「FTS 命中却算不出高亮」**。
    /// </remarks>
    private static bool IsTokenChar(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber;
    }

    /// <summary>
    /// 判断一个码位是不是 ASCII 字母或数字。
    /// </summary>
    /// <param name="rune">待判断的字符。</param>
    /// <returns>是 ASCII 字母或数字则返回 true。</returns>
    /// <remarks>
    /// <see cref="Rune"/> 上没有现成的 <c>IsAsciiLetterOrDigit</c>（那是 <see cref="char"/> 的 API），
    /// 只能自己按码位区间判断：ASCII 字母数字是「一个词」与「逐字一个词」的分界线，
    /// 判错会让 <c>hello520</c> 被切成八个词元，英文词级匹配直接失效。
    /// </remarks>
    private static bool IsAsciiLetterOrDigit(Rune rune)
    {
        var value = rune.Value;
        return value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
    }

    /// <summary>
    /// 按空白把查询串切成若干段。
    /// </summary>
    /// <param name="query">用户输入的查询串。</param>
    /// <returns>各段文本，不含空白、不含空段。</returns>
    /// <remarks>
    /// 空白按 <see cref="Rune.IsWhiteSpace"/> 判定，与切词侧**同源**：切词侧的空白一律不是
    /// <c>L*</c>/<c>N*</c>，在那边也是被丢弃的分隔符。
    /// 另用一套 <c>char.IsWhiteSpace</c> 看着等价，实际是两套规则，迟早漂移。
    /// </remarks>
    private static List<string> SplitSegments(string query)
    {
        var segments = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in query.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(rune);
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    /// <summary>
    /// 判断词元序列从指定位置起是否与查询词元序列完全相等。
    /// </summary>
    /// <param name="tokens">原文的词元序列。</param>
    /// <param name="index">候选起点。</param>
    /// <param name="needle">查询的词元序列。</param>
    /// <returns>完全相等则返回 true。</returns>
    /// <remarks>
    /// 两边的词元都是切词时转过的小写，直接比即可（大小写不敏感由此而来）。
    /// </remarks>
    private static bool MatchesAt(List<TextToken> tokens, int index, IReadOnlyList<string> needle)
    {
        for (var i = 0; i < needle.Count; i++)
        {
            if (!string.Equals(tokens[index + i].Token, needle[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
