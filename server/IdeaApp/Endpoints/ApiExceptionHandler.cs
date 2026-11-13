using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IdeaApp.Endpoints;

/// <summary>
/// 框架层异常的统一出口：把「模型绑定阶段的失败」与「未预期的异常」也变成
/// RFC 9457 的 ProblemDetails（<c>application/problem+json</c> + 扩展字段 <c>code</c>）。
///
/// <para>
/// 端点里的业务错误由 <see cref="ApiResults.Run"/> 兜住（它自己造 ProblemDetails），
/// 组合根不碰那些路径。这里管的是**框架层**抛出来的异常：
/// </para>
/// <list type="bullet">
/// <item><b>模型绑定失败</b>：请求体不是合法 JSON、body 为空、body 的类型对不上 ——
/// 框架在进端点之前就抛 <see cref="BadHttpRequestException"/>。
/// 不接住它的话，Kestrel 会回一个 400 + **空响应体**：
/// 没有 <c>application/problem+json</c>、没有 <c>code</c>，
/// 而契约（openapi/idea-v1.json）给这些端点声明的是 400 + ProblemDetails ——
/// agent 按契约读，遇到畸形 body 会拿到一个空 body，
/// 稳定的业务错误码在这条路径上整个失效。</item>
/// <item><b>其余未预期的异常</b>：500 + <c>code=internal_error</c>，
/// 真实原因只进日志，不进响应体（与 <see cref="ApiResults.Run"/> 的口径一致）。</item>
/// </list>
///
/// <para>
/// <b>不要绕开 <see cref="ApiResults.Run"/></b>：端点里的业务异常已经由它处理，
/// 走全局 handler 只会让那些错误丢掉自己的状态码与错误码。
/// </para>
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 客户端断开导致的取消：框架在调用 handler **之前**就处理掉这种情况
        // （RequestAborted 已取消 → 499，不写响应体、不刷错误日志），
        // 所以走到这里的取消不是「客户端走了」，按未预期的异常处理（500）。
        //
        // 响应已经开始（例如 SSE 推流途中出错）：响应体改不了了，
        // 原样抛回去，让框架按「响应已开始」的方式收场。
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var failure = Describe(exception);

        if (failure is not null)
        {
            // 请求本身不合法：400/413 + 对应的错误码。
            logger.LogWarning(exception, "请求体绑定失败（{Code}）", failure.Code);

            var problem = ApiResults.ProblemResponse(
                failure.Status,
                "请求失败",
                failure.Detail,
                failure.Code);

            await problem.ExecuteAsync(httpContext);
            return true;
        }

        // 其余未预期的异常：500 + internal_error。裸 500 没有
        // application/problem+json、没有 code，消费方拿不到稳定的机器可读错误码。
        logger.LogError(exception, "请求处理时发生未预期的异常");

        var error = ApiResults.ProblemResponse(
            StatusCodes.Status500InternalServerError,
            "服务器内部错误",
            "内部错误，请查看服务端日志",
            ApiErrorCodes.InternalError.Code);

        await error.ExecuteAsync(httpContext);
        return true;
    }

    /// <summary>
    /// 把框架层异常翻成业务错误；翻不出来（不是请求本身的问题）返回 null。
    /// </summary>
    /// <remarks>
    /// 状态码写成**字面量**：错误码清单的守卫测试（<c>ApiErrorCodeTests</c>）
    /// 扫的是 <c>new ApiException(状态码, "错误码", …)</c> 调用点，
    /// 状态码从变量来它就认不出来（守卫会对这个码失明）。
    /// </remarks>
    private static ApiException? Describe(Exception exception) => exception switch
    {
        // 400：请求体不是合法 JSON / body 为空 / body 类型对不上。
        BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status400BadRequest =>
            new ApiException(400, "invalid_request_body", "请求体不是合法的 JSON（模型绑定阶段的失败）。"),

        // 413：请求体超过大小限制。
        BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status413PayloadTooLarge =>
            new ApiException(413, "request_body_too_large", "请求体超过大小限制。"),

        // 415：Content-Type 不是 JSON。
        BadHttpRequestException bad when bad.StatusCode == StatusCodes.Status415UnsupportedMediaType =>
            new ApiException(415, "unsupported_media_type", "请求的 Content-Type 不是 application/json。"),

        _ => null
    };
}
