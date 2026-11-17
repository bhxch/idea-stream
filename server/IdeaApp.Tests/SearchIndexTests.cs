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

    /// <summary>
    /// 旧库的 <c>idea_fts</c> 是 4 列（含 title），而 schema 现在是 3 列。
    ///
    /// <para>
    /// FTS5 虚表的列清单是<strong>建表时定死</strong>的，<c>CREATE ... IF NOT EXISTS</c>
    /// 改不了 —— 不 DROP 就永远是 4 列。
    /// </para>
    ///
    /// <para>
    /// 后果不是报错，是<strong>静默错位</strong>：bm25 的权重个数必须等于列数，
    /// 而 SQLite 在个数对不上时<strong>不报错</strong>（多余的权重忽略、缺少的按默认 1.0 处理）——
    /// 4 列配 3 个权重，权重落到 title / content / tags 上，项目名那列拿默认 1.0。
    /// </para>
    /// </summary>
    [Fact]
    public void Initialize_rebuilds_a_legacy_idea_fts_with_the_title_column()
    {
        // 旧形状的库必须在**启动之前**造好 —— 造完就调 Initialize()。
        var path = Path.Combine(_tempDirectory, "legacy", "idea.db");
        var db = new SqliteDatabase(path);

        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            // 旧版本的 idea_fts：四列，含 title。
            command.CommandText =
                "CREATE VIRTUAL TABLE idea_fts USING fts5(title, content, tags, project);";
            command.ExecuteNonQuery();
        }

        db.Initialize();

        // 列名恰好是那 3 列（PRAGMA table_info 只返回声明的列，FTS5 的隐藏列 rank 不在里面）。
        Assert.Equal(new[] { "content", "tags", "project" }, ReadIdeaFtsColumns(path));
    }

    /// <summary>
    /// 形状已经对得上时，<c>Initialize()</c> 不能再 DROP 掉它 ——
    /// 一 DROP 索引行就没了，而版本号对得上时不会触发整体重建，索引就永远空着。
    /// </summary>
    [Fact]
    public void Initialize_keeps_the_idea_fts_shape_when_it_already_matches()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲咖啡的关键");

        using (var connection = Database.Open())
        {
            SearchIndex.UpsertIdea(
                connection, null, "i1", "手冲咖啡的关键", Array.Empty<string>(), "工作");
        }

        var before = CountIdeaFts();
        Assert.NotEqual(0, before);

        // 再跑一次 Initialize()：形状对得上 → 不 DROP → 数据一条不少。
        Database.Initialize();

        Assert.Equal(new[] { "content", "tags", "project" }, ReadIdeaFtsColumns(DatabasePath));
        Assert.Equal(before, CountIdeaFts());
        Assert.Equal(new[] { RowIdOfIdea("i1") }, MatchIdeaFts("\"咖 啡\""));
    }

    /// <summary>
    /// bm25 的<strong>权重个数必须等于列数</strong>。
    ///
    /// <para>
    /// 这条约束没有强制机制：个数对不上时 SQLite <strong>不报错</strong>
    /// （多余的权重忽略、缺少的按默认 1.0 处理），后果是相关度静默错位。
    /// 所以只能靠测试钉住 —— 权重数组与列数必须同时改。
    /// </para>
    /// </summary>
    [Fact]
    public void Search_weights_match_the_index_column_count()
    {
        Assert.Equal(ReadIdeaFtsColumns(DatabasePath).Count, SearchRepository.IdeaFtsWeights.Length);
    }

    [Fact]
    public void EnsureIndexed_on_empty_database_creates_index_rows()
    {
        // 库里还没有索引（老库就是这样），回填后每条想法都要有一行索引。
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲咖啡的关键");
        InsertIdea("i2", "p1", "第二件事");

        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(2, CountIdeaFts());
    }

    [Fact]
    public void EnsureIndexed_rebuilds_when_version_differs()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "第一条");
        SearchIndexInitializer.EnsureIndexed(Database);

        // 版本对不上 → 整体重建。
        SetIndexVersion("0");

        // 重建期间才加进来的想法也必须进索引 —— 增量写入不会碰它，
        // 只有整体重建才会（这一条就是「确实重建过」的证据）。
        InsertIdea("i2", "p1", "第二条");

        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(2, CountIdeaFts());
        Assert.Equal(SearchIndex.CurrentVersion, ReadIndexVersion());
    }

    [Fact]
    public void EnsureIndexed_is_a_noop_when_version_matches()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "第一条");
        SearchIndexInitializer.EnsureIndexed(Database);

        // 哨兵行：重建会先清空索引表，这行就没了；没重建它就还在。
        InsertSentinelIndexRow(999999);
        var before = CountIdeaFts();

        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(before, CountIdeaFts());
        Assert.Equal(SearchIndex.CurrentVersion, ReadIndexVersion());
    }

    [Fact]
    public void EnsureIndexed_writes_the_same_index_text_as_an_incremental_upsert()
    {
        // 两条路径写的是同一件事：老库回填与增量写必须产出**逐字节相同**的索引文本。
        // 不然 Task 3 一引入增量写，两条路径立刻分叉（回填出来的行搜得到，
        // 增量写出来的行搜不到，或者反过来），而且分叉点极难查。
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲记录");

        // 标签**故意按与排序相反的顺序**插入：回填必须自己按名称排序
        // （group_concat 自己不保序），不然写出来的文本就取决于插入顺序。
        LinkTag("i1", InsertTag("手冲"));
        LinkTag("i1", InsertTag("咖啡"));

        // 回填路径。
        SearchIndexInitializer.EnsureIndexed(Database);
        var backfilled = ReadIdeaFtsTags(RowIdOfIdea("i1"));

        // 回填写出的文本（顺带钉住「标签名排序后拼」的约定）。
        Assert.Equal("咖 啡 手 冲", backfilled);

        using var connection = Database.Open();

        // 先用**另一份内容**做一次增量写：它必须真的把行改掉。
        //
        // 这一步不能省：只比「回填的文本 vs 增量写之后的文本」的话，
        // 「增量写被忽略、行根本没变」也会让断言通过 —— 那样这条用例
        // 就分不清「增量写真的发生了且文本相同」与「增量写没生效」。
        SearchIndex.UpsertIdea(connection, null, "i1", "别的内容", new[] { "茶" }, "工作");
        Assert.Equal("茶", ReadIdeaFtsTags(RowIdOfIdea("i1")));

        // 再用与回填**完全相同**的数据做一次增量写：产物必须与回填逐字节相同。
        //
        // ⚠️ 标签顺序要跟回填一致：回填里标签名是**按名称排序**后拼的
        // （显式 ORDER BY name COLLATE NOCASE —— group_concat 自己不保序），
        // 增量写这条路也得按同样的顺序拼，否则同一份数据会写出两种文本。
        SearchIndex.UpsertIdea(
            connection, null, "i1", "手冲记录", new[] { "咖啡", "手冲" }, "工作");

        Assert.Equal(backfilled, ReadIdeaFtsTags(RowIdOfIdea("i1")));
    }

    [Fact]
    public async Task EnsureIndexed_skips_the_rebuild_when_another_instance_rebuilt_it_meanwhile()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲记录");
        SetIndexVersion("0");

        // 模拟「另一个实例」：占住写锁，在锁里把版本改成当前值，
        // 并往索引表里塞一行哨兵。
        //
        // 哨兵就是观察点：重建会先 DELETE FROM idea_fts，
        // 哨兵活下来 = 后来者**没有**重建。
        using var blocker = Database.Open();
        using var blockerTransaction = blocker.BeginTransaction();
        Exec(blocker, $"INSERT OR REPLACE INTO search_meta(key, value) VALUES ('index.version', '{SearchIndex.CurrentVersion}');", blockerTransaction);
        Exec(blocker, "INSERT INTO idea_fts(rowid, content, tags, project) VALUES (999999, '哨兵', '', '');", blockerTransaction);

        // 过一会儿才提交：让后来者卡在 BEGIN IMMEDIATE 上（它拿不到写锁）。
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(500);
            blockerTransaction.Commit();
        });

        // 版本对不上 → 本来会想重建；但另一个实例刚好重建过了 → 必须 no-op。
        SearchIndexInitializer.EnsureIndexed(Database);

        await releaser;

        // 哨兵还在 = 重建没有发生。
        Assert.Equal(new[] { 999999L }, IdeaFtsRowIds());
    }

    [Fact]
    public void EnsureIndexed_indexes_tags_with_the_tag_id_as_rowid()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲记录");
        var tagId = InsertTag("咖啡");
        LinkTag("i1", tagId);

        SearchIndexInitializer.EnsureIndexed(Database);

        // tag_fts 的 rowid 必须等于 tag.id（规格 §8.1 的 rowid 映射）——
        // 索引行与字典行靠它一一对应。
        Assert.Equal(new[] { tagId }, TagFtsRowIds());
    }

    [Fact]
    public void EnsureIndexed_writes_every_row_when_the_data_spans_several_chunks()
    {
        // 回填是「一条读全 + 按块多行 INSERT」。块的大小有上限（参数个数有硬上限），
        // 所以数据多了就会跨块 —— 只造一条想法的话，
        // 「只写了第一块」「块边界算错了」这类错误一个都测不出来。
        //
        // 250 条 > 单块上限（199 行/块）→ 必然跨块（199 + 51）。
        const int count = 250;

        InsertProject("p1", "工作");

        // 标签先建好（标签字典按名字唯一，同一个名字只能有一行）。
        // 名字里带空格是故意的：标签名含空格时拆分最容易出错。
        var tagIds = new long[7];
        for (var i = 0; i < tagIds.Length; i++)
        {
            tagIds[i] = InsertTag($"标签 {i}");
        }

        for (var i = 0; i < count; i++)
        {
            var id = $"i{i:D3}";
            InsertIdea(id, "p1", $"正文 {i}：手冲咖啡的关键");
            LinkTag(id, tagIds[i % tagIds.Length]);
        }

        SearchIndexInitializer.EnsureIndexed(Database);

        // 行数一条不少。
        Assert.Equal(count, CountIdeaFts());

        // 回填写出来的每一行，都要与**逐条** UpsertIdea 写出来的逐字节相同：
        // 两条路径写的是同一件事，跨块时尤其容易在块的拼接处出错
        // （漏一行、串一行、参数绑错位）。
        var backfilled = ReadIdeaFtsRows();

        using (var connection = Database.Open())
        {
            foreach (var id in IdeaIds())
            {
                var content = ReadIdeaContent(id);
                var tags = ReadTagNamesOfIdea(id);
                var projectName = ReadProjectName("p1");

                SearchIndex.UpsertIdea(connection, null, id, content, tags, projectName);

                var rowId = RowIdOfIdea(id);
                Assert.Equal(backfilled[rowId], ReadIdeaFtsRow(rowId));
            }
        }

        // 幂等：再回填一次（版本对不上 → 整体重建）不会多出行，
        // 写出来的内容也与第一次**逐字节相同**。
        SetIndexVersion("0");
        SearchIndexInitializer.EnsureIndexed(Database);

        Assert.Equal(count, CountIdeaFts());
        Assert.Equal(backfilled, ReadIdeaFtsRows());
    }

    [Fact]
    public void EnsureIndexed_keeps_a_tag_name_that_contains_a_space()
    {
        // 标签名本身含空格时，回填路径必须能把它**原样**拆回标签名列表。
        //
        // 之前回填用 group_concat 拼一段文本再按空格切：标签 ["a b", "c"] 会被切成
        // ["a", "b", "c"] —— 拆错了。索引文本上看不出来（切完再拼回去恰好还原），
        // 但那个列表本身是错的（是个陷阱）。
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲记录");

        LinkTag("i1", InsertTag("a b"));
        LinkTag("i1", InsertTag("c"));

        SearchIndexInitializer.EnsureIndexed(Database);

        // 回填路径重建出来的标签名列表：两个（"a b" 是一个标签，不是两个）。
        using (var connection = Database.Open())
        {
            var rows = SearchIndex.LoadIdeaRows(connection, null, projectId: null);
            var row = Assert.Single(rows);

            Assert.Equal(new[] { "a b", "c" }, row.Tags);
        }

        // 索引文本：标签名按名称排序后用空格连起来 → "a b c"。
        Assert.Equal(SearchIndex.ToIndexText("a b c"), ReadIdeaFtsTags(RowIdOfIdea("i1")));

        // 逐条增量写出来的文本必须与回填**逐字节相同**（两条路径同一口径）。
        using (var connection = Database.Open())
        {
            SearchIndex.UpsertIdea(connection, null, "i1", "手冲记录", new[] { "a b", "c" }, "工作");
        }

        Assert.Equal(SearchIndex.ToIndexText("a b c"), ReadIdeaFtsTags(RowIdOfIdea("i1")));
    }

    [Fact]
    public void ReindexProjectIdeas_keeps_the_rows_of_ideas_whose_project_row_is_gone()
    {
        // 「项目被硬删而想法还在」时，重建这个项目的索引行**不能**把那条想法的索引行弄丢。
        //
        // 删除范围是「project_id = 该项目的全部想法」（不 JOIN project），
        // 写回范围必须与它一致 —— 之前写回用的是 INNER JOIN，
        // 项目行不在时那条想法压根不会被写回：索引行被删掉就没了（静默丢失）。
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲咖啡的关键");

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(connection, null, "i1", "手冲咖啡的关键", Array.Empty<string>(), "工作");

        // 前置断言：索引行确实在（不然「重建后还在」是白成立的）。
        Assert.Equal(new[] { RowIdOfIdea("i1") }, MatchIdeaFts("\"咖 啡\""));

        // 把项目行删掉，只留下想法。
        //
        // 外键（idea.project_id → project.id）是开着的，不关掉它就删不掉项目行。
        // PRAGMA foreign_keys 在事务里是空操作 —— 这里没有事务，OK。
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM project WHERE id = 'p1';";
            delete.ExecuteNonQuery();
        }

        SearchIndex.ReindexProjectIdeas(connection, null, "p1");

        // 索引行还在：那条想法照样搜得到（项目名那一列写空串 —— 库里已经没有这个项目了）。
        Assert.Equal(new[] { RowIdOfIdea("i1") }, MatchIdeaFts("\"咖 啡\""));
        Assert.Equal(string.Empty, ReadIdeaFtsRow(RowIdOfIdea("i1")).Project);
    }

    [Fact]
    public void UpsertIdea_makes_the_idea_findable()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲咖啡的关键");

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", "手冲咖啡的关键", Array.Empty<string>(), "工作");

        // "咖 啡" 是两个相邻词元组成的短语 —— 正文里的「咖啡」正好是这两个词元。
        Assert.Equal(new[] { RowIdOfIdea("i1") }, MatchIdeaFts("\"咖 啡\""));
    }

    [Fact]
    public void UpsertIdea_indexes_tags_and_project_name()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲记录");
        LinkTag("i1", InsertTag("咖啡"));

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", "手冲记录", new[] { "咖啡" }, "工作");

        // 正文里没有「咖啡」也没有「工作」：命中只能来自标签列与项目名列。
        var rowId = RowIdOfIdea("i1");
        Assert.Equal(new[] { rowId }, MatchIdeaFts("\"咖 啡\""));
        Assert.Equal(new[] { rowId }, MatchIdeaFts("\"工 作\""));
    }

    [Fact]
    public void DeleteIdea_removes_the_row()
    {
        InsertProject("p1", "工作");
        InsertIdea("i1", "p1", "手冲咖啡的关键");

        using var connection = Database.Open();
        SearchIndex.UpsertIdea(
            connection, null, "i1", "手冲咖啡的关键", Array.Empty<string>(), "工作");
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

    private void InsertIdea(string id, string projectId, string content)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO idea(id, project_id, content, done, deleted_at, created_at, updated_at) " +
            "VALUES ($id, $projectId, $content, 0, NULL, $now, $now);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$projectId", projectId);
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
            "INSERT INTO idea_fts(rowid, content, tags, project) VALUES ($rowId, '哨兵', '', '');";
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

    /// <summary>索引行的一行内容（三列的原始文本）。</summary>
    private sealed record IndexRow(string Content, string Tags, string Project);

    /// <summary>读 idea_fts 的一整行（三列的原始文本）。</summary>
    private IndexRow ReadIdeaFtsRow(long rowId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content, tags, project FROM idea_fts WHERE rowid = $rowId;";
        command.Parameters.AddWithValue("$rowId", rowId);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"索引表里没有 rowid = {rowId} 的行");

        return new IndexRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    /// <summary>读 idea_fts 的**全部**行（rowid → 三列原文）。</summary>
    private Dictionary<long, IndexRow> ReadIdeaFtsRows()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, content, tags, project FROM idea_fts ORDER BY rowid;";

        var rows = new Dictionary<long, IndexRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows[reader.GetInt64(0)] = new IndexRow(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3));
        }

        return rows;
    }

    /// <summary>库里全部的想法 id（按插入顺序）。</summary>
    private IReadOnlyList<string> IdeaIds()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM idea ORDER BY rowid;";

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>读一条想法的正文。</summary>
    private string ReadIdeaContent(string ideaId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM idea WHERE id = $id;";
        command.Parameters.AddWithValue("$id", ideaId);
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    /// <summary>
    /// 读一条想法的标签名（按名称排序，大小写不敏感）——
    /// 与仓储层（<c>ReadTagNames</c>）和回填路径的口径一致。
    /// </summary>
    private IReadOnlyList<string> ReadTagNamesOfIdea(string ideaId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.name FROM idea_tag it JOIN tag t ON t.id = it.tag_id " +
            "WHERE it.idea_id = $id ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;";
        command.Parameters.AddWithValue("$id", ideaId);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>读项目名。</summary>
    private string ReadProjectName(string projectId)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM project WHERE id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
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

    /// <summary>读 idea_fts 的全部 rowid。</summary>
    private IReadOnlyList<long> IdeaFtsRowIds()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM idea_fts ORDER BY rowid;";

        var rowIds = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return rowIds;
    }

    /// <summary>
    /// 读某个库里 <c>idea_fts</c> 的<strong>声明列名</strong>（按列序）。
    /// </summary>
    /// <remarks>
    /// <c>PRAGMA table_info(idea_fts)</c> 返回的是**声明的列** ——
    /// FTS5 的隐藏列 <c>rank</c> 不在里面（实测：<c>fts5(title, content, tags, project)</c>
    /// 的 table_info 恰好返回那 4 列）。
    /// <para>
    /// 表不存在时返回**空列表**（table_info 给 0 行）——
    /// 这正是「新库」与「旧库」的分野。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ReadIdeaFtsColumns(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(idea_fts);";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // table_info 的列是 (cid, name, type, notnull, dflt_value, pk)。
            names.Add(reader.GetString(1));
        }

        return names;
    }

    /// <summary>在指定连接（可选事务）上执行一条语句。</summary>
    private static void Exec(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Now() => SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);
}
