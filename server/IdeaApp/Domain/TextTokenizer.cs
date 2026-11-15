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
/// <item>其余字符逐字成一个词元。</item>
/// <item>空白是分隔符，不产生词元。</item>
/// <item>标点与符号一律丢弃（半角与全角一视同仁）。</item>
/// </list>
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
/// 判定用 <see cref="Rune.IsPunctuation"/> / <see cref="Rune.IsSymbol"/> /
/// <see cref="Rune.IsWhiteSpace"/> —— 这些 API 在 <c>InvariantGlobalization=true</c> 下也可用，
/// 不依赖 ICU。
/// </summary>
public static class TextTokenizer
{
    /// <summary>
    /// 切词：ASCII 字母数字合并成词并转小写；其余字符逐字一个；
    /// 标点与空白丢弃。索引侧与查询侧共用这一个实现。
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

            // 空白、标点、符号一律丢弃（半角全角一样）—— 它们只当分隔符。
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune) && !Rune.IsSymbol(rune))
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
    private static string[] SplitSegments(string query)
    {
        // separator 传 null = 按空白切（char.IsWhiteSpace 与 Rune.IsWhiteSpace 是同一套码位）。
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
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
