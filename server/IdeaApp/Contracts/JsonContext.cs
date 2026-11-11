using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace IdeaApp.Contracts;

/// <summary>
/// 全项目唯一的 JSON 源生成上下文。Native AOT 下反射式序列化不可用，
/// 所有对外 DTO 都必须登记在这里，并在组合根把它注册进
/// <c>JsonOptions.SerializerOptions.TypeInfoResolverChain</c>。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IdeaDto))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(TagDto))]
[JsonSerializable(typeof(WebhookDto))]
[JsonSerializable(typeof(DeliveryDto))]
[JsonSerializable(typeof(TrashEntryDto))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(IdeaDto[]))]
[JsonSerializable(typeof(ProjectDto[]))]
[JsonSerializable(typeof(TagDto[]))]
[JsonSerializable(typeof(DeliveryDto[]))]
[JsonSerializable(typeof(TrashEntryDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ProblemDetails))]
public sealed partial class IdeaJsonContext : JsonSerializerContext;
