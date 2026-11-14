using System.Net.Sockets;
using System.Text;
using IdeaApp.Endpoints;
using IdeaApp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace IdeaApp.Tests;

/// <summary>
/// skill 路径的**HTTP 层**守卫：真发原始请求行。
///
/// <para>
/// <see cref="SkillEndpointsTests"/> 里那条「路径穿越」的用例守的是
/// <c>SkillCatalog.TryResolveFile</c> 这个**函数**的防御：
/// 「穿越形态的路径进了这个函数，不能被解析成副本之外的文件」。
/// 它不是 HTTP 层的保证 —— <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>
/// 根本不解析请求行（测试是把 <c>Request.Path</c> 直接塞进去的），
/// 真实客户端与真实 Kestrel 之间那一段（路径归一化、代理转发、
/// 编码形态的路径）它一条都没经过。
/// </para>
///
/// <para>
/// 这一组用**真的 socket** 发**真的请求行**：请求行里的路径原样送出去，
/// 服务端怎么处理就是它对外的真实行为。这里断言的是那条真实行为：
/// <b>归一化后逃出 <c>/skill/</c> 前缀的路径读不到任何文件</b>。
/// </para>
///
/// <para>
/// 为什么自己起一个 Kestrel，而不是用应用本体：
/// 应用本体在 <c>Program.cs</c> 里是顶层语句，跑起来就 <c>app.Run()</c>；
/// 集成测试用的 <c>TestServer</c> 又没有真的监听端口。
/// 这里起的宿主只挂两段东西 —— <b>生产里那两段</b>：
/// skill 端点（<c>MapSkillEndpoints</c>）与 SPA 兜底（<c>MapFallbackToFile</c>）。
/// 路由、Kestrel 的路径处理、<c>SkillCatalog</c> 全是生产用的同一份代码。
/// </para>
/// </summary>
public sealed class SkillRawRequestTests
{
    /// <summary>SPA 兜底首页里的标记（用来认「落到前端兜底了」）。</summary>
    private const string SpaMarker = "SPA-FALLBACK-MARKER";

    /// <summary>「/etc/passwd」里的标记（用来认「真读到了系统文件」）。</summary>
    private const string PasswdMarker = "root:x:";

