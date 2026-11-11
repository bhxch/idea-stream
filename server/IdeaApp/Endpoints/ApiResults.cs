using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IdeaApp.Endpoints;

/// <summary>
/// 端点共用的结果包装：把业务错误（<see cref="ApiException"/>）统一转成
/// RFC 9457 的 ProblemDetails + 扩展字段 <c>code</c>。
/// </summary>
/// <remarks>
/// 各端点文件里的异常包装逻辑逐字重复，抽到这里共用一份：
/// 改错误响应格式时只改一处，不会出现「项目接口改了、想法接口没改」。
/// </remarks>
public static class ApiResults
{
    /// <summary>执行端点逻辑，把业务错误统一转成 ProblemDetails。</summary>
    public static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ApiException ex)
        {
            var problem = new ProblemDetails
            {
                Status = ex.Status,
                Title = "请求失败",
                Detail = ex.Detail
            };

            problem.Extensions["code"] = ex.Code;

            return Results.Json(problem, IdeaJsonContext.Default.ProblemDetails,
                statusCode: ex.Status, contentType: "application/problem+json");
        }
    }
}
