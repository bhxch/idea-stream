using IdeaApp.Application;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Endpoints;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<IdeaRepository>();
builder.Services.AddSingleton<IdeaService>();
builder.Services.AddSingleton<TagRepository>();
builder.Services.AddSingleton<TagService>();

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

var app = builder.Build();

// 端点共用的异常包装需要日志器：未预期的异常只进日志，不进响应体。
ApiResults.UseLogger(app.Logger);

// 首次启动即建库建表并写入内置收件箱。
app.Services.GetRequiredService<SqliteDatabase>().Initialize();

app.MapHealthEndpoints();
app.MapProjectEndpoints();
app.MapIdeaEndpoints();
app.MapBulkIdeaEndpoints();
app.MapTagEndpoints();
app.MapEventEndpoints();

app.Run();

public partial class Program;
