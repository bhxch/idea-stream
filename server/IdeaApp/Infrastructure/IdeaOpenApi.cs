using Microsoft.AspNetCore.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// OpenAPI 文档的公共常量与公共配置。
///
/// <para>
/// 应用本体（<c>server/IdeaApp</c>）与导出工具（<c>tools/ExportOpenApi</c>）
/// 必须用**同一份**配置：两边生成的是同一个接口契约，产物不一致的话，
/// 提交进仓库的 <c>openapi/idea-v1.json</c> 就和线上服务对不上。
/// </para>
/// </summary>
public static class IdeaOpenApi
{
    /// <summary>
    /// 文档名。默认文档名就是 <c>v1</c>，对应服务端的 <c>/openapi/v1.json</c>。
    /// </summary>
    /// <remarks>
    /// 文档服务在 DI 里是**按名字注册的 keyed service**，
    /// 取文档必须用这个名字取（见 <see cref="OpenApiExporter"/>）。
    /// </remarks>
    public const string DocumentName = "v1";

    /// <summary>文档标题。</summary>
    public const string Title = "Idea Manager";

    /// <summary>文档版本。</summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// OpenAPI 文档的公共配置：固定标题与版本。
    /// </summary>
    /// <remarks>
    /// 不固定的话，框架拿「入口程序集名 | 文档名」当标题：应用本体生成的是
    /// <c>IdeaApp | v1</c>，导出工具进程里生成的是 <c>ExportOpenApi | v1</c>。
    /// 同一个接口两种标题，契约文件与服务实际提供的就对不上了。
    /// </remarks>
    public static void Configure(OpenApiOptions options) =>
        options.AddDocumentTransformer<OpenApiInfoTransformer>();
}
