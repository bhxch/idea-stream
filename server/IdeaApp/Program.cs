using IdeaApp.Application;
using IdeaApp.Contracts;
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

var app = builder.Build();

// 首次启动即建库建表并写入内置收件箱。
app.Services.GetRequiredService<SqliteDatabase>().Initialize();

app.MapHealthEndpoints();
app.MapProjectEndpoints();

app.Run();

public partial class Program;
