using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 项目 API。
///
/// AOT 硬性要求：每个端点返回的**静态类型**都必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>（集合包装类型同理）。
/// </summary>
public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", (ProjectService service) =>
            ApiResults.Run(() => Results.Json(service.List().ToList(), IdeaJsonContext.Default.ListProjectDto)))
            .Produces<List<ProjectDto>>(StatusCodes.Status200OK);

        app.MapPost("/api/projects", (CreateProjectRequest request, ProjectService service) =>
            ApiResults.Run(() => Results.Json(service.Create(request.Name),
                IdeaJsonContext.Default.ProjectDto, statusCode: StatusCodes.Status201Created)))
            .Produces<ProjectDto>(StatusCodes.Status201Created);

        app.MapPatch("/api/projects/{id}", (string id, RenameProjectRequest request, ProjectService service) =>
            ApiResults.Run(() => Results.Json(service.Rename(id, request.Name), IdeaJsonContext.Default.ProjectDto)))
            .Produces<ProjectDto>(StatusCodes.Status200OK);

        app.MapDelete("/api/projects/{id}", (string id, ProjectService service) =>
            ApiResults.Run(() =>
            {
                service.Delete(id);
                return Results.NoContent();
            }))
            .Produces(StatusCodes.Status204NoContent);

        // 恢复语义归回收站管：连带恢复「因删项目而删」的想法（见 TrashService）。
        app.MapPost("/api/projects/{id}/restore", (string id, TrashService service) =>
            ApiResults.Run(() => Results.Json(service.RestoreProject(id), IdeaJsonContext.Default.ProjectDto)))
            .Produces<ProjectDto>(StatusCodes.Status200OK);

        app.MapPost("/api/projects/reorder", (ReorderProjectsRequest request, ProjectService service) =>
            ApiResults.Run(() => Results.Json(service.Reorder(request.Ids).ToList(),
                IdeaJsonContext.Default.ListProjectDto)))
            .Produces<List<ProjectDto>>(StatusCodes.Status200OK);

        return app;
    }
}
