using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// webhook 投递记录仓储。手写 SQL，每次操作自己开连接、自己关连接。
///
/// <para>
/// 一条记录对应**一次事件投递**（一个 webhook 收到一个事件），
/// 不是每次尝试一行 —— 重试与手动重投都在**同一行**上更新，
/// <c>attempts</c> 在这一行上累加。
/// </para>
/// </summary>
public sealed class DeliveryRepository(SqliteDatabase db)
{
    private const string SelectColumns =
        "id, webhook_id, event_id, event_type, payload, status, attempts, last_error, " +
        "created_at, updated_at";

    /// <summary>
    /// 记一条投递记录。
    /// </summary>
    /// <remarks>
    /// 若同一个 (webhook, 事件) 已经有记录，**返回那一条**而不是再插一行：
    /// 手动重投走的正是这条路 —— 它重投的是同一次事件投递，
    /// <c>attempts</c> 要在**同一行**上继续累加（spec §12.4）。
    /// <para>
    /// 新记录的状态是 <see cref="DeliveryStatuses.Pending"/>：
    /// 投递还没成功之前它就是「投递中」。
    /// </para>
    /// </remarks>
    public DeliveryRecord Insert(WebhookDeliveryJob job)
    {
        var existing = FindByEvent(job.Webhook.Id, job.Event.Id);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var record = new DeliveryRecord(
            Id: MonotonicGuid.NewUuid7().ToString("N"),
            WebhookId: job.Webhook.Id,
            EventId: job.Event.Id,
            EventType: job.Event.Type,
            Payload: job.Payload,
            Status: DeliveryStatuses.Pending,
            Attempts: 0,
            LastError: null,
            CreatedAt: now,
            UpdatedAt: now);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO webhook_delivery(id, webhook_id, event_id, event_type, payload, status, " +
            "attempts, last_error, created_at, updated_at) " +
            "VALUES ($id, $webhookId, $eventId, $eventType, $payload, $status, " +
            "$attempts, $lastError, $createdAt, $updatedAt);";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$webhookId", record.WebhookId);
        command.Parameters.AddWithValue("$eventId", record.EventId);
        command.Parameters.AddWithValue("$eventType", record.EventType);
        command.Parameters.AddWithValue("$payload", record.Payload);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$attempts", record.Attempts);
        command.Parameters.AddWithValue("$lastError", (object?)record.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteDatabase.FormatTimestamp(record.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", SqliteDatabase.FormatTimestamp(record.UpdatedAt));
        command.ExecuteNonQuery();

        return record;
    }

    /// <summary>按 id 取一条投递记录，找不到返回 <c>null</c>。</summary>
    public DeliveryRecord? Get(string id)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM webhook_delivery WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>更新一条投递记录（重试与手动重投都走这里）。</summary>
    public void Update(DeliveryRecord record)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE webhook_delivery SET payload = $payload, status = $status, " +
            "attempts = $attempts, last_error = $lastError, updated_at = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$payload", record.Payload);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$attempts", record.Attempts);
        command.Parameters.AddWithValue("$lastError", (object?)record.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", SqliteDatabase.FormatTimestamp(record.UpdatedAt));
        command.Parameters.AddWithValue("$id", record.Id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 列某个 webhook 的投递记录，**最近的在前**。
    /// </summary>
    /// <param name="webhookId">webhook 的 id。</param>
    /// <param name="limit">最多返回多少条。</param>
    public IReadOnlyList<DeliveryRecord> ListForWebhook(string webhookId, int limit)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM webhook_delivery WHERE webhook_id = $webhookId " +
            "ORDER BY created_at DESC, id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$webhookId", webhookId);
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));

        var records = new List<DeliveryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(Read(reader));
        }

        return records;
    }

    /// <summary>
    /// 只保留某个 webhook 最近的 <paramref name="keep"/> 条记录，多余的删最旧的。
    /// </summary>
    /// <remarks>
    /// 投递记录会一直长（每个事件一条），不裁的话库会无限膨胀；
    /// 页面只看最近的若干条，旧的没有留存价值。
    /// </remarks>
    public void Trim(string webhookId, int keep)
    {
        if (keep <= 0)
        {
            return;
        }

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM webhook_delivery WHERE webhook_id = $webhookId AND id NOT IN (" +
            "SELECT id FROM webhook_delivery WHERE webhook_id = $webhookId " +
            "ORDER BY created_at DESC, id DESC LIMIT $keep);";
        command.Parameters.AddWithValue("$webhookId", webhookId);
        command.Parameters.AddWithValue("$keep", keep);
        command.ExecuteNonQuery();
    }

    /// <summary>按 (webhook, 事件) 找那条投递记录。</summary>
    private DeliveryRecord? FindByEvent(string webhookId, string eventId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM webhook_delivery WHERE webhook_id = $webhookId " +
            "AND event_id = $eventId LIMIT 1;";
        command.Parameters.AddWithValue("$webhookId", webhookId);
        command.Parameters.AddWithValue("$eventId", eventId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static DeliveryRecord Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        WebhookId: reader.GetString(1),
        EventId: reader.GetString(2),
        EventType: reader.GetString(3),
        Payload: reader.GetString(4),
        Status: reader.GetString(5),
        Attempts: reader.GetInt32(6),
        LastError: reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(8)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(9)));
}
