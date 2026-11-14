using System.Text;
using IdeaApp.Contracts;
using IdeaApp.Endpoints;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 把错误响应的信息写进 OpenAPI 契约。
///
/// <para>
/// 契约文件是给 agent 当接口手册用的。只写成功路径的契约，agent 遇到错误时
/// 既不知道错误长什么样（RFC 9457 的 ProblemDetails + 扩展字段 <c>code</c>），
/// 也不知道什么时候会拿到 400 / 404 —— 只能靠试错。
/// </para>
///
/// <para>
/// 这里做两件事：
/// <list type="number">
/// <item>给 <c>components.schemas.ProblemDetails</c> 写描述：说明 <c>code</c>
/// 扩展字段的存在与全部取值（取值清单来自 <see cref="ApiErrorCodes"/>）。</item>
/// <item>给每个错误响应写描述：这个端点在这个状态码下可能返回哪些 <c>code</c>。
/// 清单来自端点元数据 <see cref="ApiProblemMetadata"/>（由
/// <see cref="ApiProblemExtensions.ProducesApiProblem"/> 挂上去）。</item>
/// </list>
/// </para>
///
/// <para>
/// 为什么不用 <c>IProducesResponseTypeMetadata.Description</c>：框架生成文档时
/// 不读它（实测：写进去的描述会被状态码的英文短语盖掉），只能自己改文档。
/// </para>
/// </remarks>
public sealed class OpenApiProblemTransformer : IOpenApiDocumentTransformer
{
    /// <summary>ProblemDetails 在 components.schemas 里的名字。</summary>
    private const string ProblemDetailsSchemaName = "ProblemDetails";

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        DescribeSchema(document);
        DescribeResponses(document, context);

