using System.Text;
using IdeaApp.Domain;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 把事件类型的 schema 写进 OpenAPI 契约，并说明 SSE 端点返回的帧格式。
///
/// <para>
/// 事件（<see cref="IdeaEvent"/>）是 **SSE 的报文**，也是 webhook 的投递体 ——
/// 它是前端 SSE 客户端与任何 agent 消费者之间的**公共契约**。契约文件里没有它，
/// 消费方只能对着服务端源码猜字段名，猜错了没有第二处能发现。
/// </para>
///
/// <para>
/// 框架不会自动生成这三个 schema：框架只为**端点返回类型**生成 schema，
/// 而 SSE 端点返回的是文本流（<c>text/event-stream</c>），
/// 事件类型不出现在任何端点的返回类型里。所以这里手写。
/// </para>
///
/// <para>
/// 手写就得盯住两件事：
/// <list type="bullet">
/// <item>字段名是 camelCase —— 与 <c>IdeaJsonContext</c> 的命名策略一致，
/// 帧里的 JSON 才和 schema 对得上；</item>
/// <item>「必填可空」与「可选」是两回事：<c>changed</c> / <c>mergedFrom</c>
/// **恒定写出来**（值可能是 null），所以它们在 required 里、类型是
/// <c>["null","array"]</c>；<c>title</c> 同理（<c>string | null</c>，不是可选）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class OpenApiEventTransformer : IOpenApiDocumentTransformer
{
    /// <summary>事件 schema 在 components.schemas 里的名字。</summary>
    public const string IdeaEventSchemaName = "IdeaEvent";

    /// <summary>想法快照 schema 在 components.schemas 里的名字。</summary>
    public const string IdeaSnapshotSchemaName = "IdeaSnapshot";

    /// <summary>项目快照 schema 在 components.schemas 里的名字。</summary>
    public const string ProjectSnapshotSchemaName = "ProjectSnapshot";

    /// <summary>SSE 的内容类型。</summary>
    private const string EventStreamContentType = "text/event-stream";

    /// <summary>SSE 端点的路径。</summary>
    private const string EventsPath = "/api/events";

    /// <summary>续发用的查询参数名（与端点的处理函数参数同名）。</summary>
    private const string LastEventIdParameterName = "lastEventId";

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var components = document.Components;
        if (components is null)
        {
            return Task.CompletedTask;
        }

        var schemas = components.Schemas;
        if (schemas is null)
        {
            schemas = new Dictionary<string, IOpenApiSchema>();
            components.Schemas = schemas;
        }

        schemas[IdeaSnapshotSchemaName] = BuildIdeaSnapshotSchema();
        schemas[ProjectSnapshotSchemaName] = BuildProjectSnapshotSchema();
        schemas[IdeaEventSchemaName] = BuildIdeaEventSchema(document);

        DescribeEventStream(document);
        DescribeLastEventIdParameter(document);

        return Task.CompletedTask;
    }

    /// <summary>事件本身的 schema。</summary>
    private static OpenApiSchema BuildIdeaEventSchema(OpenApiDocument document)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description =
                "一条领域事件。SSE 端点（GET /api/events）每一帧的 data 行就是本 schema 的单行 JSON，" +
                "webhook 的投递体也是它。type 的取值共 " + EventTypes.All.Length + " 种：" +
                string.Join("、", EventTypes.All) + "。",
            Required = new HashSet<string>(StringComparer.Ordinal)
            {
                "id", "type", "timestamp", "project", "idea", "changed", "mergedFrom"
            },
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                ["id"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description =
                        "事件 id：单调 UUIDv7（32 位无连字符的小写 hex）。SSE 补发时客户端拿它当 " +
                        "Last-Event-ID，「漏了哪些事件」按字典序比较就能算出来。"
                },
                ["type"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Description =
                        "事件类型：" + string.Join("、", EventTypes.All) +
                        "。订阅端按这个清单筛选，不要假设还有别的取值。"
                },
                ["timestamp"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "date-time",
                    Description = "事件发生时间（UTC，ISO-8601）。"
                },
                ["project"] = NullableReference(
                    document,
                    ProjectSnapshotSchemaName,
                    "项目快照。project.* 事件带它，其它事件为 null。"),
                ["idea"] = NullableReference(
                    document,
                    IdeaSnapshotSchemaName,
                    "想法快照。idea.* 事件带它，其它事件为 null。"),
                ["changed"] = NullableStringArray(
                    "这次操作写过的字段名。三态：null = 该事件类型没有「改了什么」的概念" +
                    "（订阅端按「整条对象是新数据」处理）；空数组 = 写了但值没变" +
                    "（数据本身仍然是最新的，别把这次更新跳过）；非空数组 = 这次写过的字段名" +
                    "（如 title、done、tags、name、sortOrder、deletedAt）。" +
                    "注意：SetTags / SetDone 与项目改名一律列该字段，不跟旧值比对。"),
                ["mergedFrom"] = NullableStringArray(
                    "被合并掉的源想法 id 列表。只有 idea.merged 事件有值，其它事件为 null。")
            }
        };
    }

    /// <summary>想法快照的 schema。</summary>
    private static OpenApiSchema BuildIdeaSnapshotSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description =
                "想法的对外快照：去掉删除时间、标签转成字符串数组。" +
                "idea.deleted 事件带的是**删除前**的快照（deletedAt 不在快照里，回收站里的条目另有接口）。",
            Required = new HashSet<string>(StringComparer.Ordinal)
            {
                "id", "projectId", "title", "content", "tags", "done", "createdAt", "updatedAt"
            },
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                ["id"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "想法 id。" },
                ["projectId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "所属项目 id。" },
                ["title"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String | JsonSchemaType.Null,
                    Description = "标题。可以是 null（未命名想法）——键一定在，值可能是 null。"
                },
                ["content"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "正文（非空）。" },
                ["tags"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                    Description = "标签名数组（已去重）。"
                },
                ["done"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "是否已完成。" },
                ["createdAt"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "date-time",
                    Description = "创建时间（UTC，ISO-8601）。"
                },
                ["updatedAt"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "date-time",
                    Description = "最后更新时间（UTC，ISO-8601）。"
                }
            }
        };
    }

    /// <summary>项目快照的 schema。</summary>
    private static OpenApiSchema BuildProjectSnapshotSchema()
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description =
                "项目的对外快照：id、名字与排序位置。sortOrder 必带：" +
                "project.updated 的 changed 里含 sortOrder 时，订阅端要拿到**新值**才能更新本地顺序，" +
                "缺了它就只能整表重拉。",
            Required = new HashSet<string>(StringComparer.Ordinal) { "id", "name", "sortOrder" },
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                ["id"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "项目 id。" },
                ["name"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "项目名称。" },
                ["sortOrder"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer | JsonSchemaType.String,
                    Format = "int32",
                    Pattern = "^-?(?:0|[1-9]\\d*)$",
                    Description = "排序位置（越小越靠前）。"
                }
            }
        };
    }

    /// <summary>可空引用：<c>{"oneOf":[{"type":"null"},{"$ref":...}]}</c>。</summary>
    private static OpenApiSchema NullableReference(
        OpenApiDocument document,
        string schemaName,
        string description)
    {
        return new OpenApiSchema
        {
            Description = description,
            OneOf = new List<IOpenApiSchema>
            {
                new OpenApiSchema { Type = JsonSchemaType.Null },
                new OpenApiSchemaReference(schemaName, document, externalResource: null)
            }
        };
    }

    /// <summary>可空字符串数组：<c>{"type":["null","array"],"items":{"type":"string"}}</c>。</summary>
    private static OpenApiSchema NullableStringArray(string description)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Null | JsonSchemaType.Array,
            Items = new OpenApiSchema { Type = JsonSchemaType.String },
            Description = description
        };
    }

    /// <summary>
    /// 给 SSE 端点的成功响应写描述：帧格式与事件形状指向
    /// <c>components.schemas.IdeaEvent</c>。
    /// </summary>
    private static void DescribeEventStream(OpenApiDocument document)
    {
        var paths = document.Paths;
        if (paths is null)
        {
            return;
        }

        foreach (var pathItem in paths.Values)
        {
            if (pathItem?.Operations is null)
            {
                continue;
            }

            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation?.Responses is null)
                {
                    continue;
                }

                foreach (var response in operation.Responses.Values)
                {
                    // 只认事件流那个响应：错误响应是 application/problem+json。
                    if (response?.Content is null
                        || !response.Content.ContainsKey(EventStreamContentType))
                    {
                        continue;
                    }

                    response.Description = EventStreamDescription();
                }
            }
        }
    }

    /// <summary>SSE 端点成功响应的描述文本。</summary>
    private static string EventStreamDescription()
    {
        var builder = new StringBuilder();

        builder.Append("服务器推送的 SSE 事件流（Content-Type: text/event-stream）。");
        builder.Append("每一帧由三行加一个空行组成：id: <事件 id>、event: <事件类型>、data: <单行 JSON>，");
        builder.Append("随后一个空行结束该帧。");
        builder.Append($"data 是 components.schemas.{IdeaEventSchemaName} 的单行 JSON（字段含义见该 schema）。");
        builder.Append("空闲时服务端每隔心跳间隔发一行 `: ping` 注释帧，客户端应忽略。");
        builder.Append("客户端漏了事件时可以带 Last-Event-ID 请求头重连（也可以用 ?lastEventId=<id> 查询参数，");
        builder.Append("服务端以请求头优先、查询参数兜底），服务端从该 id 之后补发；");
        builder.Append("补不了时改发一帧 event: resync（data 是空对象 {}，没有 id 行），");
        builder.Append("客户端收到后应重新拉取完整列表。");
        builder.Append($"事件类型共 {EventTypes.All.Length} 种：{string.Join("、", EventTypes.All)}。");

        return builder.ToString();
    }

    /// <summary>
    /// 给 SSE 端点的 <c>lastEventId</c> 查询参数写描述。
    /// </summary>
    /// <remarks>
    /// 框架只会把这个参数写成一个光秃秃的 <c>{"name":"lastEventId","in":"query"}</c>，
    /// 看不出它是干什么的 —— 而它恰恰是这个端点**唯一**的入参，
    /// 不知道它的人只能去读服务端源码。契约文件是给 agent 当接口手册用的，
    /// 这种地方不能留白。
    /// </remarks>
    private static void DescribeLastEventIdParameter(OpenApiDocument document)
    {
        var paths = document.Paths;

        if (paths is null
            || !paths.TryGetValue(EventsPath, out var pathItem)
            || pathItem?.Operations is null)
        {
            return;
        }

        foreach (var operation in pathItem.Operations.Values)
        {
            if (operation?.Parameters is null)
            {
                continue;
            }

            foreach (var parameter in operation.Parameters)
            {
                if (parameter.Name != LastEventIdParameterName
                    || parameter.In != ParameterLocation.Query
                    || parameter is not OpenApiParameter concrete)
                {
                    continue;
                }

                concrete.Description = LastEventIdParameterDescription();
            }
        }
    }

    /// <summary><c>lastEventId</c> 查询参数的描述文本。</summary>
    private static string LastEventIdParameterDescription()
    {
        var builder = new StringBuilder();

        builder.Append("断线续发的位置：客户端最后收到的事件 id（帧里 id: 行的那一串）。");
        builder.Append("服务端从该 id 之后补发漏掉的事件；补不了时改发一帧 event: resync。");
        builder.Append("与 Last-Event-ID 请求头等价，两者都带时以请求头为准。");
        builder.Append("浏览器的原生 EventSource 不能自定义请求头，重建连接时只能用它 —— 所以它不能省。");

        return builder.ToString();
    }
}
