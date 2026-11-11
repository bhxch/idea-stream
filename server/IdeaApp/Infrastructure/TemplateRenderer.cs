using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 模板渲染器：把 <c>{{...}}</c> 占位符替换成事件数据。
///
/// <para>
/// 占位符语法是 <c>{{名字}}</c>，名字允许字母、数字、下划线与点（<c>idea.title</c>）。
/// 未识别的占位符**原样保留** —— 模板是用户手写的，名字拼错不该让投递炸掉。
/// </para>
///
/// <para>
/// JSON 模板的渲染只处理**字符串值节点**：占位符的值按 JSON 字符串上下文转义后再注入，
/// 所以正文里的引号、换行、反斜杠不会破坏 JSON。数字、布尔、null 之类的节点原样保留。
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

    /// <summary>输出 JSON 时每层缩进的空格数。</summary>
    private const int IndentSize = 2;

    /// <summary>
    /// 渲染 JSON 模板：解析模板，把字符串值节点里的占位符替换掉，输出缩进 2 格的 JSON。
    /// </summary>
    /// <param name="jsonTemplate">JSON 模板（根节点必须是 JSON 对象）。</param>
    /// <param name="values">
    /// 占位符的值。值是 <c>null</c> 表示「这个字段这次没有值」，渲染成空串；
    /// 字典里**没有**这个键才是「未识别的占位符」，原样保留。
    /// </param>
    /// <returns>缩进 2 格的 JSON。</returns>
    /// <exception cref="ArgumentException">
    /// 模板不是合法 JSON、根节点不是 JSON 对象，或对象的键里有占位符。
    /// </exception>
    public static string RenderJson(string jsonTemplate, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var root = ParseTemplate(jsonTemplate);

        var builder = new StringBuilder();
        WriteNode(root, values, builder, 0);

        return builder.ToString();
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
                // 未识别的占位符：原样保留。
                return match.Value;
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
            throw new ArgumentException($"Body 模板必须是合法的 JSON：{ex.Message}");
        }

        if (root is not JsonObject obj)
        {
            throw new ArgumentException("Body 模板的根节点必须是 JSON 对象");
        }

        EnsureNoPlaceholderInKeys(obj);

        return obj;
    }

    /// <summary>
    /// 对象的键里不许有占位符：渲染器只处理字符串值节点，键里的占位符永远不会被替换。
    /// </summary>
    private static void EnsureNoPlaceholderInKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (PlaceholderPattern.IsMatch(key))
                    {
                        throw new ArgumentException($"Body 模板的键里不能有占位符：{key}");
                    }

                    EnsureNoPlaceholderInKeys(value);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    EnsureNoPlaceholderInKeys(item);
                }

                break;
        }
    }

    /// <summary>把一个节点写成 JSON 文本。</summary>
    private static void WriteNode(JsonNode? node, IReadOnlyDictionary<string, string?> values,
                                  StringBuilder builder, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                WriteObject(obj, values, builder, depth);
                break;

            case JsonArray array:
                WriteArray(array, values, builder, depth);
                break;

            case JsonValue value:
                WriteValue(value, values, builder);
                break;

            default:
                // null（JSON 的 null 节点）。
                builder.Append("null");
                break;
        }
    }

    /// <summary>写一个 JSON 对象。</summary>
    private static void WriteObject(JsonObject obj, IReadOnlyDictionary<string, string?> values,
                                    StringBuilder builder, int depth)
    {
        if (obj.Count == 0)
        {
            builder.Append("{}");
            return;
        }

        builder.Append("{\n");

        var first = true;
        foreach (var (key, value) in obj)
        {
            if (!first)
            {
                builder.Append(",\n");
            }

            first = false;

            builder.Append(' ', (depth + 1) * IndentSize);
            builder.Append('"');
            builder.Append(JsonEncodedText.Encode(key).ToString());
            builder.Append("\": ");
            WriteNode(value, values, builder, depth + 1);
        }

        builder.Append('\n');
        builder.Append(' ', depth * IndentSize);
        builder.Append('}');
    }

    /// <summary>写一个 JSON 数组。</summary>
    private static void WriteArray(JsonArray array, IReadOnlyDictionary<string, string?> values,
                                   StringBuilder builder, int depth)
    {
        if (array.Count == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append("[\n");

        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(",\n");
            }

            builder.Append(' ', (depth + 1) * IndentSize);
            WriteNode(array[i], values, builder, depth + 1);
        }

        builder.Append('\n');
        builder.Append(' ', depth * IndentSize);
        builder.Append(']');
    }

    /// <summary>写一个 JSON 值。</summary>
    private static void WriteValue(JsonValue value, IReadOnlyDictionary<string, string?> values,
                                   StringBuilder builder)
    {
        if (value.GetValueKind() == JsonValueKind.String)
        {
            builder.Append('"');
            builder.Append(RenderString(value.GetValue<string>()!, values));
            builder.Append('"');
            return;
        }

        // 数字、布尔、null：原样写出（保留模板里的原始字面量，如 1.50 不被改写成 1.5）。
        builder.Append(value.ToJsonString());
    }

    /// <summary>
    /// 替换字符串里的占位符，返回**已按 JSON 字符串上下文转义**的内容（不含两侧的引号）。
    /// </summary>
    /// <remarks>
    /// 逐段转义再拼接：字面量段与占位符的值各自转义一次，拼起来就是最终内容 ——
    /// 值不能先转义再整体转义（那样会转两次）。
    /// </remarks>
    private static string RenderString(string text, IReadOnlyDictionary<string, string?> values)
    {
        var builder = new StringBuilder();
        var position = 0;

        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            builder.Append(JsonEncodedText.Encode(text[position..match.Index]).ToString());
            position = match.Index + match.Length;

            builder.Append(ResolveEscaped(match, values));
        }

        builder.Append(JsonEncodedText.Encode(text[position..]).ToString());

        return builder.ToString();
    }

    /// <summary>取占位符的值并按 JSON 字符串上下文转义（未识别的占位符原样保留）。</summary>
    private static string ResolveEscaped(Match match, IReadOnlyDictionary<string, string?> values)
    {
        var name = match.Groups[1].Value;

        if (!values.TryGetValue(name, out var value))
        {
            // 未识别的占位符：原样保留。
            return JsonEncodedText.Encode(match.Value).ToString();
        }

        // 值是 null：这次事件没有这个字段 → 空串（占位符的名字是认识的）。
        return JsonEncodedText.Encode(value ?? string.Empty).ToString();
    }
}
