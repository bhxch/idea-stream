using System.Text;
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
    /// 回填路径里标签名之间的分隔符：ASCII 的 SOH（<c>U+0001</c>）。
    /// </summary>
    /// <remarks>
    /// 为什么不用空格：<c>group_concat</c> 把标签名拼成一段文本，
    /// 再按空格切回标签名列表时，**标签名本身含空格**就会被切错
    /// （标签 <c>["a b", "c"]</c> 会被切成 <c>["a", "b", "c"]</c>）。
    /// 用一个标签名里几乎不可能出现的控制字符当分隔符，拆分就没有歧义。
    /// <para>
    /// ⚠️ 它只影响**回填路径内部**怎么把标签名拆回列表；
    /// 写进 <c>idea_fts</c> 的仍然是「标签名按名称排序后用单个空格连起来」的文本，
    /// 与逐条 <see cref="UpsertIdea"/> 写出来的逐字节相同（见 <see cref="InsertIdeaRows"/>）。
    /// </para>
    /// </remarks>
    private const char TagSeparator = '\u0001';

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
    /// <returns>true = 已写入；false = idea 行不存在，跳过了。</returns>
    /// <remarks>
    /// 先删后插而不是 UPDATE：改动内容后不会残留旧 token —— UPDATE 只能改列，
    /// 而「先删干净再插新的」在语义上就是「这一行的索引就是当前内容」。
    /// <para>
    /// ⚠️ <strong>调用方必须保证 idea 行已存在</strong>：本方法不做存在性断言，
    /// idea 行不在时**跳过**（不抛异常），跳过与否通过返回值告知调用方。
    /// 跳过不该抛 —— 索引行是给业务行配套的，回填这类批处理里业务行可能真的不在，
    /// 为顺序问题打断整批写入得不偿失；但跳过不该没有痕迹 ——
    /// 「先调本方法、后插 idea 行」这种顺序错误会让索引**悄悄少一条**，
    /// 搜索结果只是少了一行，极难排查。调用方（仓储层）拿返回值把它记成一条 warning。
    /// </para>
    /// </remarks>
    public static bool UpsertIdea(
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
            return false;
        }

        Execute(connection, transaction, "DELETE FROM idea_fts WHERE rowid = $rowid;", ("$rowid", rowId));

        InsertIdeaRow(connection, transaction, rowId.Value, title, content, tags, projectName);
        return true;
    }

    /// <summary>把一行想法索引数据写进 <c>idea_fts</c>（调用方负责先删掉旧行）。</summary>
    private static void InsertIdeaRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long rowId,
        string? title,
        string content,
        IReadOnlyList<string> tags,
        string projectName)
    {
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
    /// 重建一个项目下**全部**想法的索引行（含回收站里的）。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="projectId">项目 id。</param>
    /// <remarks>
    /// 项目改名后「项目名」这一索引列整批失效，只能重写 —— 这件事归在索引这边，
    /// 而不是放在某个业务仓储里（哪个仓储都不该认识另一家）。
    /// <para>
    /// ⚠️ <strong>调用方必须已经把项目行改成新名字</strong>：项目名从库里读
    /// （<c>p.name</c>），改名先行才能读到新的。
    /// </para>
    /// </remarks>
    public static void ReindexProjectIdeas(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId)
    {
        // 先物化、后写入：一边开着 reader 一边改表不是值得依赖的行为。
        var rows = LoadIdeaRows(connection, transaction, projectId);

        // 旧索引行按同一个项目范围整体删掉 —— rowid 来自 idea 表，
        // 范围与上面读到的完全一致（都不筛 deleted_at：软删的想法照样在索引里）。
        //
        // ⚠️ **删除范围与写回范围必须一致**，否则索引行会静默消失：
        // 删除范围是「project_id = 该项目的**全部**想法」（不 JOIN project），
        // 所以写回范围必须是同一集合 —— 写回用的是 LEFT JOIN（见 LoadIdeaRows）。
        // 之前写回用的是 INNER JOIN：想法的项目行不在了（项目被硬删而想法还在）时，
        // 索引行被删掉却不会写回来 —— 那条想法从此搜不到，也没有任何报错。
        Execute(
            connection,
            transaction,
            "DELETE FROM idea_fts WHERE rowid IN (SELECT rowid FROM idea WHERE project_id = $projectId);",
            ("$projectId", projectId));

        InsertIdeaRows(connection, transaction, rows);
    }

    /// <summary>
    /// 把**全部**想法的索引行整体写入（整体重建专用；调用方负责先清空 <c>idea_fts</c>）。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <remarks>
    /// 回填（<see cref="SearchIndexInitializer"/>）用整批写入而不是逐条
    /// <see cref="UpsertIdea"/>：逐条是「查 rowid + 删 + 插」四次往返，
    /// 几千条想法就是上万条语句；整批是「一条读全 + 分块多行 INSERT」，
    /// 行数再多也只有几十条语句。
    /// <para>
    /// rowid 与排序后拼接的标签名都从同一条查询里拿，排序口径与增量写一致 ——
    /// 回填写出的索引文本必须与增量写写的逐字节一致，跨两个标签的短语查询才不失灵。
    /// </para>
    /// </remarks>
    public static void InsertAllIdeaRows(SqliteConnection connection, SqliteTransaction? transaction)
        => InsertIdeaRows(connection, transaction, LoadIdeaRows(connection, transaction, projectId: null));

    /// <summary>
    /// 从业务表里读出想法的索引行数据（一条 SQL 凑齐五样，rowid 直接从查询里拿）。
    /// </summary>
    /// <param name="projectId">限定项目；null = 全部想法。</param>
    /// <remarks>
    /// ⚠️ 标签名要**显式排序**再拼：group_concat 不保证拼接顺序
    /// （实测会得到「zebra apple mango」这种顺序），顺序不稳的话
    /// 同一份数据每次重建出来的索引文本都不一样。
    /// 排序口径与仓储层读标签的那份一致（<c>COLLATE NOCASE ASC</c>）。
    /// <para>
    /// ⚠️ <strong>项目名用 LEFT JOIN 取</strong>：删除范围是「project_id = 该项目的全部想法」
    /// （不 JOIN project，见 <see cref="ReindexProjectIdeas"/>），写回范围必须与它一致。
    /// 用 INNER JOIN 时，「项目行没了但想法还在」的念头会从写回范围里消失 ——
    /// 索引行被删掉却不会写回来（静默丢失）。LEFT JOIN 下这种想法照样写回，
    /// 项目名列写空串（库里查不到的项目名不写进索引）。
    /// </para>
    /// </remarks>
    internal static List<IdeaIndexRow> LoadIdeaRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? projectId)
    {
        var sql =
            "SELECT i.rowid, i.title, i.content, p.name, " +
            // 标签名之间用 SOH（char(1)）连接：标签名本身含空格时，
            // 按空格拆分会把它拆错（["a b", "c"] → ["a", "b", "c"]）。
            "(SELECT group_concat(name, char(1)) FROM (" +
            "    SELECT t.name FROM idea_tag it JOIN tag t ON t.id = it.tag_id " +
            "    WHERE it.idea_id = i.id ORDER BY t.name COLLATE NOCASE ASC, t.name ASC)) " +
            "FROM idea i LEFT JOIN project p ON p.id = i.project_id";
        if (projectId is not null)
        {
            sql += " WHERE i.project_id = $projectId";
        }

        sql += " ORDER BY i.rowid;";

        var rows = new List<IdeaIndexRow>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            if (projectId is not null)
            {
                command.Parameters.AddWithValue("$projectId", projectId);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tagText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

                // 按 SOH 拆回标签名列表：分隔符不会出现在标签名里，拆出来就是真标签名。
                var tags = tagText.Length == 0
                    ? Array.Empty<string>()
                    : tagText.Split(TagSeparator);

                rows.Add(new IdeaIndexRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    tags,
                    // 项目行不在了（LEFT JOIN 出来是 NULL）→ 项目名列写空串。
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
            }
        }

        return rows;
    }

    /// <summary>
    /// 把一批想法索引行分块写入（多行 VALUES，块间参数数量有硬上限）。
    /// </summary>
    /// <remarks>
    /// 每块 199 行 × 5 个参数 = 995 个参数 —— 连 SQLite **最老的**变量上限（999）都放得下，
    /// 不用碰编译选项；新默认（3.32+ 的 32766）更是绰绰有余。
    /// <para>
    /// 199 不是随手取的：每行 5 个参数，999 ÷ 5 = 199.8 → 199 是还能塞进 999 的最大块大小
    /// （200 行就要 1000 个参数，**超了** 999）。
    /// </para>
    /// </remarks>
    private static void InsertIdeaRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<IdeaIndexRow> rows)
    {
        const int chunkSize = 199;

        for (var start = 0; start < rows.Count; start += chunkSize)
        {
            var chunk = rows.Skip(start).Take(chunkSize).ToList();

            var sql = new StringBuilder("INSERT INTO idea_fts(rowid, title, content, tags, project) VALUES ");
            var parameters = new List<(string Name, object Value)>(chunk.Count * 5);

            for (var i = 0; i < chunk.Count; i++)
            {
                var row = chunk[i];
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.Append($"($r{i}, $t{i}, $c{i}, $g{i}, $p{i})");
                parameters.Add(($"$r{i}", row.RowId));
                parameters.Add(($"$t{i}", ToIndexText(row.Title)));
                parameters.Add(($"$c{i}", ToIndexText(row.Content)));
                // 标签名用空格连起来再切词：每个标签名都是独立的一段，
                // 词元不会跨过标签边界（标签 "ab" 与 "cd" 连成 "abcd" 会被当成一个词元）。
                parameters.Add(($"$g{i}", ToIndexText(string.Join(" ", row.Tags))));
                parameters.Add(($"$p{i}", ToIndexText(row.ProjectName)));
            }

            sql.Append(';');
            Execute(connection, transaction, sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>重写一条想法索引行要用的数据（rowid 直接来自查询，省一次 FindRowId 往返）。</summary>
    /// <remarks>
    /// internal：测试要钉住「回填路径重建出来的标签名列表」——
    /// 标签名含空格时，按空格拆分会把它拆错，而这种错误在索引文本上看不出来
    /// （切完再拼回去恰好还原）。字段是 internal 的话，测试能直接看它。
    /// </remarks>
    internal sealed record IdeaIndexRow(
        long RowId,
        string? Title,
        string Content,
        IReadOnlyList<string> Tags,
        string ProjectName);

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
