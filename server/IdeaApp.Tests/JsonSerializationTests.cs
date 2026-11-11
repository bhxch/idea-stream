using System.Globalization;
using System.Text.Json;
using IdeaApp.Contracts;

namespace IdeaApp.Tests;

/// <summary>
/// JSON 序列化契约测试：字段名、camelCase、时间戳格式，
/// 以及源生成上下文对「端点可能返回的类型」的登记完整性。
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

        // JSON 侧走 System.Text.Json 的默认 ISO-8601 格式（偏移量 +00:00），
        // 末尾为零的小数位会被裁掉，因此整秒值输出为 "2026-11-11T09:00:00+00:00"。
        // 7 位小数的 UTC 形式（yyyy-MM-ddTHH:mm:ss.fffffffZ）只用于**存库**，
        // 不适用于 JSON 输出 —— 断言 JSON 里出现 7 位小数会永远失败。
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

    /// <summary>
    /// 白名单完整性：端点返回的**静态类型**必须在 IdeaJsonContext 里有条目。
    /// 少登记一个，对应端点在 Native AOT 产物下就是 500（没有该类型的 JsonTypeInfo 元数据），
    /// 而普通测试宿主有反射兜底、照样全绿 —— 只有真 AOT 产物才暴露。
    /// 这里直接问源生成上下文要元数据，缺一个就在这里红，不用等 AOT 产物。
    /// </summary>
    [Fact]
    public void Json_context_registers_every_type_endpoints_may_return()
    {
        // 只挂源生成上下文、不挂默认反射 resolver：
        // 未登记的类型在这里就会拿不到元数据（GetTypeInfo 抛 NotSupportedException）。
        var options = new JsonSerializerOptions { TypeInfoResolver = IdeaJsonContext.Default };

        Type[] registered =
        [
            // 单个实体
            typeof(IdeaDto),
            typeof(ProjectDto),
            typeof(TagDto),
            typeof(WebhookDto),
            typeof(DeliveryDto),
            typeof(TrashEntryDto),
            typeof(HealthResponse),
            // 数组包装
            typeof(IdeaDto[]),
            typeof(ProjectDto[]),
            typeof(TagDto[]),
            typeof(WebhookDto[]),
            typeof(DeliveryDto[]),
            typeof(TrashEntryDto[]),
            // List 包装
            typeof(List<IdeaDto>),
            typeof(List<ProjectDto>),
            typeof(List<TagDto>),
            typeof(List<WebhookDto>),
            typeof(List<DeliveryDto>),
            typeof(List<TrashEntryDto>),
            typeof(List<string>),
            // IReadOnlyList 包装
            typeof(IReadOnlyList<IdeaDto>),
            typeof(IReadOnlyList<ProjectDto>),
            typeof(IReadOnlyList<TagDto>),
            typeof(IReadOnlyList<WebhookDto>),
            typeof(IReadOnlyList<DeliveryDto>),
            typeof(IReadOnlyList<TrashEntryDto>),
            // 其他
            typeof(Dictionary<string, string>),
            typeof(Microsoft.AspNetCore.Mvc.ProblemDetails),
            typeof(Microsoft.AspNetCore.Http.HttpValidationProblemDetails),
            // 请求体记录（Native AOT 下反序列化同样需要元数据）
            typeof(CreateProjectRequest),
            typeof(RenameProjectRequest),
            typeof(ReorderProjectsRequest),
            typeof(CreateIdeaRequest),
            typeof(UpdateIdeaRequest),
            typeof(SetTagsRequest),
            typeof(SetDoneRequest)
        ];

        foreach (var type in registered)
        {
            var typeInfo = options.GetTypeInfo(type);

            Assert.NotNull(typeInfo);
        }
    }
}
