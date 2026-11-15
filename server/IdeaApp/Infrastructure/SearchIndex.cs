using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 全文索引的写入辅助：把想法、项目、标签的**可检索文本**切分后写进 FTS5 虚表。
///
/// 索引里存的是<strong>切分后的词元串</strong>（规格 §9.1），不是原文：
/// FTS5 自带的 <c>unicode61</c> 会把一整段中文当成一个 token，
/// 「手冲咖啡的关键」会变成一个搜不到「咖啡」的整词。
/// 所以索引侧自己切词，切好的词元用单个空格连起来 —— unicode61 正好把空格分隔的每段当一个 token。
///
/// 为什么切词规则只有一套（<see cref="TextTokenizer"/>）：查询侧的 MATCH 串也是同一套规则切出来的，
/// 两边只要有一处不同，就会出现「搜得到却高亮不出来」这种极难查的偏差。
///
/// **rowid 映射**（规格 §8.1）：<c>idea_fts.rowid = idea.rowid</c>、<c>project_fts.rowid = project.rowid</c>、
/// <c>tag_fts.rowid = tag.id</c>。这三张表都没有 <c>WITHOUT ROWID</c>，有隐式 rowid。
/// 索引行与业务行靠 rowid 一一对应，删除时才能精确删掉对应的那一行。
///
/// <strong>事务</strong>：索引写必须与业务写在<strong>同一个事务</strong>里（规格 §10.1）。
/// 这里一律显式写 <c>command.Transaction = transaction;</c> ——
/// 不显式写的话，命令到底会不会落在事务里取决于它是什么时候被创建的
/// （<c>CreateCommand</c> 会把连接上**当时**的 pending transaction 复制过去），
/// 这种隐式行为不值得依赖。
/// 显式写的好处是两头都稳：传对了语义与隐式加入一致；
/// 传错了（该传 null 却连着有事务）立刻抛 InvalidOperationException，不会静默走偏。
/// </summary>
public static class SearchIndex
{
    /// <summary>
    /// 当前索引版本。分词规则变了就 +1，启动时会自动重建索引。
    /// </summary>
    /// <remarks>
    /// 版本号记在 <c>search_meta</c> 表的 <c>index.version</c> 键上。
    /// 不建迁移框架：一个 key/value 表就够 —— 以后要改分词规则，
    /// 把这里改成 <c>"2"</c> 就会在下次启动时整体重建一次索引。
    /// </remarks>
    public const string CurrentVersion = "1";

