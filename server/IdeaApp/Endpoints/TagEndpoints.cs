using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 标签字典 API。
///
/// AOT 硬性要求：每个端点返回的**静态类型**都必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>（集合包装类型同理）。
/// </summary>
public static class TagEndpoints
{
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        // 标签字典（含使用计数）。
        app.MapGet("/api/tags", (TagService service) =>
            ApiResults.Run(() => Results.Json(service.List().ToList(), IdeaJsonContext.Default.ListTagDto)));

        // 输入补全：先列当前项目用过的标签，再列全局其它标签。
        app.MapGet("/api/tags/suggest", (string? projectId, string? prefix, TagService service) =>
            ApiResults.Run(() => Results.Json(service.Suggest(projectId, prefix ?? string.Empty).ToList(),
                IdeaJsonContext.Default.ListString)));

        return app;
    }
}
