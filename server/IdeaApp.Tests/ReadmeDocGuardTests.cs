using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdeaApp.Tests;

/// <summary>
/// 两份 README（中文与英文版）的**漂移守卫**。
///
/// <para>
/// README 里写死的那些**数字与清单**是最容易漂的东西：服务端加一个端点、
/// 闸门加一步、SDK 换了版本，文档里的「33 个业务端点」「29 条路径 / 36 个操作」
/// 「八道闸门」就悄悄过期了 —— 而过期得**看不出来**（数字还在，只是不准了）。
/// </para>
///
/// <para>
/// 这些守卫把文档钉在**事实来源**上：
/// <list type="bullet">
/// <item>端点清单与条数 → 契约文件 <c>openapi/idea-v1.json</c>；</item>
/// <item>闸门步数 → <c>verify.sh</c> 的 <c>TOTAL_STEPS</c>；</item>
/// <item>SDK 版本 → 仓库根的 <c>global.json</c>。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么要有这一组，而不放进文档闸门脚本</b>：仓库里
/// <c>tools/check-readme.mjs</c> 已经查链接、命令、路径与配置默认值；
/// 但那一道要 node，而 <c>dotnet test</c> 是每个改动都会跑的那道 ——
/// <b>数字漂了，后端测试就该红</b>。
/// 两边查的东西不同：脚本查「写错了」，这里查「写过期了」。
/// </para>
/// </summary>
public sealed class ReadmeDocGuardTests
{
    /// <summary>README 里端点表的一行：<c>| 能力 | GET /api/ideas |</c>。</summary>
    /// <remarks>
    /// 只认**第二格**里「方法 + 路径」的写法 —— 端点表就是这个形状。
    /// 路径不允许有空格（表格单元格里不会）。
    /// </remarks>
    private static readonly Regex EndpointRowPattern = new(
        @"^\|[^|]*\|\s*(?<method>GET|POST|PUT|PATCH|DELETE)\s+(?<path>[^\s|]+)\s*\|",
        RegexOptions.CultureInvariant);

