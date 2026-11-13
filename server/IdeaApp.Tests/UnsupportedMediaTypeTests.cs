using System.Text.RegularExpressions;

namespace IdeaApp.Tests;

/// <summary>
/// 「声明 415」与「让 415 送得到」必须成对出现的守卫。
///
/// <para>
/// 415（<c>unsupported_media_type</c>）这个错误码是**双要素**的：
/// <list type="bullet">
/// <item>端点声明 <c>.ProducesApiProblem(Status415UnsupportedMediaType, "unsupported_media_type")</c>
/// —— 契约里才写得出这个 415；</item>
/// <item>端点调用 <c>.RejectsNonJsonBodyWithProblemDetails()</c> —— 运行时才真的回 415
/// （框架在**绑定 body 时**抛 <c>BadHttpRequestException(415)</c>，由
/// <c>ApiExceptionHandler</c> 转成 ProblemDetails）。</item>
/// </list>
///
/// <para>
/// <b>缺一个，契约就在说谎</b>：
/// <list type="bullet">
/// <item>只声明不调用：契约写着「会回 415 + <c>unsupported_media_type</c>」，
/// 运行时其实回的是 <b>404 + <c>api_not_found</c></b> ——
/// .NET 10 的路由层会先按 Content-Type 把端点毙掉，请求落到
/// <c>/api/{**rest}</c> 兜底（"接口不存在"，语义完全不对）；</item>
/// <item>只调用不声明：运行时回 415，契约里却没有这个响应。</item>
/// </list>
///
/// <para>
/// 这类错配**没有任何现有测试能发现**：契约文件与运行期行为不在同一个世界里，
/// 契约里写了 415、运行时回 404，两边各自"自洽"。
/// 所以这里扫**源码**，按端点配对检查。
/// </para>
/// </summary>
public sealed class UnsupportedMediaTypeTests
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

    /// <summary>
    /// 声明了 415 与调用了机制，必须成对出现。
    /// </summary>
    [Fact]
    public void Declaring_415_and_enabling_the_rejection_go_together()
    {
        var endpoints = ScanEndpoints();

        // 扫描不能是空的：连一个声明 415 的端点都没扫到，说明扫描规则坏了
        // （端点文件挪了地方、映射写法变了……），那样下面的断言会「什么都没查」就绿。
        Assert.True(
            endpoints.Any(endpoint => endpoint.Declares415),
            $"在 {RepoPaths.Endpoints} 下没扫到任何声明 415 的端点 —— 扫描规则坏了？");

        // 方向一：声明了 415 却没调机制 → 契约说谎（运行时回 404 api_not_found）。
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Declares415 && !endpoint.UsesMechanism))
        {
            Assert.Fail(
                $"{endpoint.Label}：声明了 415（unsupported_media_type），但没有调用 RejectsNonJsonBodyWithProblemDetails()。"
                + "运行时不会回 415（请求会被 /api/{**rest} 兜底接走，回 404 api_not_found），契约说谎。");
        }

        // 方向二：调了机制却没声明 415 → 契约漏了它（运行时回 415，契约里没有）。
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.UsesMechanism && !endpoint.Declares415))
        {
            Assert.Fail(
                $"{endpoint.Label}：调用了 RejectsNonJsonBodyWithProblemDetails()，但没有声明 415（unsupported_media_type）。"
                + "运行时回 415，契约里却写不出这个响应。");
        }
    }

    /// <summary>扫到的端点。</summary>
    /// <param name="Label">端点的位置与身份（出错的提示里要用）。</param>
    /// <param name="Declares415">端点是否声明了 415 响应。</param>
    /// <param name="UsesMechanism">端点是否调用了让 415 送得到的机制。</param>
    private sealed record EndpointSource(string Label, bool Declares415, bool UsesMechanism);

    /// <summary>扫端点源码：每个端点声明了什么、调用了什么。</summary>
    private static List<EndpointSource> ScanEndpoints()
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

                endpoints.Add(new EndpointSource(
                    $"{origin}:{SourceText.LineNumber(text, mapCall.Index)} {Label(block, mapCall)}",
                    Declares415(block),
                    MechanismPattern.IsMatch(block)));

                index = end + 1;
            }
        }

        return endpoints;
    }

    /// <summary>端点的标签：方法 + 路径（<c>POST /api/ideas</c>）。</summary>
    private static string Label(string block, Match mapCall)
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
