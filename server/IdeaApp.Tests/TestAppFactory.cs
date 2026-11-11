using System.Globalization;
using IdeaApp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    /// <param name="publisher">
    /// 覆盖组合根注册的 <see cref="IEventPublisher"/>（记录型的 fake）；
    /// 不给就用应用默认的事件总线（此时没有订阅者，publish 是空操作）。
    /// </param>
    public TestAppFactory(int? maxIdeasPerResponse = null, IEventPublisher? publisher = null)
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_tempDirectory, "idea.db");
        MaxIdeasPerResponse = maxIdeasPerResponse;
        Publisher = publisher;
    }

    /// <summary>本次测试实例使用的 SQLite 文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>本次测试实例覆盖的单次返回上限（null = 用应用默认值）。</summary>
    public int? MaxIdeasPerResponse { get; }

    /// <summary>本次测试实例覆盖的事件发布器（null = 用应用默认的事件总线）。</summary>
    public IEventPublisher? Publisher { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = DatabasePath,
                ["Api:MaxIdeasPerResponse"] = MaxIdeasPerResponse?.ToString(CultureInfo.InvariantCulture)
            }));

        var publisher = Publisher;
        if (publisher is not null)
        {
            // 覆盖组合根注册的事件总线：后注册的赢。
            builder.ConfigureServices(services => services.AddSingleton(publisher));
        }
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
