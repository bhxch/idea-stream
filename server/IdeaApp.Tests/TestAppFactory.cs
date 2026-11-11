using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace IdeaApp.Tests;

/// <summary>
/// 集成测试宿主工厂。每个实例独占一个临时 SQLite 库，释放时清理。
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDirectory;

    /// <param name="maxIdeasPerResponse">
    /// 覆盖 <c>Api:MaxIdeasPerResponse</c>；不给就是应用默认值。
    /// 想测「命中数超过上限返回 400」时用小值，不用真的造两千多条。
    /// </param>
    public TestAppFactory(int? maxIdeasPerResponse = null)
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_tempDirectory, "idea.db");
        MaxIdeasPerResponse = maxIdeasPerResponse;
    }

    /// <summary>本次测试实例使用的 SQLite 文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>本次测试实例覆盖的单次返回上限（null = 用应用默认值）。</summary>
    public int? MaxIdeasPerResponse { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = DatabasePath,
                ["Api:MaxIdeasPerResponse"] = MaxIdeasPerResponse?.ToString(CultureInfo.InvariantCulture)
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不应让测试失败。
        }
    }
}
