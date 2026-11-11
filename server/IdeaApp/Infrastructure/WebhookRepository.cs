using System.Text.Json;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// webhook 配置仓储。手写 SQL，每次操作自己开连接、自己关连接。
///
/// <para>
/// <c>headers_json</c> 与 <c>events_json</c> 两列存的是 JSON 字符串：
/// 序列化与反序列化都走 <see cref="IdeaJsonContext"/>（Native AOT 下没有反射式序列化）。
/// </para>
///
/// <para>
/// <c>enabled</c> 一律**显式写入**：列的默认值是 1，而全局作用域的 webhook
/// 创建时必须禁用 —— 靠列默认值表达「默认禁用」是不行的。
/// </para>
/// </summary>
public sealed class WebhookRepository(SqliteDatabase db)
{
    private const string SelectColumns =
        "id, name, url, method, headers_json, body_template, events_json, " +
        "scope, project_id, secret, enabled, created_at, updated_at";

    /// <summary>
    /// 列出 webhook 配置。
    /// </summary>
    /// <param name="projectId">
    /// 空 = 只列**全局作用域**的 webhook；给了项目 id = 只列绑定到该项目的 webhook。
    /// </param>
    /// <remarks>
    /// 两种作用域不混在一起列：全局页与项目页各自只关心自己那批，
    /// 混着列的话前端还得自己按 scope 过滤一遍。
    /// </remarks>
    public IReadOnlyList<Webhook> List(string? projectId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            command.CommandText =
                $"SELECT {SelectColumns} FROM webhook WHERE scope = 'global' ORDER BY created_at ASC, id ASC;";
        }
        else
        {
            command.CommandText =
                $"SELECT {SelectColumns} FROM webhook WHERE scope = 'project' AND project_id = $projectId " +
                "ORDER BY created_at ASC, id ASC;";
            command.Parameters.AddWithValue("$projectId", projectId);
        }

        var webhooks = new List<Webhook>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            webhooks.Add(Read(reader));
        }

        return webhooks;
    }

    /// <summary>按 id 取一个 webhook 配置，找不到返回 <c>null</c>。</summary>
    public Webhook? Get(string id)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM webhook WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>写入一个 webhook 配置，返回落库后的那一条（时间戳以库里的为准）。</summary>
    public Webhook Insert(Webhook webhook)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO webhook(id, name, url, method, headers_json, body_template, events_json, " +
                "scope, project_id, secret, enabled, created_at, updated_at) " +
                "VALUES ($id, $name, $url, $method, $headers, $body, $events, " +
                "$scope, $projectId, $secret, $enabled, $createdAt, $updatedAt);";
            AddParameters(command, webhook);
            command.Parameters.AddWithValue("$id", webhook.Id);
            command.ExecuteNonQuery();
        }

        var stored = ReadById(connection, transaction, webhook.Id)
                     ?? throw new InvalidOperationException($"写入后读不到 webhook：{webhook.Id}");

        transaction.Commit();
        return stored;
    }

    /// <summary>
    /// 更新一个 webhook 配置，返回更新后的那一条。
    /// </summary>
    /// <remarks>
    /// 作用域与所属项目**不可改**：改了等于换了一个 webhook（投递时的匹配规则也变了）。
    /// </remarks>
    public Webhook Update(Webhook webhook)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE webhook SET name = $name, url = $url, method = $method, " +
                "headers_json = $headers, body_template = $body, events_json = $events, " +
                "secret = $secret, enabled = $enabled, updated_at = $updatedAt WHERE id = $id;";
            AddParameters(command, webhook);
            command.Parameters.AddWithValue("$id", webhook.Id);
            command.ExecuteNonQuery();
        }

        var stored = ReadById(connection, transaction, webhook.Id)
                     ?? throw new InvalidOperationException($"更新后读不到 webhook：{webhook.Id}");

        transaction.Commit();
        return stored;
    }

    /// <summary>删掉一个 webhook 配置（硬删，没有回收站：项目级 webhook 随项目隐式失效）。</summary>
    public void Delete(string id)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM webhook WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 列出**已启用**的 webhook 配置：一个事件该投给哪些 webhook。
    /// </summary>
    /// <param name="projectId">
    /// 事件所属的项目 id（没有项目的事件传 null）。
    /// </param>
    /// <remarks>
    /// 命中规则（见设计文档 §12.1）：全局且启用的 webhook 全部命中，
    /// 外加绑定到该事件所属项目且启用的 webhook。两者互不影响、各自独立投递。
    /// </remarks>
    public IReadOnlyList<Webhook> ListEnabledFor(string? projectId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            command.CommandText =
                $"SELECT {SelectColumns} FROM webhook WHERE enabled = 1 AND scope = 'global' " +
                "ORDER BY created_at ASC, id ASC;";
        }
        else
        {
            command.CommandText =
                $"SELECT {SelectColumns} FROM webhook WHERE enabled = 1 " +
                "AND (scope = 'global' OR project_id = $projectId) ORDER BY created_at ASC, id ASC;";
            command.Parameters.AddWithValue("$projectId", projectId);
        }

        var webhooks = new List<Webhook>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            webhooks.Add(Read(reader));
        }

        return webhooks;
    }

    /// <summary>绑上写入用的参数（INSERT 与 UPDATE 共用一套列名）。</summary>
    private static void AddParameters(SqliteCommand command, Webhook webhook)
    {
        command.Parameters.AddWithValue("$name", webhook.Name);
        command.Parameters.AddWithValue("$url", webhook.Url);
        command.Parameters.AddWithValue("$method", webhook.Method);
        command.Parameters.AddWithValue("$headers",
            JsonSerializer.Serialize(webhook.Headers, IdeaJsonContext.Default.DictionaryStringString));
        command.Parameters.AddWithValue("$body", webhook.BodyTemplate);
        command.Parameters.AddWithValue("$events",
            JsonSerializer.Serialize(webhook.Events, IdeaJsonContext.Default.StringArray));
        command.Parameters.AddWithValue("$scope", webhook.Scope);
        command.Parameters.AddWithValue("$projectId", (object?)webhook.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", (object?)webhook.Secret ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", webhook.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", SqliteDatabase.FormatTimestamp(webhook.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", SqliteDatabase.FormatTimestamp(webhook.UpdatedAt));
    }

    /// <summary>在给定连接（可带事务）里按 id 读一行。</summary>
    private static Webhook? ReadById(
        SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM webhook WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Webhook Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Name: reader.GetString(1),
        Url: reader.GetString(2),
        Method: reader.GetString(3),
        Headers: JsonSerializer.Deserialize(
            reader.GetString(4), IdeaJsonContext.Default.DictionaryStringString) ?? new(),
        BodyTemplate: reader.GetString(5),
        Events: JsonSerializer.Deserialize(
            reader.GetString(6), IdeaJsonContext.Default.StringArray) ?? [],
        Scope: reader.GetString(7),
        ProjectId: reader.IsDBNull(8) ? null : reader.GetString(8),
        Secret: reader.IsDBNull(9) ? null : reader.GetString(9),
        Enabled: reader.GetInt64(10) != 0,
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(11)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(12)));
}
