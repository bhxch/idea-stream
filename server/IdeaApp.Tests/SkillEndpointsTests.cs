using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdeaApp.Application;
using IdeaApp.Endpoints;
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

        // 版本串的**形状**是契约的一部分：取件器拿它当缓存目录名，
        // 只收 64 位小写十六进制（sha256 的编码）。
        // 这里守形状，守的是「形状本身」；形状背后的算法由下面那条守卫守。
        AssertVersionShape(version);

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
    /// 取文件端点给的是原文：内容**逐字相同**，内容类型按扩展名给。
    /// </summary>
    /// <remarks>
    /// 比的是**字节**，不是文本：<c>ReadAllTextAsync</c> 会把 BOM 剥掉，
    /// 字节级不一致（比如多一个 BOM）用文本比就测不出来 ——
    /// 而取件器是按字节校验 sha256 的。
    /// </remarks>
    [Fact]
    public async Task File_endpoint_returns_the_file_with_its_content_type()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/skill/idea-manager/SKILL.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(factory.SkillPath, "SKILL.md")),
            body);
    }

    /// <summary>
    /// 给的是**字节**：带 BOM 的文件，BOM 也得原样送出去。
    /// </summary>
    /// <remarks>
    /// 早先那条断言比的是**文本**（<c>ReadAllTextAsync</c>）—— BOM 被剥掉了，
    /// 字节级不一致就测不出来。而取件器是按字节校验 sha256 的：
    /// 少一个字节就是「哈希对不上」，整次同步作废。
    /// </remarks>
    [Fact]
    public async Task File_endpoint_returns_the_bytes_of_the_file_including_a_bom()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "SKILL.md");
            var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(Encoding.UTF8.GetBytes("# 索引页\n"))
                .ToArray();

            await File.WriteAllBytesAsync(path, withBom);

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/skill/idea-manager/SKILL.md");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(withBom, body);
            Assert.Equal(0xEF, body[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    /// 文件读不到（存在性检查之后被删 / 被换 / 被别的进程占着）→ 404，不是 500。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在性检查与读取之间**有缝**（TOCTOU）：早先这里是裸的
    /// <c>File.ReadAllBytes</c>，读失败抛 <c>IOException</c> → 500
    /// <c>internal_error</c>。而「读不到」对调用方来说就是「副本里没有这个文件」。
    /// </para>
    /// <para>
    /// 这里用<b>独占打开</b>把文件占住：另一个进程（或另一个句柄）读它会失败 ——
    /// 这正是「存在但读不到」最现实的形状。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task File_endpoint_returns_404_when_the_file_cannot_be_read()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "locked.md");
            await File.WriteAllTextAsync(path, "# 占着的\n");

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            // 占住它：独占打开，别人的读会失败。
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            using var response = await client.GetAsync("/skill/idea-manager/locked.md");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal("skill_file_not_found", ReadCode(body));
            Assert.DoesNotContain("internal_error", body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 超过大小上限的那一份 → 404 <c>skill_file_not_found</c>，不是 500。
    /// </summary>
    /// <remarks>
    /// 取文件端点把整个文件**读进内存**，那条路上没有闸门的话，
    /// 一份超大文件就能把内存吃光。分发副本里每份都是几十 KB 的文档 ——
    /// 超了的那一份不该在分发副本里。
    /// </remarks>
    [Fact]
    public async Task File_endpoint_returns_404_for_a_file_over_the_size_limit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# 索引页\n");
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "big.md"),
                new byte[(4 * 1024 * 1024) + 1]);

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/skill/idea-manager/big.md");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal("skill_file_not_found", ReadCode(body));
            Assert.DoesNotContain("internal_error", body, StringComparison.Ordinal);
            // 详情要说明白「超了上限」—— 那一份不该有这么大。
            // （读的是**解析后**的 detail：响应体里的中文是 JSON 转义过的。）
            Assert.Contains("大小上限", ReadDetail(body), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 读一个已经不存在的文件 → 404 <c>skill_file_not_found</c>（不是 IO 异常冒出去）。
    /// </summary>
    /// <remarks>
    /// 这是端点上那个缝（TOCTOU）最直接的形状：存在性检查时它还在，
    /// 读取时它没了。读失败要变成 404 <c>skill_file_not_found</c>，
    /// 而不是让 <c>IOException</c> 冒到框架里变成 500 <c>internal_error</c>。
    /// </remarks>
    [Fact]
    public void Reading_a_file_that_disappeared_raises_404_not_an_io_error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "gone.md");
            File.WriteAllText(path, "# 刚才还在\n");

            File.Delete(path);

            var failure = Assert.Throws<ApiException>(
                () => SkillEndpoints.ReadDistributedFile(path, "gone.md"));

            Assert.Equal(404, failure.Status);
            Assert.Equal("skill_file_not_found", failure.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 读一个超过大小上限的文件 → 404 <c>skill_file_not_found</c>。
    /// </summary>
    [Fact]
    public void Reading_a_file_over_the_size_limit_raises_404()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "big.md");
            File.WriteAllBytes(path, new byte[(4 * 1024 * 1024) + 1]);

            var failure = Assert.Throws<ApiException>(
                () => SkillEndpoints.ReadDistributedFile(path, "big.md"));

            Assert.Equal(404, failure.Status);
            Assert.Equal("skill_file_not_found", failure.Code);
            Assert.Contains("大小上限", failure.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 路径穿越**形态**的路径进了 <c>TryResolveFile</c>，解析不出副本之外的文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这条守的是函数级的防御，不是 HTTP 层的保证。</b>
    /// </para>
    /// <para>
    /// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> 根本不解析请求行 ——
    /// 这里是直接把 <c>Request.Path</c> 设成未经归一化的原始路径塞进去的。
    /// 真实 HTTP 请求里，Kestrel 会先按 RFC 3986 去掉点号段
    /// （<c>/skill/idea-manager/../../etc/passwd</c> 归一化成 <c>/etc/passwd</c>），
    /// 于是那条请求根本到不了取文件端点 —— 它落到前端的 SPA 兜底上（200 + HTML）。
    /// </para>
    /// <para>
    /// HTTP 层那条保证由 <see cref="SkillRawRequestTests"/> 守：那里用裸 socket
    /// 发**真的请求行**，断言「归一化后逃出 <c>/skill/</c> 前缀的路径读不到任何文件」。
    /// 两条守的是不同的东西，少一条都不行。
    /// </para>
    /// </remarks>
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
    /// <para>
    /// 服务端这一侧：<c>%2f</c> 解出来是路径分隔符，但**归一化发生在解码之前**，
    /// 所以点号段没被去掉 —— 路径原样进了取文件端点，由
    /// <c>TryResolveFile</c> 挡下（404 + <c>skill_file_not_found</c>）。
    /// </para>
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
    /// 路径参数没取到值（<c>GET /skill/idea-manager</c>）→ 404，不是 400。
    /// </summary>
    /// <remarks>
    /// 早先路径参数没写 <c>[FromRoute]</c>：catch-all 取不到值时框架会退回
    /// **从请求体绑定**，而 GET 没有请求体 → 400 + <c>invalid_request_body</c>
    /// 「请求体不是合法的 JSON」—— 对一个压根没有请求体的端点来说，这句话是误导，
    /// 而且契约里也没有声明 400 这条。
    /// <para>
    /// 显式绑定之后，取不到值就是「副本里没有这个文件」→ 404 + 稳定的错误码。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/skill/idea-manager")]
    [InlineData("/skill/idea-manager/")]
    public async Task File_endpoint_without_a_path_returns_404_not_found(string path)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("skill_file_not_found", ReadCode(body));
        Assert.DoesNotContain("invalid_request_body", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 开发残留目录（<c>__pycache__</c>）里的文件既不在清单里，也取不到。
    /// </summary>
    /// <remarks>
    /// 「哪些路径算分发副本的一部分」只有一处定义（<c>SkillCatalog</c> 里的那条规则），
    /// 枚举清单与取文件端点共用它。少一端就会漂移：清单里有而取不到
    /// （agent 按索引页去取就 404），或者反过来（清单里没有却给得出去）。
    /// </remarks>
    [Fact]
    public async Task Development_residue_is_neither_listed_nor_served()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(directory, "__pycache__"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "# 索引页\n");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "__pycache__", "leftover.md"),
                "开发残留\n");

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            // 清单里没有它。
            using var manifest = await client.GetAsync("/skill/idea-manager/version");
            var manifestBody = await manifest.Content.ReadAsStringAsync();

            Assert.DoesNotContain("__pycache__", manifestBody, StringComparison.Ordinal);

            // 取文件端点也不给。
            using var response = await client.GetAsync("/skill/idea-manager/__pycache__/leftover.md");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("skill_file_not_found", ReadCode(await response.Content.ReadAsStringAsync()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 分发副本的路径没配（空串）→ 404 + <c>skill_not_distributed</c>，不是 500。
    /// </summary>
    /// <remarks>
    /// 早先空路径让 <c>SkillCatalog</c> 的构造抛 <c>ArgumentException</c>，
    /// 而注册是**惰性单例** —— 异常要等到有人第一次访问 <c>/skill/**</c> 才炸出来，
    /// 那就是 500。「没分发」本来就有专门的错误码，规格里也明写「不要 500」。
    /// </remarks>
    [Theory]
    [InlineData("/skill/idea-manager/version")]
    [InlineData("/skill/idea-manager/SKILL.md")]
    public async Task Empty_skill_path_returns_404_not_distributed(string path)
    {
        using var factory = new TestAppFactory(skillPath: string.Empty);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("skill_not_distributed", ReadCode(await response.Content.ReadAsStringAsync()));
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

    /// <summary>
    /// 版本串必须是「文件清单的哈希」—— 真的是哈希，不是别的东西的十六进制编码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 口径（设计文档 §9.1）：参与分发的全部文件，按相对路径升序，
    /// 每项是「路径 + 换行 + 该文件的 sha256」，项与项之间也用换行分隔，
    /// 整体取 UTF-8 字节后算 sha256，输出 64 位小写十六进制。
    /// </para>
    /// <para>
    /// <b>期望值是独立算出来的常量</b>，不是照着被测代码再算一遍 ——
    /// 再算一遍的话，被测代码算错时这条守卫也跟着错（它只是把自己抄了一遍）。
    /// 下面的注释里给了复核它的一行 shell 命令。
    /// </para>
    /// <para>
    /// 这条守的是「算法」；端到端的那一条在 <c>server/scripts/aot-smoke.sh</c> 里 ——
    /// 那里是真 AOT 产物 + 仓库里那个真取件器（两边第一次对上面）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Version_is_the_sha256_of_the_file_list()
    {
        var directory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "SKILL.md"),
                Encoding.UTF8.GetBytes("# 索引页\n"));

            await File.WriteAllBytesAsync(
                Path.Combine(directory, "a.md"),
                Encoding.UTF8.GetBytes("a"));

            using var factory = new TestAppFactory(skillPath: directory);
            using var client = factory.CreateClient();

            var version = await ReadVersionAsync(client);

            AssertVersionShape(version);

            // 期望值（独立算出来的常量，可复核）：
            //   printf 'SKILL.md\n%s\na.md\n%s' \
            //     "$(printf '# 索引页\n' | sha256sum | cut -d' ' -f1)" \
            //     "$(printf 'a'         | sha256sum | cut -d' ' -f1)" | sha256sum
            // 路径按字节序排：「SKILL.md」在「a.md」前面（大写字母排在小写前面）。
            Assert.Equal(
                "f7fc4361a92014c40a70289100d53b4af405a9118cf4a2c65a628f72b569f36b",
                version);
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

    /// <summary>版本串的形状：64 位小写十六进制（sha256 的十六进制编码）。</summary>
    /// <remarks>
    /// 逐字符判，不走正则：正则的 <c>$</c> 也能匹配「串尾换行之前」，
    /// 「64 位 hex 再加一个换行」会蒙混过关（取件器那侧用的是 fullmatch，
    /// 同一个道理）。
    /// </remarks>
    private static void AssertVersionShape(string? version)
    {
        Assert.True(
            version is { Length: 64 }
                && version.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"版本串不是 64 位小写十六进制：{version ?? "(null)"}");
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

    /// <summary>读错误响应里给人看的说明（解析后的值：原串里的中文是 JSON 转义过的）。</summary>
    private static string ReadDetail(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("detail").GetString() ?? string.Empty;
    }

    /// <summary>读 TestServer 直接回的那个上下文里的响应体。</summary>
    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        // TestServer 的响应体是只读的管道流：不支持 Seek，从当前位置读就是全部。
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// 文件字节的 sha256（十六进制小写）。
    /// </summary>
    /// <remarks>
    /// <b>测试自己的一份</b>，不复用被测代码里的任何 helper ——
    /// 复用就等于自己抄自己：被测代码算错时，这条断言跟着一起错。
    /// </remarks>
    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
