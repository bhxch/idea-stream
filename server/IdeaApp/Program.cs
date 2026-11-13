using IdeaApp.Application;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Endpoints;
using IdeaApp.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// wwwroot 是前端构建产物的落点（web/vite.config.ts 的 build.outDir 指向这里），
// 而它被 .gitignore 排除（git 不跟踪空目录）—— 新克隆的仓库里它不存在。
//
// 光 Directory.CreateDirectory 还不够：WebRootFileProvider 是在
// WebApplication.CreateBuilder() 里就定下来的，那一刻目录不存在，
// 框架会装上一个 NullFileProvider（查不到任何文件；只打一条
// "The WebRootPath was not found" 警告，**不抛异常**）——
// 那个 provider 是进程内固定的，后面再把目录与产物补上，
// 静态文件照样 404（/ 一律 404，页面白屏）。
// 目录建出来之后自己显式指一个 PhysicalFileProvider，这条路才真的通。
//
// ⚠️ 这一行把框架可能装上的 StaticWebAssets 复合 provider 整个换掉了：
// 今天没有引用带 wwwroot 资产的包（provider 本来就是空的），所以无影响；
// 将来引入了那样的包，那些资产会 404 —— 到时改成把两个 provider 复合起来。
//
// 目录取自 WebRootPath（有人用 WebRootPath 配过别的目录时以它为准），
// 取不到再回落 "wwwroot"。WebRootPath 是相对路径时与 ContentRootPath 拼，
// 是绝对路径时 Path.Combine 直接返回它。
var webRoot = Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Environment.WebRootPath ?? "wwwroot");
Directory.CreateDirectory(webRoot);
builder.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);

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

// 反向代理头：nginx 会转发 X-Forwarded-Proto / X-Forwarded-For / X-Forwarded-Host 等
// （spec §16.3）。不接这个中间件，应用不知道自己被 HTTPS 代理着
//（Request.Scheme 永远是 http），生成的绝对链接与重定向都会指向 http。
//
// **必须显式点名要处理哪些头**：.NET 10 的 ForwardedHeadersOptions 默认值是
// ForwardedHeaders.None —— 一个头都不处理，中间件等于没挂。实测（临时探针端点）：
// 不显式点名时，带着 X-Forwarded-Proto: https 进来的请求，Request.Scheme 仍然是 http。
//
// 信任范围用默认值：只信任回环代理（KnownProxies=::1、KnownIPNetworks=127.0.0.0/8），
// nginx 与应用同机时够用；nginx 在别的机器上必须显式加 KnownProxies
// （spec §16.3 的注意事项）。
//
// 位置：Build 之后、其它中间件之前 —— 它要赶在路由与端点执行之前改写请求。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost
});

// 静态文件：把 web/ 的 Vite 构建产物发出去。
// 必须在 MapFallbackToFile 之前 —— 反了的话 wwwroot/assets/ 下的 JS/CSS 全 404，页面白屏。
app.UseStaticFiles();

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

// 未匹配的 /api/** 要按 API 契约回 404 ProblemDetails，不能落到下面的 SPA 兜底：
// 那个 catch-all 会把「路径打错」变成 200 + HTML，而这个 API 是给 agent 读的
// （契约在 openapi/idea-v1.json）—— agent 只看状态码会把「路径打错」当成成功。
//
// 用 **Map** 而不是 **MapFallback**：.NET 10 的 MapFallback 有两族公开重载
// （(string, RequestDelegate) 与 (string, Delegate)），本轮两次隔离探针
// 各测到一个，结论相反 —— 传 RequestDelegate 的那族注册出 "/ddd/{**rest}"
// （Order=int.MaxValue、pattern 被尊重），传 lambda 的那族（lambda 绑定到
// Delegate 重载）注册出的是 "{*path:nonfile}"、Order=0。
// 同一个 API 两族重载行为不一致，**不赌它**：显式写 pattern 的 Map 更可控。
//
// Map 的字面量段 "api" 让路由优先走字面量转移（胜过 catch-all 模板），
// 四条验收口径都实测过：
// /api/nonexistent → 这里（404 + ProblemDetails）；
// /api/health 等显式端点不受影响；
// /projects、/trash → 落到下面的 SPA 兜底；
// 资源级的 404（webhook 不存在……）保持原样 ——
// 那是端点自己抛的，走的是各自的错误码，不是这个兜底。
app.Map("/api/{**rest}", () => ApiResults.Run(() =>
    throw new ApiException(404, "api_not_found", "请求的接口不存在。")));

// SPA 兜底：前端用 history 模式，/projects、/trash、/webhooks、/projects/{id}/webhooks
// 都是真实路径。MapFallbackToFile("index.html") 兜住所有没匹配到端点的 GET
// （/api/** 由上面那层兜底先接走了 —— 路由的字面量段 "api" 优先）。
//
// 必须是**最后**一个映射 —— 它是 catch-all，放在它后面的 MapXxxEndpoints() 永远不会命中。
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
