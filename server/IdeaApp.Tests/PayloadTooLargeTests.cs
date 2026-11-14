using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace IdeaApp.Tests;

/// <summary>
/// 413（请求体太大）的**实测锚点**。
///
/// <para>
/// <c>skills/idea-manager/references/errors.md</c> 写着「413 的响应体是空的、没有 <c>code</c>」——
/// 那是对**平台行为**的断言。光读 <c>ApiExceptionHandler.cs</c> 与
/// <c>ApiErrorCodes.cs</c> 的注释不算数：注释会过期，行为不会。
/// 这里用真的 Kestrel 跑一遍，把「响应体是空的、没有 code」钉成可执行的事实。
/// </para>
///
/// <para>
/// 为什么用**单独的测试宿主**：Kestrel 的请求体上限（<c>MaxRequestBodySize</c>）
/// 是服务器级的设置，要调小它就得动宿主。共享的 <see cref="TestAppFactory"/>
/// 一行都不改 —— 改了会影响其它用例（默认值 30MB 时根本造不出超限请求）。
/// </para>
///
/// <para>
/// 为什么必须用**真的 Kestrel**：<c>TestServer</c>（<c>WebApplicationFactory</c> 的默认）
/// 不实现 <c>IHttpMaxRequestBodySizeFeature</c>，请求体上限在它那儿根本不生效 ——
/// 发多大的 body 都能过。平台行为只能在真服务器上测。
/// </para>
/// </summary>
public sealed class PayloadTooLargeTests
{
    /// <summary>这个宿主的请求体上限（字节）。</summary>
    private const int BodyLimitBytes = 1024;

    private readonly ITestOutputHelper _output;

    /// <summary>构造测试类。</summary>
    /// <param name="output">xunit 的输出通道（把实测到的响应头/响应体打出来，便于回溯）。</param>
    public PayloadTooLargeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 请求体超过上限 → 413 + **空响应体** + 没有 <c>code</c>。
    /// </summary>
    /// <remarks>
    /// 这三条是 skill 文档里那句「413 的响应体是空的、没有 code」的全部内容。
    /// 少任何一条，文档就开始说假话。
    /// </remarks>
    [Fact]
    public async Task A_body_over_the_limit_returns_413_with_an_empty_body_and_no_code()
    {
        using var factory = new SmallBodyLimitAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/ideas",
            new StringContent(
                new string('x', BodyLimitBytes * 4),
                Encoding.UTF8,
                "application/json"));

        _output.WriteLine("状态码: {0}", (int)response.StatusCode);
        _output.WriteLine(
            "响应头: {0}",
            string.Join(
                " | ",
                response.Headers
                    .Concat(response.Content.Headers)
                    .Select(h => $"{h.Key}: {string.Join(",", h.Value)}")));
        _output.WriteLine("Content-Type: {0}", response.Content.Headers.ContentType?.ToString() ?? "(无)");
        var body = await response.Content.ReadAsStringAsync();
        _output.WriteLine("响应体: [{0}]", body);

        // 413。
        Assert.Equal((HttpStatusCode)413, response.StatusCode);

        // 响应体是空的。
        Assert.Equal(string.Empty, body);

        // 没有 code：既没有 ProblemDetails 的 body，也没有 code 字段。
        Assert.Null(response.Content.Headers.ContentType);
        Assert.DoesNotContain("code", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 单独把请求体上限调小的测试宿主。
    /// </summary>
    /// <remarks>
    /// 共享的 <see cref="TestAppFactory"/> 用的是 TestServer（不实现
    /// <c>IHttpMaxRequestBodySizeFeature</c>，上限不生效），所以这里自己起一个
    /// **真 Kestrel** 的宿主 —— <see cref="WebApplicationFactory{TEntryPoint}.UseKestrel(Action{KestrelServerOptions})"/>
    /// 让工厂改用 Kestrel，并监听一个随机的回环端口（端口 0 = 让系统挑一个空闲的）。
    /// </remarks>
    private sealed class SmallBodyLimitAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "idea-tests",
            Guid.NewGuid().ToString("N"));

        /// <summary>构造宿主：换用真 Kestrel，并把请求体上限调到 <see cref="BodyLimitBytes"/>。</summary>
        public SmallBodyLimitAppFactory()
        {
            UseKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = BodyLimitBytes;
                options.Listen(System.Net.IPAddress.Loopback, 0);
            });
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(_tempDirectory, "idea.db")
                }));
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing)
            {
                return;
            }

            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不应让测试失败。
            }
        }
    }
}
