using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IdeaApp.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdeaApp.Tests;

/// <summary>
/// 错误契约的集成测试。
///
/// <para>
/// 契约（openapi/idea-v1.json）承诺的是：所有错误响应都是 RFC 9457 的
/// ProblemDetails —— <c>application/problem+json</c> + 扩展字段 <c>code</c>。
/// </para>
///
/// <para>
/// 这里守的是**模型绑定阶段的失败**（请求体不是合法 JSON、body 为空、
/// body 类型对不上）：它在进端点之前就失败了，以前由框架自己吞掉 ——
/// 回一个 400 + **空响应体**，既没有 problem+json，也没有 code。
/// agent 按契约读，遇到畸形 body 会拿到一个空 body，
/// 稳定的业务错误码在这条路径上整个失效。
/// </para>
/// </summary>
public sealed class ErrorContractTests
{
    /// <summary>
    /// 畸形 JSON → 400 + ProblemDetails + 稳定的错误码。
    /// </summary>
    [Fact]
    public async Task Malformed_json_body_returns_400_problem_details_with_a_code()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/ideas",
            new StringContent("{\"content\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("invalid_request_body", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    /// <summary>
    /// 空 body 也是模型绑定失败：同样 400 + ProblemDetails + 错误码。
    /// </summary>
    [Fact]
    public async Task Empty_body_returns_400_problem_details_with_a_code()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/ideas",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_request_body", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// body 的类型对不上（要对象，给了数组）：JSON 合法但绑不上 —— 同一类失败。
    /// </summary>
    [Fact]
    public async Task Body_of_the_wrong_type_returns_400_problem_details_with_a_code()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/ideas",
            new StringContent("[1, 2]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_request_body", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 不止一个端点：每个带请求体的端点都该走同一条路径。
    /// </summary>
    [Theory]
    [InlineData("/api/ideas")]
    [InlineData("/api/projects")]
    [InlineData("/api/ideas/bulk/delete")]
    [InlineData("/api/ideas/merge")]
    public async Task Every_endpoint_with_a_body_reports_malformed_json_the_same_way(string path)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            path,
            new StringContent("{\"content\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_request_body", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 业务错误的老路不能被改坏：合法 JSON 但内容非法 → 仍然是它自己的错误码。
    /// </summary>
    [Fact]
    public async Task Business_errors_keep_their_own_code()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ideas", new { content = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_content", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 框架层抛出来的未预期异常 → 500 + ProblemDetails + <c>internal_error</c>，
    /// 不能是裸 500（裸 500 没有 problem+json，也没有 code）。
    /// </summary>
    [Fact]
    public async Task Unexpected_exceptions_become_500_problem_details_with_internal_error()
    {
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);

        var context = new DefaultHttpContext();

        // 结果执行时要从 RequestServices 里取 JSON 选项。
        context.RequestServices = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .BuildServiceProvider();

        // Results.Json 写的是 BodyWriter：DefaultHttpResponse 的 BodyWriter 与 Body
        // 共用同一个 StreamResponseBodyFeature，所以设了 Body 就够。
        var stream = new MemoryStream();
        context.Response.Body = stream;

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("模拟未预期的异常"),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        await context.Response.BodyWriter.FlushAsync();

        var text = Encoding.UTF8.GetString(stream.ToArray());

        using var document = JsonDocument.Parse(text);

        var problem = document.RootElement;
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("internal_error", problem.GetProperty("code").GetString());

        // 真实异常信息不进响应体。
        Assert.DoesNotContain("模拟未预期的异常", text);
    }

    /// <summary>读错误响应：先断言内容类型是 problem+json，再把 JSON 解析出来。</summary>
    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // JsonElement 在文档释放后失效，复制一份出去。
        return document.RootElement.Clone();
    }
}
