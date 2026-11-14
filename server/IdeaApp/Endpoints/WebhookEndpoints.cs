using IdeaApp.Application;
using IdeaApp.Contracts;
using IdeaApp.Infrastructure;
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
///
/// <para>
/// 凭据默认遮蔽：配置面里的 url（路径段）与 headers（值）是凭据，
/// 响应里默认是遮蔽版，规则见 <see cref="WebhookMasking"/>。
/// 要完整值得显式 <c>?unmask=true</c>（页面用）；写接口的响应一律遮蔽。
/// </para>
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // 列表：裸数组。不带 projectId 时只列全局作用域的，
        // 带了 projectId 时只列绑定到该项目的。
        //
        // 凭据默认遮蔽（url 的路径段与 headers 的值）：命令层删掉之后，
        // 裸 curl 的响应会原样进 agent 的上下文与日志，而 bot token 就在 url 里。
        // 要完整值得显式 ?unmask=true（页面用）。
        app.MapGet("/api/webhooks", (string? projectId, bool? unmask, WebhookService service) =>
            ApiResults.Run(() =>
            {
                var webhooks = service.List(projectId).ToList();

                if (unmask != true)
                {
                    webhooks = webhooks.Select(WebhookMasking.Mask).ToList();
                }

                return Results.Json(webhooks, IdeaJsonContext.Default.ListWebhookDto);
            }))
            .Produces<List<WebhookDto>>(StatusCodes.Status200OK);

        // 写接口的响应一律遮蔽：回执里不带完整凭据。
        app.MapPost("/api/webhooks", (WebhookCreateRequest request, WebhookService service) =>
            ApiResults.Run(() => Results.Json(WebhookMasking.Mask(service.Create(request)),
                IdeaJsonContext.Default.WebhookDto, statusCode: StatusCodes.Status201Created)))
            .RejectsNonJsonBodyWithProblemDetails()
            .Produces<WebhookDto>(StatusCodes.Status201Created)
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "invalid_webhook_name",
                "invalid_webhook_url",
                "invalid_webhook_scope",
                "invalid_webhook_project",
                "invalid_event_type",
                "invalid_body_template",
                "invalid_request_body")
            .ProducesApiProblem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        // 模板预设：页面用它预填表单。放在 {id} 之前只是读起来顺，
        // 路由本身不冲突（字面量段优先于参数段，且段数也不同）。
        //
        // 不遮蔽：url 里是 <BOT_TOKEN> 占位符，不是凭据。
        app.MapGet("/api/webhooks/templates/telegram", () =>
            ApiResults.Run(() => Results.Json(
                new WebhookTemplateDto(TelegramTemplate.Url, TelegramTemplate.BodyTemplate),
                IdeaJsonContext.Default.WebhookTemplateDto)))
            .Produces<WebhookTemplateDto>(StatusCodes.Status200OK);

        app.MapGet("/api/webhooks/{id}", (string id, bool? unmask, WebhookService service) =>
            ApiResults.Run(() =>
            {
                var webhook = service.Get(id);

                return Results.Json(unmask == true ? webhook : WebhookMasking.Mask(webhook),
                    IdeaJsonContext.Default.WebhookDto);
            }))
            .Produces<WebhookDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found");

        // 改配置的响应也一律遮蔽：回执是遮蔽版不会把配置改坏 ——
        // url 是三态的（传 null = 不改），库里存的原值不在这条路径上。
        app.MapPatch("/api/webhooks/{id}", (string id, WebhookUpdateRequest request, WebhookService service) =>
            ApiResults.Run(() => Results.Json(WebhookMasking.Mask(service.Update(id, request)),
                IdeaJsonContext.Default.WebhookDto)))
            .RejectsNonJsonBodyWithProblemDetails()
            .Produces<WebhookDto>(StatusCodes.Status200OK)
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "invalid_webhook_name",
                "invalid_webhook_url",
                "invalid_event_type",
                "invalid_body_template",
                "invalid_request_body")
            .ProducesApiProblem(StatusCodes.Status404NotFound, "webhook_not_found")
            .ProducesApiProblem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

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
