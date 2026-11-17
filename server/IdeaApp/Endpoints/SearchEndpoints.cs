using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 全库搜索 API（给 agent 用的统一入口）。
///
/// AOT 硬性要求：端点返回的**静态类型**必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>（少登记一个，AOT 产物下这个端点就是 500，
/// 而普通 <c>dotnet test</c> 全绿）。
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // 全库搜索：一次请求拿到想法、项目、标签三组命中，三组各自带分页信息。
        //
        // limit / offset 绑成 string?（不是 int?）：自己解析、自己判范围，
        // 非法一律 400 invalid_pagination（detail 说清楚收到的是什么）。
        // 绑 int? 时 ?limit=abc 在参数绑定阶段就失败，框架**只设一个 400 状态码
        // 就返回** —— 空 body、没有 code，agent 连「是哪儿错了」都读不出来。
        //
        // includeTrash 默认 **true**：它是「全库找东西」，
        // 东西被删进回收站不该凭空消失（用户八成是想找回来）。
        // 每条结果都标 trashed，agent 一眼能看出这条是不是已经删了。
        app.MapGet("/api/search",
            (string? q, string? limit, string? offset, bool? includeTrash, SearchService service) =>
                ApiResults.Run(() =>
                {
                    var (pageLimit, pageOffset) = SearchService.ParsePagination(limit, offset);

                    var response = service.Search(
                        q ?? string.Empty,
                        pageLimit,
                        pageOffset,
                        includeTrash ?? SearchService.DefaultIncludeTrash);

                    return Results.Json(response, IdeaJsonContext.Default.SearchResponse);
                }))
            .Produces<SearchResponse>(StatusCodes.Status200OK)
            // ⚠️ 这里**不**声明 invalid_request_body：那是**请求体**绑定失败的码，
            // 而 GET /api/search 没有请求体、查询串绑定失败又拿不到它（见上）。
            // 声明一个真不会返回的码，契约就不准了。
            //
            // ⚠️ 也不声明 415：这个端点没有请求体，
            // Content-Type 是什么都无所谓（契约里给无 body 的端点声明 415 就是骗人）。
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "invalid_pagination",
                "invalid_search_query");

        return app;
    }
}
