using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IdeaApp.Contracts;

/// <summary>
/// 全项目唯一的 JSON 源生成上下文。Native AOT 下反射式序列化不可用，
/// 所有对外 DTO 都必须登记在这里，并在组合根把它注册进
/// <c>JsonOptions.SerializerOptions.TypeInfoResolverChain</c>。
/// </summary>
/// <remarks>
/// 白名单的登记范围：端点返回的**静态类型**必须在这里有条目。
/// 少登记一个，对应端点在 AOT 产物下就是 500，而普通 <c>dotnet test</c> 全绿
/// （测试宿主有反射式兜底），只有 <c>server/scripts/aot-smoke.sh</c> 能测出来。
/// 集合包装类型同理：返回 <c>List&lt;X&gt;</c> 与返回 <c>X[]</c> 是两种类型，
/// 端点改成哪种写法就得登记哪种。
/// </remarks>
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
[JsonSerializable(typeof(WebhookDto[]))]
[JsonSerializable(typeof(List<IdeaDto>))]
[JsonSerializable(typeof(List<ProjectDto>))]
[JsonSerializable(typeof(List<TagDto>))]
[JsonSerializable(typeof(List<WebhookDto>))]
[JsonSerializable(typeof(List<DeliveryDto>))]
[JsonSerializable(typeof(List<TrashEntryDto>))]
[JsonSerializable(typeof(IReadOnlyList<IdeaDto>))]
[JsonSerializable(typeof(IReadOnlyList<ProjectDto>))]
[JsonSerializable(typeof(IReadOnlyList<TagDto>))]
[JsonSerializable(typeof(IReadOnlyList<WebhookDto>))]
[JsonSerializable(typeof(IReadOnlyList<DeliveryDto>))]
[JsonSerializable(typeof(IReadOnlyList<TrashEntryDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// ProblemDetails 及其子类：子类元数据不会从基类自动继承，必须各自登记。
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
public sealed partial class IdeaJsonContext : JsonSerializerContext;
