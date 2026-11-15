using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 老库回填：启动时检查索引版本，对不上就整体重建一次索引。
///
/// 老库是没有索引的（索引表是后加的），不回填的话第一次搜索一条都搜不到。
/// 回填放在启动路径上、同步做完，保证服务起来时搜索就已经可用。
/// </summary>
public static class SearchIndexInitializer
{
    /// <summary>
    /// 启动时调用：版本对得上就什么都不做；对不上就整体重建。
    /// 失败直接抛 —— 索引与业务不一致时，搜索会给出<strong>错误的结果</strong>，
    /// 那比「服务起不来」危险得多。
    /// </summary>
    /// <param name="db">数据库工厂（用来开连接）。</param>
    /// <remarks>
    /// 必须在 <see cref="SqliteDatabase.Initialize"/> 之后调用 ——
    /// 三张索引表与 <c>search_meta</c> 都是它建出来的。
    /// <para>
    /// 整体重建在一个事务里做完：要么索引与业务数据完全对齐，要么什么都没变 ——
    /// 半截的索引比没有索引更糟。
    /// </para>
    /// <para>
    /// 版本号读**两次**：事务外读一次（版本对得上时不该占写锁），
    /// 拿到写锁后再在事务内读一次（期间别的实例可能已经重建过了）。
    /// </para>
    /// <para>
    /// 失败时异常里带着**失败阶段**与**库路径**：起不来的原因不应该让人对着
    /// 「应用起不来」猜（规格 §10.4）。
    /// </para>
    /// </remarks>
    public static void EnsureIndexed(SqliteDatabase db)
    {
        using var connection = db.Open();

        var phase = "读取索引版本";
        try
        {
            // 事务外先读一次：版本对得上就**什么都不做**，也不占写锁 ——
            // 每次启动都 BEGIN IMMEDIATE 会跟正在跑的实例抢锁。
            if (ReadVersion(connection) == SearchIndex.CurrentVersion)
            {
                return;
            }

            // BEGIN IMMEDIATE：拿写锁。Microsoft.Data.Sqlite 的 BeginTransaction()
            // 发的就是 BEGIN IMMEDIATE（实测：它持有写锁时另一个连接写会
            // 「database is locked」；BeginTransaction(deferred: true) 才是 deferred）。
            //
            // 两个实例同时启动时，后来者会卡在这里（Microsoft.Data.Sqlite 的
            // 默认超时 30 秒），等先到的那位提交后继续往下走。
            using var transaction = connection.BeginTransaction();

            // 拿到写锁之后再读一次版本号：**期间别的实例可能已经重建过了**。
            // 已是当前值 → 别的实例刚干完 → 直接 no-op（事务随 using 回滚），
            // 不会重复重建一遍。
            if (ReadVersion(connection, transaction) == SearchIndex.CurrentVersion)
            {
                return;
            }

            phase = "清空索引";
            ClearIndex(connection, transaction);

            phase = "写入标签索引";
            foreach (var name in ReadTagNames(connection, transaction))
            {
                SearchIndex.UpsertTag(connection, transaction, name);
            }

            phase = "写入项目索引";
            foreach (var project in ReadProjects(connection, transaction))
            {
                SearchIndex.UpsertProject(connection, transaction, project.Id, project.Name);
            }

            phase = "写入想法索引";
            foreach (var idea in ReadIdeas(connection, transaction))
            {
                SearchIndex.UpsertIdea(
                    connection, transaction, idea.Id, idea.Title, idea.Content, idea.Tags, idea.ProjectName);
            }

            phase = "写入索引版本";
            WriteVersion(connection, transaction);

            transaction.Commit();
        }
        catch (Exception ex)
        {
            // 事务随 using 一起回滚：失败时索引保持原样，不会留下半截状态。
            throw new InvalidOperationException(
                $"全文索引重建失败（阶段：{phase}；库：{db.DatabasePath}）：{ex.Message}", ex);
        }
    }

    /// <summary>读当前索引版本；没有记录时返回 null（= 从没建过索引）。</summary>
    private static string? ReadVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM search_meta WHERE key = 'index.version';";
        return command.ExecuteScalar() as string;
    }

    /// <summary>清空三张索引表 —— 重建前先清干净，不留上一版的残留行。</summary>
    private static void ClearIndex(SqliteConnection connection, SqliteTransaction transaction)
    {
        // 表名是写死的字面量（不是外部输入），拼进 SQL 没有注入风险。
        foreach (var table in new[] { "idea_fts", "project_fts", "tag_fts" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>写上当前索引版本 —— 这一行是「索引已是最新」的凭据。</summary>
    private static void WriteVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR REPLACE INTO search_meta(key, value) VALUES ('index.version', $version);";
        command.Parameters.AddWithValue("$version", SearchIndex.CurrentVersion);
        command.ExecuteNonQuery();
    }

    /// <summary>读标签字典里的全部标签名。</summary>
    private static List<string> ReadTagNames(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM tag ORDER BY id;";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>读全部项目（id 与名字）。</summary>
    private static List<ProjectRow> ReadProjects(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name FROM project ORDER BY rowid;";

        var projects = new List<ProjectRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(new ProjectRow(reader.GetString(0), reader.GetString(1)));
        }

        return projects;
    }

    /// <summary>读全部想法（连同标签名与项目名）。</summary>
    private static List<IdeaRow> ReadIdeas(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // 一条 SQL 把三样凑齐：想法本身、它的标签名、它所属的项目名。
        //
        // ⚠️ 标签名要**显式排序**再拼：group_concat 不保证拼接顺序
        // （实测会得到「zebra apple mango」这种顺序），顺序不稳的话
        // 同一个库每次回填出来的索引文本都不一样。
        command.CommandText =
            "SELECT i.id, i.title, i.content, p.name, " +
            "(SELECT group_concat(name, ' ') FROM (" +
            "    SELECT t.name FROM idea_tag it JOIN tag t ON t.id = it.tag_id " +
            "    WHERE it.idea_id = i.id ORDER BY t.name COLLATE NOCASE ASC, t.name ASC)) " +
            "FROM idea i JOIN project p ON p.id = i.project_id " +
            "ORDER BY i.rowid;";

        var ideas = new List<IdeaRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tagText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            // group_concat 用单个空格连接标签名，按空格切回标签名列表。
            // 「按空格切开再按空格连回去」是恒等变换（分隔符还是空格），
            // 所以索引里的词元串与直接切标签名原文完全一致。
            var tags = tagText.Length == 0
                ? Array.Empty<string>()
                : tagText.Split(' ');

            ideas.Add(new IdeaRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                tags,
                reader.GetString(3)));
        }

        return ideas;
    }

    /// <summary>回填用的一条项目数据。</summary>
    private sealed record ProjectRow(string Id, string Name);

    /// <summary>回填用的一条想法数据。</summary>
    private sealed record IdeaRow(
        string Id,
        string? Title,
        string Content,
        IReadOnlyList<string> Tags,
        string ProjectName);
}
