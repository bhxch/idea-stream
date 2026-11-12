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

        // 先把状态改成「投递中」再入队：入队之后 worker 才会改它，
        // 顺序反了的话 worker 更快、状态会被改回「投递中」卡住。
        // 同时清掉上次的错误 —— 「投递中」却挂着上次那个错，看的人会误解。
        deliveries.MarkPending(record.Id);

        // 重发的是**已渲染的 payload**：这里不碰模板。
        var job = new WebhookDeliveryJob(webhook, SyntheticEvent(record), record.Payload);

        if (!worker.Enqueue(job))
        {
            // 队列满了，这次重投没排上：把状态改回去，
            // 别让这条记录永远停在「投递中」（根本没有投递在跑）。
            // 只改 status —— 这一行上可能已经有在途投递写进去的新 attempts / last_error，
            // 拿旧快照整行覆盖回去会把它们抹掉。
            deliveries.RestoreStatus(record.Id, record.Status);

            throw new ApiException(503, "delivery_queue_full", "投递队列已满，请稍后重试");
        }

        return ToDto(record with
        {
            Status = DeliveryStatuses.Pending,
            LastError = null,
            UpdatedAt = DateTimeOffset.UtcNow
        });
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
