using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 全文索引的存储与回填测试。
///
/// 索引里存的是**切分后的词元串**（规格 §9.1），查询侧的 MATCH 串也是同一套规则切出来的
/// （规格 §9.3）—— 两边只要有一处不同，就会出现「搜得到却高亮不出来」这种极难查的偏差。
/// 所以这里的 MATCH 串（<c>"咖 啡"</c> 这种词元短语）是钉死的，不是随手写的。
///
/// 数据一律用裸 SQL 造：这一层只关心「索引表里有没有正确的行」，
/// 不该被 HTTP 层的行为干扰。
/// </summary>
public sealed class SearchIndexTests : IDisposable
{
    private readonly string _tempDirectory;

    public SearchIndexTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_tempDirectory, "idea.db");
        Database = new SqliteDatabase(DatabasePath);
        Database.Initialize();
    }

    public string DatabasePath { get; }

    public SqliteDatabase Database { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不应让测试失败。
        }
    }

    [Fact]
    public void EnsureIndexed_on_empty_database_creates_index_rows()
    {
        // 库里还没有索引（老库就是这样），回填后每条想法都要有一行索引。
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "手冲咖啡的关键");
        InsertIdea("i2", "p1", null, "第二件事");

        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(2, CountIdeaFts());
    }

    [Fact]
    public void EnsureIndexed_rebuilds_when_version_differs()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "第一条");
        SearchIndexInitializer.EnsureIndexed(Database);

        // 版本对不上 → 整体重建。
        SetIndexVersion("0");

        // 重建期间才加进来的想法也必须进索引 —— 增量写入不会碰它，
        // 只有整体重建才会（这一条就是「确实重建过」的证据）。
        InsertIdea("i2", "p1", null, "第二条");

        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(2, CountIdeaFts());
        Assert.Equal("1", ReadIndexVersion());
    }

    [Fact]
    public void EnsureIndexed_is_a_noop_when_version_matches()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "第一条");
        SearchIndexInitializer.EnsureIndexed(Database);

        // 哨兵行：重建会先清空索引表，这行就没了；没重建它就还在。
        InsertSentinelIndexRow(999999);
        var before = CountIdeaFts();

        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(before, CountIdeaFts());
        Assert.Equal("1", ReadIndexVersion());
    }

    [Fact]
    public void EnsureIndexed_writes_the_same_index_text_as_an_incremental_upsert()
    {
        // 两条路径写的是同一件事：老库回填与增量写必须产出**逐字节相同**的索引文本。
        // 不然 Task 3 一引入增量写，两条路径立刻分叉（回填出来的行搜得到，
        // 增量写出来的行搜不到，或者反过来），而且分叉点极难查。
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "手冲记录");

        // 标签**故意按与排序相反的顺序**插入：回填必须自己按名称排序
        // （group_concat 自己不保序），不然写出来的文本就取决于插入顺序。
        LinkTag("i1", InsertTag("手冲"));
        LinkTag("i1", InsertTag("咖啡"));

        // 回填路径。
        SearchIndexInitializer.EnsureIndexed(Database);
        var backfilled = ReadIdeaFtsTags(RowIdOfIdea("i1"));

        // 增量写路径：同样的内容、同样顺序的标签名。
        //
        // ⚠️ 标签顺序要跟回填一致：回填里标签名是**按名称排序**后拼的
        // （显式 ORDER BY name COLLATE NOCASE —— group_concat 自己不保序），
        // 增量写这条路也得按同样的顺序拼，否则同一份数据会写出两种文本。
        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", null, "手冲记录", new[] { "咖啡", "手冲" }, "工作");

        // 回填写出的文本（顺带钉住「标签名排序后拼」的约定）。
        Assert.Equal("咖 啡 手 冲", backfilled);

        // 增量写之后，两条路径的产物完全一样。
        Assert.Equal(backfilled, ReadIdeaFtsTags(RowIdOfIdea("i1")));
    }

    [Fact]
    public void EnsureIndexed_indexes_tags_with_the_tag_id_as_rowid()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "手冲记录");
        var tagId = InsertTag("咖啡");
        LinkTag("i1", tagId);

        SearchIndexInitializer.EnsureIndexed(Database);

        // tag_fts 的 rowid 必须等于 tag.id（规格 §8.1 的 rowid 映射）——
        // 索引行与字典行靠它一一对应。
        Assert.Equal(new[] { tagId }, TagFtsRowIds());
    }

    [Fact]
    public void UpsertIdea_makes_the_idea_findable()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "手冲咖啡的关键");

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", null, "手冲咖啡的关键", Array.Empty<string>(), "工作");

        // "咖 啡" 是两个相邻词元组成的短语 —— 正文里的「咖啡」正好是这两个词元。
        Assert.Equal(new[] { RowIdOfIdea("i1") }, MatchIdeaFts("\"咖 啡\""));
    }

    [Fact]
    public void UpsertIdea_indexes_tags_and_project_name()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "手冲记录");
        LinkTag("i1", InsertTag("咖啡"));

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", null, "手冲记录", new[] { "咖啡" }, "工作");

        // 正文里没有「咖啡」也没有「工作」：命中只能来自标签列与项目名列。
        var rowId = RowIdOfIdea("i1");
        Assert.Equal(new[] { rowId }, MatchIdeaFts("\"咖 啡\""));
        Assert.Equal(new[] { rowId }, MatchIdeaFts("\"工 作\""));
    }

    [Fact]
    public void DeleteIdea_removes_the_row()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", null, "手冲咖啡的关键");

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", null, "手冲咖啡的关键", Array.Empty<string>(), "工作");
        Assert.NotEmpty(MatchIdeaFts("\"咖 啡\""));

        // idea 行还在 —— DeleteIdea 靠它取 rowid。
        SearchIndex.DeleteIdea(connection, null, "i1");

        Assert.Empty(MatchIdeaFts("\"咖 啡\""));
    }

    private void InsertProject(string id, string name)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO project(id, name, sort_order, deleted_at, created_at, updated_at) " +
            "VALUES ($id, $name, 0, NULL, $now, $now);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    private void InsertIdea(string id, string projectId, string? title, string content)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO idea(id, project_id, title, content, done, deleted_at, created_at, updated_at) " +
            "VALUES ($id, $projectId, $title, $content, 0, NULL, $now, $now);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    private long InsertTag(string name)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tag(name) VALUES ($name);";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT id FROM tag WHERE name = $name COLLATE NOCASE;";
        read.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(read.ExecuteScalar());
    }

    private void LinkTag(string ideaId, long tagId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO idea_tag(idea_id, tag_id) VALUES ($ideaId, $tagId);";
        command.Parameters.AddWithValue("$ideaId", ideaId);
        command.Parameters.AddWithValue("$tagId", tagId);
        command.ExecuteNonQuery();
    }

    private void InsertSentinelIndexRow(long rowId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO idea_fts(rowid, title, content, tags, project) VALUES ($rowId, '哨兵', '', '', '');";
        command.Parameters.AddWithValue("$rowId", rowId);
        command.ExecuteNonQuery();
    }

    private long CountIdeaFts()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM idea_fts;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string? ReadIndexVersion()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM search_meta WHERE key = 'index.version';";
        return command.ExecuteScalar() as string;
    }

    private void SetIndexVersion(string value)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO search_meta(key, value) VALUES ('index.version', $value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private long RowIdOfIdea(string ideaId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM idea WHERE id = $id;";
        command.Parameters.AddWithValue("$id", ideaId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private IReadOnlyList<long> MatchIdeaFts(string matchExpression)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM idea_fts WHERE idea_fts MATCH $q;";
        command.Parameters.AddWithValue("$q", matchExpression);

        var rowIds = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return rowIds;
    }

    /// <summary>读 idea_fts 某行的 tags 列**原始文本**（不是 token 化的结果）。</summary>
    private string ReadIdeaFtsTags(long rowId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tags FROM idea_fts WHERE rowid = $rowId;";
        command.Parameters.AddWithValue("$rowId", rowId);
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    /// <summary>读 tag_fts 的全部 rowid（规格 §8.1：它应当等于 tag.id）。</summary>
    private IReadOnlyList<long> TagFtsRowIds()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM tag_fts ORDER BY rowid;";

        var rowIds = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return rowIds;
    }

    private static string Now() => SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);
}
