using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 模板渲染器：把 <c>{{...}}</c> 占位符替换成事件数据。
///
/// <para>
/// 占位符语法是 <c>{{名字}}</c>，名字允许字母、数字、下划线与点（<c>idea.content</c>）。
/// 未识别的占位符**原样保留** —— 模板是用户手写的，名字拼错不该让投递炸掉。
/// </para>
///
/// <para>
/// **已下线的占位符**（见 <see cref="RetiredPlaceholders"/>）渲染成空串 ——
/// 老模板不该在正文里露出占位符的字面量。
/// </para>
///
/// <para>
/// JSON 模板的渲染只处理**字符串值节点**：占位符的值按 JSON 字符串上下文转义后再注入，
/// 所以正文里的引号、换行、反斜杠不会破坏 JSON。数字、布尔、null 之类的节点原样保留。
/// </para>
///
/// <para>
/// JSON 的文本输出交给 <see cref="Utf8JsonWriter"/>：模板解析成 <see cref="JsonNode"/> 后递归遍历，
/// 写出来的结果与 <c>JsonNode.ToJsonString(WriteIndented)</c> **逐字节相同**
/// （缩进 2 格、数字保留原始字面量、字符串按 JSON 上下文转义）。手写 JSON 文本是最容易出细微错的地方
/// （转义、缩进、数字字面量、空容器），不值得重造一遍 BCL。
/// </para>
///
/// <para>
/// 模板的形状也是契约的一部分：根节点必须是 JSON 对象，占位符不能出现在对象的键里 ——
/// 渲染器不会处理键里的占位符，留到投递时才暴露这件事太晚了。
/// </para>
/// </summary>
public static class TemplateRenderer
{
    /// <summary>占位符的正则：名字允许字母、数字、下划线与点。</summary>
    private static readonly Regex PlaceholderPattern =
        new(@"\{\{([a-zA-Z0-9_.]+)\}\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// 已经下线、但老模板里可能还留着的占位符。
    ///
    /// <para>
    /// 能力去掉之后，这些占位符不再出现在 <see cref="EventPlaceholders"/> 的字典里 ——
    /// 但渲染器**不能**把它们当「未识别的占位符」原样保留：老模板的正文里会冒出
    /// 「{{idea.title}}」这样的字面量。规格 §4.3 D 要的是**渲染成空串**。
    /// </para>
    ///
    /// <para>
    /// 为什么单开一张表：渲染器原本的语义是「键在、值为 null → 空串；键不在 →
    /// 原样保留」。占位符下线时键会消失，于是落进「原样保留」那一档 ——
    /// 这张表把「已下线」与「拼错了」区分开。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> RetiredPlaceholders =
        new(StringComparer.Ordinal) { "idea.title" };

    /// <summary>
    /// 渲染 JSON 模板：解析模板，把字符串值节点里的占位符替换掉，输出缩进 2 格的 JSON。
    /// </summary>
    /// <param name="jsonTemplate">JSON 模板（根节点必须是 JSON 对象）。</param>
    /// <param name="values">
    /// 占位符的值。值是 <c>null</c> 表示「这个字段这次没有值」，渲染成空串；
    /// 字典里**没有**这个键才是「未识别的占位符」，原样保留
    /// （已下线的除外，见 <see cref="RetiredPlaceholders"/>）。
    /// </param>
    /// <returns>缩进 2 格的 JSON（与 <c>JsonNode.ToJsonString(WriteIndented)</c> 逐字节相同）。</returns>
    /// <exception cref="ArgumentException">
    /// 模板不是合法 JSON、根节点不是 JSON 对象，或对象的键里有占位符。
    /// </exception>
    public static string RenderJson(string jsonTemplate, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var root = ParseTemplate(jsonTemplate);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteNode(writer, root, values);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 渲染纯文本模板：只替换占位符，**不转义**（转义是 JSON 那一层的事）。
    /// </summary>
    /// <param name="template">纯文本模板。</param>
    /// <param name="values">占位符的值（语义与 <see cref="RenderJson"/> 一样）。</param>
    public static string RenderPlain(string template, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        return PlaceholderPattern.Replace(template, match =>
        {
            var name = match.Groups[1].Value;

            if (!values.TryGetValue(name, out var value))
            {
                // 未识别的占位符：原样保留 —— **但已下线的占位符渲染成空串**。
                return RetiredPlaceholders.Contains(name) ? string.Empty : match.Value;
            }

            // 值是 null：这次事件没有这个字段 → 空串。
            return value ?? string.Empty;
        });
    }

    /// <summary>
    /// 校验 Body 模板。模板的规则只有这一份：根节点必须是 JSON 对象，
    /// 且占位符只能出现在字符串值节点里（对象的键里有占位符要拒掉）。
    /// </summary>
    /// <param name="template">要校验的模板。</param>
    /// <returns>
    /// 模板本身（校验通过后它一定非空，调用方不用再判一次空）。
    /// </returns>
    /// <exception cref="ArgumentException">模板不合法。</exception>
    public static string Validate(string? template)
    {
        ParseTemplate(template);

        // ParseTemplate 抛异常时模板非空，走到这里它一定非空。
        return template!;
    }

    /// <summary>
    /// 解析并校验模板：根节点必须是 JSON 对象，且对象的键里不能有占位符。
    /// </summary>
    /// <exception cref="ArgumentException">模板不合法。</exception>
    private static JsonNode ParseTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("Body 模板不能为空");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(template);
        }
        catch (JsonException ex)
        {
            // JsonException 的消息是 BCL 的英文原文，而且**可能回显模板内容**：
            // 「'xxx' is an invalid JSON literal」那条会把读到的那个裸串原样带出来
            // （实测无长度截断）—— 模板里可能有 bot token，不能原样透给 API 用户。
            // 所以对外的消息只用中文，ex 作为 inner exception 传出去供日志排查。
            throw new ArgumentException("Body 模板必须是合法的 JSON", ex);
        }
        catch (ArgumentException ex)
        {
            // 不是语法问题，是结构问题：内部消息是英文的，换成中文再抛出去，
            // 别把内部实现的消息直接透给 API 用户。
            throw new ArgumentException("Body 模板的 JSON 结构不合法", ex);
        }

        if (root is not JsonObject obj)
        {
            throw new ArgumentException("Body 模板的根节点必须是 JSON 对象");
        }

        var keyIndex = 0;
        EnsureValidShape(obj, ref keyIndex);

        return obj;
    }

