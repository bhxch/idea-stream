using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 批量操作与合并。
///
/// 批量：一次多选 → 标记完成 / 加标签 / 删除；
/// 合并：多条想法 → 一条全新的想法，源想法硬删。
///
/// AOT 硬性要求：每个端点返回的**静态类型**都必须在 <see cref="IdeaJsonContext"/> 里有条目。
/// 批量返回 <c>List&lt;IdeaDto&gt;</c>、合并返回 <c>IdeaDto</c>，两者都已在白名单里。
/// </summary>
public static class BulkIdeaEndpoints
{
    public static IEndpointRouteBuilder MapBulkIdeaEndpoints(this IEndpointRouteBuilder app)
    {
        // 批量标记完成/取消完成：返回受影响的想法数组。
        // 路径里的 bulk 是字面量段，路由优先级高于 /api/ideas/{id}/complete 的 {id} 段。
        app.MapPost("/api/ideas/bulk/complete", (BulkCompleteRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(
                service.BulkSetDone(request.Ids ?? Array.Empty<string>(), request.Done).ToList(),
                IdeaJsonContext.Default.ListIdeaDto)));

        // 批量设置标签：默认并集式追加，replace: true 时覆盖。
        app.MapPost("/api/ideas/bulk/tags", (BulkTagsRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(
                service.BulkSetTags(
                    request.Ids ?? Array.Empty<string>(),
                    request.Tags ?? Array.Empty<string>(),
                    request.Replace ?? false).ToList(),
                IdeaJsonContext.Default.ListIdeaDto)));

        // 批量删除：软删（进回收站），没有响应体。
        app.MapPost("/api/ideas/bulk/delete", (BulkDeleteRequest request, IdeaService service) =>
            ApiResults.Run(() =>
            {
                service.BulkDelete(request.Ids ?? Array.Empty<string>());
                return Results.NoContent();
            }));

        // 合并：多条想法 → 一条全新的想法，返回合并结果。
        app.MapPost("/api/ideas/merge", (MergeIdeasRequest request, IdeaService service) =>
            ApiResults.Run(() => Results.Json(
                service.Merge(request.Ids ?? Array.Empty<string>(), request.Content, request.Title),
                IdeaJsonContext.Default.IdeaDto)));

        return app;
    }
}
