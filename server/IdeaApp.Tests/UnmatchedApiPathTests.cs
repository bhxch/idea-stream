using System.Net;
using System.Text.Json;

namespace IdeaApp.Tests;

/// <summary>
/// 未匹配的 <c>/api/**</c> 路径的守卫。
///
/// <para>
/// 前端是 history 模式的 SPA，后端用 <c>MapFallbackToFile("index.html")</c>
/// 兜住没匹配到的 GET。而那个 catch-all 兜的是**所有**没匹配到端点的请求 ——
/// 包括 <c>/api/**</c>。接上之后，agent 打错一个 API 路径会拿到
/// **200 + index.html**：只按状态码判断的调用方会把「路径打错」当成成功，
/// 再去把 HTML 当 JSON 解析。
/// </para>
///
/// <para>
/// 这个 API 是给 agent 读的（契约在 <c>openapi/idea-v1.json</c>），
/// 所以未匹配的 API 路径必须按契约回 **404 + ProblemDetails**，
/// 带机器可读的 <c>code</c>。
/// </para>
///
/// <para>
/// 这几条测试是 **hermetic** 的：不依赖前端是否构建过、
/// 不依赖文件系统里有没有 <c>wwwroot/index.html</c>。
/// </para>
/// </remarks>
public sealed class UnmatchedApiPathTests
{
    /// <summary>
    /// 未匹配的 API 路径回 404 ProblemDetails，<b>不是</b> HTML。
    /// </summary>
    [Fact]
    public async Task Unmatched_api_path_returns_404_problem_details()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // 关键：不能是前端页面。
        Assert.DoesNotContain("<!DOCTYPE html>", body, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(body);

        Assert.Equal("api_not_found", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// 显式端点不能被兜底吃掉：<c>/api/health</c> 仍然是 200 JSON。
    /// </summary>
    [Fact]
    public async Task Explicit_api_endpoint_is_not_swallowed_by_the_fallback()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// 资源级的 404 要保持原样：<c>/api/webhooks/{不存在的 id}</c> 是
    /// <c>webhook_not_found</c>，不能被改写成兜底的 <c>api_not_found</c>。
    /// </summary>
    [Fact]
    public async Task Resource_level_404_keeps_its_own_error_code()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/webhooks/no-such-webhook");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("webhook_not_found", document.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// 非 <c>/api</c> 的路径不能被 API 兜底吃掉：<c>/projects</c> 是前端路由，
    /// 归 SPA 兜底管。它在测试环境里回什么（构建过前端就有 index.html，
    /// 没构建就是 404）都不重要 —— 重要的是**不能**是 API 兜底那份
    /// <c>api_not_found</c> ProblemDetails。
    /// </summary>
    [Fact]
    public async Task Frontend_route_is_not_swallowed_by_the_api_fallback()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/projects");

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType == "application/problem+json")
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var code = document.RootElement.TryGetProperty("code", out var value)
                ? value.GetString()
                : null;

            Assert.NotEqual("api_not_found", code);
        }

        // SPA 兜底在位时应该是 200 + HTML；测试环境没有前端产物时是 404。
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"/projects 的状态码是 {response.StatusCode}，既不是 200 也不是 404");
    }
}
