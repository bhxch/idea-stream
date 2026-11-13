using System.Globalization;
using System.Text.RegularExpressions;
using IdeaApp.Contracts;

namespace IdeaApp.Tests;

/// <summary>
/// 错误码清单的守卫。
///
/// <para>
/// <see cref="ApiErrorCodes"/> 是契约文件里错误码取值的**唯一来源**：
/// OpenAPI 契约、skill 计划的错误码对照表都从它取值。三者一旦对不上，
/// agent 拿到的契约就会说一套、实际返回另一套 —— 而且不会有任何测试失败。
/// </para>
///
/// <para>
/// 这里扫源码来核对：
/// <list type="bullet">
/// <item>实际抛出的错误码（<c>new ApiException(状态码, "错误码", …)</c>）必须都在清单里；</item>
/// <item>清单里的错误码必须都还有人抛（没人抛的登记是过期信息）；</item>
/// <item>端点声明的错误码（<c>ProducesApiProblem(...)</c>）必须在清单里，
/// 且声明的状态码与清单一致。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>扫描必须认全所有写法</b>。状态码既可能是字面量 <c>400</c>，
/// 也可能是常量 <c>StatusCodes.Status400BadRequest</c>；错误码必须是字符串字面量。
/// 扫不出来（状态码从常量/变量来、错误码不是字面量、错误码不符合 <c>[a-z0-9_]+</c>）
/// 的抛出点一律**报错**而不是跳过 —— 跳过就等于让守卫对这个码失明：
/// 码没进 <c>ApiErrorCodes.All</c>，契约的 ProblemDetails 取值全集里也就没有它，
/// 而测试全绿，没人会发现。
/// </para>
/// </summary>
public sealed class ApiErrorCodeTests
{
    /// <summary>
    /// 抛出点：<c>new ApiException(…)</c>。
    /// </summary>
    /// <remarks>
    /// 必须带 <c>new</c>：注释里写着 <c>catch (...) =&gt; ApiException(400, "x", …)</c>
    /// 这种示例，那不是真的抛出点。
    /// </remarks>
    private static readonly Regex CallSitePattern = new(
        @"\bnew\s+[\w.]*ApiException\s*\(",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 抛出点的参数：状态码 + 错误码。
    /// </summary>
    /// <remarks>
    /// 状态码先整个抓下来再单独解析（见 <see cref="LiteralStatusPattern"/> 与
    /// <see cref="ConstantStatusPattern"/>），而不是在一条正则里分两支：
    /// <c>StatusCodes.Status400BadRequest</c> 的数字后面还跟着名字后缀
    /// （<c>BadRequest</c>），写成 <c>Status(\d{3})\s*,</c> 是匹配不上的。
    /// 错误码捕获的是任意引号里的内容（不预筛 <c>[a-z0-9_]+</c>），
    /// 不合约定时单独报错，不能让它整个匹配不上。
    /// </remarks>
    private static readonly Regex ThrownPattern = new(
        @"\bnew\s+[\w.]*ApiException\s*\(\s*(?<status>[^,]+?)\s*,\s*""(?<code>[^""]*)""",
        RegexOptions.CultureInvariant);

    /// <summary>状态码字面量：400。</summary>
    private static readonly Regex LiteralStatusPattern = new(
        @"^\d{3}$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 状态码常量：StatusCodes.Status400BadRequest → 400。
    /// </summary>
    /// <remarks>
    /// 数字后面那截名字（<c>BadRequest</c>）只是常量名的一部分，忽略掉；
    /// 前面也允许带命名空间（<c>Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest</c>）。
    /// </remarks>
    private static readonly Regex ConstantStatusPattern = new(
        @"^(?:[\w.]+\.)?StatusCodes\s*\.\s*Status(\d{3})",
        RegexOptions.CultureInvariant);

    /// <summary>错误码的约定：只允许小写字母、数字与下划线。</summary>
    private static readonly Regex CodeConventionPattern = new(
        "^[a-z0-9_]+$",
        RegexOptions.CultureInvariant);

    /// <summary>端点声明的错误响应：ProducesApiProblem(StatusCodes.Status400BadRequest, "a", "b")。</summary>
    /// <remarks>
    /// 前面的点号是必须的：没有它会连辅助方法自己的**声明**一起扫到
    /// （那里没有状态码）。参数里也没有嵌套的括号（状态码是
    /// <c>StatusCodes.Status400BadRequest</c> 这样的常量），
    /// 所以到第一个右括号为止就是这次调用的全部参数。
    /// </remarks>
    private static readonly Regex DeclaredPattern = new(
        "\\.ProducesApiProblem\\(([^)]*)\\)",
        RegexOptions.CultureInvariant);

    /// <summary>声明里的状态码：StatusCodes.Status400BadRequest → 400。</summary>
    private static readonly Regex StatusPattern = new(
        "StatusCodes\\.Status(\\d{3})",
        RegexOptions.CultureInvariant);

    /// <summary>声明里的错误码字面量："invalid_content"。</summary>
    private static readonly Regex CodeLiteralPattern = new(
        "\"([a-z0-9_]+)\"",
        RegexOptions.CultureInvariant);

    /// <summary>清单里的错误码必须与代码里实际抛出的完全一致。</summary>
    [Fact]
    public void Documented_codes_match_the_ones_actually_thrown()
    {
        var thrown = ScanThrownCodes();
        var documented = DocumentedCodes();

        var missing = MissingFromManifest(thrown.Codes, documented);

        Assert.True(
            missing.Count == 0,
            $"代码里抛了但 ApiErrorCodes.All 里没登记：{string.Join("、", missing)}");

        foreach (var (code, status) in thrown.Codes)
        {
            Assert.Equal(status, documented[code]);
        }

        var stale = documented.Keys
            .Where(code => !thrown.Codes.ContainsKey(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"ApiErrorCodes.All 里登记了但已经没人抛：{string.Join("、", stale)}");
    }

    /// <summary>
    /// 每一个抛出点都得扫得出来。
    /// </summary>
    /// <remarks>
    /// 扫不出来（状态码从常量/变量来、错误码不是字面量、错误码不符合约定）
    /// 的抛出点如果被跳过，守卫就对这个码失明了 —— 那才是真正的静默漏洞。
    /// </remarks>
    [Fact]
    public void Every_throw_site_can_be_read_by_the_guard()
    {
        var scan = ScanThrownCodes();

        Assert.True(
            scan.Problems.Count == 0,
            "有抛出点扫不出来（守卫会对它们失明）："
            + string.Join("；", scan.Problems));
    }

    /// <summary>
    /// 用常量状态码（<c>StatusCodes.Status400BadRequest</c>）抛出的码也要被扫到。
    /// </summary>
    /// <remarks>
    /// 这就是原来那个静默洞：正则只认字面量三位数，
    /// <c>new ApiException(StatusCodes.Status400BadRequest, "brand_new_code", …)</c>
    /// 扫不到 → 守卫全绿 → 而这个码并不会进契约的 ProblemDetails 取值全集
    /// （那里由 <c>ApiErrorCodes.All</c> 生成）→ 契约与代码就此不一致且无人报警。
    /// </remarks>
    [Fact]
    public void Thrown_codes_are_found_even_when_the_status_is_a_StatusCodes_constant()
    {
        const string source = """
            throw new ApiException(StatusCodes.Status400BadRequest, "brand_new_code", "消息");
            """;

        var scan = ScanThrownCodes(source, "片段");

        Assert.Empty(scan.Problems);
        Assert.True(
            scan.Codes.ContainsKey("brand_new_code"),
            "用常量状态码抛出的码没被扫到");
        Assert.Equal(400, scan.Codes["brand_new_code"]);

        // 扫到了还不够：它没登记在清单里，所以真这么抛的话守卫会红。
        Assert.Null(ApiErrorCodes.Find("brand_new_code"));

        // 端到端：清单核对这一步也得把它算成「漏登记」，守卫才是真的会报警。
        var missing = MissingFromManifest(scan.Codes, DocumentedCodes());

        Assert.Contains("brand_new_code", missing);
    }

    /// <summary>
    /// 带命名空间前缀的状态码常量也要认得出。
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest</c>
    /// 与 <c>StatusCodes.Status400BadRequest</c> 是同一个常量，
    /// 认不出带前缀的那种就又是一个静默洞。
    /// </remarks>
    [Fact]
    public void Thrown_codes_are_found_even_when_the_status_constant_is_namespace_qualified()
    {
        const string source = """
            throw new ApiException(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest, "brand_new_code", "消息");
            """;

        var scan = ScanThrownCodes(source, "片段");

        Assert.Empty(scan.Problems);
        Assert.True(
            scan.Codes.ContainsKey("brand_new_code"),
            "带命名空间前缀的状态码常量没被扫到");
        Assert.Equal(400, scan.Codes["brand_new_code"]);
    }

    /// <summary>
    /// 状态码或错误码不是字面量的抛出点要报错，不能被跳过。
    /// </summary>
    /// <remarks>
    /// 提示里要带上**认不出来的那个表达式**，否则改的人还得自己回去找。
    /// </remarks>
    [Fact]
    public void A_throw_site_the_guard_cannot_read_is_reported()
    {
        const string source = """
            throw new ApiException(ErrorCodes.BadRequest, "brand_new_code", "消息");
            """;

        var scan = ScanThrownCodes(source, "片段");

        Assert.Empty(scan.Codes);
        Assert.NotEmpty(scan.Problems);
        Assert.Contains(
            scan.Problems,
            problem => problem.Contains("ErrorCodes.BadRequest", StringComparison.Ordinal));
    }

    /// <summary>
    /// 错误码字面量不符合 <c>[a-z0-9_]+</c> 约定要报错，不能被跳过。
    /// </summary>
    [Fact]
    public void A_code_that_does_not_follow_the_convention_is_reported()
    {
        const string source = """
            throw new ApiException(400, "not-found", "消息");
            """;

        var scan = ScanThrownCodes(source, "片段");

        Assert.Empty(scan.Codes);
        Assert.NotEmpty(scan.Problems);
    }

    /// <summary>端点声明的错误码必须在清单里，且状态码一致。</summary>
    [Fact]
    public void Endpoint_declared_codes_are_documented_with_the_right_status()
    {
        var declared = ScanDeclaredCodes();

        Assert.True(declared.Count > 0, "一个 ProducesApiProblem 都没扫到（扫描规则坏了？）");

        foreach (var (status, code) in declared)
        {
            var known = ApiErrorCodes.Find(code);

            Assert.True(
                known is not null,
                $"端点声明的错误码 {code} 不在 ApiErrorCodes 里（契约里会没有它的说明）");

            Assert.Equal(status, known!.Status);
        }
    }

    /// <summary>清单里的错误码 → 状态码。</summary>
    private static Dictionary<string, int> DocumentedCodes() =>
        ApiErrorCodes.All.ToDictionary(
            code => code.Code,
            code => code.Status,
            StringComparer.Ordinal);

    /// <summary>抛了但清单里没登记的错误码（守卫就是靠这个报错）。</summary>
    private static List<string> MissingFromManifest(
        IReadOnlyDictionary<string, int> thrown,
        IReadOnlyDictionary<string, int> documented) =>
        thrown.Keys
            .Where(code => !documented.ContainsKey(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

    /// <summary>扫描结果：错误码 → 状态码，外加「扫不出来 / 不合约定」的问题清单。</summary>
    private sealed record ScanResult(
        IReadOnlyDictionary<string, int> Codes,
        IReadOnlyList<string> Problems);

    /// <summary>扫源码：实际抛出的错误码 → 状态码。</summary>
    private static ScanResult ScanThrownCodes()
    {
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var file in RepoPaths.SourceFiles(RepoPaths.AppSource))
        {
            var origin = Path.GetRelativePath(RepoPaths.Root, file);
            var scan = ScanThrownCodes(File.ReadAllText(file), origin);

            problems.AddRange(scan.Problems);

            foreach (var (code, status) in scan.Codes)
            {
                if (codes.TryGetValue(code, out var existing))
                {
                    if (existing != status)
                    {
                        problems.Add(
                            $"错误码 {code} 在源码里对应了两个状态码（{existing} 与 {status}）");
                    }

                    continue;
                }

                codes[code] = status;
            }
        }

        return new ScanResult(codes, problems);
    }

    /// <summary>扫一段源码：实际抛出的错误码 → 状态码。</summary>
    /// <param name="text">源码文本。</param>
    /// <param name="origin">出处（文件路径或「片段」），只用来拼报错信息。</param>
    private static ScanResult ScanThrownCodes(string text, string origin)
    {
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var problems = new List<string>();

        var source = SourceText.StripComments(text);

        foreach (Match callSite in CallSitePattern.Matches(source))
        {
            var match = ThrownPattern.Match(source, callSite.Index);

            // 匹配不到（或匹配到的不是当前这个抛出点）说明这个抛出点写法是扫不出来的：
            // 状态码不是字面量、错误码不是字面量。报出来，不跳过。
            if (!match.Success || match.Index != callSite.Index)
            {
                problems.Add(
                    $"{origin}:{SourceText.LineNumber(source, callSite.Index)} 的 ApiException 抛出点扫不出来"
                    + "（状态码必须是 400 或 StatusCodes.Status400BadRequest 这样的字面量/常量，"
                    + "错误码必须是字符串字面量）");
                continue;
            }

            // 状态码两个分支都要认：字面量 400，或者常量 StatusCodes.Status400BadRequest。
            var statusText = match.Groups["status"].Value.Trim();
            var code = match.Groups["code"].Value;

            int status;

            if (LiteralStatusPattern.IsMatch(statusText))
            {
                status = int.Parse(statusText, CultureInfo.InvariantCulture);
            }
            else
            {
                var constant = ConstantStatusPattern.Match(statusText);

                if (!constant.Success)
                {
                    problems.Add(
                        $"{origin}:{SourceText.LineNumber(source, callSite.Index)} 的状态码 {statusText} 认不出来"
                        + "（只认 400 这样的字面量，或 StatusCodes.Status400BadRequest 这样的常量）");
                    continue;
                }

                status = int.Parse(constant.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            if (!CodeConventionPattern.IsMatch(code))
            {
                problems.Add(
                    $"{origin}:{SourceText.LineNumber(source, callSite.Index)} 的错误码 {code} "
                    + "不符合 [a-z0-9_]+ 约定");
                continue;
            }

            if (codes.TryGetValue(code, out var existing))
            {
                if (existing != status)
                {
                    problems.Add(
                        $"错误码 {code} 在源码里对应了两个状态码（{existing} 与 {status}）");
                }

                continue;
            }

            codes[code] = status;
        }

        return new ScanResult(codes, problems);
    }

    /// <summary>扫端点源码：声明的错误码 → 状态码。</summary>
    private static List<(int Status, string Code)> ScanDeclaredCodes()
    {
        var result = new List<(int Status, string Code)>();

        foreach (var file in RepoPaths.SourceFiles(RepoPaths.Endpoints))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in DeclaredPattern.Matches(text))
            {
                var arguments = match.Groups[1].Value;

                var statusMatch = StatusPattern.Match(arguments);
                Assert.True(
                    statusMatch.Success,
                    $"ProducesApiProblem 的参数里没找到状态码：{arguments}");

                var status = int.Parse(
                    statusMatch.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);

                foreach (Match code in CodeLiteralPattern.Matches(arguments))
                {
                    result.Add((status, code.Groups[1].Value));
                }
            }
        }

        return result;
    }
}
