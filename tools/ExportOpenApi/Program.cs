using IdeaApp.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ExportOpenApi;

/// <summary>
/// 导出 OpenAPI 文档的小工具：把应用本体起一个实例，让它把文档写出来。
///
/// <para>
/// 为什么在工具里跑**应用本体**（而不是照着接口手抄一份路由表）：
/// 这个文件是给 agent 当**接口契约**用的，抄出来的那份迟早和应用对不上。
/// 这里跑的是 <c>server/IdeaApp</c> 自己的组合根 —— 端点映射、依赖注入、
/// OpenAPI 配置全是应用自己那份，导出的文档与应用实际暴露的一致。
/// </para>
///
/// <para>
/// 用法：
/// <c>dotnet run --project tools/ExportOpenApi</c> 写到 &lt;仓库根&gt;/openapi/idea-v1.json；
/// <c>dotnet run --project tools/ExportOpenApi -- &lt;路径&gt;</c> 写到指定路径。
/// </para>
///
/// <para>
/// 不需要先构建前端：应用目录下的 <c>wwwroot/</c> 缺了工具会自己建
/// （框架的静态资源加载器要求它在，否则宿主起不来）。
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(RepositoryRoot(), "openapi", "idea-v1.json");

        // 数据库指到临时目录：起应用会建库建表，不能污染仓库里的 data/idea.db。
        var databasePath = Path.Combine(
            Path.GetTempPath(), "idea-export-openapi", "idea.db");

        // 内容根 = 应用的源码目录（server/IdeaApp）。
        var contentRoot = AppProjectDirectory();

        // 先把 <内容根>/wwwroot 建出来，否则宿主起不来。
        //
        // 为什么这个目录会缺：框架的静态资源加载器（StaticWebAssetsLoader）会为
        // <内容根>/wwwroot 建一个 PhysicalFileProvider，而 PhysicalFileProvider
        // 对不存在的目录直接抛 DirectoryNotFoundException —— 崩在 WebApplicationFactory
        // 拉起宿主的那一步，导出工具连第一行输出都打不出来。
        // 用 UseWebRoot 绕不开：那个路径是按**内容根**拼出来的，不看 WebRootPath。
        //
        // 为什么它会缺：wwwroot 是前端构建产物的落点（vite build --outDir），
        // 而 .gitignore 排除了它 —— git 不跟踪空目录，所以**新克隆的仓库里
        // 这个目录根本不存在**。上一轮靠手工 mkdir -p 绕过去，那不是修复：
        // 任何人重新生成契约文件都会先崩一次。工具自己建，导出就不依赖
        // 「谁记得先建一下目录」这种口头约定。
        Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot"));

        // global::Program 是**应用本体**的入口类型（IdeaApp 的 Program.cs 里
        // 声明的 public partial class Program）。本工具的入口类型在自己的命名空间里，
        // 所以这里写 global:: 不会有歧义。
        using var factory = new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder =>
            {
                // 内容根显式给应用的源码目录：框架默认按**当前工作目录**找
                // MvcTestingAppManifest.json，而这个工具通常是从仓库根跑的
                // （那里没有这个文件），解析失败会退化成不存在的路径。
                builder.UseContentRoot(contentRoot);

                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Database:Path"] = databasePath
                    }));
            });

        // 先起服务：端点是在启动时才进 ApiExplorer 的，应用没起来之前生成的文档是空的。
        // client 本身用不上 —— 它只是把宿主拉起来的副作用。
        using var client = factory.CreateClient();

        await OpenApiExporter.ExportAsync(factory.Services, outputPath);

        Console.WriteLine($"已导出 OpenAPI 文档：{outputPath}");
        return 0;
    }

    /// <summary>
    /// 往上找仓库根：bin/Debug/net10.0 → … → 仓库根（认 IdeaApp.sln）。
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IdeaApp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"找不到仓库根：从 {AppContext.BaseDirectory} 往上找不到 IdeaApp.sln。");
    }

    /// <summary>
    /// 应用的源码目录（内容根）：仓库根下 server/ 里带 IdeaApp.csproj 的那个目录。
    /// </summary>
    private static string AppProjectDirectory()
    {
        var serverDirectory = Path.Combine(RepositoryRoot(), "server");

        foreach (var directory in Directory.EnumerateDirectories(serverDirectory))
        {
            if (File.Exists(Path.Combine(directory, "IdeaApp.csproj")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException($"找不到应用项目目录：{serverDirectory} 下没有 IdeaApp.csproj。");
    }
}
