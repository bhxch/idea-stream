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
        // 可按项目 / 状态 / 标签过滤；命中数超上限时返回 400（不静默截断）。
        app.MapGet("/api/ideas",
            (string? projectId, string? status, string? tags, string? tagMode, IdeaService service) =>
                ApiResults.Run(() =>
                {
                    var query = IdeaService.ParseQuery(projectId, status, tags, tagMode);

                    return Results.Json(service.List(query).ToList(), IdeaJsonContext.Default.ListIdeaDto);
                }));

        app.MapPost("/api/ideas", (CreateIdeaRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.Create(
                    request.ProjectId ?? string.Empty,
                    request.Content ?? string.Empty,
                    request.Title,
                    request.Tags ?? Array.Empty<string>()),
                IdeaJsonContext.Default.IdeaDto, statusCode: StatusCodes.Status201Created)));

        app.MapPatch("/api/ideas/{id}", (string id, UpdateIdeaRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.Update(id, request.Title, request.Content),
                IdeaJsonContext.Default.IdeaDto)));

        app.MapPut("/api/ideas/{id}/tags", (string id, SetTagsRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.SetTags(id, request.Tags ?? Array.Empty<string>()),
                IdeaJsonContext.Default.IdeaDto)));

        app.MapPost("/api/ideas/{id}/complete", (string id, SetDoneRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(service.SetDone(id, request.Done), IdeaJsonContext.Default.IdeaDto)));

        app.MapDelete("/api/ideas/{id}", (string id, IdeaService service) =>
            ApiResults.Run(() =>
            {
                service.Delete(id);
                return Results.NoContent();
            }));

        // 恢复语义归回收站管（见 TrashService）。
        app.MapPost("/api/ideas/{id}/restore", (string id, TrashService service) =>
            ApiResults.Run(() => Results.Json(service.RestoreIdea(id), IdeaJsonContext.Default.IdeaDto)));

        return app;
    }
}