    /// <summary>
    /// 原始请求行里的穿越路径，**读不到任何文件**。
    /// </summary>
    /// <remarks>
    /// 两条形态：
    /// <list type="bullet">
    /// <item><c>/skill/idea-manager/../../etc/passwd</c>：点号段是**明文**的 ——
    /// 服务端会先把它归一化，路径因此逃出了 <c>/skill/</c> 前缀，
    /// 最后落到前端兜底（200 + HTML）。安全属性是「没读到文件」；
    /// 状态码与内容形态是**真实行为**，一并钉住（agent 读到 HTML 时要知道那是路径写错了）。</item>
    /// <item><c>/skill/idea-manager/..%2f..%2fetc%2fpasswd</c>：编码形态 ——
    /// 归一化不认它（<c>%2f</c> 不是路径分隔符），于是它原样进了路由，
    /// 由 <c>TryResolveFile</c> 挡下 → 404 + <c>skill_file_not_found</c>。</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task A_raw_request_line_with_dot_segments_never_reads_a_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            // 分发副本：只有入口页。
            var skillDirectory = Path.Combine(root, "skill");
            Directory.CreateDirectory(skillDirectory);
            await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "# 索引页\n");

            // SPA 兜底的前端首页（生产里是 wwwroot/index.html 的角色）。
            var webRoot = Path.Combine(root, "wwwroot");
            Directory.CreateDirectory(webRoot);
            await File.WriteAllTextAsync(
                Path.Combine(webRoot, "index.html"),
                $"<!DOCTYPE html><html><body>{SpaMarker}</body></html>");

            await using var app = await StartAsync(root, skillDirectory);
            var (host, port) = AddressOf(app);

            // 明文点号段：归一化后逃出 /skill/ 前缀。
            var plain = await SendRawAsync(host, port, "GET /skill/idea-manager/../../etc/passwd");

            Assert.DoesNotContain(PasswdMarker, plain, StringComparison.Ordinal);
            // 真实行为：落到前端兜底，回 200 + HTML（**不是** 404，也不是文件内容）。
            Assert.Contains("200", plain.Split("\r\n")[0], StringComparison.Ordinal);
            Assert.Contains(SpaMarker, plain, StringComparison.Ordinal);

            // 编码形态：原样进路由，被 TryResolveFile 挡下。
            var encoded = await SendRawAsync(host, port, "GET /skill/idea-manager/..%2f..%2fetc%2fpasswd");

            Assert.DoesNotContain(PasswdMarker, encoded, StringComparison.Ordinal);
            Assert.Contains("404", encoded.Split("\r\n")[0], StringComparison.Ordinal);
            Assert.Contains("skill_file_not_found", encoded, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 归一化**之后**仍在 <c>/skill/</c> 前缀内的路径，照常取到文件。
    /// </summary>
    /// <remarks>
    /// 守着的是「归一化没有把合法路径也弄坏」：<c>/skill/idea-manager/a/../SKILL.md</c>
    /// 归一化成 <c>/skill/idea-manager/SKILL.md</c>，取到的应该是入口页本身。
    /// </remarks>
    [Fact]
    public async Task A_raw_request_line_normalizes_inside_the_skill_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var skillDirectory = Path.Combine(root, "skill");
            Directory.CreateDirectory(skillDirectory);
            await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "# 索引页\n");

            var webRoot = Path.Combine(root, "wwwroot");
            Directory.CreateDirectory(webRoot);
            await File.WriteAllTextAsync(
                Path.Combine(webRoot, "index.html"),
                $"<!DOCTYPE html><html><body>{SpaMarker}</body></html>");

            await using var app = await StartAsync(root, skillDirectory);
            var (host, port) = AddressOf(app);

            var response = await SendRawAsync(host, port, "GET /skill/idea-manager/a/../SKILL.md");

            Assert.Contains("200", response.Split("\r\n")[0], StringComparison.Ordinal);
            Assert.Contains("# 索引页", response, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 起一个真的 Kestrel 监听：只挂 skill 端点与 SPA 兜底（生产里那两段）。
    /// </summary>
    private static async Task<WebApplication> StartAsync(string contentRoot, string skillDirectory)
    {
        // 内容根 = 临时目录（wwwroot 与分发副本都在它下面）。
        // 走 WebApplicationOptions 而不是 builder.WebHost.UseContentRoot：
        // 后者在宿主配置定下来之后再改内容根会被框架拒绝。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot
        });

        // 端口 0：让系统挑一个没人用的端口（并行跑测试时不会撞）。
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(new SkillCatalog(skillDirectory));

        var app = builder.Build();

        // 生产里这两段的顺序也是这个：skill 端点在前，SPA 兜底在最后。
        app.MapSkillEndpoints();
        app.MapFallbackToFile("index.html");

        await app.StartAsync();

        return app;
    }

    /// <summary>宿主实际绑定的地址。</summary>
    private static (string Host, string Port) AddressOf(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        var address = addresses?.FirstOrDefault();

        Assert.False(string.IsNullOrEmpty(address), "宿主没有绑到任何地址");

        var uri = new Uri(address!);

        return (uri.Host, uri.Port.ToString());
    }

    /// <summary>
    /// 用裸 socket 发一条**原始请求行**，把响应原样读回来。
    /// </summary>
    /// <remarks>
    /// 不能用 <c>HttpClient</c>：它（以及 <c>Uri</c>）会先按 RFC 3986 归一化路径，
    /// 于是「原样送出去」这件事根本做不到 —— 要守的正是这一段。
    /// </remarks>
    private static async Task<string> SendRawAsync(string host, string port, string requestLine)
    {
        using var client = new TcpClient();

        await client.ConnectAsync(host, int.Parse(port));

        using var stream = client.GetStream();

        var request = $"{requestLine} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();

        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
