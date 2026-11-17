using System.Globalization;
using IdeaApp.Domain;
using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 「索引写入被跳过」时的 warning 测试。
///
/// <para>
/// <see cref="SearchIndex.UpsertIdea"/> 在 idea 行不存在时返回 false（跳过，不抛异常）。
/// 仓储层（<see cref="IdeaRepository"/>）拿这个返回值记一条 warning ——
/// 跳过本身不该打断整批写入，但「这条想法从此搜不到」不能没有痕迹：
/// 没有异常、没有日志的话，只能靠人肉排查（规格 §10.1）。
/// </para>
///
/// <para>
/// ⚠️ <strong>事务路径才是真正写索引的那条路</strong>：事务里用的仓储是
/// <see cref="SqliteDatabase.InTransaction{T}"/> 现造的，logger 必须跟着进去，
/// 否则那条 warning 永远不响（仓储自己开的那个 logger 是空的）。
/// 所以这里的主用例走 <see cref="IdeaRepository.InTransaction{T}"/>。
/// </para>
///
/// <para>
/// 「idea 行不存在」这个场景用**触发器**造，不用改生产代码：
/// 触发器在 <c>idea</c> 行插入之后立刻把它删掉，仓储写索引时就找不到业务行了。
/// </para>
/// </summary>
public sealed class IndexSkippedWarningTests : IDisposable
{
    private readonly string _tempDirectory;

    public IndexSkippedWarningTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_tempDirectory, "idea.db");
        Database = new SqliteDatabase(DatabasePath);
        Database.Initialize();

        // 想法要挂在某个项目下（idea.project_id 有外键），项目名叫什么不影响这组用例。
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO project(id, name, sort_order, deleted_at, created_at, updated_at) " +
            "VALUES ('p1', '工作', 0, NULL, $now, $now);";
        command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
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
    /// 事务路径：索引写入被跳过时，事务里那个仓储也要把 warning 记下来。
    /// </summary>
    [Fact]
    public void Skipped_index_write_logs_a_warning_inside_a_transaction()
    {
        DropTheIdeaRightAfterInsert();

        var logger = new RecordingLogger<IdeaRepository>();
        var repository = new IdeaRepository(Database, logger);

        // 事务路径：事务里现造的仓储拿到的 logger 就是这个。
        repository.InTransaction(ideas =>
        {
            ideas.Insert(NewIdea());
        });

        var warning = Assert.Single(logger.Warnings);

        // warning 里必须带 idea id 与操作名 —— 没有它们就不知道是哪条想法、哪一步出的问题。
        Assert.Contains("idea-1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Insert", warning.Message, StringComparison.Ordinal);

        // 前提核对：索引行确实没写进去（这才是「被跳过」）。
        Assert.Equal(0, CountIdeaFts());
    }

    /// <summary>
    /// 不在事务里时（仓储自己开事务）也要记 warning ——
    /// 两条路径的仓储是同一个类，行为不该有差别。
    /// </summary>
    [Fact]
    public void Skipped_index_write_logs_a_warning_outside_a_transaction()
    {
        DropTheIdeaRightAfterInsert();

        var logger = new RecordingLogger<IdeaRepository>();
        var repository = new IdeaRepository(Database, logger);

        repository.Insert(NewIdea());

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("idea-1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Insert", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 索引**写成功**时不记 warning：warning 是「出事了」的信号，
    /// 成功路径也喊一声的话，这条日志就没人看了。
    /// </summary>
    [Fact]
    public void Successful_index_write_does_not_log_a_warning()
    {
        var logger = new RecordingLogger<IdeaRepository>();
        var repository = new IdeaRepository(Database, logger);

        repository.InTransaction(ideas =>
        {
            ideas.Insert(NewIdea());
        });

        Assert.Empty(logger.Warnings);

        // 前提核对：索引行确实写进去了（不然「没记 warning」是因为压根没走到那一步）。
        Assert.Equal(1, CountIdeaFts());
    }

    /// <summary>
    /// 挂一个触发器：想法行一插入就被删掉 ——
    /// 仓储写索引时 business 行已经不在了，<see cref="SearchIndex.UpsertIdea"/> 只能跳过。
    /// </summary>
    private void DropTheIdeaRightAfterInsert()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();

        // 触发器里不能用绑定变量，id 直接写死在 DDL 里。
        command.CommandText =
            "CREATE TRIGGER drop_idea AFTER INSERT ON idea WHEN NEW.id = 'idea-1' " +
            "BEGIN DELETE FROM idea WHERE id = NEW.id; END;";

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 要插入的想法。
    ///
    /// ⚠️ **不能带标签**：触发器把 idea 行删掉之后，写标签链接那一步会撞外键
    /// （<c>idea_tag.idea_id</c> 指向 <c>idea.id</c>，外键是开着的），
    /// 异常会盖掉我们要测的那条 warning。
    /// </summary>
    private static Idea NewIdea() => new(
        Id: "idea-1",
        ProjectId: "p1",
        Content: "手冲记录",
        Tags: Array.Empty<string>(),
        Done: false,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        DeletedAt: null);

    private int CountIdeaFts()
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM idea_fts;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
