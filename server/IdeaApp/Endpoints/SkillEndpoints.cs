using IdeaApp.Application;
using IdeaApp.Contracts;
using IdeaApp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// skill 内容分发端点。
///
/// <para>
/// 服务端是 skill 内容的**唯一权威**：agent 手上的说明书会随服务更新而过期，
/// 过期的手册会让 agent 按旧语义操作。这一组端点让 agent 随时取到
/// 「当前服务端这一份」内容。
/// </para>
///
/// <para>
/// 三个端点：
/// <list type="bullet">
/// <item><c>GET /skill/idea-manager/version</c>：清单（版本 + 入口 + 逐个文件的哈希）；</item>
/// <item><c>GET /skill/idea-manager/{**path}</c>：取文件；</item>
/// <item><c>GET /skill/{**rest}</c>：<c>/skill/**</c> 下的兜底，回 404 + ProblemDetails。</item>
/// </list>
/// </para>
///
/// <para>
/// 兜底那一条是必需的：SPA 兜底（<c>MapFallbackToFile</c>）是 catch-all，
/// 会把没匹配到的 GET 变成 200 + index.html —— 只看状态码的调用方会把
/// 「路径打错」当成成功。
/// </para>
///
/// <para>
/// AOT 硬性要求：端点返回的**静态类型**必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>。
/// </para>
/// </summary>
public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        // 清单：版本 + 入口 + 逐个文件的哈希与大小。
        // 取件器拿它判断要不要重新同步，并逐个校验取回来的内容。
        app.MapGet("/skill/idea-manager/version", (SkillCatalog catalog) =>
            ApiResults.Run(() => Results.Json(catalog.BuildManifest(),
                IdeaJsonContext.Default.SkillManifestDto)))
            .Produces<SkillManifestDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "skill_not_distributed");

        // 取文件：路径解析与存在性由 SkillCatalog 把关（穿越一律 false）。
        //
        // 路径参数**显式**从路由取（[FromRoute]）：不写的话，catch-all 没取到值时
        // 框架会退回从请求体绑定，而 GET 没有请求体 ——
        // 结果是 400 + invalid_request_body「请求体不是合法的 JSON」，
        // 对一个压根没有请求体的端点来说，这句话是误导（规格里也没有 400 这条）。
        // 显式绑定之后，取不到值就是「副本里没有这个文件」→ 404。
        app.MapGet("/skill/idea-manager/{**path}", ([FromRoute] string? path, SkillCatalog catalog) =>
            ApiResults.Run(() =>
            {
                // 分发副本目录不在：这是「部署漏了」，不是「文件没有」——
                // 两个错误码要分得开，装 skill 的人该知道该去查哪一头。
                if (!catalog.IsDistributed)
                {
                    throw new ApiException(
                        404,
                        "skill_not_distributed",
                        catalog.NotDistributedDetail);
                }

                if (!catalog.TryResolveFile(path, out var fullPath))
                {
                    throw new ApiException(
                        404,
                        "skill_file_not_found",
                        string.IsNullOrEmpty(path)
                            ? "分发副本里没有这个文件：路径是空的。"
                            : $"分发副本里没有这个文件：{path}");
                }

                return Results.File(File.ReadAllBytes(fullPath), ContentTypeOf(fullPath));
            }))
            // 内容类型按扩展名给（markdown / json / 纯文本），
            // 契约里要写出来 —— 只写 .Produces<string>(200) 的话框架会默认成
            // application/json，agent 按契约去解析 markdown 就 parse 错了。
            .Produces<string>(
                StatusCodes.Status200OK,
                "text/markdown",
                "application/json",
                "text/plain")
            .ProducesApiProblem(
                StatusCodes.Status404NotFound,
                "skill_file_not_found",
                "skill_not_distributed");

        // /skill/** 下的兜底：没有对应端点就是 404 + ProblemDetails，
        // 绝不能落到 SPA 兜底变成 200 + HTML。
        //
        // 路由里的 catch-all 参数**要绑出来**：不绑的话契约里这条路径一个参数都没有，
        // agent 看着这条路径不知道该往里填什么（契约是给 agent 当手册用的）。
        app.MapGet("/skill/{**rest}", ([FromRoute] string? rest) => ApiResults.Run(() =>
            throw new ApiException(
                404,
                "skill_endpoint_not_found",
                $"/skill 下的路径没有对应端点：{(string.IsNullOrEmpty(rest) ? "(空路径)" : rest)}")))
            .ProducesApiProblem(StatusCodes.Status404NotFound, "skill_endpoint_not_found");

        return app;
    }

    /// <summary>按扩展名给内容类型（其余一律按纯文本给）。</summary>
    private static string ContentTypeOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" => "text/markdown; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            _ => "text/plain; charset=utf-8"
        };
}
