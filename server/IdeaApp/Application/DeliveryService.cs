using IdeaApp.Contracts;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Extensions.Options;

namespace IdeaApp.Application;

/// <summary>
/// webhook 投递记录服务：列表与手动重投。
///
/// <para>
/// 手动重投**重发表里已渲染的 payload**，不按当前模板重新渲染 ——
/// 重渲染需要的输入（事件的全量数据）根本没有被存下来，
/// 硬要重渲染就只能渲染出一份空壳。
/// </para>
/// </summary>
public sealed class DeliveryService(
    DeliveryRepository deliveries,
    WebhookRepository webhooks,
    WebhookDeliveryWorker worker,
    IOptions<AppOptions> options)
{
    /// <summary>
    /// 列某个 webhook 的投递记录（最近的在前）。
    /// </summary>
    /// <remarks>
    /// 项目被删掉不影响这里：项目级 webhook 的配置行还在（随项目到期清理才被删），
    /// 投递记录要能查得到 —— 否则用户连「死信」都看不到了。
    /// </remarks>
    public IReadOnlyList<DeliveryDto> ListForWebhook(string webhookId)
    {
        EnsureWebhookExists(webhookId);

        var records = deliveries.ListForWebhook(webhookId, ListLimit);

        var result = new List<DeliveryDto>(records.Count);
        foreach (var record in records)
        {
            result.Add(ToDto(record));
        }

        return result;
    }

    /// <summary>
    /// 手动重投一条投递记录。
    /// </summary>
    /// <remarks>
    /// 重投是**异步**的：把任务投进投递队列就返回，
    /// 请求不会在目标端点前面干等（目标挂了就等于把这一条 HTTP 请求挂死）。
    /// <para>
    /// 重投用的密钥、URL、请求头取**当前**的配置：
    /// 用户修好了填错的 URL，重投就该走新地址。
    /// </para>
    /// <para>
    /// 重投与在途投递可能**同时**改着同一行，所以这里只做「定向的状态改动」，
    /// 不拿读出来的旧快照整行覆盖回去（那会把在途投递刚写的 attempts 抹掉）。
    /// </para>
    /// </remarks>
    public DeliveryDto Retry(string webhookId, string deliveryId)
    {
        var webhook = EnsureWebhookExists(webhookId);

        var record = deliveries.Get(deliveryId);
        if (record is null || record.WebhookId != webhookId)
        {
            throw new ApiException(404, "delivery_not_found", $"投递记录不存在：{deliveryId}");
        }

        // 重发的是**已渲染的 payload**：这里不碰模板。
        var job = new WebhookDeliveryJob(webhook, SyntheticEvent(record), record.Payload);

        // **先入队**：入队失败时这一行压根没被碰过 ——
        // 还是原来的 failed + 原来的 last_error，用户看得到上次失败的原因。
        // （反过来先置 pending 的话，清掉的 last_error 就找不回来了：
        //   入队失败还原成 failed 时，那条记录会**没有错误原因**。）
        if (!worker.Enqueue(job))
        {
            throw new ApiException(503, "delivery_queue_full", "投递队列已满，请稍后重试");
        }

        // 入队成功 → 把这一行置回「投递中」并清掉上次的错误。
        // 带前置条件：只有这一行还是我们读到时的样子（状态与尝试次数都没变）才改 ——
        // 入队之后 worker 可能已经把这次投递投完了，那边的结果比我们的重投更该算数。
        var rearmed = deliveries.MarkPending(record.Id, record.Status, record.Attempts);

        // 没置成（那一行在我们读它之后被改过）就如实返回我们读到的那份：
        // 与其报一个假的「投递中」，不如把这一行现在的样子给出去。
        return ToDto(rearmed
            ? record with
            {
                Status = DeliveryStatuses.Pending,
                LastError = null,
                UpdatedAt = DateTimeOffset.UtcNow
            }
            : record);
    }

    /// <summary>列表最多返回多少条（与每个 webhook 保留的记录数一致）。</summary>
    private int ListLimit => Math.Max(1, options.Value.Webhook.MaxDeliveryRecords);

    /// <summary>取 webhook 配置，不存在时 404。</summary>
    private Webhook EnsureWebhookExists(string webhookId)
    {
        return webhooks.Get(webhookId)
               ?? throw new ApiException(404, "webhook_not_found", $"webhook 不存在：{webhookId}");
    }

    /// <summary>
    /// 用记录里的事件 id 与类型造一个**只有标识**的事件。
    /// </summary>
    /// <remarks>
    /// 它只用来标识「这是哪一次投递」（记录行靠 (webhook, 事件) 找），
    /// 事件的数据本身没有用 —— 请求体早就渲染好了。
    /// </remarks>
    private static IdeaEvent SyntheticEvent(DeliveryRecord record) => new(
        Id: record.EventId,
        Type: record.EventType,
        Timestamp: DateTimeOffset.UtcNow,
        Project: null,
        Idea: null,
        Changed: null,
        MergedFrom: null);

    /// <summary>投递记录 → DTO。</summary>
    private static DeliveryDto ToDto(DeliveryRecord record) => new(
        Id: record.Id,
        WebhookId: record.WebhookId,
        EventId: record.EventId,
        EventType: record.EventType,
        Payload: record.Payload,
        Status: record.Status,
        Attempts: record.Attempts,
        LastError: record.LastError,
        CreatedAt: record.CreatedAt,
        UpdatedAt: record.UpdatedAt);
}
