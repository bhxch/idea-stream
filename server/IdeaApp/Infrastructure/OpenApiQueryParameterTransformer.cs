using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 给搜索相关端点的查询参数补 description 与 default，并把
/// <c>GET /api/search</c> 的 <c>q</c> 标成必填。
///
/// <para>
/// 契约是 agent 的接口手册。框架只按参数的 CLR 类型生成 schema ——
/// 「limit 是 1..1000 的字符串」「两个端点的 offset 方向相反」「includeTrash 默认 true」
/// 这些语义只活在 skill 手册里；agent 直接读契约时看不到，只能拿没有错误码的 400 一次次试错。
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
/// ⚠️ Microsoft.OpenApi 2.x 的 <see cref="IOpenApiParameter"/> 接口成员是只读的，
/// 改写必须转成具体类型 <see cref="OpenApiParameter"/>；schema 的
/// <see cref="OpenApiSchema.Default"/> 是 <see cref="JsonNode"/>，default 的 JSON 类型
/// 要与参数自己声明的 schema 类型一致（string 参数用字符串 default，boolean 用 true）。
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
        // 只认 GET /api/ideas 与 GET /api/search：这两处的查询参数有「类型正确但语义缺失」的毛病。
        var description = context.Description;
        if (description is not { HttpMethod: "GET" })
        {
            return Task.CompletedTask;
        }

        // ApiDescription.RelativePath 不带前导斜杠（如 "api/ideas"）。
        var isIdeas = description.RelativePath == "api/ideas";
        var isSearch = description.RelativePath == "api/search";
        if (!isIdeas && !isSearch)
        {
            return Task.CompletedTask;
        }

        foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
        {
            switch (parameter.Name)
            {
                case "q":
                    parameter.Description = isSearch
                        ? "关键词（必填）。中文按词元相邻匹配（标点不挡路：搜「咖啡水温」能命中「咖啡，水温」），"
                          + "英文按词级（搜 hel 命中不了 hello）；多段之间是 AND。"
                          + "缺失或全是标点/空白 → 400 invalid_search_query"
                        : "关键词，可选。中文按词元相邻匹配（标点不挡路），英文按词级；"
                          + "缺失或全是标点/空白 = 没有关键词（不过滤）";
                    break;

                case "limit":
                    parameter.Description = isSearch
                        ? "每类结果（想法/项目/标签）各自的最大条数，1..100 的整数；缺省 20"
                        : "返回条数上限，1..1000 的整数；不给 = 全量（超过单次返回上限报 400 too_many_ideas），"
                          + "给了 limit 就不再触发那个 400";
                    if (isSearch)
                    {
                        SetDefault(parameter, JsonValue.Create("20"));
                    }

                    break;

                case "offset":
                    parameter.Description = isSearch
                        ? "翻相关度页：跳过最相关的前 N 条（该组按 bm25 相关度排序）；缺省 0。"
                          + "注意与 GET /api/ideas 的 offset 方向相反"
                        : "往回翻历史：跳过最新的 N 条（列表按 updated_at 升序、最新的在底部），"
                          + "0 = 最新的一页；必须与 limit 一起给。注意与 GET /api/search 的 offset 方向相反";
                    SetDefault(parameter, JsonValue.Create("0"));
                    break;

                case "includeTrash":
                    if (isSearch)
                    {
                        parameter.Description = "是否包含回收站里的条目；缺省 true。"
                            + "只给 true / false —— 别的值会得到一个没有错误码的 400（框架的参数绑定失败）";
                        SetDefault(parameter, JsonValue.Create(true));
                    }

                    break;
            }
        }

        // 契约里 q 显示为可选（端点绑 string? 的必然结果），但服务层把缺失的 q
        // 当 400 invalid_search_query —— 这里把它标成必填，与实际行为对齐。
        if (isSearch)
        {
            foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
            {
                if (parameter.Name == "q")
                {
                    parameter.Required = true;
                }
            }
        }

        return Task.CompletedTask;
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
