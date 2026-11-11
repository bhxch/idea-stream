using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>健康检查响应体。</summary>
public sealed record HealthResponse(string Status);

/// <summary>
/// 健康检查响应体的 JSON 源生成上下文。Native AOT 下反射式序列化不可用，
/// 因此端点必须通过 <see cref="JsonTypeInfo{T}"/> 显式指定序列化器。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonContext : JsonSerializerContext;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health", () =>
            Results.Json(new HealthResponse("ok"), HealthJsonContext.Default.HealthResponse));
        return endpoints;
    }
}
