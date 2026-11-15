using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IdeaApp.Tests;

/// <summary>
/// 写路径与全文索引的一致性测试（规格 §10.2）。
///
/// <para>
/// 走**真实路径**（HTTP 打端点）再直接查 <c>idea_fts</c>：仓储层自己维护索引，
/// 这层测的是「业务写完之后索引跟上了吗」，不是「索引助手能不能写」。
/// </para>
///
/// <para>
/// MATCH 串是钉死的词元短语（<c>"咖 啡"</c> 这种）—— 索引里存的是切分后的词元串，
/// 一个汉字一个 token，短语查询要求两个 token **相邻**。
/// </para>
/// </summary>
public sealed class SearchIndexConsistencyTests
{
    /// <summary>新建的想法能搜到：索引行跟着业务行一起写进去了。</summary>
    [Fact]
    public async Task Create_idea_makes_it_findable()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡机滤芯");

        Assert.Equal(
            new[] { RowIdOfIdea(factory.DatabasePath, id) },
            MatchFts(factory.DatabasePath, "idea_fts", "\"咖 啡\""));
    }

    /// <summary>改正文/标题：旧内容搜不到了，新内容搜得到（索引行是「先删后插」，不留旧 token）。</summary>
    [Fact]
    public async Task Update_content_updates_the_index()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡机滤芯");

        var response = await client.PatchAsJsonAsync($"/api/ideas/{id}", new { content = "喝茶" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(MatchFts(factory.DatabasePath, "idea_fts", "\"咖 啡\""));
        Assert.Equal(
            new[] { RowIdOfIdea(factory.DatabasePath, id) },
            MatchFts(factory.DatabasePath, "idea_fts", "\"喝 茶\""));
    }

    /// <summary>换标签：标签名进索引列，所以标签也搜得到；标签清空后也搜不到了。</summary>
    [Fact]
    public async Task Set_tags_updates_the_index()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "手冲记录");
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        var set = await client.PutAsJsonAsync($"/api/ideas/{id}/tags", new { tags = new[] { "烘焙" } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "idea_fts", "\"烘 焙\""));

        // 改回空：标签列被清空，搜不到了。
        var cleared = await client.PutAsJsonAsync($"/api/ideas/{id}/tags", new { tags = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        Assert.Empty(MatchFts(factory.DatabasePath, "idea_fts", "\"烘 焙\""));
    }

    /// <summary>
    /// 合并会硬删源想法：源想法的索引行必须一起删掉。
    /// 留着就是孤儿行 —— 它占着 rowid，而 idea 表没有显式整数主键、rowid 会被复用，
    /// 下一条新想法可能顶到它的位置上，索引就串了（规格 §10.3）。
    /// </summary>
    [Fact]
    public async Task Merge_hard_deletes_the_source_from_the_index()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条想法");
        var second = await CreateIdeaAsync(client, "第二条想法");

        var firstRowId = RowIdOfIdea(factory.DatabasePath, first);
        var secondRowId = RowIdOfIdea(factory.DatabasePath, second);

        var merged = await client.PostAsJsonAsync(
            "/api/ideas/merge", new { ids = new[] { first, second } });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        // 源想法的索引行没了。
        Assert.Equal(0, CountFtsRow(factory.DatabasePath, "idea_fts", firstRowId));
        Assert.Equal(0, CountFtsRow(factory.DatabasePath, "idea_fts", secondRowId));

        // 合并出来的那条**在**索引里（不能把索引写反了：删了旧的、没写新的）。
        var mergedId = await ReadIdAsync(merged);
        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "idea_fts", RowIdOfIdea(factory.DatabasePath, mergedId)));
    }

    /// <summary>
    /// 软删**不动索引**：行还在索引里，搜索时用 <c>deleted_at</c> 判断在不在回收站。
    /// </summary>
    [Fact]
    public async Task Delete_idea_keeps_the_row_in_the_index()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡机滤芯");
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        var deleted = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "idea_fts", rowId));
        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "idea_fts", "\"咖 啡\""));
    }

    /// <summary>
    /// 项目名进了想法的索引列（搜项目名要能搜到想法），所以**项目改名**要把该项目下
    /// 所有想法的索引行重建一遍：旧项目名搜不到了，新项目名搜得到。
    /// </summary>
    [Fact]
    public async Task Rename_project_updates_the_index_of_its_ideas()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "旧项目");
        var id = await CreateIdeaAsync(client, "手冲记录", projectId);
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        // 想法正文里没有项目名，命中只能来自索引的项目名列。
        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "idea_fts", "\"旧 项 目\""));

        var renamed = await client.PatchAsJsonAsync($"/api/projects/{projectId}", new { name = "新项目" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        Assert.Empty(MatchFts(factory.DatabasePath, "idea_fts", "\"旧 项 目\""));
        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "idea_fts", "\"新 项 目\""));
    }

    /// <summary>
    /// 硬删之后再建想法：新想法的正文搜得到，旧想法的正文搜不到
    /// （硬删时索引行必须一起删掉，否则会串到新想法的 rowid 上）。
    /// </summary>
    [Fact]
    public async Task Hard_delete_then_create_does_not_reuse_a_stale_row()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var old = await CreateIdeaAsync(client, "旧稿内容");
        var other = await CreateIdeaAsync(client, "另一条想法");

        // 合并时**显式给正文**：默认的合并结果会把源想法的正文拼进去，
        // 那样「旧稿」还在新想法的正文里，搜不到它就分不清是索引删干净了
        // 还是文本被搬走了。
        var merged = await client.PostAsJsonAsync(
            "/api/ideas/merge", new { ids = new[] { old, other }, content = "合并结果" });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        var fresh = await CreateIdeaAsync(client, "新稿内容");

        // 新想法的正文搜得到。
        Assert.Equal(
            new[] { RowIdOfIdea(factory.DatabasePath, fresh) },
            MatchFts(factory.DatabasePath, "idea_fts", "\"新 稿\""));

        // 旧想法的正文搜不到。
        Assert.Empty(MatchFts(factory.DatabasePath, "idea_fts", "\"旧 稿\""));
    }

    /// <summary>
    /// 回收站到期清理是裸 SQL 批量删，绕过仓储层，所以要单独验证索引行也被删了
    /// （否则留下孤儿行，而 rowid 会被复用）。
    /// </summary>
    [Fact]
    public async Task Trash_cleanup_removes_the_index_rows()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "过期的想法");
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        // 软删：行还在（也在索引里）。
        var deleted = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 前置断言：清理**之前**索引行确实在（不然「清理后查不到」是白成立的）。
        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "idea_fts", rowId));

        // 直接改库，把删除时刻挪到 10 天前（保留期 1 天）。
        SetDeletedAt(factory.DatabasePath, "idea", id, DateTimeOffset.UtcNow.AddDays(-10));

        var worker = new TrashCleanupWorker(
            new SqliteDatabase(factory.DatabasePath),
            Options.Create(new AppOptions { Trash = new TrashOptions { RetentionDays = 1 } }),
            NullLogger<TrashCleanupWorker>.Instance);
        worker.Cleanup();

        // 业务行没了，索引行也必须没了。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea WHERE id = $value;", id));
        Assert.Equal(0, CountFtsRow(factory.DatabasePath, "idea_fts", rowId));
    }

    /// <summary>
    /// 新建的标签进标签索引（<c>tag_fts</c>）：标签名是要能搜到的。
    /// </summary>
    /// <remarks>
    /// 标签字典只增不改，所以标签索引行一旦写入就不用再动 ——
    /// 但**新建**标签的那一刻必须写，否则它永远不在索引里（回填只在版本变化时跑）。
    /// </remarks>
    [Fact]
    public async Task New_tag_is_indexed()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateIdeaAsync(client, "手冲记录", tags: new[] { "烘焙" });

        // tag_fts 的 rowid 是标签字典行的 id（规格 §8.1 的 rowid 映射）。
        var tagId = QuerySingleInt(factory.DatabasePath, "SELECT id FROM tag WHERE name = $value;", "烘焙");

        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "tag_fts", tagId));
        Assert.Equal(new[] { tagId }, MatchFts(factory.DatabasePath, "tag_fts", "\"烘 焙\""));
    }

    /// <summary>新建的项目进项目索引（<c>project_fts</c>）：项目名是要能搜到的。</summary>
    [Fact]
    public async Task Create_project_is_indexed()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "手冲工坊");

        // project_fts 的 rowid 是项目行的 rowid（规格 §8.1 的 rowid 映射）。
        var rowId = QuerySingleInt(factory.DatabasePath, "SELECT rowid FROM project WHERE id = $value;", projectId);

        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "project_fts", rowId));
        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "project_fts", "\"手 冲 工 坊\""));
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(
        HttpClient client, string content, string? projectId = null, string[]? tags = null)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content, projectId, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadIdAsync(response);
    }

    /// <summary>读响应体里的 id。</summary>
    private static async Task<string> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// 跑一条 MATCH 查询，返回命中的行号（rowid）。
    /// </summary>
    /// <param name="databasePath">库文件路径。</param>
    /// <param name="table">索引表名（<c>idea_fts</c> / <c>project_fts</c> / <c>tag_fts</c>）。</param>
    /// <param name="matchExpression">MATCH 表达式（词元短语，如 <c>"咖 啡"</c>）。</param>
    private static IReadOnlyList<long> MatchFts(string databasePath, string table, string matchExpression)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid FROM {table} WHERE {table} MATCH $q;";
        command.Parameters.AddWithValue("$q", matchExpression);

        var rowIds = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rowIds.Add(reader.GetInt64(0));
        }

        return rowIds;
    }

    /// <summary>取想法行的 rowid（索引行靠它与业务行一一对应）。</summary>
    private static long RowIdOfIdea(string databasePath, string ideaId)
    {
        return QuerySingleInt(databasePath, "SELECT rowid FROM idea WHERE id = $value;", ideaId);
    }

    /// <summary>数一数索引表里有没有某一行。</summary>
    private static int CountFtsRow(string databasePath, string table, long rowId)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE rowid = $rowId;";
        command.Parameters.AddWithValue("$rowId", rowId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>跑一条只返回一行的查询，取它的整数值（参数名固定是 <c>$value</c>）。</summary>
    private static long QuerySingleInt(string databasePath, string sql, string value)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>直接改库，把某条记录的删除时刻挪到指定时刻（用来造过期条目）。</summary>
    private static void SetDeletedAt(string databasePath, string table, string id, DateTimeOffset at)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET deleted_at = $at WHERE id = $id;";
        command.Parameters.AddWithValue("$at", SqliteDatabase.FormatTimestamp(at));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>打开库文件（只读查询用，不带应用的 PRAGMA）。</summary>
    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }
}
