using System.Globalization;
using System.Net;
using System.Text.Json;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdeaApp.Tests;

/// <summary>
/// JSON 序列化契约测试：字段名、camelCase、时间戳格式与源生成上下文的注册情况。
/// </summary>
public sealed class JsonSerializationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IdeaDto_serializes_with_camel_case_and_iso_timestamps()
    {
        var dto = new IdeaDto(
            Id: "0198f0a1b2c3d4e5f60718293a4b5c6d",
            ProjectId: "inbox",
            Title: "标题",
            Content: "正文",
            Tags: ["标签"],
            Done: false,
            CreatedAt: Timestamp,
            UpdatedAt: Timestamp);

        var json = JsonSerializer.Serialize(dto, IdeaJsonContext.Default.IdeaDto);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("inbox", root.GetProperty("projectId").GetString());

        // DateTimeOffset 的默认序列化格式是 ISO-8601 扩展格式（偏移量 +00:00），
        // 末尾为零的小数位会被裁掉，因此整秒值输出为 "2026-11-11T09:00:00+00:00"。
        Assert.Equal("2026-11-11T09:00:00+00:00", root.GetProperty("createdAt").GetString());
        Assert.Equal("2026-11-11T09:00:00+00:00", root.GetProperty("updatedAt").GetString());
        Assert.Equal(Timestamp, DateTimeOffset.Parse(
            root.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IdeaDto_tags_is_empty_array_not_null()
    {
        var dto = new IdeaDto(
            Id: "0198f0a1b2c3d4e5f60718293a4b5c6d",
            ProjectId: "inbox",
            Title: null,
            Content: "正文",
            Tags: [],
            Done: false,
            CreatedAt: Timestamp,
            UpdatedAt: Timestamp);

        var json = JsonSerializer.Serialize(dto, IdeaJsonContext.Default.IdeaDto);

        using var document = JsonDocument.Parse(json);
        var tags = document.RootElement.GetProperty("tags");

        Assert.Equal(JsonValueKind.Array, tags.ValueKind);
        Assert.Empty(tags.EnumerateArray());
    }

    [Fact]
    public void WebhookDto_omits_secret_value()
    {
        var dto = new WebhookDto(
            Id: "0198f0a1b2c3d4e5f60718293a4b5c6d",
            Name: "钩子",
            Url: "https://example.com/hook",
            Method: "POST",
            Headers: new Dictionary<string, string> { ["X-Token"] = "value" },
            BodyTemplate: "{{title}}",
            Events: ["idea.created"],
            Scope: "global",
            ProjectId: null,
            Enabled: true,
            HasSecret: true,
            CreatedAt: Timestamp,
            UpdatedAt: Timestamp);

        var json = JsonSerializer.Serialize(dto, IdeaJsonContext.Default.WebhookDto);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Contains("hasSecret", names);
        Assert.DoesNotContain("secret", names, StringComparer.OrdinalIgnoreCase);
        Assert.True(root.GetProperty("hasSecret").GetBoolean());
    }

    [Fact]
    public async Task Health_endpoint_returns_200_not_500_under_aot()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var property = Assert.Single(root.EnumerateObject());
        Assert.Equal("status", property.Name);
        Assert.Equal("ok", property.Value.GetString());

        // 端点一旦退回匿名对象或漏掉源生成上下文的注册，AOT 产物下会变成 500。
        // 非 AOT 的测试宿主察觉不到序列化器缺失，因此直接断言 resolver chain 里有 IdeaJsonContext。
        var jsonOptions = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value;
        Assert.Contains(IdeaJsonContext.Default, jsonOptions.SerializerOptions.TypeInfoResolverChain);
    }
}
