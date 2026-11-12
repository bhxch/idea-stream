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

        // 只比路径、组件与文档信息：servers 段跟当前监听地址有关（默认是回环地址），
        // 换台机器跑测试就不一样了，它不是契约的一部分。
        AssertEqual(currentDocument, committedDocument, "info");
        AssertEqual(currentDocument, committedDocument, "paths");
        AssertEqual(currentDocument, committedDocument, "components");
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

    private static void AssertEqual(JsonDocument current, JsonDocument committed, string section)
    {
        Assert.True(
            current.RootElement.TryGetProperty(section, out var currentSection),
            $"当前文档没有 {section} 段");

        Assert.True(
            committed.RootElement.TryGetProperty(section, out var committedSection),
            $"提交的契约文件没有 {section} 段");

        Assert.Equal(currentSection.GetRawText(), committedSection.GetRawText());
    }

    /// <summary>仓库根下的 openapi/idea-v1.json（从测试程序集往上找）。</summary>
    private static string CommittedDocumentPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "openapi", "idea-v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"找不到 openapi/idea-v1.json：从 {AppContext.BaseDirectory} 往上找不到。");
    }
}
