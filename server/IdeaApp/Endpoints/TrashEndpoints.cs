using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 回收站 API。
///
/// <para>
/// 恢复端点（<c>POST /api/ideas/{id}/restore</c>、<c>POST /api/projects/{id}/restore</c>）
/// 留在各自的资源文件里，但服务换成 <see cref="TrashService"/> —— 恢复语义由回收站统一负责。
/// </para>
///
/// <para>
/// AOT 硬性要求：端点返回的**静态类型**必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 集合包装类型同理。
/// </para>
/// </summary>
public static class TrashEndpoints
{
    public static IEndpointRouteBuilder MapTrashEndpoints(this IEndpointRouteBuilder app)
    {
        // 回收站条目列表：容器（已删项目）+ 顶层想法，裸数组，不套信封。
        app.MapGet("/api/trash", (TrashService service) =>
            ApiResults.Run(() => Results.Json(service.List().ToList(),
                IdeaJsonContext.Default.ListTrashEntryDto)));

        return app;
    }
}
