using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 给**全部**端点的查询参数（以及路径参数）补 description 与 default，
/// 并把 <c>GET /api/search</c> 的 <c>q</c> 标成必填。
///
/// <para>
/// 契约是 agent 的接口手册。框架只按参数的 CLR 类型生成 schema ——
/// 「limit 是 1..1000 的字符串」「两个端点的 offset 方向相反」「includeTrash 默认 true」
/// 这些语义只活在 skill 手册里；agent 直接读契约时看不到，只能拿没有错误码的 400 一次次试错。
/// </para>
///
/// <para>
/// <strong>覆盖范围是全库</strong>，不只是搜索那两个端点：每一个 <c>in: query</c> 的参数
/// 都要有非空 description —— 契约里留白的地方，agent 只能靠试错去填。
/// </para>
///
/// <para>
/// <c>q</c> 为什么要手动标必填：端点把它绑成 <c>string?</c>（绑非空的 <c>string</c>
/// 会让「没给 q」变成绑定失败 —— 一个空 body、没有错误码的 400），框架据此把它生成
/// 成可选；而服务层对缺失的 <c>q</c> 回 400 <c>invalid_search_query</c>。契约说可选、
/// 实际必填，这两句话必须有人对齐 —— 在这里对齐。
/// </para>
///
/// <para>
/// <c>default</c> 只给**服务端真的有默认值**的参数：<c>/api/ideas</c> 的 limit
/// 不给就是「全量」，没有默认值可写；<c>/api/search</c> 的 limit 不给就是 20，那个才有。
/// 写上去的值必须与服务端的行为一致（见 <see cref="ParameterDoc"/> 各项）。
/// </para>
///
/// <para>
/// ⚠️ Microsoft.OpenApi 2.x 的 <see cref="IOpenApiParameter"/> 接口成员是只读的，
/// 改写必须转成具体类型 <see cref="OpenApiParameter"/>；schema 的
/// <see cref="OpenApiSchema.Default"/> 是 <see cref="JsonNode"/>，default 的 JSON 类型
/// 要与参数自己声明的 schema 类型一致（string 参数用字符串 default，boolean 用 true）。
/// </para>
///
/// <para>
/// ⚠️ SSE 端点的 <c>lastEventId</c> 不在这里：它的描述由 <see cref="OpenApiEventTransformer"/>
/// 写（那份更详细，而且写的是**文档级** transformer，跑在操作级 transformer 之后）。
/// </para>
/// </summary>
public sealed class OpenApiQueryParameterTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var description = context.Description;
        if (description is null)
        {
            return Task.CompletedTask;
        }

        // ApiDescription.RelativePath 不带前导斜杠（如 "api/ideas"）。
        // catch-all 路由的相对路径里是 "{**path}"，契约里写成 "{path}" —— 这里统一成后者。
        var path = (description.RelativePath ?? string.Empty)
            .TrimStart('/')
            .Replace("{**", "{", StringComparison.Ordinal);

        foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
        {
            var doc = FindDoc(path, parameter);
            if (doc is null)
            {
                continue;
            }

            parameter.Description = doc.Description;

            if (doc.Default is not null)
            {
                SetDefault(parameter, doc.Default);
            }

            if (doc.Required)
            {
                parameter.Required = true;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 一个参数的说明：描述、默认值、是否必填。
    /// </summary>
    /// <param name="Description">这个参数到底做什么（语义、单位、取值范围、留空是什么意思）。</param>
    /// <param name="Default">
    /// 服务端真的有的默认值；没有就留 null（缺省 = 不传）。
    /// 值必须与服务端的实际行为一致。
    /// </param>
    /// <param name="Required">服务端是否真的要求这个参数（不要求就别标）。</param>
    private sealed record ParameterDoc(string Description, JsonNode? Default, bool Required = false);

    /// <summary>
    /// 逐个参数的说明表。键是「操作相对路径 | 参数名」。
    /// </summary>
    /// <remarks>
    /// 为什么按「路径 + 参数名」而不是只按参数名：同一个名字在不同端点上的语义不同 ——
    /// <c>limit</c> 在 <c>/api/ideas</c> 是「1..1000，不给 = 全量」，
    /// 在 <c>/api/search</c> 是「每类 1..100，不给 = 20」；
    /// <c>offset</c> 的方向在两个端点上还是**相反**的。
    /// </remarks>
    private static readonly Dictionary<string, ParameterDoc> Docs = new(StringComparer.Ordinal)
    {
        // ── GET /api/ideas ────────────────────────────────────────────────
        ["api/ideas|projectId"] = new(
            "项目 id：只列这个项目下的想法。不给或空串 = 不限项目（返回所有项目的想法）。"
            + "服务端不校验项目是否存在：不存在的项目 id → 空列表（不是 404）。",
            Default: null),
        ["api/ideas|status"] = new(
            "状态过滤。取值 all / open / done，大小写不敏感；"
            + "不给或空串 = all（不过滤，open = 未完成，done = 已完成）。"
            + "不认识的值 → 400 invalid_status。",
            Default: JsonValue.Create("all")),
        ["api/ideas|tags"] = new(
            "标签过滤：逗号分隔的标签名列表（如 tags=咖啡,手冲）。"
            + "去掉每项的首尾空白、丢掉空项、按大小写不敏感去重。"
            + "不给或空串 = 不按标签过滤。",
            Default: null),
        ["api/ideas|tagMode"] = new(
            "多个标签之间的关系：or（缺省，命中其中任意一个即可）/ and（必须同时命中全部），大小写不敏感。"
            + "不认识的值 → 400 invalid_tag_mode。只有一个标签时两种模式等价。",
            Default: JsonValue.Create("or")),
        ["api/ideas|q"] = new(
            "关键词，可选。中文按词元相邻匹配（标点不挡路），英文按词级；"
            + "缺失或全是标点/空白 = 没有关键词（不过滤）",
            Default: null),
        ["api/ideas|limit"] = new(
            "返回条数上限，1..1000 的整数；不给 = 全量（超过单次返回上限报 400 too_many_ideas），"
            + "给了 limit 就不再触发那个 400",
            Default: null),
        ["api/ideas|offset"] = new(
            "往回翻历史：跳过最新的 N 条（列表按 updated_at 升序、最新的在底部），"
            + "0 = 最新的一页；必须与 limit 一起给。注意与 GET /api/search 的 offset 方向相反",
            Default: JsonValue.Create("0")),

        // ── GET /api/search ───────────────────────────────────────────────
        ["api/search|q"] = new(
            "关键词（必填）。中文按词元相邻匹配（标点不挡路：搜「咖啡水温」能命中「咖啡，水温」），"
            + "英文按词级（搜 hel 命中不了 hello）；多段之间是 AND。"
            + "缺失或全是标点/空白 → 400 invalid_search_query",
            Default: null,
            Required: true),
        ["api/search|limit"] = new(
            "每类结果（想法/项目/标签）各自的最大条数，1..100 的整数；缺省 20",
            Default: JsonValue.Create("20")),
        ["api/search|offset"] = new(
            "翻相关度页：跳过最相关的前 N 条（该组按 bm25 相关度排序）；缺省 0。"
            + "注意与 GET /api/ideas 的 offset 方向相反",
            Default: JsonValue.Create("0")),
        ["api/search|includeTrash"] = new(
            "是否包含回收站里的条目；缺省 true。"
            + "只给 true / false —— 别的值会得到一个没有错误码的 400（框架的参数绑定失败）",
            Default: JsonValue.Create(true)),

        // ── GET /api/tags/suggest ─────────────────────────────────────────
        ["api/tags/suggest|projectId"] = new(
            "当前项目 id。给了就先列该项目下**未删**想法用过的标签，再列全局其它标签；"
            + "不给或空串 = 不区分项目，只列全局标签字典。项目不存在 → 只列全局标签。",
            Default: null),
        ["api/tags/suggest|prefix"] = new(
            "标签名前缀，大小写不敏感匹配。给了就只列以它开头的标签；"
            + "不给或空串 = 不过滤前缀（返回全部标签）。",
            Default: JsonValue.Create("")),

        // ── GET /api/webhooks ─────────────────────────────────────────────
        ["api/webhooks|projectId"] = new(
            "只列绑定到该项目的 webhook（scope=project）；"
            + "不给或空串 = 只列全局作用域的 webhook（scope=global）。",
            Default: null),
        ["api/webhooks|unmask"] = new(
            "是否返回未遮蔽的凭据（url 的路径段与请求头的值）。缺省 = 遮蔽（凭据被替换成 ***）；"
            + "只给 true 才给原值，false 与不给等价。",
            Default: JsonValue.Create(false)),
        ["api/webhooks/{id}|unmask"] = new(
            "是否返回未遮蔽的凭据（url 的路径段与请求头的值）。缺省 = 遮蔽（凭据被替换成 ***）；"
            + "只给 true 才给原值，false 与不给等价。",
            Default: JsonValue.Create(false)),

        // ── 路径参数 ──────────────────────────────────────────────────────
        ["api/ideas/{id}|id"] = new("想法 id（URL 里的这一段）。", Default: null),
        ["api/ideas/{id}/tags|id"] = new("想法 id（URL 里的这一段）。", Default: null),
        ["api/ideas/{id}/complete|id"] = new("想法 id（URL 里的这一段）。", Default: null),
        ["api/ideas/{id}/restore|id"] = new("想法 id（URL 里的这一段）。", Default: null),
        ["api/projects/{id}|id"] = new("项目 id（URL 里的这一段）。", Default: null),
        ["api/projects/{id}/restore|id"] = new("项目 id（URL 里的这一段）。", Default: null),
        ["api/webhooks/{id}|id"] = new("webhook 配置 id（URL 里的这一段）。", Default: null),
        ["api/webhooks/{id}/deliveries|id"] = new("webhook 配置 id（URL 里的这一段）。", Default: null),
        ["api/webhooks/{id}/deliveries/{deliveryId}/retry|id"] =
            new("webhook 配置 id（URL 里的这一段）。", Default: null),
        ["api/webhooks/{id}/deliveries/{deliveryId}/retry|deliveryId"] =
            new("投递记录 id（URL 里的这一段）。", Default: null),
        ["api/settings/{key}|key"] = new("设置项的键名（URL 里的这一段）。", Default: null),
        ["skill/idea-manager/{path}|path"] = new(
            "skill 分发副本里的相对文件路径（如 SKILL.md、references/errors.md）；路径穿越一律 404。",
            Default: null),
        ["skill/{rest}|rest"] = new(
            "/skill 下的路径（catch-all）：没有对应端点时回 404 skill_endpoint_not_found。",
            Default: null),
    };

    /// <summary>
    /// 查一个参数的说明。查不到就返回 null（框架自己生成的描述比瞎写强）。
    /// </summary>
    private static ParameterDoc? FindDoc(string path, OpenApiParameter parameter)
    {
        // 键里带了参数名：查询参数与路径参数天然不冲突（同名也不会撞）。
        return Docs.TryGetValue($"{path}|{parameter.Name}", out var doc) ? doc : null;
    }

    /// <summary>给参数的 schema 写 default。schema 缺了就不写 —— 有类型才有默认值可言。</summary>
    private static void SetDefault(OpenApiParameter parameter, JsonNode value)
    {
        if (parameter.Schema is OpenApiSchema schema)
        {
            schema.Default = value;
        }
    }
}
