using System.Text.RegularExpressions;

namespace IdeaApp.Tests;

/// <summary>
/// 扫端点源码：每个端点声明了什么、调用了什么。
///
/// <para>
/// 放在这里而不是某个测试类的私有方法里：不止一个守卫要扫端点源码 ——
/// 415「声明了 + 调了机制」的配对守卫、运行期 415 用例清单与源码扫描的交叉核对守卫。
/// 规则得是**同一份** —— 两边各写一份扫描器，迟早有一边认不全某种写法，
/// 而那种漏认看起来「测试都绿」，实际上少查了一类端点。
/// </para>
///
/// <para>
/// 扫描规则本身（怎么认一个端点、怎么切一条声明）不在这里改：
/// 这里的规则是上一轮定下来的，任何调整都要同时想清楚两个守卫的后果。
/// </para>
/// </summary>
internal static class EndpointScanner
{
    /// <summary>
    /// 端点映射的调用：<c>app.MapGet(…)</c> / <c>endpoints.MapPost(…)</c> 等。
    /// </summary>
    /// <remarks>
    /// 接收者不写死：<c>HealthEndpoints</c> 用的就是 <c>endpoints.MapGet</c>，
    /// 写死 <c>app</c> 的话那个文件会被漏掉。
    /// </remarks>
    private static readonly Regex MapCallPattern = new(
        @"(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\.Map(?<method>Get|Post|Put|Patch|Delete|Methods)?\s*\(",
        RegexOptions.CultureInvariant);

    /// <summary>端点声明的错误响应：<c>.ProducesApiProblem(…)</c>。</summary>
    private static readonly Regex DeclaredPattern = new(
        @"\.ProducesApiProblem\(([^)]*)\)",
        RegexOptions.CultureInvariant);

    /// <summary>声明里的状态码：<c>StatusCodes.Status415UnsupportedMediaType</c> → 415。</summary>
    private static readonly Regex StatusPattern = new(
        @"StatusCodes\.Status(\d{3})",
        RegexOptions.CultureInvariant);

    /// <summary>让 415 送得到的机制调用。</summary>
    private static readonly Regex MechanismPattern = new(
        @"\.RejectsNonJsonBodyWithProblemDetails\s*\(",
        RegexOptions.CultureInvariant);

    /// <summary>扫到的端点。</summary>
    /// <param name="Label">端点的位置与身份（出错的提示里要用）。</param>
    /// <param name="Route">端点的「方法 + 路径」（<c>POST /api/ideas</c>），用来跨来源比对。</param>
    /// <param name="Declares415">端点是否声明了 415 响应。</param>
    /// <param name="UsesMechanism">端点是否调用了让 415 送得到的机制。</param>
    public sealed record EndpointSource(string Label, string Route, bool Declares415, bool UsesMechanism);

    /// <summary>扫端点源码：每个端点声明了什么、调用了什么。</summary>
    public static List<EndpointSource> ScanEndpoints()
    {
        var endpoints = new List<EndpointSource>();

        foreach (var file in RepoPaths.SourceFiles(RepoPaths.Endpoints))
        {
            // 注释里的话不算数：注释里常写着示例代码。
            var text = SourceText.StripComments(File.ReadAllText(file));
            var origin = Path.GetRelativePath(RepoPaths.Root, file);

            var index = 0;

            while (index < text.Length)
            {
                var mapCall = MapCallPattern.Match(text, index);

                if (!mapCall.Success)
                {
                    break;
                }

                // 端点声明是一条语句：从映射调用开始，到**最外层**的分号结束
                // （lambda 里的分号不算 —— 那是块 lambda 的语句）。
                var end = FindStatementEnd(text, mapCall.Index);
                var block = text[mapCall.Index..end];
                var route = Route(block, mapCall);

                endpoints.Add(new EndpointSource(
                    $"{origin}:{SourceText.LineNumber(text, mapCall.Index)} {route}",
                    route,
                    Declares415(block),
                    MechanismPattern.IsMatch(block)));

                index = end + 1;
            }
        }

        return endpoints;
    }

    /// <summary>端点的路由：方法 + 路径（<c>POST /api/ideas</c>）。</summary>
    private static string Route(string block, Match mapCall)
    {
        // block 是从映射调用那一点开始的，所以左括号的相对位置就是匹配的长度减一。
        var open = mapCall.Length - 1;
        var quote = block.IndexOf('"', open);

        if (quote < 0)
        {
            return "?";
        }

        var literalEnd = SourceText.SkipStringLiteral(block, quote);
        var path = block[(quote + 1)..(literalEnd - 1)];
        var method = mapCall.Groups["method"].Value;

        return string.IsNullOrEmpty(method) ? $"MAP {path}" : $"{method.ToUpperInvariant()} {path}";
    }

    /// <summary>端点声明里有没有 415。</summary>
    private static bool Declares415(string block)
    {
        foreach (Match match in DeclaredPattern.Matches(block))
        {
            var status = StatusPattern.Match(match.Groups[1].Value);

            if (status.Success && status.Groups[1].Value == "415")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>语句结束的位置（最外层的分号）；字符串/字符字面量里的分号不算。</summary>
    private static int FindStatementEnd(string text, int start)
    {
        var depth = 0;
        var index = start;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '"')
            {
                index = SourceText.SkipStringLiteral(text, index);
                continue;
            }

            if (current == '\'')
            {
                index = SourceText.SkipCharLiteral(text, index);
                continue;
            }

            if (current is '(' or '{' or '[')
            {
                depth++;
                index++;
                continue;
            }

            if (current is ')' or '}' or ']')
            {
                depth--;
                index++;
                continue;
            }

            if (current == ';' && depth == 0)
            {
                return index;
            }

            index++;
        }

        return text.Length;
    }
}
