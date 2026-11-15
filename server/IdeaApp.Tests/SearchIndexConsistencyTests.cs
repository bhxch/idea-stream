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
/// 有一条例外：<c>Hard_delete_rolls_back_the_index_delete_when_the_business_delete_fails</c>
/// 直接调仓储的 <c>HardDelete</c> —— 合并路径整个包在外层事务里，
/// 在那条路径上看不出「硬删自己开的事务」与「各自自动提交」的区别。
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
    /// 索引里的标签列必须用**库里读回来的排序版**标签名，不是调用方给的那份顺序。
    /// <para>
    /// 为什么要较这个真：老库回填（<c>SearchIndexInitializer</c>）是把标签名
    /// **按名称排序后**再拼的，增量写必须逐字节一致 ——
    /// 顺序不一样的话，普通匹配不受影响，但**跨两个标签的短语查询**会时灵时不灵。
    /// </para>
    /// <para>
    /// 用**逆序**的两个标签：排序前后的文本不一样，才分得出用的是哪一份。
    /// 一个标签时排序与不排序没有区别，那种用例钉不住这条要求。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Index_stores_the_tags_in_the_sorted_order()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 故意用逆序的两个标签。
        var id = await CreateIdeaAsync(client, "手冲记录", tags: new[] { "zebra", "Apple" });
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        // 期望值 = 按名称排序（大小写不敏感）后的标签名拼成的词元串（"apple zebra"）——
        // 与回填路径写出的文本逐字节相同。排序规则：t.name COLLATE NOCASE ASC, t.name ASC。
        var expected = SearchIndex.ToIndexText(string.Join(" ", new[] { "Apple", "zebra" }));

        // 建想法的路径：调用方给的是逆序，索引里存的必须是排序版。
        Assert.Equal(expected, ReadFtsText(factory.DatabasePath, "idea_fts", rowId, "tags"));

        // 换标签的路径：用户给的顺序还是逆序，索引里存的仍然必须是排序版。
        var set = await client.PutAsJsonAsync($"/api/ideas/{id}/tags", new { tags = new[] { "zebra", "Apple" } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.Equal(expected, ReadFtsText(factory.DatabasePath, "idea_fts", rowId, "tags"));
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
    /// <para>
    /// 项目**自己**的索引行（<c>project_fts</c>）也要跟着改名 ——
    /// 不改的话，搜新项目名搜不到这个项目、搜旧项目名反倒搜得到。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rename_project_updates_the_index_of_its_ideas()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "旧项目");
        var id = await CreateIdeaAsync(client, "手冲记录", projectId);
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        // project_fts 的 rowid 是项目行的 rowid（规格 §8.1 的 rowid 映射）。
        var projectRowId = QuerySingleInt(
            factory.DatabasePath, "SELECT rowid FROM project WHERE id = $value;", projectId);

        // 想法正文里没有项目名，命中只能来自索引的项目名列。
        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "idea_fts", "\"旧 项 目\""));

        var renamed = await client.PatchAsJsonAsync($"/api/projects/{projectId}", new { name = "新项目" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        Assert.Empty(MatchFts(factory.DatabasePath, "idea_fts", "\"旧 项 目\""));
        Assert.Equal(new[] { rowId }, MatchFts(factory.DatabasePath, "idea_fts", "\"新 项 目\""));

        // 项目自己的索引行：改名后只能搜到新名字。
        Assert.Equal(new[] { projectRowId }, MatchFts(factory.DatabasePath, "project_fts", "\"新 项 目\""));
        Assert.Empty(MatchFts(factory.DatabasePath, "project_fts", "\"旧 项 目\""));
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
    /// 索引写与业务写必须在**同一个事务**里：索引写失败时，业务写要一起撤销。
    /// <para>
    /// 怎么让它失败而又不动生产代码：把索引表 <c>idea_fts</c> 删掉 ——
    /// 索引写会抛「no such table: idea_fts」。
    /// 每个测试独占一个库文件（<see cref="TestAppFactory"/>），删表只影响本测试。
    /// </para>
    /// <para>
    /// 少了「同一个事务」这条：索引写被挪到业务写之外（提交之后），
    /// 业务行会留在库里而索引里没有它 —— 那条想法再也搜不到，
    /// 而且没有任何异常、没有任何日志。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Index_write_failure_rolls_back_the_business_write()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 前置断言：索引表确实在（宿主已启动、回填已跑过），不然删的是一张不存在的表。
        Assert.Equal(1, QueryScalarInt(factory.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'idea_fts';"));

        // 删掉索引表：索引写会抛「no such table: idea_fts」。
        ExecuteSql(factory.DatabasePath, "DROP TABLE idea_fts;");

        var response = await client.PostAsJsonAsync("/api/ideas", new { content = "咖啡机滤芯" });

        // 索引写失败 → 整笔写入回滚 → 500（失败也有稳定的机器可读错误码）。
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", await ReadProblemCodeAsync(response));

        // 业务行也没了：索引写与业务写同生共死。
        Assert.Equal(0, QueryScalarInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea;"));
        Assert.Empty(await ReadIdeaIdsAsync(client));
    }

    /// <summary>
    /// 合并走的是硬删源想法的路径：合并过程中的索引写失败时，整笔合并要回滚 ——
    /// 源想法不能被删掉（源没了、结果也没建成的中间态最坏）。
    /// <para>
    /// 制造失败的手段与上一条一样：删掉索引表，让索引写抛「no such table: idea_fts」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Index_write_failure_rolls_back_the_merge()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条想法");
        var second = await CreateIdeaAsync(client, "第二条想法");

        ExecuteSql(factory.DatabasePath, "DROP TABLE idea_fts;");

        var merged = await client.PostAsJsonAsync("/api/ideas/merge", new { ids = new[] { first, second } });

        Assert.Equal(HttpStatusCode.InternalServerError, merged.StatusCode);
        Assert.Equal("internal_error", await ReadProblemCodeAsync(merged));

        // 两条源想法的业务行都还在：索引写失败把整笔合并回滚了。
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea WHERE id = $value;", first));
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea WHERE id = $value;", second));

        // 合并结果也没建成（库里只有那两条源想法）。
        Assert.Equal(2, QueryScalarInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea;"));
    }

    /// <summary>
    /// 硬删的「删索引」与「删业务行」必须在**同一个事务**里：
    /// 业务删失败时，索引删也要一起撤销。
    /// <para>
    /// 这条只能直接调仓储：合并路径整个包在外层事务里（<c>repo.InTransaction</c>），
    /// 那样「同事务」与「各自自动提交」的结果完全一样，看不出区别。
    /// 直接调仓储时硬删自己开事务，两种写法的差别才露得出来。
    /// </para>
    /// <para>
    /// 制造失败的手段：给 <c>idea</c> 表挂一个触发器，删到那条想法时中止 ——
    /// 于是「删索引」已经执行、「删业务行」失败。
    /// 如果这两步不在同一个事务里（各自自动提交），索引行就真被删掉了，
    /// 而业务行还在 —— 留下一条占着 rowid 的孤儿索引行。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Hard_delete_rolls_back_the_index_delete_when_the_business_delete_fails()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "会被硬删的想法");
        var rowId = RowIdOfIdea(factory.DatabasePath, id);

        // 触发器里不能用绑定变量，id 直接写死在 DDL 里。
        ExecuteSql(factory.DatabasePath,
            $"CREATE TRIGGER block_delete BEFORE DELETE ON idea WHEN OLD.id = '{id}' " +
            "BEGIN SELECT RAISE(ABORT, 'blocked'); END;");

        var repository = new IdeaRepository(new SqliteDatabase(factory.DatabasePath));

        // 业务删被触发器挡住了 → 整笔硬删失败。
        Assert.Throws<SqliteException>(() => repository.HardDelete(id));

        // 业务行还在（删除被挡住）。
        Assert.Equal(1, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea WHERE id = $value;", id));

        // 索引行也还在：索引删与业务删同生共死，业务删失败 → 索引删被撤销。
        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "idea_fts", rowId));
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
    /// 到期清理有**三条**分支：过期的顶层想法、过期**容器项目**下的全部想法、
    /// 过期容器项目自己。上一条用例只走了第一条分支 ——
    /// 后两条（按项目的删除时刻判定）一条都没跑到，
    /// 少写一条删索引的语句就留着成百上千条孤儿索引行。
    /// <para>
    /// 容器项目到期时，它下面的想法**一条都不能留**（哪怕想法自己的删除时刻还很新）：
    /// 容器没了，挂在它下面的想法也不能留下。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Trash_cleanup_removes_the_index_rows_of_an_expired_project()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "过期项目");
        var ideaId = await CreateIdeaAsync(client, "容器里的想法", projectId);

        var projectRowId = QuerySingleInt(
            factory.DatabasePath, "SELECT rowid FROM project WHERE id = $value;", projectId);
        var ideaRowId = RowIdOfIdea(factory.DatabasePath, ideaId);

        // 软删项目：项目与它下面的想法一起进回收站（索引行不动，还在索引里）。
        var deleted = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 前置断言：清理**之前**两条索引行都在（不然「清理后查不到」是白成立的）。
        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "project_fts", projectRowId));
        Assert.Equal(1, CountFtsRow(factory.DatabasePath, "idea_fts", ideaRowId));

        // 直接改库，把**项目**的删除时刻挪到 10 天前（保留期 1 天）→ 容器项目到期。
        // 想法自己的删除时刻还是「刚刚」（它单独没到期）：它只能被**容器分支**带走。
        SetDeletedAt(factory.DatabasePath, "project", projectId, DateTimeOffset.UtcNow.AddDays(-10));

        var worker = new TrashCleanupWorker(
            new SqliteDatabase(factory.DatabasePath),
            Options.Create(new AppOptions { Trash = new TrashOptions { RetentionDays = 1 } }),
            NullLogger<TrashCleanupWorker>.Instance);
        worker.Cleanup();

        // 业务行没了：项目行与它下面的想法行都被清掉。
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM project WHERE id = $value;", projectId));
        Assert.Equal(0, QuerySingleInt(factory.DatabasePath, "SELECT COUNT(*) FROM idea WHERE id = $value;", ideaId));

        // 索引行也必须没了：留着的孤儿行占着 rowid，而 rowid 会被复用。
        Assert.Equal(0, CountFtsRow(factory.DatabasePath, "project_fts", projectRowId));
        Assert.Equal(0, CountFtsRow(factory.DatabasePath, "idea_fts", ideaRowId));
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

    /// <summary>跑一条不带参数的只返回一行的查询，取它的整数值。</summary>
    private static long QueryScalarInt(string databasePath, string sql)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>读索引行里某一列的文本（索引表名与列名都是写死的字面量）。</summary>
    private static string ReadFtsText(string databasePath, string table, long rowId, string column)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE rowid = $rowId;";
        command.Parameters.AddWithValue("$rowId", rowId);
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>执行一条裸 SQL（改库用：删表、挂触发器造故障）。</summary>
    private static void ExecuteSql(string databasePath, string sql)
    {
        using var connection = OpenDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>读想法列表里的 id（裸数组）。</summary>
    private static async Task<IReadOnlyList<string>> ReadIdeaIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/ideas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToList();
    }

    /// <summary>读错误响应里的机器可读错误码（<c>code</c> 扩展字段）。</summary>
    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
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
