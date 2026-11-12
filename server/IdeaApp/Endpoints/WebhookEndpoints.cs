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
/// 这一组端点管两件事：**配置面**（增删改查与模板预设）与**投递面**
/// （投递记录列表与手动重投）。
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
                IdeaJsonContext.Default.ListWebhookDto)))
            .Produces<List<WebhookDto>>(StatusCodes.Status200OK);

        app.MapPost("/api/webhooks", (WebhookCreateRequest request, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.Create(request),
                IdeaJsonContext.Default.WebhookDto, statusCode: StatusCodes.Status201Created)))
            .Produces<WebhookDto>(StatusCodes.Status201Created)
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "invalid_webhook_name",
                "invalid_webhook_url",
                "invalid_webhook_scope",
                "invalid_webhook_project",
                "invalid_event_type",
                "invalid_body_template");

        // 模板预设：页面用它预填表单。放在 {id} 之前只是读起来顺，
        // 路由本身不冲突（字面量段优先于参数段，且段数也不同）。
        app.MapGet("/api/webhooks/templates/telegram", () =>
            ApiResults.Run(() => Results.Json(
                new WebhookTemplateDto(TelegramTemplate.Url, TelegramTemplate.BodyTemplate),
                IdeaJsonContext.Default.WebhookTemplateDto)))
            .Produces<WebhookTemplateDto>(StatusCodes.Status200OK);

        app.MapGet("/api/webhooks/{id}", (string id, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.Get(id), IdeaJsonContext.Default.WebhookDto)))
            .Produces<WebhookDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found");

        app.MapPatch("/api/webhooks/{id}", (string id, WebhookUpdateRequest request, WebhookService service) =>
            ApiResults.Run(() => Results.Json(service.Update(id, request), IdeaJsonContext.Default.WebhookDto)))
            .Produces<WebhookDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "invalid_webhook_name",
                "invalid_webhook_url",
                "invalid_event_type",
                "invalid_body_template")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found");

        app.MapDelete("/api/webhooks/{id}", (string id, WebhookService service) =>
            ApiResults.Run(() =>
            {
                service.Delete(id);
                return Results.NoContent();
            }))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found");

        // 投递记录：最近的在前。列表返回裸数组。
        app.MapGet("/api/webhooks/{id}/deliveries", (string id, DeliveryService service) =>
            ApiResults.Run(() => Results.Json(service.ListForWebhook(id).ToList(),
                IdeaJsonContext.Default.ListDeliveryDto)))
            .Produces<List<DeliveryDto>>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found");

        // 手动重投：重发记录里已渲染的 payload（不按当前模板重新渲染）。
        app.MapPost("/api/webhooks/{id}/deliveries/{deliveryId}/retry",
            (string id, string deliveryId, DeliveryService service) =>
                ApiResults.Run(() => Results.Json(service.Retry(id, deliveryId),
                    IdeaJsonContext.Default.DeliveryDto)))
            .Produces<DeliveryDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found", "delivery_not_found")
            .ProducesApiProblem(StatusCodes.Status503ServiceUnavailable, "delivery_queue_full");

        return app;
    }
}
