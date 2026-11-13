using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace IdeaApp.Endpoints;

/// <summary>
/// 端点声明的错误响应元数据：某个状态码下会返回哪些错误码。
/// </summary>
/// <param name="StatusCode">HTTP 状态码。</param>
/// <param name="Codes">该端点在这个状态码下可能返回的错误码。</param>
public sealed record ApiProblemMetadata(int StatusCode, IReadOnlyList<string> Codes);

/// <summary>
/// 端点声明错误响应的辅助方法。
///
/// <para>
/// 契约文件（<c>openapi/idea-v1.json</c>）是给 agent 当接口手册用的：
/// 只写成功路径的契约等于「只知道成功时该干什么，不知道失败时长什么样」。
/// 所以每个会出错的端点都要用 <see cref="ProducesApiProblem"/> 声明它的错误响应。
/// </para>
///
/// <para>
/// 它做两件事：
/// <list type="number">
/// <item>加上 <c>ProducesResponseTypeMetadata</c>：响应类型是
/// <c>ProblemDetails</c>，内容类型是 <c>application/problem+json</c> ——
/// 与 <c>ApiResults</c> 实际产出的响应一致（框架自带的
/// <c>ProducesProblem()</c> 做的就是这件事）。</item>
/// <item>挂上 <see cref="ApiProblemMetadata"/>：这个端点在这个状态码下
/// 可能返回哪些错误码。<see cref="Infrastructure.OpenApiProblemTransformer"/>
/// 读它，把错误码写进契约里每个错误响应的描述。</item>
/// </list>
/// </para>
/// </remarks>
public static class ApiProblemExtensions
{
    /// <summary>错误响应的内容类型（与 <c>ApiResults</c> 实际产出的响应一致）。</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>
    /// 声明一个错误响应：ProblemDetails + 该端点在这个状态码下可能出现的错误码。
    /// </summary>
    /// <param name="builder">端点构造器。</param>
    /// <param name="statusCode">HTTP 状态码（400 / 404 / 409 / 500 / 503 …）。</param>
    /// <param name="codes">
    /// 该端点可能返回的错误码。取值见 <see cref="Contracts.ApiErrorCodes"/>；
    /// 写错码名会被守卫测试挡下（错误码清单与实际抛出的码必须一致）。
    /// </param>
    public static RouteHandlerBuilder ProducesApiProblem(
        this RouteHandlerBuilder builder,
        int statusCode,
        params string[] codes)
    {
        // 框架自带的 ProducesProblem：ProblemDetails + application/problem+json。
        builder.ProducesProblem(statusCode, ContentType);

        // 错误码清单挂在端点元数据上，由 OpenApiProblemTransformer 写进契约。
        return builder.WithMetadata(new ApiProblemMetadata(statusCode, codes));
    }

    /// <summary>
    /// 把「Content-Type 不是 JSON」的拒绝留在我们自己的错误契约里。
    /// </summary>
    ///
    /// <para>
    /// <b>为什么需要它</b>：框架会给「带 JSON body 的端点」自动加一条
    /// <see cref="AcceptsMetadata"/>（内容类型 <c>application/json</c>），
    /// 路由层据此在 Content-Type 不对时**把这个端点毙掉**：
    /// 要么回一个**空的** 415（没有 <c>application/problem+json</c>、没有 <c>code</c>），
    /// 要么（像本应用这样有 <c>/api/{**rest}</c> 兜底时）落到兜底端点，
    /// 回一个语义完全不对的 404 + <c>api_not_found</c>。
    /// 两条路都不符合本应用「所有错误响应都是 ProblemDetails + 稳定错误码」的约定。
    /// </para>
    ///
    /// <para>
    /// <b>它做了什么</b>：把框架推断出来的那条 <see cref="AcceptsMetadata"/> 去掉 ——
    /// 路由层因此不再按 Content-Type 拦截，请求会真的进到端点，
    /// 框架在**绑定 body 时**抛 <c>BadHttpRequestException(415)</c>，
    /// 再由 <see cref="ApiExceptionHandler"/> 统一转成
    /// ProblemDetails + <c>code=unsupported_media_type</c>
    /// —— 与畸形 body 的 400（<c>invalid_request_body</c>）走同一条路。
    /// </para>
    ///
    /// <para>
    /// <b>对契约没有副作用</b>：OpenAPI 生成器在找不到 <see cref="AcceptsMetadata"/> 时
    /// 会按 body 参数的类型回落到 <c>application/json</c>，所以 requestBody 那一段不变。
    /// </para>
    /// </remarks>
    /// <param name="builder">端点构造器。</param>
    public static TBuilder RejectsNonJsonBodyWithProblemDetails<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            // 从后往前删：删除会改变下标。
            for (var index = endpoint.Metadata.Count - 1; index >= 0; index--)
            {
                if (endpoint.Metadata[index] is AcceptsMetadata)
                {
                    endpoint.Metadata.RemoveAt(index);
                }
            }
        });

        return builder;
    }
}
