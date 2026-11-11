using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// SQLite 连接工厂。负责建库建表、写入内置收件箱，并按需打开带 PRAGMA 的连接。
/// </summary>
public sealed class SqliteDatabase(string databasePath)
{
    /// <summary>建表脚本作为嵌入资源的逻辑名。</summary>
    private const string SchemaResourceName = "IdeaApp.Infrastructure.schema.sql";

    /// <summary>时间戳在库中的统一格式：UTC ISO-8601。</summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>数据库文件的绝对路径（相对路径按当前工作目录解析）。</summary>
    public string DatabasePath { get; } = ResolveDatabasePath(databasePath);

    /// <summary>
    /// 打开一个连接，并已执行 <c>PRAGMA journal_mode=WAL</c> 与 <c>PRAGMA foreign_keys=ON</c>。
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteScalar();
        }

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>
    /// 建库建表并写入内置收件箱。可重复调用。
    /// </summary>
    public void Initialize()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var statement in ReadSchemaStatements())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO project(id, name, sort_order, deleted_at, created_at, updated_at) " +
                "VALUES ('inbox', '收件箱', 0, NULL, :now, :now);";
            insert.Parameters.AddWithValue(":now", UtcNow());
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>读取嵌入的建表脚本，按分号切成独立语句。</summary>
    private static IEnumerable<string> ReadSchemaStatements()
    {
        using var stream = typeof(SqliteDatabase).Assembly.GetManifestResourceStream(SchemaResourceName)
                           ?? throw new InvalidOperationException($"找不到嵌入资源：{SchemaResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = reader.ReadToEnd();

        foreach (var fragment in sql.Split(';'))
        {
            var statement = fragment.Trim();
            if (statement.Length > 0)
            {
                yield return statement;
            }
        }
    }

    private static string UtcNow() => FormatTimestamp(DateTimeOffset.UtcNow);

    /// <summary>
    /// 把时间点转成库里的存储格式（UTC ISO-8601，7 位小数）。
    /// 仓储层写库时统一走这里，格式只有这一处定义。
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析库里的时间戳字符串。库里一律是 UTC，解析时也按 UTC 处理，
    /// 免得本机时区把偏移量带偏。
    /// </summary>
    public static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>相对路径按当前工作目录解析，并确保所在目录存在。</summary>
    private static string ResolveDatabasePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return fullPath;
    }
}
