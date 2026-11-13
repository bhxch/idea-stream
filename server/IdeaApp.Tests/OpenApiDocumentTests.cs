using System.Text.Json;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Tests;

/// <summary>
/// OpenAPI 契约文件的守卫。
///
/// <para>
/// <c>openapi/idea-v1.json</c> 是**提交进仓库的构建产物快照**：它给 agent 当接口契约用。
/// 改了端点、DTO 或状态码却忘了重新导出，契约文件就与实际暴露的接口对不上 ——
/// 而且不会编译报错、也不会有别的测试失败，只会让 agent 拿着旧契约调接口。
/// </para>
/// </summary>
public sealed class OpenApiDocumentTests
{
    /// <summary>
    /// 提交进仓库的契约文件必须与应用当前生成的文档一致。
    /// </summary>
    [Fact]
    public async Task Committed_document_matches_the_running_app()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 触发宿主启动：文档要等端点进 ApiExplorer 之后才有内容。
        using var response = await client.GetAsync("/api/health");
        Assert.True(response.IsSuccessStatusCode);

        var current = await OpenApiExporter.GenerateJsonAsync(factory.Services);
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());

        using var currentDocument = JsonDocument.Parse(current);
        using var committedDocument = JsonDocument.Parse(committed);

        // 整份文档做 JSON **语义**比较（忽略空白与对象键的顺序）：
        // 逐段比原始文本太脆 —— 换个框架版本就可能在某个数组里换顺序，
        // 那种改动不该让测试红。servers 段不在文档里（生成器清掉了它），
        // 所以整份比较不会踩到「运行期地址」的问题。
        AssertJsonEqual(currentDocument.RootElement, committedDocument.RootElement, "$");
    }

    /// <summary>
    /// 契约文件不能是空壳：路径与 schema 都要有内容。
    /// </summary>
    [Fact]
    public async Task Committed_document_is_not_an_empty_shell()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.EnumerateObject().Any(), "paths 一个都没有");

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.EnumerateObject().Any(), "components.schemas 一个都没有");
    }

    /// <summary>
    /// 契约里必须写得出错误响应。
    /// </summary>
    /// <remarks>
    /// 这份文件唯一的用途是给 agent 用。契约里只有成功路径的话，agent 遇到
    /// 400 / 404 既不知道错误长什么样（RFC 9457 的 ProblemDetails + 扩展字段
    /// <c>code</c>），也不知道什么时候会拿到 —— 只能靠试错。
    /// <para>
    /// 只断言「至少有一个 400 与一个 404」：具体每个端点该有哪些错误码由
    /// <c>ApiErrorCodeTests</c> 把关，这里守的是「错误响应整体没被漏掉」。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Committed_document_declares_error_responses()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var statuses = DeclaredStatusCodes(document.RootElement);

        Assert.Contains("400", statuses);
        Assert.Contains("404", statuses);
    }

    /// <summary>
    /// ProblemDetails 的 schema 必须写清楚扩展字段 <c>code</c> 的存在与取值。
    /// </summary>
    /// <remarks>
    /// <c>Extensions</c> 不是 ProblemDetails 的类型化属性，schema 里不会自动出现它 ——
    /// 不显式写，agent 就不知道错误响应里有个可分支处理的 <c>code</c> 字段。
    /// </remarks>
    [Fact]
    public async Task ProblemDetails_schema_documents_the_code_extension()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProblemDetails");

        var description = schema.GetProperty("description").GetString() ?? string.Empty;

        Assert.Contains("code", description, StringComparison.Ordinal);
        Assert.Contains("idea_not_found", description, StringComparison.Ordinal);
        Assert.Contains("project_not_found", description, StringComparison.Ordinal);

        // 扩展字段：要么写成 additionalProperties，要么至少在描述里点名。
        var documented = schema.TryGetProperty("additionalProperties", out _)
            || description.Contains("扩展字段", StringComparison.Ordinal);

        Assert.True(documented, "ProblemDetails 的 schema 既没有 additionalProperties，也没在描述里说明扩展字段");
    }

    /// <summary>文档里出现过的全部响应状态码。</summary>
    private static HashSet<string> DeclaredStatusCodes(JsonElement root)
    {
        var statuses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                // 路径项里除了各方法，还有路径级的 parameters（数组）——跳过。
                if (operation.Value.ValueKind != JsonValueKind.Object
                    || !operation.Value.TryGetProperty("responses", out var responses))
                {
                    continue;
                }

                foreach (var response in responses.EnumerateObject())
                {
                    statuses.Add(response.Name);
                }
            }
        }

        return statuses;
    }

    /// <summary>
    /// 契约里必须写得出事件类型：SSE 的 data 帧与 webhook 的投递体都是它。
    ///
    /// <para>
    /// 事件是**前端 SSE 客户端与任何 agent 消费者之间的公共契约**。
    /// 契约文件里没有它，消费方只能对着服务端源码写类型，写错了没有第二处能发现。
    /// </para>
    ///
    /// <para>
    /// 这里守三件事：
    /// <list type="number">
    /// <item>三个 schema（事件与两个快照）在 <c>components.schemas</c> 里；</item>
    /// <item><c>changed</c> / <c>mergedFrom</c> 是**必填可空**（后端恒定写出这两个键，
    /// 值可能是 null）—— 改成「可选」就说明消费方可能整个键都读不到；</item>
    /// <item>schema 里的字段名与**实际报文**一致（拿真事件序列化一遍来比），
    /// 免得手写的 schema 与后端记录悄悄对不上。</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task Committed_document_declares_event_schemas()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[] { "IdeaEvent", "IdeaSnapshot", "ProjectSnapshot" })
        {
            Assert.True(schemas.TryGetProperty(name, out _), $"components.schemas 里没有 {name}");
        }

        var ideaEvent = schemas.GetProperty("IdeaEvent");
        var eventProperties = ideaEvent.GetProperty("properties");
        var eventRequired = Required(ideaEvent);

        Assert.Contains("changed", eventRequired);
        Assert.Contains("mergedFrom", eventRequired);

        // 必填可空 ≠ 可选：键恒在，值可能是 null。
        Assert.True(AllowsNull(eventProperties.GetProperty("changed")), "changed 必须可空（值可能是 null）");
        Assert.True(AllowsNull(eventProperties.GetProperty("mergedFrom")), "mergedFrom 必须可空（值可能是 null）");

        // title 同理：string | null，不是「可选」。
        var snapshot = schemas.GetProperty("IdeaSnapshot");
        var snapshotProperties = snapshot.GetProperty("properties");

        Assert.Contains("title", Required(snapshot));
        Assert.True(AllowsNull(snapshotProperties.GetProperty("title")), "title 必须可空（string | null）");

        // 字段名必须与实际报文一致：拿一个「什么都是 null」的真事件序列化一遍。
        var evt = new IdeaEvent(
            Id: "0198f0a1b2c3d4e5f60718293a4b5c6d",
            Type: "idea.created",
            Timestamp: new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero),
            Project: null,
            Idea: null,
            Changed: null,
            MergedFrom: null);

        var payload = JsonDocument.Parse(JsonSerializer.Serialize(evt, IdeaJsonContext.Default.IdeaEvent));

        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("changed").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("mergedFrom").ValueKind);

        Assert.Equal(
            SortedNames(eventProperties),
            SortedNames(payload.RootElement));

        // SSE 端点的响应描述要指向 IdeaEvent：消费方看响应就够知道帧的形状。
        var description = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/events")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("description")
            .GetString() ?? string.Empty;

        Assert.Contains("IdeaEvent", description, StringComparison.Ordinal);
        Assert.Contains("text/event-stream", description, StringComparison.Ordinal);
    }

    /// <summary>schema 的 required 清单。</summary>
    private static HashSet<string> Required(JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        if (!schema.TryGetProperty("required", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return required;
        }

        foreach (var item in list.EnumerateArray())
        {
            var name = item.GetString();
            if (name is not null)
            {
                required.Add(name);
            }
        }

        return required;
    }

    /// <summary>
    /// schema 是否允许 null：<c>type</c> 里带 null，或 <c>oneOf</c>/<c>anyOf</c> 里有 null 分支。
    /// </summary>
    private static bool AllowsNull(JsonElement schema)
    {
        if (SchemaTypes(schema).Contains("null"))
        {
            return true;
        }

        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (!schema.TryGetProperty(keyword, out var branches) || branches.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var branch in branches.EnumerateArray())
            {
                if (SchemaTypes(branch).Contains("null"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>schema 的 type 取值（type 可能是字符串，也可能是数组）。</summary>
    private static HashSet<string> SchemaTypes(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);

        if (!schema.TryGetProperty("type", out var type))
        {
            return types;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            var name = type.GetString();
            if (name is not null)
            {
                types.Add(name);
            }

            return types;
        }

        if (type.ValueKind != JsonValueKind.Array)
        {
            return types;
        }

        foreach (var item in type.EnumerateArray())
        {
            var name = item.GetString();
            if (name is not null)
            {
                types.Add(name);
            }
        }

        return types;
    }

    /// <summary>对象里的字段名（排好序，便于比较）。</summary>
    private static string[] SortedNames(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>JSON 语义比较：忽略空白与对象键的顺序，数组按顺序比。</summary>
    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            Assert.Fail($"{path}: 类型不同（期望 {expected.ValueKind}，实际 {actual.ValueKind}）");
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedKeys = expected.EnumerateObject().Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
                var actualKeys = actual.EnumerateObject().Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

                Assert.True(expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
                    $"{path}: 字段不同。期望 [{string.Join(", ", expectedKeys)}]，实际 [{string.Join(", ", actualKeys)}]");

                foreach (var property in expected.EnumerateObject())
                {
                    AssertJsonEqual(property.Value, actual.GetProperty(property.Name), $"{path}.{property.Name}");
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();

                Assert.True(expectedItems.Count == actualItems.Count,
                    $"{path}: 数组长度不同（期望 {expectedItems.Count}，实际 {actualItems.Count}）");

                for (var index = 0; index < expectedItems.Count; index++)
                {
                    AssertJsonEqual(expectedItems[index], actualItems[index], $"{path}[{index}]");
                }

                break;
            }

            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    /// <summary>
    /// skill 目录里那份契约副本必须与仓库根下的契约文件**逐字相同**。
    /// </summary>
    /// <remarks>
    /// <c>skills/idea-manager/references/openapi.json</c> 是手工 <c>cp</c> 过去的副本：
    /// agent 读的是 skill 目录里那份，而契约文件在仓库根下。
    /// 两份漂移了（改了契约忘了同步副本）不会有任何编译错误，
    /// 也不会有别的测试失败 —— agent 却拿着旧契约调接口。
    /// 上一轮修复波就因此手工同步过一次，所以这里加一条守卫。
    /// </remarks>
    [Fact]
    public async Task The_skill_copy_of_the_contract_is_byte_for_byte_the_same_file()
    {
        var contract = await File.ReadAllBytesAsync(CommittedDocumentPath());
        var copy = await File.ReadAllBytesAsync(SkillContractPath());

        Assert.True(
            contract.AsSpan().SequenceEqual(copy),
            "skill 里的契约副本与 openapi/idea-v1.json 不是同一份："
            + $"前者 {copy.Length} 字节，后者 {contract.Length} 字节。"
            + "重新 cp 一份：cp openapi/idea-v1.json skills/idea-manager/references/openapi.json");
    }

    /// <summary>
    /// 带请求体的端点必须声明 415，不带请求体的端点不许声明 415。
    /// </summary>
    /// <remarks>
    /// 契约是给 agent 当接口手册用的：Content-Type 不是 application/json 时，
    /// 应用回 415 + ProblemDetails + <c>code=unsupported_media_type</c>，
    /// 契约里就必须写得出这个 415 —— 否则 agent 按契约预期不到它。
    /// <para>
    /// 反过来也守着：没有请求体的端点不会因为 Content-Type 被判 415，
    /// 契约里给它们也声明一个 415 就是骗人。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Only_endpoints_with_a_request_body_declare_415()
    {
        var committed = await File.ReadAllTextAsync(CommittedDocumentPath());
        using var document = JsonDocument.Parse(committed);

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                // 路径项里除了各方法，还有路径级的 parameters（数组）——跳过。
                if (operation.Value.ValueKind != JsonValueKind.Object
                    || !operation.Value.TryGetProperty("responses", out var responses))
                {
                    continue;
                }

                var hasBody = operation.Value.TryGetProperty("requestBody", out var requestBody)
                    && requestBody.ValueKind == JsonValueKind.Object
                    && requestBody.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Object
                    && content.EnumerateObject().Any();

                var declares415 = responses.TryGetProperty("415", out _);

                var label = $"{operation.Name.ToUpperInvariant()} {path.Name}";

                if (hasBody)
                {
                    Assert.True(declares415, $"{label}：带请求体却没有声明 415");
                }
                else
                {
                    Assert.False(declares415, $"{label}：没有请求体却声明了 415");
                }
            }
        }
    }

    /// <summary>skill 目录里那份契约副本。</summary>
    private static string SkillContractPath()
    {
        var path = RepoPaths.SkillContract;

        Assert.True(File.Exists(path), $"找不到契约副本：{path}");

        return path;
    }

    /// <summary>
    /// 仓库根下的 openapi/idea-v1.json。
    /// </summary>
    /// <remarks>
    /// 先按仓库根（认 <c>IdeaApp.sln</c>）定位，再拼出契约文件的路径 ——
    /// 逐级上溯找 <c>openapi/idea-v1.json</c> 会命中别处的同名目录
    /// （比如某个依赖包里恰好也有 openapi/idea-v1.json），那就找错文件了。
    /// </remarks>
    private static string CommittedDocumentPath()
    {
        var path = RepoPaths.Contract;

        Assert.True(File.Exists(path), $"找不到契约文件：{path}");

        return path;
    }
}