    /// <summary>契约里的操作集合（方法 + 路径）。</summary>
    /// <remarks>
    /// 契约文件里 <c>/openapi/**</c> 那类路径没有操作（只有 servers 之类），
    /// 逐个操作对象扫一遍就够。
    /// </remarks>
    private static HashSet<(string Method, string Path)> ContractOperations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Contract));

        var operations = new HashSet<(string Method, string Path)>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                operations.Add((operation.Name.ToUpperInvariant(), path.Name));
            }
        }

        return operations;
    }

    /// <summary>契约里的路径数（不含 <c>/openapi/**</c>）。</summary>
    private static int ContractPathCount()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Contract));

        return document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Count(path => !path.Name.StartsWith("/openapi/", StringComparison.Ordinal));
    }

    /// <summary>契约里的**业务**操作数（不含 <c>/skill/**</c> 与 <c>/openapi/**</c>）。</summary>
    /// <remarks>
    /// README 里说的「33 个业务端点」说的是这一批 —— 分发端点另算。
    /// </remarks>
    private static int ContractBusinessOperationCount()
    {
        return ContractOperations()
            .Count(operation =>
                !operation.Path.StartsWith("/skill/", StringComparison.Ordinal)
                && !operation.Path.StartsWith("/openapi/", StringComparison.Ordinal));
    }

    /// <summary>README 里端点表的行（方法 + 路径）。</summary>
    private static List<(string Method, string Path)> ReadmeEndpointRows(string file)
    {
        var rows = new List<(string Method, string Path)>();

        foreach (var line in File.ReadAllLines(file))
        {
            var match = EndpointRowPattern.Match(line);

            if (match.Success)
            {
                rows.Add((match.Groups["method"].Value, match.Groups["path"].Value));
            }
        }

        return rows;
    }

    /// <summary>
    /// README 的端点表必须**恰好**是契约里的那些操作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条守的是「服务端 → 文档」这一向：服务端加了能力而 README 没写，
    /// 读者照着 README 调不到那条路；反过来 README 写了契约里没有的端点，
    /// 读者照着调只会拿到 404。
    /// </para>
    /// <para>
    /// 比的是 **(方法, 路径) 二元组**，不是子串：<c>GET /api/ideas</c> 是
    /// <c>GET /api/ideas/{id}</c> 的前缀，只做子串匹配的话，
    /// 「能力清单里整行删掉了一条端点」这种漂移会漏过去。
    /// </para>
    /// <para>
    /// <b>重复也要红</b>：同一对出现两次，说明表里多了一行 —— 集合比不出来，
    /// 所以这里另外比一次条数。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_readme_endpoint_tables_list_exactly_the_contract_operations()
    {
        var expected = ContractOperations();

        foreach (var file in Readmes)
        {
            var rows = ReadmeEndpointRows(file.Path);
            var actual = rows.ToHashSet();

            var missing = expected.Except(actual).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            var extra = actual.Except(expected).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();

            Assert.True(
                missing.Length == 0 && extra.Length == 0 && rows.Count == actual.Count,
                $"{file.Name} 的端点表与契约对不上："
                + (missing.Length > 0 ? $"少了 {string.Join("、", missing.Select(item => $"{item.Method} {item.Path}"))}；" : string.Empty)
                + (extra.Length > 0 ? $"多了 {string.Join("、", extra.Select(item => $"{item.Method} {item.Path}"))}；" : string.Empty)
                + (rows.Count != actual.Count ? $"表里有 {rows.Count} 行，但只有 {actual.Count} 个不同的端点（重复了）。" : string.Empty)
                + "端点表必须与 openapi/idea-v1.json 一一对应。");
        }
    }

    /// <summary>
    /// README 里写死的**条数**必须与契约对得上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上一轮这三个数字互不相同（手册索引页 33 行 / README 表 35 行 / 契约 29 路径 36 操作），
    /// 而文档里还写着「与契约保持同步」—— 数字错了没人看得出来。
    /// </para>
    /// <para>
    /// 这里把文档里的数字**抽出来**跟契约比，而不是把数字抄进测试里 ——
    /// 抄进去只是把同一份会过期的东西又造了一份。
    /// 服务端加了端点而文档没改时，这条会红，并告诉它实际是多少。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_endpoint_counts_in_the_readmes_match_the_contract()
    {
        var business = ContractBusinessOperationCount();
        var paths = ContractPathCount();
        var operations = ContractOperations().Count;

        foreach (var file in Readmes)
        {
            var text = File.ReadAllText(file.Path);

            foreach (var claim in file.BusinessCountPatterns)
            {
                AssertCount(text, claim, business, file.Name, "业务端点");
            }

            foreach (var claim in file.PathCountPatterns)
            {
                AssertCount(text, claim, paths, file.Name, "路径");
            }

            foreach (var claim in file.OperationCountPatterns)
            {
                AssertCount(text, claim, operations, file.Name, "操作");
            }
        }
    }

    /// <summary>
    /// 文档里写的那个数必须等于实际的那个数（报错时要说是哪个文件、哪个数）。
    /// </summary>
    private static void AssertCount(string text, string pattern, int expected, string file, string what)
    {
        var claimed = ClaimedNumber(text, pattern, file);

        Assert.True(
            claimed == expected,
            $"{file} 说有 {claimed} 个{what}，契约里是 {expected} 个 —— "
            + "文档里的数字过期了（要么改文档，要么改契约）。");
    }

    /// <summary>
    /// 文档里写的那个数（抽出来；抽不到就是文档写法变了，守卫要红）。
    /// </summary>
    private static int ClaimedNumber(string text, string pattern, string file)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);

        Assert.True(
            match.Success,
            $"{file} 里找不到这个计数（{pattern}）—— 守卫定位不到它。文档改了写法就同步改守卫。");

        return int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// README 写下的闸门步数必须与 <c>verify.sh</c> 的 <c>TOTAL_STEPS</c> 一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 闸门加一步而文档没改，读者照着文档跑会少跑一道 —— 而少跑的那道看起来是绿的。
    /// </para>
    /// <para>
    /// 只看**闸门那几节**（「CI 与闸门」/「测试」/「CI and Gates」/「Testing」）：
    /// 快速开始那一节也写着步数（「五步」），那是另一回事。
    /// </para>
    /// <para>
    /// <b>「一」不算计数</b>：中文的「任一步」「这一步」与英文的「one step at a time」
    /// 是量词。把它们当计数会把没漂移的文档判成漂了 —— 而一条会误报的守卫，
    /// 很快就没人看了。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_readmes_state_the_gate_step_count_that_verify_sh_runs()
    {
        var steps = VerifyStepCount();

        foreach (var file in Readmes)
        {
            var text = File.ReadAllText(file.Path);
            var sections = GateSections(text, file);

            Assert.True(
                sections.Count > 0,
                $"{file.Name} 里找不到讲闸门的那一节（标题要含「{file.SectionHint}」）—— 守卫定位不到它。");

            var mentions = 0;

            foreach (var section in sections)
            {
                // 步表里的行号必须恰好是 1..N。
                var rows = SectionRowNumbers(section);

                if (rows.Count > 0)
                {
                    var expected = Enumerable.Range(1, steps).ToArray();

                    Assert.True(
                        rows.SequenceEqual(expected),
                        $"{file.Name} 闸门那节的步表行号是 [{string.Join(",", rows)}]，应当恰好是 1..{steps}。");
                }

                // 正文里写的步数也要对得上。
                foreach (Match match in Regex.Matches(section, file.StepCountPattern, RegexOptions.CultureInvariant))
                {
                    mentions++;

                    var claimed = file.ChineseNumerals
                        ? ChineseValue(match.Groups["count"].Value, file.Name)
                        : EnglishValue(match.Groups["count"].Value, file.Name);

                    Assert.True(
                        claimed == steps,
                        $"{file.Name} 说闸门有 {claimed} 道，而 verify.sh 是 {steps} 步 —— 文档与脚本对不上。");
                }
            }

            // 防假绿：一条都没扫到时，上面的循环等于没跑。
            Assert.True(
                mentions > 0,
                $"{file.Name} 的闸门那节里一处步数都没提到 —— 守卫会假绿，请检查写法"
                + $"（正则：{file.StepCountPattern}）。");
        }
    }

    /// <summary>
    /// 取出标题命中的那些小节（到**同级或更高级**的标题为止）。
    /// </summary>
    private static List<string> GateSections(string text, Readme file)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sections = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var heading = Regex.Match(lines[index], @"^(#{2,6})\s+(.*)$");

            if (!heading.Success || !file.SectionPattern.IsMatch(heading.Groups[2].Value))
            {
                continue;
            }

            var level = heading.Groups[1].Value.Length;
            var body = new List<string> { lines[index] };

            for (var next = index + 1; next < lines.Length; next++)
            {
                var nextHeading = Regex.Match(lines[next], @"^(#{2,6})\s");

                if (nextHeading.Success && nextHeading.Groups[1].Value.Length <= level)
                {
                    break;
                }

                body.Add(lines[next]);
            }

            sections.Add(string.Join("\n", body));
        }

        return sections;
    }

    /// <summary>一节里表格第一列是数字的那些行的行号。</summary>
    private static List<int> SectionRowNumbers(string section) =>
        Regex.Matches(section, @"^\|\s*(\d+)\s*\|", RegexOptions.CultureInvariant)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    /// <summary>
    /// 中文数字 → 数字（「八」→ 8）。
    /// </summary>
    private static int ChineseValue(string word, string file)
    {
        var digits = "零一二三四五六七八九";

        if (word == "十")
        {
            return 10;
        }

        var value = 0;

        foreach (var character in word)
        {
            var digit = digits.IndexOf(character);

            Assert.True(digit >= 0, $"{file} 的步数写成了「{word}」，认不出来。");

            if (digit > 0)
            {
                value = digit;
            }
        }

        return value;
    }

    /// <summary>
    /// 英文数字 → 数字（「eight」→ 8；「8」→ 8）。
    /// </summary>
    private static int EnglishValue(string word, string file)
    {
        if (int.TryParse(word, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        var words = new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
        var index = Array.IndexOf(words, word.ToLowerInvariant());

        Assert.True(index >= 0, $"{file} 的步数写成了「{word}」，认不出来。");

        return index;
    }

    /// <summary><c>verify.sh</c> 里的闸门步数。</summary>
    private static int VerifyStepCount()
    {
        var match = Regex.Match(
            File.ReadAllText(RepoPaths.VerifyScript),
            @"TOTAL_STEPS=(\d+)",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success, "verify.sh 里找不到 TOTAL_STEPS —— 守卫定位不到闸门步数。");

        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// README 写下的 SDK 版本必须与 <c>global.json</c> 钉住的一致。
    /// </summary>
    /// <remarks>
    /// <c>global.json</c> 是事实来源（工具层面真正拦人的那一份）；
    /// README 只是把它写出来给人看 —— 两边不一致时，以 <c>global.json</c> 为准，
    /// 但文档也得跟着改。
    /// </remarks>
    [Fact]
    public void The_readmes_pin_the_sdk_version_that_global_json_pins()
    {
        var version = PinnedSdkVersion();

        foreach (var file in Readmes)
        {
            var text = File.ReadAllText(file.Path);

            // 前置条件表里那行「.NET SDK」必须写着 global.json 钉住的版本。
            //
            // 只查「文档里出现过这个版本」是不够的：文档里别处（自检那一行、
            // 已知限制那一节）也会出现同一个版本号，前置条件表写错了照样绿。
            var row = text.Replace("\r\n", "\n").Split('\n')
                .FirstOrDefault(line => line.StartsWith("| .NET SDK", StringComparison.Ordinal));

            Assert.False(
                row is null,
                $"{file.Name} 的前置条件表里没有「.NET SDK」那一行 —— 守卫定位不到它。");

            Assert.True(
                row!.Contains(version, StringComparison.Ordinal),
                $"{file.Name} 的前置条件表里 .NET SDK 那一行没有写 global.json 钉住的版本 {version}：{row.Trim()}");

            Assert.False(
                Regex.IsMatch(text, file.NoGlobalJsonPattern, RegexOptions.CultureInvariant),
                $"{file.Name} 说仓库里没有 global.json，但仓库根有（它把 SDK 钉在 {version}）。");
        }
    }

    /// <summary><c>global.json</c> 里钉住的 SDK 版本。</summary>
    private static string PinnedSdkVersion()
    {
        // global.json 允许注释（JSON.parse 读不了），先去掉 // 与 /* */。
        var raw = Regex.Replace(File.ReadAllText(RepoPaths.GlobalJson), @"/\*[\s\S]*?\*/", string.Empty);
        raw = Regex.Replace(raw, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);

        using var document = JsonDocument.Parse(raw);
        var version = document.RootElement.GetProperty("sdk").GetProperty("version").GetString();

        Assert.False(string.IsNullOrWhiteSpace(version), "global.json 里没有钉住 SDK 版本。");

        return version!;
    }

    /// <summary>
    /// 搜索的关键词不许被写成「可选」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GET /api/search</c> 的 <c>q</c> 在契约里是**必填**（缺失 → 400
    /// <c>invalid_search_query</c>；见 <c>OpenApiDocumentTests</c>）。
    /// 文档里曾经写成「契约里标成可选」—— 与契约正好相反，
    /// 而照着读的人会一直带着空关键词去调，然后拿到 400 却不知道原因。
    /// </para>
    /// <para>
    /// 这里只查**反向**：凡是提到 <c>q</c> 的那一行，都必须说它是必填的。
    /// 换个说法（「必填」「required」）都认得；写成「可选」就红。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_readmes_do_not_call_the_search_query_optional()
    {
        foreach (var file in Readmes)
        {
            var lines = File.ReadAllLines(file.Path);

            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains("`q`", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.True(
                    Regex.IsMatch(lines[index], file.RequiredPattern, RegexOptions.CultureInvariant),
                    $"{file.Name} 第 {index + 1} 行提到 `q` 却没说它必填：{lines[index].Trim()} —— "
                    + "GET /api/search 的 q 在契约里是必填的（缺失 → 400 invalid_search_query）。");
            }
        }
    }

    /// <summary>两份 README 各自的路径与写法差异。</summary>
    /// <param name="Name">文件名（报错时用）。</param>
    /// <param name="Path">文件路径。</param>
    /// <param name="BusinessCountPatterns">「业务端点」那个数的写法。</param>
    /// <param name="PathCountPatterns">「路径」那个数的写法。</param>
    /// <param name="OperationCountPatterns">「操作」那个数的写法。</param>
    /// <param name="StepCountPattern">闸门步数的写法。</param>
    /// <param name="ChineseNumerals">步数是不是用中文数字写的。</param>
    /// <param name="SectionPattern">哪几节算「讲闸门的那几节」。</param>
    /// <param name="SectionHint">标题里该出现的词（报错时给人看）。</param>
    /// <param name="NoGlobalJsonPattern">「仓库里没有 global.json」的写法。</param>
    /// <param name="RequiredPattern">「必填」的写法。</param>
    private sealed record Readme(
        string Name,
        string Path,
        string[] BusinessCountPatterns,
        string[] PathCountPatterns,
        string[] OperationCountPatterns,
        string StepCountPattern,
        bool ChineseNumerals,
        Regex SectionPattern,
        string SectionHint,
        string NoGlobalJsonPattern,
        string RequiredPattern);

    /// <summary>中文版的那些写法。</summary>
    private static readonly Readme Chinese = new(
        Name: "README.md",
        Path: RepoPaths.ReadmeZh,
        BusinessCountPatterns: [@"(?<count>\d+)\s*个业务端点"],
        PathCountPatterns: [@"(?<count>\d+)\s*条路径"],
        OperationCountPatterns: [@"(?<count>\d+)\s*个操作"],
        // 「一」不算计数（量词）：「任一步」「这一步」不是步数。
        StepCountPattern: @"(?<count>[零二三四五六七八九十\d]+)\s*(?:道闸门|步)",
        ChineseNumerals: true,
        SectionPattern: new Regex("闸门|测试", RegexOptions.CultureInvariant),
        SectionHint: "闸门",
        NoGlobalJsonPattern: @"没有\s*`?global\.json`?",
        RequiredPattern: @"必填|required");

    /// <summary>英文版的那些写法。</summary>
    private static readonly Readme English = new(
        Name: "README_EN.md",
        Path: RepoPaths.ReadmeEn,
        BusinessCountPatterns: [@"(?<count>\d+)\s+business endpoints"],
        PathCountPatterns: [@"(?<count>\d+)\s+paths\b"],
        OperationCountPatterns: [@"(?<count>\d+)\s+operations\b"],
        // 「one」不算计数（量词）：「one step at a time」不是步数。
        StepCountPattern: @"\b(?<count>two|three|four|five|six|seven|eight|nine|ten|\d+)\s+(?:steps?|gates?)\b",
        ChineseNumerals: false,
        SectionPattern: new Regex("Gates|Testing", RegexOptions.IgnoreCase),
        SectionHint: "Gates",
        NoGlobalJsonPattern: @"no\s+`?global\.json`?",
        RequiredPattern: @"required");

    /// <summary>两份 README（按 <see cref="Chinese"/>、<see cref="English"/> 的顺序）。</summary>
    private static readonly Readme[] Readmes = [Chinese, English];
}