        return Task.CompletedTask;
    }

    /// <summary>给 ProblemDetails 的 schema 写描述与扩展字段说明。</summary>
    private static void DescribeSchema(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null
            || !schemas.TryGetValue(ProblemDetailsSchemaName, out var schema)
            || schema is not OpenApiSchema concrete)
        {
            return;
        }

        concrete.Description = SchemaDescription();

        // Extensions 不是 ProblemDetails 的类型化属性，schema 里不会有它 ——
        // 显式写成 additionalProperties，agent 才知道还能往里放东西。
        concrete.AdditionalProperties = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "扩展字段。应用目前只放 code（取值见本 schema 的 description）。"
        };
    }

    /// <summary>给每个错误响应写描述：这个端点可能返回哪些错误码。</summary>
    private static void DescribeResponses(OpenApiDocument document, OpenApiDocumentTransformerContext context)
    {
        if (document.Paths is null)
        {
            return;
        }

        var endpoints = EndpointProblems(context.ApplicationServices);

        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem.Operations is null)
            {
                continue;
            }

            foreach (var (method, operation) in pathItem.Operations)
            {
                if (operation is null)
                {
                    continue;
                }

                var key = (method.Method.ToUpperInvariant(), NormalizeRouteTemplate(path));
                var declared = endpoints.TryGetValue(key, out var found)
                    ? found
                    : Array.Empty<ApiProblemMetadata>();

                if (operation.Responses is null)
                {
                    continue;
                }

                foreach (var (statusCode, response) in operation.Responses)
                {
                    if (response is null || !IsProblemResponse(response))
                    {
                        continue;
                    }

                    var codes = declared
                        .Where(problem => problem.StatusCode.ToString() == statusCode)
                        .SelectMany(problem => problem.Codes)
                        .ToList();

                    response.Description = DescribeResponse(statusCode, codes);
                }
            }
        }
    }

    /// <summary>
    /// 端点 → 它声明的错误码清单。
    /// </summary>
    /// <remarks>
    /// 用 ApiExplorer 的 ApiDescription 反查端点元数据：文档里的路径就是
    /// 路由模板（<c>/api/projects/{id}</c>），与 ApiDescription 的
    /// <c>RelativePath</c>（前面少一个斜杠）对得上。
    /// <para>
    /// **catch-all 的路由模板要归一化**：路由写的是 <c>{**path}</c>（吞掉剩余路径段），
    /// 而文档里的键是 <c>{path}</c>（生成器把两个星号去掉了）。
    /// 不归一化就查不到 —— 端点明明声明了错误码，契约里却写着
    /// 「该状态码下的 code 取值全集见 components.schemas.ProblemDetails」，
    /// 契约是给 agent 当手册用的，这句退化的话没有手册价值。
    /// </para>
    /// </remarks>
    private static Dictionary<(string Method, string Path), IReadOnlyList<ApiProblemMetadata>> EndpointProblems(
        IServiceProvider services)
    {
        var map = new Dictionary<(string Method, string Path), IReadOnlyList<ApiProblemMetadata>>();

        var provider = services.GetService<IApiDescriptionGroupCollectionProvider>();
        if (provider is null)
        {
            return map;
        }

        foreach (var group in provider.ApiDescriptionGroups.Items)
        {
            foreach (var api in group.Items)
            {
                var problems = api.ActionDescriptor.EndpointMetadata
                    .OfType<ApiProblemMetadata>()
                    .ToList();

                if (problems.Count == 0)
                {
                    continue;
                }

                var method = (api.HttpMethod ?? "GET").ToUpperInvariant();
                var path = "/" + (api.RelativePath ?? string.Empty).TrimStart('/');

                map[(method, NormalizeRouteTemplate(path))] = problems;
            }
        }

        return map;
    }

    /// <summary>
    /// 路由模板归一化：catch-all 的 <c>{**x}</c> 写成 <c>{x}</c>。
    /// </summary>
    /// <remarks>
    /// 路由模板里 catch-all 写成 <c>{**path}</c>（两个星号 = 吞掉剩余的路径段），
    /// 而 OpenAPI 生成器写进文档的键是 <c>{path}</c>。两边对不上就查不到端点
    /// 声明的错误码 —— 契约里那条响应就退化成一句没用的「见 ProblemDetails」。
    /// </remarks>
    private static string NormalizeRouteTemplate(string path) =>
        path.Replace("{**", "{", StringComparison.Ordinal);

    /// <summary>响应是不是错误响应（内容类型是 application/problem+json）。</summary>
    private static bool IsProblemResponse(IOpenApiResponse response) =>
        response.Content is not null
        && response.Content.ContainsKey(ApiProblemExtensions.ContentType);

    /// <summary>错误响应的描述文本。</summary>
    private static string DescribeResponse(string statusCode, IReadOnlyList<string> codes)
    {
        var builder = new StringBuilder();

        builder.Append($"请求失败（{statusCode}）。响应体是 RFC 9457 的 ProblemDetails：");
        builder.Append("扩展字段 code 是稳定的机器可读错误码，detail 是给人看的说明。");

        if (codes.Count > 0)
        {
            builder.Append("本端点可能出现的 code：");
            builder.Append(string.Join("、", codes.Select(DescribeCode)));
            builder.Append('。');
        }
        else
        {
            builder.Append($"该状态码下的 code 取值全集见 components.schemas.{ProblemDetailsSchemaName}。");
        }

        return builder.ToString();
    }

    /// <summary>ProblemDetails schema 的描述文本（含 code 取值全集）。</summary>
    private static string SchemaDescription()
    {
        var builder = new StringBuilder();

        builder.Append("RFC 9457 的 ProblemDetails。业务错误一律带扩展字段 code（字符串）：");
        builder.Append("它是稳定的机器可读错误码（agent 拿它分支处理，不要去匹配 detail 的文字）；");
        builder.Append("detail 是给人看的说明。code 取值全集（按 HTTP 状态码分组）：");

        foreach (var group in ApiErrorCodes.Documented.GroupBy(code => code.Status).OrderBy(group => group.Key))
        {
            builder.Append($"\n{group.Key}：");
            builder.Append(string.Join("、", group.Select(item => DescribeCode(item.Code))));
            builder.Append('。');
        }

        return builder.ToString();
    }

    /// <summary>错误码 → 「码名（含义）」。</summary>
    private static string DescribeCode(string code)
    {
        var known = ApiErrorCodes.Find(code);
        return known is null ? code : $"{code}（{known.Meaning}）";
    }
}
