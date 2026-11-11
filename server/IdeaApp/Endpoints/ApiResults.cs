using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdeaApp.Endpoints;

/// <summary>
/// 端点共用的结果包装：把业务错误（<see cref="ApiException"/>）与未预期的异常
/// 统一转成 RFC 9457 的 ProblemDetails + 扩展字段 <c>code</c>。
///
/// <remarks>
/// 各端点文件里的异常包装逻辑逐字重复，抽到这里共用一份：
/// 改错误响应格式时只改一处，不会出现「项目接口改了、想法接口没改」。
/// </remarks>
public static class ApiResults
{
    /// <summary>
    /// 未预期的异常记在这里：真实异常信息只进日志，不进响应体。
    /// </summary>
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>由组合根在启动时注入日志器。</summary>
    /// <remarks>
    /// <see cref="Run"/> 是静态工具，拿不到 DI 里的 <see cref="ILogger"/>。
    /// 与其为它改全部端点的签名（每个端点多带一个日志器参数），
    /// 不如由组合根注入一次，端点文件保持原样。
    /// </remarks>
    public static void UseLogger(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>执行端点逻辑，把业务错误统一转成 ProblemDetails。</summary>
    public static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ApiException ex)
        {
            return ProblemResponse(ex.Status, "请求失败", ex.Detail, ex.Code);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开导致的取消：不是服务端错误，原样抛出。
            // 转成 500 的话，一次断连会在日志里刷出一屏错误。
            throw;
        }
        catch (Exception ex)
        {
            // 其余异常（DB 层等）统一成 500 + ProblemDetails：
            // 裸 500 没有 application/problem+json、没有 code，
            // agent 消费方拿不到稳定的机器可读错误码。
            // 真实异常信息只进日志，不泄露给外部。
            _logger.LogError(ex, "端点执行时发生未预期的异常");

            return ProblemResponse(500, "服务器内部错误", "内部错误，请查看服务端日志", "internal_error");
        }
    }

    /// <summary>造一个带 <c>code</c> 扩展字段的 ProblemDetails 响应。</summary>
    /// <remarks>
    /// 流式端点（如 SSE）没法把逻辑包进 <see cref="Run"/>：
    /// 响应一旦开始就没法再改成 ProblemDetails，
    /// 所以那里要在响应开始之前自己造一份。
    /// </remarks>
    public static IResult ProblemResponse(int status, string title, string detail, string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };

        problem.Extensions["code"] = code;

        return Results.Json(problem, IdeaJsonContext.Default.ProblemDetails,
            statusCode: status, contentType: "application/problem+json");
    }
}