    /// <summary>
    /// 模板的形状检查：对象的键里不许有占位符（渲染器只处理字符串值节点，
    /// 键里的占位符永远不会被替换），同一个对象里也不许有重复的键。
    /// </summary>
    /// <param name="node">要检查的节点。</param>
    /// <param name="keyIndex">
    /// 已检查过的键的计数（引用传递，递归时累加）。
    /// </param>
    /// <remarks>
    /// 键名**不回显进异常消息**：键名是用户写的模板内容，可能含凭据，
    /// 而异常消息会经 <c>WebhookService</c> 的
    /// <c>catch (ArgumentException ex) =&gt; ApiException(400, "invalid_body_template", ex.Message)</c>
    /// 进 API 响应的 <c>detail</c>。消息里只给「第 N 个键」这种位置信息 ——
    /// 定位能力保留，内容不外泄。
    /// </remarks>
    private static void EnsureValidShape(JsonNode? node, ref int keyIndex)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in EnumerateEntries(obj))
                {
                    keyIndex++;

                    if (PlaceholderPattern.IsMatch(key))
                    {
                        throw new ArgumentException($"Body 模板的键里不能有占位符（第 {keyIndex} 个键）");
                    }

                    EnsureValidShape(value, ref keyIndex);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    EnsureValidShape(item, ref keyIndex);
                }

                break;
        }
    }

    /// <summary>
    /// 枚举对象的键值对。
    /// </summary>
    /// <remarks>
    /// 重复的键要到这里才暴露：<see cref="JsonObject"/> 解析时不报错，**枚举**时才会抛
    /// <see cref="ArgumentException"/>，消息是内部的英文（"An item with the same key has already been added"）。
    /// 这里换成中文再抛出去，别把内部消息透给 API 用户。
    /// </remarks>
    private static List<KeyValuePair<string, JsonNode?>> EnumerateEntries(JsonObject obj)
    {
        try
        {
            return obj.ToList();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("Body 模板里同一个对象出现了重复的键", ex);
        }
    }

    /// <summary>把一个节点写进 JSON 输出。</summary>
    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node,
                                  IReadOnlyDictionary<string, string?> values)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in EnumerateEntries(obj))
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, value, values);
                }
                writer.WriteEndObject();
                break;

            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteNode(writer, item, values);
                }
                writer.WriteEndArray();
                break;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                WriteStringValue(writer, value.GetValue<string>()!, values);
                break;

            case JsonValue value:
                WriteLiteral(writer, value);
                break;

            default:
                // null（JSON 的 null 节点）。
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// 写一个字符串值：按段写，占位符的值替换进去之后由 <see cref="Utf8JsonWriter"/> 转义。
    /// </summary>
    /// <remarks>
    /// 逐段写而不是先拼成一个整串：<see cref="Utf8JsonWriter.WriteStringValueSegment(ReadOnlySpan{char}, bool)"/>
    /// 自己会按 JSON 字符串上下文转义（用的就是默认编码器，与 <c>JsonEncodedText.Encode</c> 同一套），
    /// 所以**不能**再先用 <c>JsonEncodedText.Encode</c> 转义一遍 —— 那会转义两次（引号会变成 <c>\\"</c>）。
    /// </remarks>
    private static void WriteStringValue(Utf8JsonWriter writer, string text,
                                          IReadOnlyDictionary<string, string?> values)
    {
        var position = 0;

        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            // 字面量段与占位符的值各写一段。
            writer.WriteStringValueSegment(text.AsSpan(position, match.Index - position), isFinalSegment: false);
            writer.WriteStringValueSegment(Resolve(match, values), isFinalSegment: false);
            position = match.Index + match.Length;
        }

        writer.WriteStringValueSegment(text.AsSpan(position), isFinalSegment: true);
    }

    /// <summary>
    /// 写一个非字符串的值（数字、布尔）：用底层的 <see cref="JsonElement"/> 写出，
    /// 保留模板里的原始字面量（<c>1.50</c> 不会被改写成 <c>1.5</c>，<c>1e5</c> 不会被改写成 <c>100000</c>）。
    /// </summary>
    private static void WriteLiteral(Utf8JsonWriter writer, JsonValue value)
    {
        value.GetValue<JsonElement>().WriteTo(writer);
    }

    /// <summary>取占位符的值（未识别的占位符原样保留，已下线的渲染成空串）。</summary>
    private static string Resolve(Match match, IReadOnlyDictionary<string, string?> values)
    {
        var name = match.Groups[1].Value;

        if (!values.TryGetValue(name, out var value))
        {
            // 未识别的占位符：原样保留 —— **但已下线的占位符渲染成空串**。
            return RetiredPlaceholders.Contains(name) ? string.Empty : match.Value;
        }

        // 值是 null：这次事件没有这个字段 → 空串（占位符的名字是认识的）。
        return value ?? string.Empty;
    }
}
