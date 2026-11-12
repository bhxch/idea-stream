using IdeaApp.Application;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Endpoints;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// 环境变量前缀 IDEA_：README、spec §16.2 与 skill 计划里的启动命令都用这个前缀
// （IDEA_Urls= / IDEA_Database__Path= / IDEA_Trash__RetentionDays=）。
//
// 默认的无前缀 provider 保留（Urls=、Trash__RetentionDays= 仍然生效），
// 两套并存：后注册的 provider 赢，所以带前缀的优先。
//
// 少这一行，IDEA_ 开头的环境变量会被静默忽略 —— 应用照样起得来，
// 只是连的是默认端口、默认库路径，出问题时没人查得出来。
builder.Configuration.AddEnvironmentVariables("IDEA_");

// OpenAPI 文档：/openapi/v1.json。给 agent 一份可读的接口契约，
// 提交进仓库的 openapi/idea-v1.json 就是它的快照。
//
// 用 IdeaOpenApi.Configure 固定标题与版本：不固定的话，框架拿
// 「入口程序集名 | 文档名」当标题 —— 应用本体生成的是 "IdeaApp | v1"，
// 导出工具进程里生成的是 "ExportOpenApi | v1"，同一个接口两种标题，
// 契约文件和服务实际提供的就对不上了。
//
// 注册位置在下面那段 JSON 配置**之前**，是有意的：OpenAPI 的注册也会往
// JSON 解析链里塞自己的上下文（IConfigureOptions<JsonOptions>），
// 配置项按注册顺序执行 —— 后注册的「直接赋值源生成上下文」会把那条链整个换掉，
// 保住「未登记的类型在开发期就抛 NoMetadataForType」这条 AOT 防线。
builder.Services.AddOpenApi(IdeaOpenApi.Configure);

// Native AOT 下没有反射式序列化：把源生成上下文直接设成 JSON 解析器，
// 端点才能用 Results.Ok(dto) 返回已登记的 DTO，否则会 500。
//
// 直接赋值（而不是插进 TypeInfoResolverChain）是有意的：链条里那个默认反射
// resolver 会让未登记的类型在开发期照样序列化，于是「开发全绿、AOT 产物全挂」。
// 直接赋值后，未登记的类型在开发期就抛 NoMetadataForType，测试阶段就能发现。
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolver = IdeaJsonContext.Default);

builder.Services.Configure<AppOptions>(builder.Configuration);
builder.Services.AddSingleton(sp =>
    new SqliteDatabase(sp.GetRequiredService<IOptions<AppOptions>>().Value.Database.Path));
builder.Services.AddSingleton<ProjectRepository>();
builder.Services.AddSingleton<SettingRepository>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<IdeaRepository>();
builder.Services.AddSingleton<IdeaService>();
builder.Services.AddSingleton<TagRepository>();
builder.Services.AddSingleton<TagService>();
builder.Services.AddSingleton<TrashService>();
builder.Services.AddSingleton<WebhookRepository>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<DeliveryRepository>();
builder.Services.AddSingleton<DeliveryService>();

// 投递用的 HTTP 客户端工厂：每次投递拿一个客户端，超时按 Webhook:TimeoutSeconds 设。
builder.Services.AddHttpClient();

// 回收站自动清理：到期条目唯一的出口（没有手动彻底删除）。
// 保留期只从配置读（Trash:RetentionDays），页面与 API 都不提供设置入口。
//
// Testing 环境**不注册**：集成测试启动宿主就会触发一次清理写事务，
// 与测试请求并发争同一个 SQLite 文件（SQLITE_BUSY 抖动窗口 —— flaky 的测试
// 比没有测试更糟）。要验证清理逻辑时直接调 TrashCleanupWorker.Cleanup()。
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<TrashCleanupWorker>();
}

// 事件总线：服务层只认 IEventPublisher，测试可以换成记录型的 fake。
// IEventHandler 的实现 Task 9（SSE）/ 14（webhook）才注册，
// 那时通道数不为 0；现在通道数为 0，publish 是空操作 —— 这是对的。
builder.Services.AddSingleton<IEventPublisher>(sp => new EventBus(
    sp.GetServices<IEventHandler>(),
    sp.GetRequiredService<ILogger<EventBus>>()));

// SSE 广播器：内存缓冲（断线补发用）+ 订阅列表。
// 缓冲条数与心跳间隔读配置（默认 500 条 / 25 秒）。
builder.Services.AddSingleton(sp => new SseBroadcaster(
    sp.GetRequiredService<IOptions<AppOptions>>().Value.Sse.BufferSize,
    sp.GetRequiredService<IOptions<AppOptions>>().Value.Sse.HeartbeatSeconds,
    sp.GetRequiredService<ILogger<SseBroadcaster>>()));

// SSE 订阅者：把总线上的事件推进广播器。
// 必须注册成 Singleton —— 事件总线在**根** provider 里解析所有 IEventHandler，
// 注册成 Scoped 会在开发期（ValidateScopes）抛 InvalidOperationException、
// 在生产期泄漏作用域。
builder.Services.AddSingleton<IEventHandler, SseEventHandler>();

// webhook 投递：事件 → 匹配 → 渲染 → 队列 → 后台 worker 发 HTTP。
//
// 依赖方向是单向的：EventBus → IEventHandler → WebhookEventHandler → WebhookDeliveryWorker。
// worker **不注入 EventBus** —— 投递不产生事件（注入它会构成 DI 循环）。
//
// worker 既是单例（handler 要往它的队列里投任务）又是 hosted service（后台消费队列），
// 所以先注册单例、再用工厂把同一个实例交给宿主。
builder.Services.AddSingleton<WebhookDeliveryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryWorker>());
builder.Services.AddSingleton<IEventHandler, WebhookEventHandler>();

var app = builder.Build();

// 端点共用的异常包装需要日志器：未预期的异常只进日志，不进响应体。
ApiResults.UseLogger(app.Logger);

// 首次启动即建库建表并写入内置收件箱。
app.Services.GetRequiredService<SqliteDatabase>().Initialize();

app.MapHealthEndpoints();
app.MapProjectEndpoints();
app.MapSettingsEndpoints();
app.MapIdeaEndpoints();
app.MapBulkIdeaEndpoints();
app.MapTagEndpoints();
app.MapEventEndpoints();
app.MapTrashEndpoints();
app.MapWebhookEndpoints();

// OpenAPI 文档端点：/openapi/v1.json（文档名不是 v1 时返回 404）。
app.MapOpenApi("/openapi/{documentName}.json");

// 导出 OpenAPI 文档到 IDEA_EXPORT_OPENAPI 指定的文件（没设这个环境变量就不导出）。
OpenApiExporter.ExportIfRequested(app);

app.Run();

public partial class Program;
