using IdeaApp.Application;
using IdeaApp.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// webhook 配置 API。
///
/// <para>
/// 这一组端点只管**配置面**：增删改查与模板预设。
/// 投递记录与手动重投是投递那轮加的端点。
/// </para>
///
/// <para>
/// AOT 硬性要求：每个端点返回的**静态类型**都必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>（集合包装类型同理）。
/// </para>
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // 列表：裸数组。不带 projectId 时只列全局作用域的，
        // 带了 projectId 时只列绑定到该项目的。
        app.MapGet("/api/webhooks", (string? projectId, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.List(projectId).ToList(),
                IdeaJsonContext.Default.ListWebhookDto)));

        app.MapPost("/api/webhooks", (WebhookCreateRequest request, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.Create(request),
                IdeaJsonContext.Default.WebhookDto, statusCode: StatusCodes.Status201Created)));

        // 模板预设：页面用它预填表单。放在 {id} 之前只是读起来顺，
        // 路由本身不冲突（字面量段优先于参数段，且段数也不同）。
        app.MapGet("/api/webhooks/templates/telegram", () =>
            ApiResults.Run(() => Results.Json(
                new WebhookTemplateDto(TelegramTemplate.Url, TelegramTemplate.BodyTemplate),
                IdeaJsonContext.Default.WebhookTemplateDto)));

        app.MapGet("/api/webhooks/{id}", (string id, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.Get(id), IdeaJsonContext.Default.WebhookDto)));

        app.MapPatch("/api/webhooks/{id}", (string id, WebhookUpdateRequest request, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.Update(id, request), IdeaJsonContext.Default.WebhookDto)));

        app.MapDelete("/api/webhooks/{id}", (string id, WebhookService service) =>
            ApiResults.Run(() =>
            {
                service.Delete(id);
                return Results.NoContent();
            }));

        return app;
    }
}
