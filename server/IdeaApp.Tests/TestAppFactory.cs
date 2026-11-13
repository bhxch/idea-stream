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
    /// <param name="sseHeartbeatSeconds">
    /// 覆盖 SSE 心跳间隔（秒）；不给就是应用默认值（25 秒）。
    /// 测心跳时用 1 秒 —— 25 秒的等待没法写测试。
    /// </param>
    /// <param name="webhookMaxAttempts">
    /// 覆盖 <c>Webhook:MaxAttempts</c>（投递尝试次数，含首次）；不给就是应用默认值（4）。
    /// 测「重试到上限」时用小值 —— 默认值下要走满 1s/5s/30s 三段退避，太慢。
    /// </param>
    /// <param name="webhookTimeoutSeconds">
    /// 覆盖 <c>Webhook:TimeoutSeconds</c>（单次 HTTP 请求的超时秒数）；不给就是应用默认值（10）。
    /// 测「目标端点卡住」时用大值 —— 默认 10 秒一到，请求就超时了，
    /// 卡住的状态会被超时解除（并发位腾出来、请求记录被写回）。
    /// </param>
    /// <param name="skillPath">
    /// 覆盖 <c>Skill:Path</c>（skill 分发副本的位置）；不给就用仓库里的内容源
    /// （<c>skills/idea-manager/</c>）。测「服务端没分发 skill 内容」时给一个不存在的目录。
    /// </param>
    public TestAppFactory(
        int? maxIdeasPerResponse = null,
        IEventPublisher? publisher = null,
        int? sseHeartbeatSeconds = null,
        int? webhookMaxAttempts = null,
        int? webhookTimeoutSeconds = null,
        string? skillPath = null)
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_tempDirectory, "idea.db");
        MaxIdeasPerResponse = maxIdeasPerResponse;
        Publisher = publisher;
        SseHeartbeatSeconds = sseHeartbeatSeconds;
        WebhookMaxAttempts = webhookMaxAttempts;
        WebhookTimeoutSeconds = webhookTimeoutSeconds;
        SkillPath = skillPath ?? Path.Combine(RepoPaths.Root, "skills", "idea-manager");
    }

    /// <summary>本次测试实例使用的 SQLite 文件路径。</summary>
    public string DatabasePath { get; }

    /// <summary>本次测试实例覆盖的单次返回上限（null = 用应用默认值）。</summary>
    public int? MaxIdeasPerResponse { get; }

    /// <summary>本次测试实例覆盖的事件发布器（null = 用应用默认的事件总线）。</summary>
    public IEventPublisher? Publisher { get; }

    /// <summary>本次测试实例覆盖的 SSE 心跳间隔（秒，null = 用应用默认值）。</summary>
    public int? SseHeartbeatSeconds { get; }

    /// <summary>本次测试实例覆盖的投递尝试次数上限（null = 用应用默认值）。</summary>
    public int? WebhookMaxAttempts { get; }

    /// <summary>本次测试实例覆盖的单次请求超时秒数（null = 用应用默认值）。</summary>
    public int? WebhookTimeoutSeconds { get; }

    /// <summary>本次测试实例使用的 skill 分发副本目录。</summary>
    public string SkillPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = DatabasePath,
                ["Api:MaxIdeasPerResponse"] = MaxIdeasPerResponse?.ToString(CultureInfo.InvariantCulture),
                ["Sse:HeartbeatSeconds"] = SseHeartbeatSeconds?.ToString(CultureInfo.InvariantCulture),
                ["Webhook:MaxAttempts"] = WebhookMaxAttempts?.ToString(CultureInfo.InvariantCulture),
                ["Webhook:TimeoutSeconds"] = WebhookTimeoutSeconds?.ToString(CultureInfo.InvariantCulture),
                ["Skill:Path"] = SkillPath
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
