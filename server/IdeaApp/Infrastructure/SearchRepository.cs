using System.Globalization;
using System.Text;
using IdeaApp.Contracts;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 命中的想法：想法本身 + 是否在回收站 + 命中位置。
/// </summary>
/// <param name="Idea">命中的那条想法（含标签名）。</param>
/// <param name="Trashed">它是不是在回收站里。</param>
/// <param name="Hits">命中位置（相对各字段的完整字符串）。</param>
public sealed record IdeaHit(Idea Idea, bool Trashed, IReadOnlyList<SearchMatch> Hits);

/// <summary>
/// 命中的项目：项目本身 + 是否在回收站。
/// </summary>
/// <param name="Project">命中的那个项目。</param>
/// <param name="Trashed">它是不是在回收站里。</param>
public sealed record ProjectHit(Project Project, bool Trashed);

/// <summary>
/// 一页命中结果 + 命中总数（总数不受分页影响）。
/// </summary>
/// <param name="Items">这一页的命中项。</param>
/// <param name="Total">该类的命中总数。</param>
public sealed record SearchSlice<T>(IReadOnlyList<T> Items, int Total);

/// <summary>
/// 全库搜索的查询：想法、项目、标签三组。
///
/// <para>
/// 三组各走各的索引（<c>idea_fts</c> / <c>project_fts</c> / <c>tag_fts</c>），
/// 各自按 <c>bm25()</c> 的相关度排序。<c>bm25()</c> 返回**负值，越负越相关**，
/// 所以 <c>ORDER BY bm25(...) ASC</c>（不加 <c>DESC</c>）就是「最相关在前」。
/// </para>
///
/// <para>
/// <b>总数要另跑一次计数查询</b>：按相关度排序的计数必须把所有命中项都算一遍
/// （切页是切完之后的事），所以分页省的是传输与 agent 的上下文，不是检索。
/// </para>
///
/// <para>
/// 三组的口径差别（规格 §11.3、§11.6）：
/// <list type="bullet">
/// <item>想法组：默认**含**回收站（软删的想法仍在索引里），每条标 <c>trashed</c>。</item>
/// <item>项目组：同样默认含回收站。</item>
/// <item>标签组：<b>没有</b>回收站概念；它带的 <c>count</c> 口径与 <c>GET /api/tags</c>
/// 一致 —— 只数**未删**的想法，不随 <c>includeTrash</c> 变化。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SearchRepository(SqliteDatabase db)
{
    /// <summary>
    /// <c>idea_fts</c> 的 bm25 权重，<strong>按列顺序</strong>：正文、标签、项目。
    /// </summary>
    /// <remarks>
    /// ⚠️ <strong>权重个数必须等于 <c>idea_fts</c> 的列数</strong> —— FTS5 的列清单建表时定死。
    /// <para>
    /// 个数对不上时 SQLite <strong>不报错</strong>（实测 3.53.3：多余的权重忽略、
    /// 缺少的按默认 1.0 处理），后果是相关度<strong>静默错位</strong>：
    /// 4 列配 3 个权重，权重落到 title / content / tags 上，项目名那列拿默认 1.0。
    /// </para>
    /// <para>
    /// 正文权重 5.0：标题列去掉之后，正文权重 1 会被标签 5 / 项目 3 压过去，
    /// 「正文里写了咖啡」的那条会排在「项目名带咖啡」的那条后面。
    /// </para>
    /// </remarks>
    internal static readonly double[] IdeaFtsWeights = [5.0, 5.0, 3.0];

    /// <summary>
    /// 命中的想法（一页 + 总数）。
    /// </summary>
    /// <param name="matchExpression">
    /// FTS5 的 MATCH 表达式；<c>null</c> = 没有关键词（不过滤，返回全部）。
    /// </param>
    /// <param name="query">
    /// 用户给的查询串。<b>只有算命中位置时用得上</b> ——
    /// MATCH 表达式是切过词的（「"咖 啡"」），拿它算偏移会算错。
    /// </param>
    /// <param name="includeTrash">是否包含回收站里的想法。</param>
    /// <param name="limit">这一页最多几条。</param>
    /// <param name="offset">跳过最相关的前几条。</param>
    public SearchSlice<IdeaHit> Ideas(
        string? matchExpression,
        string query,
        bool includeTrash,
        int limit,
        int offset)
    {
        using var connection = db.Open();

        // 一页：连项目名一起带出来（命中位置要用它算「project」那一列）。
        var rows = new List<(Idea Idea, string ProjectName)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = IdeasPageSql(matchExpression, includeTrash);

            BindMatch(command, matchExpression);

            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // 项目名那一列的下标：SELECT 去掉 i.title 之后它落到 7（8 变成 score 列）。
                // 不改就去读一个 REAL/NULL → InvalidCastException → /api/search 对所有用户 500。
                rows.Add((ReadIdea(reader), reader.GetString(7)));
            }
        }

        // 标签名另跑一条批量查（按这一页的 id 集合查一次）——
        // 一条想法一条查询的话，一页就是几十次往返。
        var tags = ReadTagNames(connection, rows.Select(row => row.Idea.Id));

        var items = new List<IdeaHit>(rows.Count);

        foreach (var (idea, projectName) in rows)
        {
            var withTags = idea with { Tags = tags.TryGetValue(idea.Id, out var names) ? names : Array.Empty<string>() };

            // 没有关键词就没有命中位置（响应里那个 matches 键也就不该出现）。
            var hits = matchExpression is null
                ? Array.Empty<SearchMatch>()
                : IdeaMatches.For(withTags, projectName, query);

            items.Add(new IdeaHit(withTags, withTags.DeletedAt is not null, hits));
        }

        return new SearchSlice<IdeaHit>(items, CountIdeas(connection, matchExpression, includeTrash));
    }

    /// <summary>
    /// 命中的项目（一页 + 总数）。
    /// </summary>
    /// <param name="matchExpression">
    /// FTS5 的 MATCH 表达式；<c>null</c> = 没有关键词（不过滤，返回全部）。
    /// </param>
    /// <param name="includeTrash">是否包含回收站里的项目。</param>
    /// <param name="limit">这一页最多几条。</param>
    /// <param name="offset">跳过最相关的前几条。</param>
    public SearchSlice<ProjectHit> Projects(
        string? matchExpression,
        bool includeTrash,
        int limit,
        int offset)
    {
        using var connection = db.Open();

        var projects = new List<Project>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = ProjectsPageSql(matchExpression, includeTrash);

            BindMatch(command, matchExpression);

            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(ReadProject(reader));
            }
        }

        var items = projects
            .Select(project => new ProjectHit(project, project.DeletedAt is not null))
            .ToList();

        return new SearchSlice<ProjectHit>(items, CountProjects(connection, matchExpression, includeTrash));
    }

    /// <summary>
    /// 命中的标签（一页 + 总数）。
    /// </summary>
    /// <param name="matchExpression">
    /// FTS5 的 MATCH 表达式；<c>null</c> = 没有关键词（不过滤，返回全部）。
    /// </param>
    /// <param name="limit">这一页最多几条。</param>
    /// <param name="offset">跳过最相关的前几条。</param>
    /// <remarks>
    /// 标签组**不看** <c>includeTrash</c>：标签字典里没有「已删除的标签」这回事，
    /// 而它带的 <c>count</c> 口径恒定是「只数未删的想法」——
    /// 它回答的是「这个标签现在被用着多少次」，不是「搜索结果里出现了多少次」。
    /// </remarks>
    public SearchSlice<TagDto> Tags(string? matchExpression, int limit, int offset)
    {
        using var connection = db.Open();

        var tags = new List<TagDto>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = TagsPageSql(matchExpression);

            BindMatch(command, matchExpression);

            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tags.Add(new TagDto(reader.GetString(0), reader.GetInt32(1)));
            }
        }

        return new SearchSlice<TagDto>(tags, CountTags(connection, matchExpression));
    }

    /// <summary>
    /// 一页想法的 SQL。
    /// </summary>
    /// <param name="matchExpression">MATCH 表达式；<c>null</c> = 不过滤。</param>
    /// <param name="includeTrash">是否包含回收站里的想法。</param>
    /// <remarks>
    /// 两条 FROM：有关键词时走 <c>idea_fts</c>（要它的 MATCH 与 <c>bm25()</c>），
    /// 没关键词时不碰索引表（<c>bm25()</c> 没有 MATCH 时用不了）。
    /// <para>
    /// <c>bm25()</c> 的权重按**列顺序**：第一个实参是最左列的权重。
    /// <c>idea_fts</c> 的列序是 <c>content, tags, project</c> →
    /// 正文 5.0、标签 5.0、项目 3.0。
    /// <strong>权重个数必须等于列数</strong>（FTS5 的列清单建表时定死）——
    /// 个数对不上时 SQLite 不报错，只是相关度静默错位。
    /// </para>
    /// <para>
    /// <c>id ASC</c> 兜底 → 全序 → 分页不漂。
    /// </para>
    /// </remarks>
    private static string IdeasPageSql(string? matchExpression, bool includeTrash)
    {
        var sql = new StringBuilder();

        sql.Append("SELECT i.id, i.project_id, i.content, i.done, i.created_at, i.updated_at, i.deleted_at, p.name, ");

        if (matchExpression is null)
        {
            sql.Append("NULL AS score FROM idea i JOIN project p ON p.id = i.project_id WHERE 1 = 1 ");
        }
        else
        {
            // 权重从 IdeaFtsWeights 拼出来，不写死字面量 ——
            // 写死了将来加列就会忘了改（而改漏了不会报错，只会静默错位）。
            sql.Append($"bm25(idea_fts, {string.Join(", ", IdeaFtsWeights)}) AS score ");
            sql.Append("FROM idea_fts JOIN idea i ON i.rowid = idea_fts.rowid ");
            sql.Append("JOIN project p ON p.id = i.project_id WHERE idea_fts MATCH $match ");
        }

        if (!includeTrash)
        {
            sql.Append("AND i.deleted_at IS NULL ");
        }

        sql.Append(matchExpression is null ? "ORDER BY i.id ASC " : "ORDER BY score ASC, i.id ASC ");
        sql.Append("LIMIT $limit OFFSET $offset;");

        return sql.ToString();
    }

    /// <summary>想法命中总数的 SQL（另跑一次，不受分页影响）。</summary>
    private static string IdeasCountSql(string? matchExpression, bool includeTrash)
    {
        var sql = new StringBuilder();

        if (matchExpression is null)
        {
            sql.Append("SELECT COUNT(*) FROM idea i WHERE 1 = 1 ");
        }
        else
        {
            sql.Append("SELECT COUNT(*) FROM idea_fts JOIN idea i ON i.rowid = idea_fts.rowid ");
            sql.Append("WHERE idea_fts MATCH $match ");
        }

        if (!includeTrash)
        {
            sql.Append("AND i.deleted_at IS NULL ");
        }

        sql.Append(';');

        return sql.ToString();
    }

    /// <summary>一页项目的 SQL。</summary>
    private static string ProjectsPageSql(string? matchExpression, bool includeTrash)
    {
        var sql = new StringBuilder();

        sql.Append("SELECT p.id, p.name, p.sort_order, p.created_at, p.updated_at, p.deleted_at, ");

        if (matchExpression is null)
        {
            sql.Append("NULL AS score FROM project p WHERE 1 = 1 ");
        }
        else
        {
            sql.Append("bm25(project_fts) AS score ");
            sql.Append("FROM project_fts JOIN project p ON p.rowid = project_fts.rowid ");
            sql.Append("WHERE project_fts MATCH $match ");
        }

        if (!includeTrash)
        {
            sql.Append("AND p.deleted_at IS NULL ");
        }

        sql.Append(matchExpression is null ? "ORDER BY p.id ASC " : "ORDER BY score ASC, p.id ASC ");
        sql.Append("LIMIT $limit OFFSET $offset;");

        return sql.ToString();
    }

    /// <summary>项目命中总数的 SQL。</summary>
    private static string ProjectsCountSql(string? matchExpression, bool includeTrash)
    {
        var sql = new StringBuilder();

        if (matchExpression is null)
        {
            sql.Append("SELECT COUNT(*) FROM project p WHERE 1 = 1 ");
        }
        else
        {
            sql.Append("SELECT COUNT(*) FROM project_fts JOIN project p ON p.rowid = project_fts.rowid ");
            sql.Append("WHERE project_fts MATCH $match ");
        }

        if (!includeTrash)
        {
            sql.Append("AND p.deleted_at IS NULL ");
        }

        sql.Append(';');

        return sql.ToString();
    }

    /// <summary>
    /// 一页标签的 SQL。
    /// </summary>
    /// <param name="matchExpression">MATCH 表达式；<c>null</c> = 不过滤。</param>
    /// <remarks>
    /// 使用次数的口径与 <c>GET /api/tags</c> 一致：**只数未删的想法**
    /// （那条子查询与 <see cref="TagRepository.ListWithCounts"/> 逐字相同）。
    /// <para>
    /// 排序：相关度 → 使用次数倒序（用得多的更可能是用户要的）→ 名称。
    /// </para>
    /// </remarks>
    private static string TagsPageSql(string? matchExpression)
    {
        var sql = new StringBuilder();

        sql.Append("SELECT t.name, ");
        sql.Append("(SELECT COUNT(*) FROM idea_tag it JOIN idea i ON i.id = it.idea_id ");
        sql.Append(" WHERE it.tag_id = t.id AND i.deleted_at IS NULL) AS usage_count, ");

        if (matchExpression is null)
        {
            sql.Append("NULL AS score FROM tag t ");
        }
        else
        {
            sql.Append("bm25(tag_fts) AS score FROM tag_fts JOIN tag t ON t.id = tag_fts.rowid ");
            sql.Append("WHERE tag_fts MATCH $match ");
        }

        sql.Append(matchExpression is null
            ? "ORDER BY usage_count DESC, t.name ASC "
            : "ORDER BY score ASC, usage_count DESC, t.name ASC ");

        sql.Append("LIMIT $limit OFFSET $offset;");

        return sql.ToString();
    }

    /// <summary>标签命中总数的 SQL。</summary>
    private static string TagsCountSql(string? matchExpression)
    {
        if (matchExpression is null)
        {
            return "SELECT COUNT(*) FROM tag t;";
        }

        return "SELECT COUNT(*) FROM tag_fts JOIN tag t ON t.id = tag_fts.rowid WHERE tag_fts MATCH $match;";
    }

    /// <summary>
    /// 把 MATCH 表达式绑上去。
    /// </summary>
    /// <param name="command">要执行的命令。</param>
    /// <param name="matchExpression">MATCH 表达式；<c>null</c> = 没有关键词（不过滤）。</param>
    /// <remarks>
    /// 没有关键词时**不绑**：那条 SQL 里没有 <c>$match</c> 这个占位符
    /// （走的是「不过滤」那套 FROM），绑一个用不上的参数没有意义。
    /// <para>
    /// 六个查询（页 + 计数 × 三组）都走这里：「有没有关键词」这件事只有一处判。
    /// </para>
    /// </remarks>
    private static void BindMatch(SqliteCommand command, string? matchExpression)
    {
        if (matchExpression is not null)
        {
            command.Parameters.AddWithValue("$match", matchExpression);
        }
    }

    /// <summary>想法命中总数。</summary>
    private static int CountIdeas(SqliteConnection connection, string? matchExpression, bool includeTrash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = IdeasCountSql(matchExpression, includeTrash);

        BindMatch(command, matchExpression);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>项目命中总数。</summary>
    private static int CountProjects(SqliteConnection connection, string? matchExpression, bool includeTrash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ProjectsCountSql(matchExpression, includeTrash);

        BindMatch(command, matchExpression);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>标签命中总数。</summary>
    private static int CountTags(SqliteConnection connection, string? matchExpression)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TagsCountSql(matchExpression);

        BindMatch(command, matchExpression);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 一批想法的标签名（想法 id → 标签名列表）。
    /// </summary>
    /// <remarks>
    /// 标签名按名称排序（大小写不敏感）—— 与索引里那一列的文本逐字一致，
    /// 命中位置的偏移才对得上。
    /// </remarks>
    private static Dictionary<string, string[]> ReadTagNames(
        SqliteConnection connection,
        IEnumerable<string> ideaIds)
    {
        var ids = ideaIds.ToArray();

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (ids.Length == 0)
        {
            return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        }

        using var command = connection.CreateCommand();

        var placeholders = new StringBuilder();

        for (var index = 0; index < ids.Length; index++)
        {
            placeholders.Append(index == 0 ? "$id0" : ",$id" + index.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "$id" + index.ToString(CultureInfo.InvariantCulture),
                ids[index]);
        }

        command.CommandText =
            "SELECT i.id, t.name FROM idea i " +
            "JOIN idea_tag it ON it.idea_id = i.id " +
            "JOIN tag t ON t.id = it.tag_id " +
            $"WHERE i.id IN ({placeholders}) " +
            "ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ideaId = reader.GetString(0);

            if (!result.TryGetValue(ideaId, out var names))
            {
                names = new List<string>();
                result[ideaId] = names;
            }

            names.Add(reader.GetString(1));
        }

        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>读一行想法（标签另查）。</summary>
    /// <remarks>
    /// ⚠️ 列序是 <c>id, project_id, content, done, created_at, updated_at, deleted_at</c> ——
    /// 搜索路径的 SELECT 里**没有 title 了**（想法不再有标题，Task 3 才把
    /// 领域记录 <c>Idea.Title</c> 一起去掉），所以这里给 <c>Title: null</c> 占位。
    /// </remarks>
    private static Idea ReadIdea(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        ProjectId: reader.GetString(1),
        Title: null,
        Content: reader.GetString(2),
        Tags: Array.Empty<string>(),
        Done: reader.GetInt64(3) != 0,
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(4)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(5)),
        DeletedAt: reader.IsDBNull(6) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(6)));

    /// <summary>读一行项目。</summary>
    private static Project ReadProject(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Name: reader.GetString(1),
        SortOrder: reader.GetInt32(2),
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(3)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(4)),
        DeletedAt: reader.IsDBNull(5) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(5)));
}