    /// <summary>
    /// 原文 → 索引里存的文本（切分后的词元串，词元间用单个空格）。
    /// </summary>
    /// <param name="text">原文（可为 null —— 想法大多没有标题）。</param>
    /// <returns>词元串；没有词元时返回空串。</returns>
    /// <remarks>
    /// 为什么要这一步：FTS5 的 <c>unicode61</c> 会把一整段中文当成一个 token，
    /// 直接存原文的话「手冲咖啡的关键」是一个 token，搜「咖啡」就搜不到。
    /// 自己切好的词元串里，「咖」「啡」是两个独立 token，短语查询才命中得了。
    /// </remarks>
    public static string ToIndexText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var tokens = TextTokenizer.Tokenize(text);
        return tokens.Count == 0 ? string.Empty : string.Join(" ", tokens);
    }

    /// <summary>
    /// 写入/更新一条想法的索引行（先删后插，保证幂等）。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="ideaId">想法 id。</param>
    /// <param name="title">标题（可为 null）。</param>
    /// <param name="content">正文。</param>
    /// <param name="tags">该想法的全部标签名。</param>
    /// <param name="projectName">所属项目名。</param>
    /// <remarks>
    /// 先删后插而不是 UPDATE：改动内容后不会残留旧 token —— UPDATE 只能改列，
    /// 而「先删干净再插新的」在语义上就是「这一行的索引就是当前内容」。
    /// <para>
    /// ⚠️ <strong>调用方必须保证 idea 行已存在</strong>：本方法不做存在性断言，
    /// idea 行不在时**静默跳过**（不抛异常、不记日志）。
    /// 静默是有代价的 —— 「先调本方法、后插 idea 行」这种顺序错误会让索引**悄悄少一条**，
    /// 没有任何异常、没有任何日志，搜索结果只是少了一行，很难查。
    /// 索引行是给业务行配套的：业务行没了就没有索引行可写，
    /// 为这种顺序问题抛异常反而会把整批写入打断。
    /// </para>
    /// </remarks>
    public static void UpsertIdea(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ideaId,
        string? title,
        string content,
        IReadOnlyList<string> tags,
        string projectName)
    {
        var rowId = FindRowId(connection, transaction, "SELECT rowid FROM idea WHERE id = $id;", ideaId);
        if (rowId is null)
        {
            return;
        }

        Execute(connection, transaction, "DELETE FROM idea_fts WHERE rowid = $rowid;", ("$rowid", rowId));

        Execute(
            connection,
            transaction,
            "INSERT INTO idea_fts(rowid, title, content, tags, project) " +
            "VALUES ($rowId, $title, $content, $tags, $project);",
            ("$rowId", rowId),
            ("$title", ToIndexText(title)),
            ("$content", ToIndexText(content)),
            // 标签名用空格连起来再切词：每个标签名都是独立的一段，
            // 词元不会跨过标签边界（标签 "ab" 与 "cd" 连成 "abcd" 会被当成一个词元）。
            ("$tags", ToIndexText(string.Join(" ", tags))),
            ("$project", ToIndexText(projectName)));
    }

    /// <summary>
    /// 删除一条想法的索引行。<strong>必须在 idea 行还在时调用</strong>（它靠 idea 行取 rowid）。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="ideaId">想法 id。</param>
    /// <remarks>
    /// idea 行已经不在了就什么都不做 —— 索引行如果还在，
    /// 说明它已经成了孤儿（占着 rowid，而 rowid 会被复用），那种情况由整体重建收拾。
    /// <para>
    /// ⚠️ 正因为 rowid 取自 idea 行，**硬删想法的路径必须先调本方法、再删 idea 行**：
    /// 反过来（先删业务行再删索引行）会留下一条孤儿索引行，
    /// 而 idea 表的 rowid 会被复用 —— 新想法可能拿到同一条 rowid，索引就串了（规格 §10.3）。
    /// </para>
    /// </remarks>
    public static void DeleteIdea(SqliteConnection connection, SqliteTransaction? transaction, string ideaId)
    {
        var rowId = FindRowId(connection, transaction, "SELECT rowid FROM idea WHERE id = $id;", ideaId);
        if (rowId is null)
        {
            return;
        }

        Execute(connection, transaction, "DELETE FROM idea_fts WHERE rowid = $rowid;", ("$rowid", rowId));
    }

    /// <summary>
    /// 写入/更新一个项目的索引行。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="projectId">项目 id。</param>
    /// <param name="projectName">项目名。</param>
    /// <remarks>
    /// 项目行不存在时静默跳过，与 <see cref="UpsertIdea"/> 同一口径。
    /// </remarks>
    public static void UpsertProject(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        string projectName)
    {
        var rowId = FindRowId(connection, transaction, "SELECT rowid FROM project WHERE id = $id;", projectId);
        if (rowId is null)
        {
            return;
        }

        Execute(connection, transaction, "DELETE FROM project_fts WHERE rowid = $rowid;", ("$rowid", rowId));

        Execute(
            connection,
            transaction,
            "INSERT INTO project_fts(rowid, name) VALUES ($rowId, $name);",
            ("$rowId", rowId),
            ("$name", ToIndexText(projectName)));
    }

    /// <summary>
    /// 删除一个项目的索引行。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="projectId">项目 id。</param>
    /// <remarks>
    /// 项目行已经不在了就什么都不做，与 <see cref="DeleteIdea"/> 同一口径。
    /// </remarks>
    public static void DeleteProject(SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        var rowId = FindRowId(connection, transaction, "SELECT rowid FROM project WHERE id = $id;", projectId);
        if (rowId is null)
        {
            return;
        }

        Execute(connection, transaction, "DELETE FROM project_fts WHERE rowid = $rowid;", ("$rowid", rowId));
    }

    /// <summary>
    /// 写入一个标签的索引行（标签名）。已存在则忽略。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="tagName">标签名。</param>
    /// <remarks>
    /// <c>tag_fts</c> 的 rowid 必须是 <c>tag.id</c>（标签字典表的整数主键），
    /// 所以这里用 <c>INSERT ... SELECT</c> 从 <c>tag</c> 表里取 id，而不是自己编一个。
    /// <para>
    /// 标签名按大小写不敏感匹配（<c>tag.name UNIQUE COLLATE NOCASE</c>），
    /// 字典里没有这个标签就什么也不写 —— 索引里不该出现字典外的标签。
    /// </para>
    /// </remarks>
    public static void UpsertTag(SqliteConnection connection, SqliteTransaction? transaction, string tagName)
    {
        Execute(
            connection,
            transaction,
            "INSERT OR IGNORE INTO tag_fts(rowid, name) " +
            "SELECT id, $indexText FROM tag WHERE name = $name COLLATE NOCASE;",
            ("$indexText", ToIndexText(tagName)),
            ("$name", tagName));
    }

    /// <summary>
    /// 取业务行的 rowid（索引行靠它与业务行一一对应）。
    /// </summary>
    /// <returns>rowid；行不存在时返回 null。</returns>
    private static long? FindRowId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);

        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>在给定连接（可选事务）上执行一条带参数的语句。</summary>
    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
