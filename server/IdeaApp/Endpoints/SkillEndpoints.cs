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
    /// <summary>
    /// 分发副本里一份文件的大小上限（4 MiB）。
    /// </summary>
    /// <remarks>
    /// 取文件端点会把整个文件**读进内存**（<c>File.ReadAllBytes</c>）——
    /// 那条路上没有闸门的话，一份超大文件就能把内存吃光。
    /// <para>
    /// 分发副本里每份都是几十 KB 的文档，4 MiB 已经是宽得离谱的上限：
    /// 超了的那一份**不该在分发副本里**，所以按「没有这个文件」处理。
    /// </para>
    /// </remarks>
    private const long MaxFileBytes = 4 * 1024 * 1024;

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

                return Results.File(ReadDistributedFile(fullPath, path), ContentTypeOf(fullPath));
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

    /// <summary>
    /// 读分发副本里的一份文件；读不到就是 404，不是 500。
    /// </summary>
    /// <param name="fullPath">副本里的绝对路径（已经过 <c>TryResolveFile</c> 的把关）。</param>
    /// <param name="path">请求里的相对路径（进错误详情用）。</param>
    /// <remarks>
    /// <para>
    /// <b>存在性检查与读取之间有缝</b>（TOCTOU）：文件可能在这中间被删掉、
    /// 被换掉、被换成一个读不了的东西（另一个进程占着它、权限被抽掉……）。
    /// 早先这里是裸的 <c>File.ReadAllBytes</c>：读失败抛 <c>IOException</c> →
    /// 500 <c>internal_error</c>。而「读不到」对调用方来说就是
    /// 「副本里没有这个文件」→ 404 <c>skill_file_not_found</c>。
    /// </para>
    /// <para>
    /// <b>大小有闸门</b>：整个文件读进内存没有上限是不行的（见 <see cref="MaxFileBytes"/>）。
    /// 超限的那一份按「副本里没有这个文件」处理 —— 分发副本里不该有这么大的一份。
    /// </para>
    /// </remarks>
    internal static byte[] ReadDistributedFile(string fullPath, string? path)
    {
        var name = string.IsNullOrEmpty(path) ? fullPath : path;

        try
        {
            // 先看大小：闸门要在**读进内存之前**。
            var length = new FileInfo(fullPath).Length;

            if (length > MaxFileBytes)
            {
                throw new ApiException(
                    404,
                    "skill_file_not_found",
                    TooLargeDetail(name, length));
            }

            var bytes = File.ReadAllBytes(fullPath);

            // 再量一遍**读回来的**字节：文件可能在「看大小」与「读」之间被换掉了
            // （TOCTOU 的另一半）—— 换成了大的那份，也不能让它进内存。
            if (bytes.LongLength > MaxFileBytes)
            {
                throw new ApiException(
                    404,
                    "skill_file_not_found",
                    TooLargeDetail(name, bytes.LongLength));
            }

            return bytes;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 文件没了、读不了、被换成了别的东西：都是「副本里没有这个文件」。
            // **不是 500** —— 500 说的是「服务端自己坏了」，而这里坏的是副本的内容。
            throw new ApiException(
                404,
                "skill_file_not_found",
                $"分发副本里的这个文件读不到了：{name}（在检查存在与读取之间被删掉、被换掉或被别的进程占着）。");
        }
    }

    /// <summary>「文件超过分发副本的大小上限」那一句（进错误详情）。</summary>
    private static string TooLargeDetail(string name, long length) =>
        $"分发副本里的这个文件超过分发副本的大小上限（{MaxFileBytes / 1024 / 1024} MiB）："
        + $"{name}（实际 {length} 字节）—— 分发副本里不该有这么大的一份。";
}
