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
///
/// <para>
/// <b>并发安全</b>：同一行会被两条路径同时改 ——
/// 后台 worker 的投递尝试，和用户点的手动重投。
/// 所以这里**没有**「读出来算好再整行覆盖写回」的方法：
/// 计数（<c>attempts = attempts + 1</c>）与状态跃迁都在 SQL 侧原子完成，
/// 状态跃迁还带前置条件（只允许 <c>pending / failed → delivered</c>，
/// 以及「行还是我们读到时的样子」才置 pending）。
/// 拿旧快照整行覆盖回去会把旁边那次写入抹掉：
/// 「记录只记了 2 次尝试，实际发了 3 次」，或者「已经成功的投递被改回失败」。
/// </para>
/// </summary>
public sealed class DeliveryRepository(SqliteDatabase db)
{
    private const string SelectColumns =
        "id, webhook_id, event_id, event_type, payload, status, attempts, last_error, " +
        "created_at, updated_at";

    /// <summary>
    /// 记一条投递记录（同一个 (webhook, 事件) 只会有**一行**）。
    /// </summary>
    /// <remarks>
    /// 若同一个 (webhook, 事件) 已经有记录，**返回那一条**而不是再插一行：
    /// 手动重投走的正是这条路 —— 它重投的是同一次事件投递，
    /// <c>attempts</c> 要在**同一行**上继续累加（spec §12.4）。
    /// <para>
    /// 「一行」这件事由唯一索引 <c>ix_delivery_event</c> 兜底：
    /// 手动重投与在途投递撞在一起时，光靠代码里的「先查后插」会插出两行
    /// （查的时候都还没插上）。这里用 <c>INSERT OR IGNORE</c> +
    /// 唯一索引把并发下的重复行挡在库外。
    /// </para>
    /// <para>
    /// 新记录的状态是 <see cref="DeliveryStatuses.Pending"/>：
    /// 投递还没成功之前它就是「投递中」。
    /// </para>
    /// </remarks>
    public DeliveryRecord Insert(WebhookDeliveryJob job)
    {
        var now = DateTimeOffset.UtcNow;
        var id = MonotonicGuid.NewUuid7().ToString("N");

        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT OR IGNORE INTO webhook_delivery(id, webhook_id, event_id, event_type, payload, status, " +
                "attempts, last_error, created_at, updated_at) " +
                "VALUES ($id, $webhookId, $eventId, $eventType, $payload, $status, " +
                "$attempts, $lastError, $createdAt, $updatedAt);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$webhookId", job.Webhook.Id);
            command.Parameters.AddWithValue("$eventId", job.Event.Id);
            command.Parameters.AddWithValue("$eventType", job.Event.Type);
            command.Parameters.AddWithValue("$payload", job.Payload);
            command.Parameters.AddWithValue("$status", DeliveryStatuses.Pending);
            command.Parameters.AddWithValue("$attempts", 0);
            command.Parameters.AddWithValue("$lastError", DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", SqliteDatabase.FormatTimestamp(now));
            command.Parameters.AddWithValue("$updatedAt", SqliteDatabase.FormatTimestamp(now));
            command.ExecuteNonQuery();
        }

        // 读回：多数时候是刚插进去的那一行；
        // 但同一个 (webhook, 事件) 已经有行时（手动重投走的就是这条路），
        // INSERT 被唯一索引挡下来了，读回来的是**原来那一行** ——
        // attempts 要在它上面继续累加。
        return FindByEvent(job.Webhook.Id, job.Event.Id)
               ?? throw new InvalidOperationException(
                   $"投递记录插进去又读不回来：webhook {job.Webhook.Id} / 事件 {job.Event.Id}");
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

    /// <summary>
    /// 记一次投递尝试的结果：<c>attempts</c> 加 1，并按结果落状态。
    /// </summary>
    /// <param name="id">投递记录 id。</param>
    /// <param name="succeeded">这次尝试是不是成功了。</param>
    /// <param name="lastError">失败原因（成功时传 <c>null</c>）。</param>
    /// <remarks>
    /// 这一步是**原子**的：计数与状态跃迁在同一条 UPDATE 里完成，
    /// 中间没有「读出来算好再写回」的窗口。
    /// <list type="bullet">
    /// <item><c>attempts</c> **总是**加 1 —— 每真的发出去一次 HTTP 就该记一次，
    /// 哪怕这一行已经是终态（那次请求确实发出去了，不能不认账）。</item>
    /// <item>成功：<c>pending / failed → delivered</c>。
    /// <c>failed</c> 也让成功改写是**必须的**：手动重投一条死信时，
    /// 这一行在重投那一刻可能还是 <c>failed</c>（重投要先入队、入队之后才置 pending，
    /// worker 可能已经把这次投递投完了）。这次投递**确实成功了**，
    /// 记录就该是 delivered —— 否则用户重投成功了却还是看到一条 failed。</item>
    /// <item>失败：状态不动（还是 pending，等着重试；还是 failed，就是死信），
    /// 但 <c>last_error</c> 会更新成**这次**的错误。</item>
    /// <item>已经是 <c>delivered</c> 的行：什么都不动 ——
    /// 后到的失败不能把一次成功的投递改判成失败。</item>
    /// </list>
    /// </remarks>
    public void RecordAttempt(string id, bool succeeded, string? lastError)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();

        // CASE 里的 status 取的是**这一行的旧值**（UPDATE 的右值一律按更新前的行求值），
        // 所以「还是 pending / failed 才改状态」这条前置条件是原子的。
        command.CommandText =
            "UPDATE webhook_delivery SET " +
            "attempts = attempts + 1, " +
            "status = CASE " +
            "WHEN $succeeded = 1 AND status IN ($pending, $failed) THEN $delivered " +
            "ELSE status END, " +
            "last_error = CASE " +
            "WHEN $succeeded = 1 AND status IN ($pending, $failed) THEN NULL " +
            "WHEN $succeeded = 0 AND status IN ($pending, $failed) THEN $lastError " +
            "ELSE last_error END, " +
            "updated_at = $updatedAt " +
            "WHERE id = $id;";
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$pending", DeliveryStatuses.Pending);
        command.Parameters.AddWithValue("$failed", DeliveryStatuses.Failed);
        command.Parameters.AddWithValue("$delivered", DeliveryStatuses.Delivered);
        command.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt",
            SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 把还在「投递中」的投递记录标成失败（死信）。
    /// </summary>
    /// <returns>true = 这一行被标成失败了；false = 它已经是终态，没动。</returns>
    /// <remarks>
    /// 已经是终态的行**不动**：重试到上限的那次失败是「后到的旧结果」，
    /// 它不能把别处已经成功（delivered）的那次投递改判成失败。
    /// </remarks>
    public bool MarkFailed(string id)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE webhook_delivery SET status = $failed, updated_at = $updatedAt " +
            "WHERE id = $id AND status = $pending;";
        command.Parameters.AddWithValue("$failed", DeliveryStatuses.Failed);
        command.Parameters.AddWithValue("$pending", DeliveryStatuses.Pending);
        command.Parameters.AddWithValue("$updatedAt",
            SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 手动重投：把记录置成「投递中」，并**清掉上次的错误**。
    /// </summary>
    /// <param name="id">投递记录 id。</param>
    /// <param name="expectedStatus">
    /// 前置条件：这一行现在得是**这个状态**（调用方读到的那份快照里的状态）。
    /// </param>
    /// <param name="expectedAttempts">
    /// 前置条件：这一行的尝试次数得是**这个数**。
    /// </param>
    /// <returns>true = 置成「投递中」了；false = 这一行在调用方读它之后被别人改过，没动。</returns>
    /// <remarks>
    /// <para>
    /// 清错误是语义要求：一条「投递中」却挂着上次那个错的记录会误导用户
    /// （看着像还没重投成功、又要再点一次）。
    /// </para>
    /// <para>
    /// <b>前置条件是必须的</b>：入队之后、置 pending 之前，
    /// worker 可能已经把这次投递投完了（写成 delivered / failed）。
    /// 这时候再无条件置 pending，就是把**终态改回投递中** ——
    /// 那一行会永远停在「投递中」，因为已经没有投递在跑了。
    /// </para>
    /// <para>
    /// 前置条件为什么是「状态 + 尝试次数」两个：
    /// <c>attempts</c> 只增不减，每记一次尝试就 +1 ——
    /// 它变了就说明「这一行在我们读它之后被人动过」。
    /// 只看状态不够：一次**成功**的投递落在已经是 <c>delivered</c> 的行上时，
    /// 状态不变、只有 attempts 变了 —— 那一次投递同样已经发生过了。
    /// </para>
    /// </remarks>
    public bool MarkPending(string id, string expectedStatus, int expectedAttempts)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE webhook_delivery SET status = $pending, last_error = NULL, updated_at = $updatedAt " +
            "WHERE id = $id AND status = $expectedStatus AND attempts = $expectedAttempts;";
        command.Parameters.AddWithValue("$pending", DeliveryStatuses.Pending);
        command.Parameters.AddWithValue("$updatedAt",
            SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expectedStatus", expectedStatus);
        command.Parameters.AddWithValue("$expectedAttempts", expectedAttempts);

        return command.ExecuteNonQuery() > 0;
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
