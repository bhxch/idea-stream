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
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer<OpenApiInfoTransformer>();

        // 查询参数的 description / default 与 /api/search 的 q 必填标记。
        // 操作级 transformer：框架先生成每个操作（含参数），再在这里按路径改写。
        options.AddOperationTransformer<OpenApiQueryParameterTransformer>();

        // 错误响应：错误码清单与每个错误响应的描述都写进契约。
        // 顺序无关（一个改 info、一个改 paths/components），但两者都在
        // 「文档已生成」之后跑：框架先生成 paths，再跑文档 transformer。
        options.AddDocumentTransformer<OpenApiProblemTransformer>();

        // 事件类型：框架只为「端点返回类型」生成 schema，而事件（IdeaEvent）
        // 不出现在任何端点的返回类型里（它是 SSE 的报文、也是 webhook 的投递体）——
        // 它是消费方的公共契约，必须自己写进契约文件。
        options.AddDocumentTransformer<OpenApiEventTransformer>();
    }
}
