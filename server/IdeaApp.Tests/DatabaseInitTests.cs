using System.Net;
using IdeaApp.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Tests;

/// <summary>
/// 数据库初始化测试。每个测试用例独占一个临时库目录，互不干扰。
/// </summary>
public sealed class DatabaseInitTests : IDisposable
{
    private readonly string _tempDirectory;

    public DatabaseInitTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "idea-tests", Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_tempDirectory, "idea.db");
        Database = new SqliteDatabase(DatabasePath);
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
    public void Initialize_creates_all_tables_and_indexes()
    {
        Database.Initialize();

        var objects = ReadSchemaObjects();

        foreach (var table in new[]
                 {
                     "project", "idea", "tag", "idea_tag", "webhook", "webhook_delivery", "setting",
                     // 全文索引：三张 FTS5 虚表 + 一张索引版本表。
                     "idea_fts", "project_fts", "tag_fts", "search_meta"
                 })
        {
            Assert.Contains(table, objects);
        }

        foreach (var index in new[] { "ix_idea_list", "ix_idea_tag_tag", "ix_delivery_webhook" })
        {
            Assert.Contains(index, objects);
        }
    }

    [Fact]
    public void Initialize_inserts_inbox_project()
    {
        Database.Initialize();

        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM project WHERE id = 'inbox'";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read(), "收件箱项目应当存在");
        Assert.Equal("inbox", reader.GetString(0));
        Assert.Equal("收件箱", reader.GetString(1));
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        Database.Initialize();
        Database.Initialize();

        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM project WHERE id = 'inbox'";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Open_enables_wal_and_foreign_keys()
    {
        using var connection = Database.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)command.ExecuteScalar());

        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task App_startup_initializes_the_configured_database()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 触发宿主启动（组合根里的 Initialize 会在启动路径上跑）。
        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(File.Exists(factory.DatabasePath), $"临时库应当建在 {factory.DatabasePath}");

        using var connection = new SqliteConnection($"Data Source={factory.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM project WHERE id = 'inbox'";
        Assert.Equal("收件箱", (string?)command.ExecuteScalar());
    }

    private HashSet<string> ReadSchemaObjects()
    {
        var objects = new HashSet<string>(StringComparer.Ordinal);

        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index')";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }
}
