using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// OpenAPI 文档的生成与落盘。
///
/// <para>
/// 两条导出路径共用这一份代码，因此产物一致：
/// ① 应用启动时按环境变量 <c>IDEA_EXPORT_OPENAPI=&lt;路径&gt;</c> 导出；
/// ② <c>dotnet run --project tools/ExportOpenApi</c>（工具项目复用本类）。
/// </para>
/// </summary>
public static class OpenApiExporter
{
    /// <summary>导出路径的环境变量名。</summary>
    public const string ExportPathVariable = "IDEA_EXPORT_OPENAPI";

    /// <summary>
    /// 生成 OpenAPI 文档的 JSON 文本。
    /// </summary>
    /// <remarks>
    /// 文档服务在 DI 里是**按文档名注册的 keyed service**，取文档要带上文档名。
    /// </remarks>
    /// <exception cref="InvalidOperationException">服务没注册 OpenAPI（漏了 AddOpenApi）。</exception>
    public static async Task<string> GenerateJsonAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(IdeaOpenApi.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken);

        // 序列化成 OpenAPI 3.1（框架默认版本）。
        return await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);
    }

    /// <summary>
    /// 把 OpenAPI 文档写到指定文件（父目录不存在就建）。
    /// </summary>
    public static async Task ExportAsync(
        IServiceProvider services,
        string path,
        CancellationToken cancellationToken = default)
    {
        var json = await GenerateJsonAsync(services, cancellationToken);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 末尾补一个换行：提交进仓库的 JSON 文件按 POSIX 惯例以换行结尾。
        await File.WriteAllTextAsync(path, json + "\n", cancellationToken);
    }

    /// <summary>
    /// 启动后按环境变量 <c>IDEA_EXPORT_OPENAPI</c> 把文档导出到磁盘；没设就什么都不做。
    /// </summary>
    /// <remarks>
    /// 挂在 <c>ApplicationStarted</c> 上而不是启动前：文档的内容来自已映射的端点，
    /// 应用没起来之前生成出来是空的（路径一个都没有）。
    /// </remarks>
    public static void ExportIfRequested(WebApplication app)
    {
        var path = Environment.GetEnvironmentVariable(ExportPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                // 回调是同步的（ApplicationStarted 是 CancellationToken 的回调），
                // 这里同步等导出完成：导出的是几百 KB 的 JSON，阻塞可以接受。
                ExportAsync(app.Services, path).GetAwaiter().GetResult();
                app.Logger.LogInformation("OpenAPI 文档已导出到 {Path}", path);
            }
            catch (Exception ex)
            {
                // 导出失败不该把应用带崩：启动回调里的异常会冒泡成宿主启动失败。
                app.Logger.LogError(ex, "导出 OpenAPI 文档失败：{Path}", path);
            }
        });
    }
}
