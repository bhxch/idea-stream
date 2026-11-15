using IdeaApp.Domain;

namespace IdeaApp.Tests;

/// <summary>
/// 分词器、MATCH 表达式与命中偏移的测试。
///
/// 这套规则是索引侧与查询侧共用的一套语义（规格 §7.1 / §9 / §12）：
/// 索引里存的 token 串与查询侧的 MATCH 串必须出自同一个实现，
/// 否则会出现「搜得到但高亮不出来」这种极难查的偏差。
/// 所以这里的每条断言都是钉死的边界，不允许「差不多就行」。
/// </summary>
public sealed class TextTokenizerTests
{
    [Fact]
    public void Tokenize_merges_ascii_letters_and_digits_into_one_lowercase_word()
    {
        // ASCII 字母数字合并成一个词并转小写；其余字符逐字一个。
        Assert.Equal(new[] { "hello520", "世", "界" }, TextTokenizer.Tokenize("Hello520世界"));
    }

    [Fact]
    public void Tokenize_splits_chinese_into_single_character_tokens()
    {
        // 中文没有词边界，逐字成词 → 短语查询等价于子串匹配。
        Assert.Equal(
            new[] { "手", "冲", "咖", "啡", "的", "关", "键", "在", "于", "水", "温" },
            TextTokenizer.Tokenize("手冲咖啡的关键在于水温"));
    }

    [Theory]
    [InlineData("项目, 温度!")]
    [InlineData("项目，温度！")]
    public void Tokenize_drops_punctuation_regardless_of_width(string text)
    {
        // 半角与全角一视同仁：都是分隔符，都不产生 token。
        Assert.Equal(new[] { "项", "目", "温", "度" }, TextTokenizer.Tokenize(text));
    }

    [Fact]
    public void Tokenize_drops_emoji_because_symbols_are_separators()
    {
        // emoji 是 Symbol，按规则丢弃（否则会变成一个没人会搜的 token）。
        Assert.Equal(new[] { "咖", "啡" }, TextTokenizer.Tokenize("👍 咖啡"));
    }

    [Fact]
    public void Tokenize_returns_nothing_for_punctuation_only_text()
    {
        Assert.Empty(TextTokenizer.Tokenize("..."));
    }

    [Fact]
    public void Analyze_reports_utf16_offsets_of_each_token()
    {
        // 逗号在索引里被丢弃，但偏移仍按**原文**算：它占了 1 个 code unit。
        Assert.Equal(
            new[]
            {
                new TextToken("咖", 0, 1),
                new TextToken("啡", 1, 1),
                new TextToken("水", 3, 1),
                new TextToken("温", 4, 1),
            },
            TextTokenizer.Analyze("咖啡，水温"));
    }

    [Fact]
    public void Analyze_counts_an_astral_rune_as_two_utf16_code_units()
    {
        // 👍 是代理对，占 2 个 code unit → 后面的 token 起点要往后挪 2。
        Assert.Equal(
            new[]
            {
                new TextToken("咖", 2, 1),
                new TextToken("啡", 3, 1),
            },
            TextTokenizer.Analyze("👍咖啡"));
    }

    [Theory]
    [InlineData("咖啡", "\"咖 啡\"")]
    [InlineData("咖啡 水温", "\"咖 啡\" AND \"水 温\"")]
    [InlineData("Hello520", "\"hello520\"")]
    [InlineData("咖啡 ... 水温", "\"咖 啡\" AND \"水 温\"")]
    public void BuildMatchExpression_joins_tokens_of_a_segment_as_a_phrase(string query, string expected)
    {
        // 段内是短语查询（token 相邻），段间是 AND（都出现即可）。
        // 返回值两侧带双引号，是 FTS5 的短语语法。
        Assert.Equal(expected, TextTokenizer.BuildMatchExpression(query));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildMatchExpression_returns_null_when_the_query_has_no_token(string query)
    {
        // 全空 = 没有关键词 = 不过滤。
        Assert.Null(TextTokenizer.BuildMatchExpression(query));
    }

    [Fact]
    public void FindMatches_locates_a_chinese_phrase_in_the_text()
    {
        Assert.Equal(new[] { new TextMatch(2, 2) }, TextTokenizer.FindMatches("手冲咖啡的关键", "咖啡"));
    }

    [Fact]
    public void FindMatches_reports_every_occurrence_separately()
    {
        Assert.Equal(
            new[] { new TextMatch(0, 2), new TextMatch(3, 2) },
            TextTokenizer.FindMatches("咖啡和咖啡", "咖啡"));
    }

    [Fact]
    public void FindMatches_spans_across_dropped_punctuation()
    {
        // 逗号不是 token → token 仍相邻 → 命中区间跨过逗号（含逗号）。
        Assert.Equal(new[] { new TextMatch(0, 5) }, TextTokenizer.FindMatches("咖啡，水温", "咖啡水温"));
    }

    [Fact]
    public void FindMatches_does_not_match_across_a_chinese_stop_character()
    {
        // 「的」是汉字 token，会打断相邻 → 不命中。
        Assert.Empty(TextTokenizer.FindMatches("咖啡的水温", "咖啡水温"));
    }

    [Fact]
    public void FindMatches_reports_one_hit_per_query_segment()
    {
        Assert.Equal(
            new[] { new TextMatch(0, 2), new TextMatch(2, 2) },
            TextTokenizer.FindMatches("咖啡水温", "咖啡 水温"));
    }

    [Fact]
    public void FindMatches_requires_the_whole_english_word()
    {
        // 英文是词级：搜 "hel" 命中不了 "Hello"。
        Assert.Equal(new[] { new TextMatch(0, 5) }, TextTokenizer.FindMatches("Hello world", "hello"));
        Assert.Empty(TextTokenizer.FindMatches("Hello world", "hel"));
    }

    [Fact]
    public void FindMatches_counts_offsets_across_lines()
    {
        // 换行占 1 个 code unit，且被丢弃，但偏移仍按原文算。
        Assert.Equal(new[] { new TextMatch(7, 2) }, TextTokenizer.FindMatches("第一行\n第二行咖啡", "咖啡"));
    }

    [Fact]
    public void FindMatches_returns_nothing_for_a_query_without_token()
    {
        Assert.Empty(TextTokenizer.FindMatches("咖啡", "..."));
    }
}
