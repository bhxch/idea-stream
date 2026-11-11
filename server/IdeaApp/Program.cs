using IdeaApp.Contracts;
using IdeaApp.Endpoints;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Native AOT 下没有反射式序列化：把源生成上下文挂进 JSON 解析链，
// 端点才能用 Results.Ok(dto) 返回已登记的 DTO，否则会 500。
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, IdeaJsonContext.Default));

builder.Services.Configure<AppOptions>(builder.Configuration);
builder.Services.AddSingleton(sp =>
    new SqliteDatabase(sp.GetRequiredService<IOptions<AppOptions>>().Value.Database.Path));

var app = builder.Build();

// 首次启动即建库建表并写入内置收件箱。
app.Services.GetRequiredService<SqliteDatabase>().Initialize();

app.MapHealthEndpoints();

app.Run();

public partial class Program;
