namespace IdeaApp.Domain;

/// <summary>
/// 投递记录的状态。
/// </summary>
/// <remarks>
/// 一条记录对应**一次事件投递**（不是每次尝试一行）：
/// 状态从 <see cref="Pending"/> 走到 <see cref="Delivered"/> 或 <see cref="Failed"/>。
/// </remarks>
public static class DeliveryStatuses
{
    /// <summary>投递中：已入队，或正在重试。</summary>
    public const string Pending = "pending";

    /// <summary>投递成功。</summary>
    public const string Delivered = "delivered";

    /// <summary>投递失败：重试到上限仍未成功（死信）。</summary>
    public const string Failed = "failed";
}

/// <summary>
/// 一次 webhook 投递的记录。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Payload"/> 存的是**已经渲染好的请求体**：手动重投重发的就是它
/// —— 重渲染需要的输入（事件的全量数据）根本没有被存下来。
/// </para>
/// <para>
/// <see cref="Attempts"/> 的口径是**总尝试次数（含首次）**：首次投递记 1，
/// 失败重试继续累加。手动重投也在这同一行上累加。
/// </para>
/// </remarks>
public sealed record DeliveryRecord(string Id, string WebhookId, string EventId, string EventType,
                                    string Payload, string Status, int Attempts, string? LastError,
                                    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// 一次待投递的任务：目标 webhook + 触发它的事件 + 渲染好的请求体。
/// </summary>
/// <remarks>
/// 渲染在**入队之前**就做完：队列里放的是成品，不是模板 ——
/// 排队期间模板被改了，也不该让这一次的请求体跟着变。
/// </remarks>
public sealed record WebhookDeliveryJob(Webhook Webhook, IdeaEvent Event, string Payload);
