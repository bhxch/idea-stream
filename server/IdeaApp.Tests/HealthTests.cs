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
    /// 源生成上下文必须是 JSON 解析器本身（<c>TypeInfoResolver</c>），
    /// 不能只是解析链里的一个成员：链条里那个默认反射 resolver 会让未登记的类型
    /// 在开发期照样序列化，形成「开发全绿、AOT 产物全挂」。
    /// </summary>
    [Fact]
    public async Task Json_context_is_the_only_type_info_resolver()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 触发宿主启动，确保组合根的注册已经生效。
        using var response = await client.GetAsync("/api/health");
        Assert.True(response.IsSuccessStatusCode);

        var jsonOptions = factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value;

        Assert.Same(IdeaJsonContext.Default, jsonOptions.SerializerOptions.TypeInfoResolver);
    }
}
