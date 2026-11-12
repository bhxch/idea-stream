using System.Text.Json;
using IdeaApp.Infrastructure;

namespace IdeaApp.Tests;

/// <summary>
/// OpenAPI 契约文件的守卫。
///
/// <para>
/// <c>openapi/idea-v1.json</c> 是**提交进仓库的构建产物快照**：它给 agent 当接口契约用。
/// 改了端点、DTO 或状态码却忘了重新导出，契约文件就与实际暴露的接口对不上 ——
/// 而且不会编译报错、也不会有别的测试失败，只会让 agent 拿着旧契约调接口。
/// </para>
/// </summary>
public sealed class OpenApiDocumentTests
{
    /// <summary>
    /// 提交进仓库的契约文件必须与应用当前生成的文档一致。
    /// </summary>
    [Fact]
    public async Task Committed_document_matches_the_running_app()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 触发宿主启动：文档要等端点进 ApiExplorer 之后才有内容。
        using var response = await client.GetAsync("/api/health");
        Assert.True(response.IsSuccessStatusCode);

        var current = await OpenApiExporter.GenerateJsonAsync(factory.Services);
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());

        using var currentDocument = JsonDocument.Parse(current);
        using var committedDocument = JsonDocument.Parse(committed);

        // 整份文档做 JSON **语义**比较（忽略空白与对象键的顺序）：
        // 逐段比原始文本太脆 —— 换个框架版本就可能在某个数组里换顺序，
        // 那种改动不该让测试红。servers 段不在文档里（生成器清掉了它），
        // 所以整份比较不会踩到「运行期地址」的问题。
        AssertJsonEqual(currentDocument.RootElement, committedDocument.RootElement, "$");
    }

    /// <summary>
    /// 契约文件不能是空壳：路径与 schema 都要有内容。
    /// </summary>
    [Fact]
    public async Task Committed_document_is_not_an_empty_shell()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.EnumerateObject().Any(), "paths 一个都没有");

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.EnumerateObject().Any(), "components.schemas 一个都没有");
    }

    /// <summary>
    /// 契约里必须写得出错误响应。
    /// </summary>
    /// <remarks>
    /// 这份文件唯一的用途是给 agent 用。契约里只有成功路径的话，agent 遇到
    /// 400 / 404 既不知道错误长什么样（RFC 9457 的 ProblemDetails + 扩展字段
    /// <c>code</c>），也不知道什么时候会拿到 —— 只能靠试错。
    /// <para>
    /// 只断言「至少有一个 400 与一个 404」：具体每个端点该有哪些错误码由
    /// <c>ApiErrorCodeTests</c> 把关，这里守的是「错误响应整体没被漏掉」。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Committed_document_declares_error_responses()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var statuses = DeclaredStatusCodes(document.RootElement);

        Assert.Contains("400", statuses);
        Assert.Contains("404", statuses);
    }

    /// <summary>
    /// ProblemDetails 的 schema 必须写清楚扩展字段 <c>code</c> 的存在与取值。
    /// </summary>
    /// <remarks>
    /// <c>Extensions</c> 不是 ProblemDetails 的类型化属性，schema 里不会自动出现它 ——
    /// 不显式写，agent 就不知道错误响应里有个可分支处理的 <c>code</c> 字段。
    /// </remarks>
    [Fact]
    public async Task ProblemDetails_schema_documents_the_code_extension()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProblemDetails");

        var description = schema.GetProperty("description").GetString() ?? string.Empty;

        Assert.Contains("code", description, StringComparison.Ordinal);
        Assert.Contains("idea_not_found", description, StringComparison.Ordinal);
        Assert.Contains("project_not_found", description, StringComparison.Ordinal);

        // 扩展字段：要么写成 additionalProperties，要么至少在描述里点名。
        var documented = schema.TryGetProperty("additionalProperties", out _)
            || description.Contains("扩展字段", StringComparison.Ordinal);

        Assert.True(documented, "ProblemDetails 的 schema 既没有 additionalProperties，也没在描述里说明扩展字段");
    }

    /// <summary>文档里出现过的全部响应状态码。</summary>
    private static HashSet<string> DeclaredStatusCodes(JsonElement root)
    {
        var statuses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                // 路径项里除了各方法，还有路径级的 parameters（数组）——跳过。
                if (operation.Value.ValueKind != JsonValueKind.Object
                    || !operation.Value.TryGetProperty("responses", out var responses))
                {
                    continue;
                }

                foreach (var response in responses.EnumerateObject())
                {
                    statuses.Add(response.Name);
                }
            }
        }

        return statuses;
    }

    /// <summary>JSON 语义比较：忽略空白与对象键的顺序，数组按顺序比。</summary>
    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            Assert.Fail($"{path}: 类型不同（期望 {expected.ValueKind}，实际 {actual.ValueKind}）");
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedKeys = expected.EnumerateObject().Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
                var actualKeys = actual.EnumerateObject().Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

                Assert.True(expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
                    $"{path}: 字段不同。期望 [{string.Join(", ", expectedKeys)}]，实际 [{string.Join(", ", actualKeys)}]");

                foreach (var property in expected.EnumerateObject())
                {
                    AssertJsonEqual(property.Value, actual.GetProperty(property.Name), $"{path}.{property.Name}");
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();

                Assert.True(expectedItems.Count == actualItems.Count,
                    $"{path}: 数组长度不同（期望 {expectedItems.Count}，实际 {actualItems.Count}）");

                for (var index = 0; index < expectedItems.Count; index++)
                {
                    AssertJsonEqual(expectedItems[index], actualItems[index], $"{path}[{index}]");
                }

                break;
            }

            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    /// <summary>
    /// 仓库根下的 openapi/idea-v1.json。
    /// </summary>
    /// <remarks>
    /// 先按仓库根（认 <c>IdeaApp.sln</c>）定位，再拼出契约文件的路径 ——
    /// 逐级上溯找 <c>openapi/idea-v1.json</c> 会命中别处的同名目录
    /// （比如某个依赖包里恰好也有 openapi/idea-v1.json），那就找错文件了。
    /// </remarks>
    private static string CommittedDocumentPath()
    {
        var path = RepoPaths.Contract;

        Assert.True(File.Exists(path), $"找不到契约文件：{path}");

        return path;
    }
}
