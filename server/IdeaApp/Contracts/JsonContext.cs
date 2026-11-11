using System.Text.Json.Serialization;
using IdeaApp.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IdeaApp.Contracts;

/// <summary>
/// 全项目唯一的 JSON 源生成上下文。Native AOT 下反射式序列化不可用，
/// 所有对外 DTO 都必须登记在这里，并在组合根把它注册成
/// <c>JsonOptions.SerializerOptions.TypeInfoResolver</c>。
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
[JsonSerializable(typeof(WebhookTemplateDto))]
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
// 补全结果是纯字符串数组：包装类型也要登记，否则 AOT 产物下端点直接 500。
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<IdeaDto>))]
[JsonSerializable(typeof(IReadOnlyList<ProjectDto>))]
[JsonSerializable(typeof(IReadOnlyList<TagDto>))]
[JsonSerializable(typeof(IReadOnlyList<WebhookDto>))]
[JsonSerializable(typeof(IReadOnlyList<DeliveryDto>))]
[JsonSerializable(typeof(IReadOnlyList<TrashEntryDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// webhook 的订阅事件数组：存库时序列化成 JSON 字符串、读库时反序列化回来，
// 少了这一条，AOT 产物下读写 webhook 配置就是 500。
[JsonSerializable(typeof(string[]))]
// ProblemDetails 及其子类：子类元数据不会从基类自动继承，必须各自登记。
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
// 请求体记录：Native AOT 下请求体的反序列化同样需要源生成元数据，
// 否则端点在开发期就抛 NoMetadataForType、在 AOT 产物下直接失败。
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(RenameProjectRequest))]
[JsonSerializable(typeof(ReorderProjectsRequest))]
[JsonSerializable(typeof(CreateIdeaRequest))]
[JsonSerializable(typeof(UpdateIdeaRequest))]
[JsonSerializable(typeof(SetTagsRequest))]
[JsonSerializable(typeof(SetDoneRequest))]
// 批量与合并的请求体：请求体的反序列化同样走源生成元数据，
// 少登记一个，在开发期就抛 NoMetadataForType、在 AOT 产物下直接失败。
[JsonSerializable(typeof(BulkCompleteRequest))]
[JsonSerializable(typeof(BulkTagsRequest))]
[JsonSerializable(typeof(BulkDeleteRequest))]
[JsonSerializable(typeof(MergeIdeasRequest))]
// webhook 配置的请求体：请求体的反序列化同样走源生成元数据，
// 少登记一个，开发期就抛 NoMetadataForType、AOT 产物下直接失败。
[JsonSerializable(typeof(WebhookCreateRequest))]
[JsonSerializable(typeof(WebhookUpdateRequest))]
// 领域事件：SSE 的 data 帧与 webhook 的投递体都要序列化它，
// 白名单里少登记一个，AOT 产物下这两条链路就是 500。
[JsonSerializable(typeof(IdeaEvent))]
[JsonSerializable(typeof(IdeaSnapshot))]
[JsonSerializable(typeof(ProjectSnapshot))]
public sealed partial class IdeaJsonContext : JsonSerializerContext;
