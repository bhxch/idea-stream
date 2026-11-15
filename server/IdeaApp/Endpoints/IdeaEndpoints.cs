using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 想法 API。
///
/// AOT 硬性要求：每个端点返回的**静态类型**都必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>（集合包装类型同理）。
/// </summary>
public static class IdeaEndpoints
{
    public static IEndpointRouteBuilder MapIdeaEndpoints(this IEndpointRouteBuilder app)
    {
        // 列表：裸数组，排序恒为 updated_at ASC, id ASC（最老的在顶、最新的在底）。
        // 可按项目 / 状态 / 标签过滤；q 走全文索引（语义是「词元相邻」：
        // 中文等价于子串，英文是词级）；limit/offset 是往回翻历史的分页
        // （offset 从最新一端数）。
        // 不给 limit 时命中数超上限返回 400（不静默截断）；给了 limit 就只给那一页。
        app.MapGet("/api/ideas",
            // limit / offset 绑成 int?（不是 string?）：契约里它们是 integer，
            // 而 limit=abc 这种非数字值在进端点之前就绑定失败，
            // 由框架统一回 invalid_request_body（不是 invalid_pagination）。
            (string? projectId, string? status, string? tags, string? tagMode,
             string? q, int? limit, int? offset, IdeaService service) =>
                ApiResults.Run(() =>
                {
                    var query = IdeaService.ParseQuery(projectId, status, tags, tagMode, q, limit, offset);

                    // 关键词：trim 后为空 = 没有关键词（不过滤）。
                    var keyword = IdeaService.ParseKeyword(q);

                    return Results.Json(
                        service.List(query, keyword, limit, offset).ToList(),
                        IdeaJsonContext.Default.ListIdeaDto);
                }))
            .Produces<List<IdeaDto>>(StatusCodes.Status200OK)
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "too_many_ideas",
                "invalid_pagination",
                "invalid_status",
                "invalid_tag_mode",
                // limit / offset 绑成 int? 的代价：?limit=abc 这种非数字值
                // 在进端点之前就绑定失败，框架统一回 invalid_request_body ——
                // 这个码本端点真会返回，契约里就得写得出它（agent 照契约分支处理）。
                "invalid_request_body");

        app.MapPost("/api/ideas", (CreateIdeaRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.Create(
                    request.ProjectId ?? string.Empty,
                    request.Content ?? string.Empty,
                    request.Title,
                    request.Tags ?? Array.Empty<string>()),
                IdeaJsonContext.Default.IdeaDto, statusCode: StatusCodes.Status201Created)))
            .RejectsNonJsonBodyWithProblemDetails()
            .Produces<IdeaDto>(StatusCodes.Status201Created)
            .ProducesApiProblem(StatusCodes.Status400BadRequest, "invalid_content", "invalid_request_body")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "project_not_found")
            .ProducesApiProblem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        app.MapPatch("/api/ideas/{id}", (string id, UpdateIdeaRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.Update(id, request.Title, request.Content),
                IdeaJsonContext.Default.IdeaDto)))
            .RejectsNonJsonBodyWithProblemDetails()
            .Produces<IdeaDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status400BadRequest, "invalid_content", "invalid_request_body")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "idea_not_found")
            .ProducesApiProblem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        app.MapPut("/api/ideas/{id}/tags", (string id, SetTagsRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.SetTags(id, request.Tags ?? Array.Empty<string>()),
                IdeaJsonContext.Default.IdeaDto)))
            .RejectsNonJsonBodyWithProblemDetails()
            .Produces<IdeaDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status400BadRequest, "invalid_request_body")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "idea_not_found")
            .ProducesApiProblem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        app.MapPost("/api/ideas/{id}/complete", (string id, SetDoneRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.SetDone(id, request.Done), IdeaJsonContext.Default.IdeaDto)))
            .RejectsNonJsonBodyWithProblemDetails()
            .Produces<IdeaDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status400BadRequest, "invalid_request_body")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "idea_not_found")
            .ProducesApiProblem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        app.MapDelete("/api/ideas/{id}", (string id, IdeaService service) =>
            ApiResults.Run(() =>
            {
                service.Delete(id);
                return Results.NoContent();
            }))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "idea_not_found");

        // 恢复语义归回收站管（见 TrashService）。
        app.MapPost("/api/ideas/{id}/restore", (string id, TrashService service) =>
            ApiResults.Run(() => Results.Json(service.RestoreIdea(id), IdeaJsonContext.Default.IdeaDto)))
            .Produces<IdeaDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status400BadRequest, "not_in_trash")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "idea_not_found");

        return app;
    }
}
