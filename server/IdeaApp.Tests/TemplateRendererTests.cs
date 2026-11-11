using System.Text.Json;
using System.Text.Json.Nodes;
using IdeaApp.Application;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;

namespace IdeaApp.Tests;

/// <summary>
/// 占位符渲染与 HMAC 签名的单元测试。
///
/// <para>
/// 渲染器只处理**字符串值节点**里的占位符，所以模板的形状本身也是契约的一部分：
/// 根节点必须是 JSON 对象，占位符不能写在对象的键里 —— 这两条由
/// <see cref="TemplateRenderer.Validate"/> 把关。
/// </para>
/// </summary>
public sealed class TemplateRendererTests
{
    private const string IdeaId = "0198f0c0-0000-7000-8000-000000000001";

    /// <summary>一个带项目、想法与变更字段的事件（占位符字典的输入）。</summary>
    private static IdeaEvent SampleEvent(bool done = true, string[]? changed = null) => new(
        Id: "0198f0c0-0000-7000-8000-0000000000ff",
        Type: EventTypes.IdeaUpdated,
        Timestamp: new DateTimeOffset(2026, 11, 12, 3, 4, 5, TimeSpan.Zero),
        Project: new ProjectSnapshot("0198f0c0-0000-7000-8000-0000000000aa", "项目A", 0),
        Idea: new IdeaSnapshot(
            Id: IdeaId,
            ProjectId: "0198f0c0-0000-7000-8000-0000000000aa",
            Title: "标题",
            Content: "正文",
            Tags: ["标签一", "标签二"],
            Done: done,
            CreatedAt: new DateTimeOffset(2026, 11, 12, 3, 4, 5, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 11, 12, 3, 4, 5, TimeSpan.Zero)),
        Changed: changed ?? ["title", "done"],
        MergedFrom: null);

    [Fact]
    public void Render_json_substitutes_placeholders()
    {
        var rendered = TemplateRenderer.RenderJson(
            """{"text":"[{{project}}] {{event}}"}""",
            new Dictionary<string, string?>
            {
                ["project"] = "项目A",
                ["event"] = "idea.created"
            });

        using var document = JsonDocument.Parse(rendered);

        Assert.Equal("[项目A] idea.created", document.RootElement.GetProperty("text").GetString());
    }

    /// <summary>
    /// 占位符的值按 JSON 字符串上下文转义后再注入：正文里的引号、换行、反斜杠不能破坏 JSON。
    /// </summary>
    [Fact]
    public void Render_json_escapes_special_characters_in_values()
    {
        var content = "有\"引号\"、有\n换行、也有\\反斜杠";

        var rendered = TemplateRenderer.RenderJson(
            """{"text":"{{content}}"}""",
            new Dictionary<string, string?> { ["content"] = content });

        // 输出必须是合法 JSON（能解析回来就说明合法）。
        using var document = JsonDocument.Parse(rendered);

        // 解析回来的值逐字相同。
        Assert.Equal(content, document.RootElement.GetProperty("text").GetString());

        // 换行被转义成了 \n（值里的裸换行会破坏 JSON，能解析回来就说明它被转义了）。
        Assert.Contains("\\n", rendered);
    }

