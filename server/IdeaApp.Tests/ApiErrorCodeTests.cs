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
/// </summary>
public sealed class ApiErrorCodeTests
{
    /// <summary>实际抛出的错误码：new ApiException(400, "invalid_content", …)。</summary>
    private static readonly Regex ThrownPattern = new(
        "ApiException\\(\\s*(\\d{3})\\s*,\\s*\"([a-z0-9_]+)\"",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 端点声明的错误响应：ProducesApiProblem(StatusCodes.Status400BadRequest, "a", "b")。
    /// </summary>
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
        var documented = ApiErrorCodes.All.ToDictionary(
            code => code.Code,
            code => code.Status,
            StringComparer.Ordinal);

        var missing = thrown.Keys
            .Where(code => !documented.ContainsKey(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"代码里抛了但 ApiErrorCodes.All 里没登记：{string.Join("、", missing)}");

        foreach (var (code, status) in thrown)
        {
            Assert.Equal(status, documented[code]);
        }

        var stale = documented.Keys
            .Where(code => !thrown.ContainsKey(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"ApiErrorCodes.All 里登记了但已经没人抛：{string.Join("、", stale)}");
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

    /// <summary>扫源码：实际抛出的错误码 → 状态码。</summary>
    private static Dictionary<string, int> ScanThrownCodes()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in RepoPaths.SourceFiles(RepoPaths.AppSource))
        {
            var text = File.ReadAllText(file);

            foreach (Match match in ThrownPattern.Matches(text))
            {
                var status = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var code = match.Groups[2].Value;

                if (result.TryGetValue(code, out var existing))
                {
                    Assert.True(
                        existing == status,
                        $"错误码 {code} 在源码里对应了两个状态码（{existing} 与 {status}）");
                    continue;
                }

                result[code] = status;
            }
        }

        return result;
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
