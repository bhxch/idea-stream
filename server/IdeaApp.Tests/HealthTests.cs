using System.Net;
using System.Text.Json;
using IdeaApp.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdeaApp.Tests;

public class HealthTests
{
    [Fact]
    public async Task Health_returns_ok()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var property = Assert.Single(root.EnumerateObject());
        Assert.Equal("status", property.Name);
        Assert.Equal("ok", property.Value.GetString());
    }

    /// <summary>
    /// 源生成上下文必须挂在应用的 JSON 解析链上，否则端点在 Native AOT 下无法序列化 DTO。
    ///
    /// 注意本测试**只**守得住「上下文已注册」这一半：端点若退回匿名对象
    /// （<c>Results.Ok(new { status = "ok" })</c>），普通测试宿主有反射兜底，
    /// 输出与静态类型 DTO 逐字节相同，这里测不出来。那一半由
    /// <c>server/scripts/aot-smoke.sh</c> 在真 AOT 产物上守
    /// （匿名类型没有 JsonTypeInfo → AOT 下 500）。
    /// </summary>
    [Fact]
    public async Task Json_context_is_registered_in_the_resolver_chain()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 触发宿主启动，确保组合根的注册已经生效。
        using var response = await client.GetAsync("/api/health");
        Assert.True(response.IsSuccessStatusCode);

        var jsonOptions = factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value;

        Assert.Contains(IdeaJsonContext.Default, jsonOptions.SerializerOptions.TypeInfoResolverChain);
    }
}
