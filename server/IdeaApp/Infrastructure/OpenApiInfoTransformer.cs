using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 把 OpenAPI 文档的标题与版本固定成常量，并去掉 servers 段。
///
/// <para>
/// 标题：框架默认用「入口程序集名 | 文档名」，导出工具与应用本体的入口程序集
/// 不一样，标题就会不一样（见 <see cref="IdeaOpenApi.Configure"/> 的说明）。
/// </para>
///
/// <para>
/// servers 段：框架会按**当前请求地址**填一条 server。契约文件是快照，
/// 里面写死一个回环地址（http://127.0.0.1:5080）既没用（对外地址由 nginx 决定）
/// 又碍事（换个端口跑一次导出，文件就变了）。去掉之后，OpenAPI 的默认语义是
/// 「与文档同源」，两条导出路径的产物也逐字节一致。
/// </para>
/// </summary>
public sealed class OpenApiInfoTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = IdeaOpenApi.Title,
            Version = IdeaOpenApi.Version
        };

        // Servers 可空：为空说明文档还没填 server，不用管。
        document.Servers?.Clear();

        return Task.CompletedTask;
    }
}