    /// <summary>未识别的占位符原样保留：模板是用户写的，拼错名字不该让投递炸掉。</summary>
    [Fact]
    public void Render_json_keeps_unknown_placeholder_verbatim()
    {
        var rendered = TemplateRenderer.RenderJson(
            """{"text":"{{unknown}}"}""",
            new Dictionary<string, string?> { ["known"] = "值" });

        using var document = JsonDocument.Parse(rendered);

        Assert.Equal("{{unknown}}", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Render_json_throws_on_invalid_json_template()
    {
        Assert.Throws<ArgumentException>(() => TemplateRenderer.RenderJson(
            "not json",
            new Dictionary<string, string?>()));
    }

    /// <summary>
    /// 渲染 JSON 模板时根节点也必须是 JSON 对象 —— 与 <see cref="TemplateRenderer.Validate"/> 同一份规则，
    /// 不能出现「校验放行、渲染炸掉」的口子。
    /// </summary>
    [Fact]
    public void Render_json_rejects_non_object_root()
    {
        var values = new Dictionary<string, string?>();

        Assert.Throws<ArgumentException>(() => TemplateRenderer.RenderJson("123", values));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.RenderJson("\"x\"", values));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.RenderJson("[1,2]", values));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.RenderJson("null", values));
    }

    /// <summary>嵌套对象与数组里的字符串值也要替换。</summary>
    [Fact]
    public void Render_json_renders_nested_objects_and_arrays()
    {
        var rendered = TemplateRenderer.RenderJson(
            """{"a":{"b":"{{x}}"},"c":["{{x}}",1],"d":true,"e":null}""",
            new Dictionary<string, string?> { ["x"] = "值" });

        using var document = JsonDocument.Parse(rendered);
        var root = document.RootElement;

        Assert.Equal("值", root.GetProperty("a").GetProperty("b").GetString());
        Assert.Equal("值", root.GetProperty("c")[0].GetString());
        Assert.Equal(1, root.GetProperty("c")[1].GetInt32());
        Assert.True(root.GetProperty("d").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("e").ValueKind);
    }

    /// <summary>输出是缩进 2 格的 JSON。</summary>
    [Fact]
    public void Render_json_indents_with_two_spaces()
    {
        var rendered = TemplateRenderer.RenderJson(
            """{"a":1,"b":{"c":2}}""",
            new Dictionary<string, string?>());

        var lines = rendered.Split('\n');

        Assert.Equal("{", lines[0]);
        Assert.StartsWith("  \"a\": 1", lines[1]);
        Assert.StartsWith("  \"b\": {", lines[2]);
        Assert.StartsWith("    \"c\": 2", lines[3]);
    }

    /// <summary>
    /// 数字保留模板里的**原始字面量**：<c>1.50</c> 不被改写成 <c>1.5</c>，<c>1e5</c> 不被改写成 <c>100000</c>。
    /// </summary>
    /// <remarks>
    /// 序列化时把数字重新算一遍（走 decimal/double）就会改掉字面量，
    /// 所以输出必须直接用底层 <see cref="JsonElement"/> 的原始文本。
    /// </remarks>
    [Fact]
    public void Render_json_preserves_number_literal()
    {
        var rendered = TemplateRenderer.RenderJson(
            """{"a":1.50,"b":1e5,"c":3}""",
            new Dictionary<string, string?>());

        using var document = JsonDocument.Parse(rendered);

        Assert.Equal("1.50", document.RootElement.GetProperty("a").GetRawText());
        Assert.Equal("1e5", document.RootElement.GetProperty("b").GetRawText());
        Assert.Equal("3", document.RootElement.GetProperty("c").GetRawText());
    }

    /// <summary>
    /// 占位符**在字典里但值是 null**（事件里没有这个字段）时，渲染成空串 ——
    /// 占位符名字是认识的，只是这次事件没有对应的值；留着 <c>{{project}}</c> 原样更糟。
    /// </summary>
    [Fact]
    public void Render_json_replaces_known_placeholder_with_empty_string_when_value_is_null()
    {
        var evt = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);
        var values = EventPlaceholders.Build(evt, null);

        Assert.Null(values["project"]);

        var rendered = TemplateRenderer.RenderJson(
            """{"text":"[{{project}}]"}""",
            values);

        using var document = JsonDocument.Parse(rendered);

        Assert.Equal("[]", document.RootElement.GetProperty("text").GetString());
    }

    /// <summary>纯文本模板：替换，但不转义（转义是 JSON 那一层的事）。</summary>
    [Fact]
    public void Render_plain_substitutes_without_escaping()
    {
        var rendered = TemplateRenderer.RenderPlain(
            "正文 {{content}} 结束 {{unknown}}",
            new Dictionary<string, string?> { ["content"] = "a\"b\nc" });

        Assert.Equal("正文 a\"b\nc 结束 {{unknown}}", rendered);
    }

    /// <summary>
    /// 纯文本模板里，**键存在但值是 null** 同样渲染成空串（与 <see cref="TemplateRenderer.RenderJson"/> 一致）：
    /// 占位符的名字是认识的，只是这次事件没有对应的值。
    /// </summary>
    [Fact]
    public void Render_plain_replaces_known_placeholder_with_empty_string_when_value_is_null()
    {
        var rendered = TemplateRenderer.RenderPlain(
            "正文 [{{project}}] 结束 {{unknown}}",
            new Dictionary<string, string?> { ["project"] = null });

        Assert.Equal("正文 [] 结束 {{unknown}}", rendered);
    }

    [Fact]
    public void Build_placeholders_contains_all_documented_keys()
    {
        var values = EventPlaceholders.Build(SampleEvent(), "https://x.example");

        foreach (var key in new[]
                 {
                     "event", "timestamp", "project", "project.id",
                     "idea.id", "idea.title", "idea.content", "idea.tags", "idea.done",
                     "changed", "idea.url"
                 })
        {
            Assert.True(values.ContainsKey(key), $"占位符字典缺少键：{key}");
        }
    }

    /// <summary>合并事件的源想法 id 不进占位符清单（<c>{{mergedFrom}}</c> 不提供）。</summary>
    [Fact]
    public void Build_does_not_provide_merged_from_placeholder()
    {
        var values = EventPlaceholders.Build(SampleEvent(), "https://x.example");

        Assert.False(values.ContainsKey("mergedFrom"));
    }

    [Fact]
    public void Build_idea_url_uses_public_base_url()
    {
        var values = EventPlaceholders.Build(SampleEvent(), "https://x.example");

        Assert.Equal($"https://x.example/?idea={IdeaId}", values["idea.url"]);
    }

    /// <summary>
    /// 没配公开地址时，<c>{{idea.url}}</c> 是**空串**：半截链接（<c>/?idea=xxx</c>）
    /// 既点不开，又比没有链接更难认。
    /// </summary>
    [Fact]
    public void Build_idea_url_is_empty_when_public_base_url_is_missing()
    {
        Assert.Equal(string.Empty, EventPlaceholders.Build(SampleEvent(), null)["idea.url"]);
        Assert.Equal(string.Empty, EventPlaceholders.Build(SampleEvent(), string.Empty)["idea.url"]);
    }

    /// <summary>
    /// 事件里没有想法时，<c>{{idea.url}}</c> 同样是**空串**：没有 id 就拼不出深链，
    /// 不能拼出一个 <c>/?idea=</c> 的半截链接。
    /// </summary>
    [Fact]
    public void Build_idea_url_is_empty_when_idea_id_is_missing()
    {
        var evt = EventFactory.Create(EventTypes.IdeaCreated, project: null, idea: null);

        Assert.Equal(string.Empty, EventPlaceholders.Build(evt, "https://x.example")["idea.url"]);
    }

    [Fact]
    public void Build_idea_tags_is_comma_separated()
    {
        var values = EventPlaceholders.Build(SampleEvent(), "https://x.example");

        Assert.Equal("标签一,标签二", values["idea.tags"]);
    }

    /// <summary><c>{{idea.done}}</c> 输出的是字符串 <c>"true"</c> / <c>"false"</c>。</summary>
    [Fact]
    public void Build_idea_done_is_true_or_false_string()
    {
        Assert.Equal("true", EventPlaceholders.Build(SampleEvent(done: true), null)["idea.done"]);
        Assert.Equal("false", EventPlaceholders.Build(SampleEvent(done: false), null)["idea.done"]);
    }

    /// <summary><c>{{changed}}</c> 输出逗号分隔的字段名。</summary>
    [Fact]
    public void Build_changed_is_comma_separated()
    {
        var values = EventPlaceholders.Build(
            SampleEvent(changed: ["title", "done"]),
            null);

        Assert.Equal("title,done", values["changed"]);
    }

    /// <summary>
    /// 根节点必须是 JSON 对象：<c>123</c>、<c>"x"</c>、<c>[1,2]</c> 都要拒掉。
    /// </summary>
    [Fact]
    public void Validate_rejects_non_object_root()
    {
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("123"));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("\"x\""));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("[1,2]"));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("null"));
    }

    /// <summary>
    /// 占位符只允许出现在字符串值节点里：把占位符写在对象的键里要拒掉。
    /// </summary>
    [Fact]
    public void Validate_rejects_placeholder_in_object_key()
    {
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("""{"{{x}}":1}"""));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("""{"outer":{"{{x}}":"v"}}"""));
    }

    [Fact]
    public void Validate_accepts_object_with_placeholders_in_values()
    {
        TemplateRenderer.Validate("""{"text":"[{{project}}] {{event}}","n":1}""");
    }

    /// <summary>
    /// Telegram 预设的 Body 模板要能通过收紧后的校验：它是随产品发出去的模板，
    /// 校验一收紧，它要是过不了，页面上的「添加 Telegram webhook」就是坏的。
    /// </summary>
    [Fact]
    public void Telegram_preset_body_template_passes_validation()
    {
        TemplateRenderer.Validate(TelegramTemplate.BodyTemplate);
    }

    /// <summary>
    /// 同一个对象里出现重复的键要拒掉，而且消息是**中文**的：
    /// <see cref="JsonObject"/> 解析时不报错，要到枚举时才发现重复键，
    /// 抛出来的是内部的英文消息（"An item with the same key has already been added"）——
    /// 那条消息不能透给 API 用户。
    /// </summary>
    [Fact]
    public void Validate_rejects_duplicate_keys_with_chinese_message()
    {
        var ex = Assert.Throws<ArgumentException>(() => TemplateRenderer.Validate("""{"a":1,"a":2}"""));

        Assert.Contains("重复", ex.Message);
        Assert.DoesNotContain("same key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 渲染结果与 BCL 自己的序列化（<c>JsonNode.ToJsonString(WriteIndented)</c>）**逐字节相同**：
    /// 输出的格式（缩进、转义、数字字面量、空容器）是 BCL 的口径，不是我们自己拼的。
    /// </summary>
    /// <remarks>
    /// 参照物是「占位符换成值之后的 JSON」交给 BCL 序列化 —— 转义与格式都由 BCL 决定，
    /// 渲染器只要有一点自己拼文本的地方（缩进宽度、数字被改写、转义次数）这条就会红。
    /// </remarks>
    [Fact]
    public void Render_json_output_matches_json_node_to_json_string_byte_for_byte()
    {
        const string template =
            """{"text":"[{{project}}] {{event}}","n":1.50,"sci":1e5,"empty":{},"none":[],"nested":{"x":["{{x}}",true,null],"quote":"a\"b","chinese":"中文"}}""";

        var values = new Dictionary<string, string?>
        {
            ["project"] = "项目A",
            ["event"] = "idea.created",
            ["x"] = "值"
        };

        var rendered = TemplateRenderer.RenderJson(template, values);

        var expected = JsonNode.Parse(SubstitutePlaceholders(template, values))!
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Assert.Equal(expected, rendered);
    }

    /// <summary>
    /// 把占位符换成值，作为参照物的输入：转义用 BCL 自己的口径 ——
    /// <c>JsonSerializer.Serialize</c> 给出的是带引号的 JSON 字符串字面量，
    /// 去掉两侧的引号就是它的内容。
    /// </summary>
    private static string SubstitutePlaceholders(string template, IReadOnlyDictionary<string, string?> values)
    {
        var result = template;

        foreach (var (name, value) in values)
        {
            var literal = JsonSerializer.Serialize(value ?? string.Empty);
            result = result.Replace("{{" + name + "}}", literal[1..^1]);
        }

        return result;
    }

    [Fact]
    public void Sign_is_deterministic_and_prefixed()
    {
        var first = HmacSigner.Sign("k", "p");
        var second = HmacSigner.Sign("k", "p");

        Assert.Equal(first, second);
        Assert.StartsWith("sha256=", first);
        Assert.Equal(64, first["sha256=".Length..].Length);
    }

    /// <summary>
    /// 已知答案向量：HMAC-SHA256(key="key", data="The quick brown fox jumps over the lazy dog")。
    /// 它钉住「密钥与正文都按 UTF-8 编码、摘要取小写 hex」这套口径。
    /// </summary>
    [Fact]
    public void Sign_matches_known_hmac_sha256_vector()
    {
        Assert.Equal(
            "sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            HmacSigner.Sign("key", "The quick brown fox jumps over the lazy dog"));
    }
}
