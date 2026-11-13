using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace IdeaApp.Tests;

/// <summary>
/// <c>/skill/**</c> 端点的集成测试。
///
/// <para>
/// 这一组端点是服务端作为 skill 内容**唯一权威**的出口：
/// 清单端点给版本与文件清单，取文件端点给内容。
/// agent 拿到的是「当前服务端这一份」的手册，而不是仓库里可能已经过期的副本。
/// </para>
///
/// <para>
/// 这里守的是三件容易塌掉的事：
/// <list type="number">
/// <item>清单里的 sha256 与文件字节对得上（取件器按它逐个校验，对不上就整次同步作废）；</item>
/// <item>路径穿越读不到分发副本之外的文件；</item>
/// <item>拼错的路径回 404 + ProblemDetails，而不是落到 SPA 兜底变成 200 + HTML
/// （只按状态码判断的调用方会把 200 当成成功）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SkillEndpointsTests
{
    /// <summary>
    /// 清单端点给的是分发副本的现状：版本、入口、逐个文件的哈希与大小。
    /// </summary>
    [Fact]
    public async Task Version_endpoint_returns_the_manifest_of_the_skill_directory()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/skill/idea-manager/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        var version = root.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));

        Assert.Equal("SKILL.md", root.GetProperty("entry").GetString());

        var files = root.GetProperty("files");
        Assert.Equal(JsonValueKind.Array, files.ValueKind);

        // 清单里必须有入口文件，且它的哈希与文件字节对得上 ——
        // 取件器按这个哈希逐个校验，对不上就整次同步作废。
        var entry = files.EnumerateArray()
            .Single(file => file.GetProperty("path").GetString() == "SKILL.md");

        var entryPath = Path.Combine(factory.SkillPath, "SKILL.md");
        var expected = Sha256Hex(await File.ReadAllBytesAsync(entryPath));

        Assert.Equal(expected, entry.GetProperty("sha256").GetString());
        Assert.Equal(new FileInfo(entryPath).Length, entry.GetProperty("size").GetInt64());
    }

    /// <summary>
    /// 取文件端点给的是原文：内容逐字相同，内容类型按扩展名给。
    /// </summary>
    [Fact]
    public async Task File_endpoint_returns_the_file_with_its_content_type()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/skill/idea-manager/SKILL.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(factory.SkillPath, "SKILL.md")),
            body);
    }

    /// <summary>
    /// 内容类型按扩展名给：markdown / json / 其余按纯文本。
    /// </summary>
    /// <remarks>
    /// 用一个临时目录做分发副本，不依赖内容源目录里的具体文件 ——
    /// 内容源的形状会变（手册重写后文件会换），测试不该跟着它变。
    /// </remarks>
    [Fact]
    public async Task File_endpoint_gives_each_file_its_content_type()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# 索引页\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "a.json"), "{\"a\":1}");
            await File.WriteAllTextAsync(Path.Combine(directory, "b.txt"), "纯文本");

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            Assert.Equal(
                "text/markdown; charset=utf-8",
                await ContentTypeOfAsync(client, "/skill/idea-manager/SKILL.md"));

            Assert.Equal(
                "application/json; charset=utf-8",
                await ContentTypeOfAsync(client, "/skill/idea-manager/a.json"));

            Assert.Equal(
                "text/plain; charset=utf-8",
                await ContentTypeOfAsync(client, "/skill/idea-manager/b.txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 路径穿越读不到分发副本之外的文件。
    ///
    /// <para>
    /// 请求路径里的 <c>..</c> 由客户端自己先归一化了（<see cref="Uri"/> 按 RFC 3986
    /// 去掉点号段），所以这里直接用 <see cref="Microsoft.AspNetCore.TestHost.TestServer.SendAsync"/>
    /// 把**未经归一化**的原始路径塞给服务端 —— 那才是真正要防的请求形态。
    /// </para>
    /// </summary>
    [Fact]
    public async Task File_endpoint_rejects_path_traversal()
    {
        using var factory = new TestAppFactory();

        var context = await factory.Server.SendAsync(http =>
        {
            http.Request.Method = HttpMethods.Get;
            http.Request.Path = "/skill/idea-manager/../../etc/passwd";
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);

        var body = await ReadResponseBodyAsync(context);

        Assert.Equal("skill_file_not_found", ReadCode(body));
        Assert.DoesNotContain("root:", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 编码过的穿越（<c>%2f</c>）同样读不到别的文件。
    /// </summary>
    /// <remarks>
    /// 这一条走的是普通的 HTTP 客户端：<c>%2f</c> 是保留字符，<see cref="Uri"/> 不会
    /// 把它归一化掉，于是这串路径真的送到了服务端。
    /// </remarks>
    [Fact]
    public async Task File_endpoint_rejects_an_encoded_path_traversal()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/skill/idea-manager/..%2f..%2fetc%2fpasswd");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("skill_file_not_found", ReadCode(body));
        Assert.DoesNotContain("root:", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 副本里没有这个文件 → 404 + 稳定的错误码。
    /// </summary>
    [Fact]
    public async Task File_endpoint_returns_404_for_a_missing_file()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/skill/idea-manager/nope.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal("skill_file_not_found", ReadCode(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// <c>/skill/**</c> 下没有对应端点时回 404 + ProblemDetails，**不是** HTML。
    /// </summary>
    /// <remarks>
    /// SPA 兜底是 catch-all，会把所有没匹配到的 GET 变成 200 + index.html ——
    /// 只看状态码的调用方会把「路径打错」当成成功。
    /// </remarks>
    [Fact]
    public async Task Unmatched_skill_path_returns_404_problem_details_not_html()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var typo = await client.GetAsync("/skill/idea-manger/SKILL.md");

        Assert.Equal(HttpStatusCode.NotFound, typo.StatusCode);
        Assert.Equal("application/problem+json", typo.Content.Headers.ContentType?.MediaType);

        var typoBody = await typo.Content.ReadAsStringAsync();

        Assert.DoesNotContain("<!DOCTYPE html>", typoBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("skill_endpoint_not_found", ReadCode(typoBody));

        using var root = await client.GetAsync("/skill");

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);

        Assert.Equal("skill_endpoint_not_found", ReadCode(await root.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// 服务端没分发 skill 内容（目录不存在）→ 404 + <c>skill_not_distributed</c>。
    /// </summary>
    /// <remarks>
    /// 这个码与 <c>skill_file_not_found</c> 要分得开：前者是「部署漏了目录」
    /// （装 skill 的人该去查服务端），后者是「要的文件没有」。
    /// </remarks>
    [Fact]
    public async Task Missing_skill_directory_returns_404_not_distributed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        using var factory = new TestAppFactory(skillPath: missing);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/skill/idea-manager/SKILL.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal("skill_not_distributed", ReadCode(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// 内容变了，版本必须跟着变 —— 版本串是取件器判断「要不要重新同步」的唯一依据。
    /// </summary>
    [Fact]
    public async Task Version_changes_when_the_content_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# 索引页\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "a.md"), "a");

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            var before = await ReadVersionAsync(client);

            await File.WriteAllTextAsync(Path.Combine(directory, "a.md"), "b");

            var after = await ReadVersionAsync(client);

            Assert.NotEqual(before, after);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>取清单端点的版本串。</summary>
    private static async Task<string> ReadVersionAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/skill/idea-manager/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("version").GetString()!;
    }

    /// <summary>取一个文件的内容类型。</summary>
    private static async Task<string?> ContentTypeOfAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return response.Content.Headers.ContentType?.ToString();
    }

    /// <summary>读错误响应里的机器可读错误码。</summary>
    private static string ReadCode(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>读 TestServer 直接回的那个上下文里的响应体。</summary>
    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        // TestServer 的响应体是只读的管道流：不支持 Seek，从当前位置读就是全部。
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }

    /// <summary>文件字节的 sha256（十六进制小写）。</summary>
    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
