using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// SQLite 连接工厂。负责建库建表、写入内置收件箱，并按需打开带 PRAGMA 的连接。
/// </summary>
public sealed class SqliteDatabase
{
    /// <summary>建表脚本作为嵌入资源的逻辑名。</summary>
    private const string SchemaResourceName = "IdeaApp.Infrastructure.schema.sql";

    /// <summary>时间戳在库中的统一格式：UTC ISO-8601。</summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>
    /// 相对路径的基准目录：**应用目录**（可执行文件所在目录）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不能用当前工作目录（CWD）：用 systemd 跑且**没设** <c>WorkingDirectory</c> 时
    /// CWD 是 <c>/</c>，按 CWD 解析相对路径会把库文件落到 <c>/data/idea.db</c> ——
    /// 不可预期、可能没权限，也可能误伤别的数据。文档承诺的是「相对应用目录」。
    /// </para>
    /// <para>
    /// <c>AppContext.BaseDirectory</c> 在 AOT 单文件发布下是**可执行文件所在的目录**
    /// （例如 <c>/opt/idea/</c>）；在 <c>dotnet run</c> 下是
    /// <c>server/IdeaApp/bin/Debug/net10.0/</c>（不是项目目录）。
    /// 两种情况的实际落点都写在 README 的「配置项」一节里。
    /// </para>
    /// </remarks>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// 建一个指向指定 SQLite 文件的工厂。
    /// </summary>
    /// <param name="databasePath">
    /// 数据库文件路径。相对路径以 <see cref="AppDirectory"/>（应用目录）为基准，
    /// **与当前工作目录无关**；绝对路径原样使用。
    /// </param>
    public SqliteDatabase(string databasePath)
        : this(databasePath, AppDirectory)
    {
    }

    /// <summary>
    /// 建一个指向指定 SQLite 文件的工厂，并显式给出相对路径的基准目录。
    /// </summary>
    /// <param name="databasePath">数据库文件路径（相对或绝对）。</param>
    /// <param name="baseDirectory">相对路径的基准目录。</param>
    internal SqliteDatabase(string databasePath, string baseDirectory)
    {
        DatabasePath = ResolveDatabasePath(databasePath, baseDirectory);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>数据库文件的绝对路径（相对路径以应用目录为基准）。</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 打开一个连接，并已执行 <c>PRAGMA busy_timeout</c>、<c>PRAGMA journal_mode=WAL</c>
    /// 与 <c>PRAGMA foreign_keys=ON</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>busy_timeout</c> 的值与设计文档 §16 的描述一致（30 秒）：写锁被占时 SQLite 自己等，
    /// 而不是立刻回 <c>SQLITE_BUSY</c>。
    /// </para>
    /// <para>
    /// ⚠️ <strong>它没有改变任何可观察行为</strong>：Microsoft.Data.Sqlite 的托管层本来就会重试
    /// （<c>CommandTimeout</c> 默认 30 秒 —— 实测：另一个连接占着写锁时，
    /// 托管层会一直重试到 <c>CommandTimeout</c> 用完才抛 <c>SQLITE_BUSY</c>）。
    /// 显式设这条 pragma 只是把「等多久」这件事显式化到数据库一层，让它在本文件里可见、可调；
    /// 它**不是**「修掉了 400 抖动」之类的修复。
    /// </para>
    /// <para>
    /// <strong>为什么它是第一条 pragma</strong>：<c>journal_mode=WAL</c> 的切换是要拿锁的操作
    /// （从 delete 模式切到 WAL 要改库头）。排在 <c>busy_timeout</c> 后面的话，
    /// 那一刻还没有超时设置 —— 拿不到锁就直接 <c>SQLITE_BUSY</c>，享受不到这个等待。
    /// </para>
    /// </remarks>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        connection.Open();

        // busy_timeout 必须是第一条：journal_mode=WAL 的切换要拿锁，
        // 排在它后面的话，切换那一刻还没有超时设置。
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=30000";
            pragma.ExecuteNonQuery();
        }

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
    /// 在一个 SQLite 事务里执行一组写操作：全部成功才提交，任一步抛异常则整体回滚。
    /// 回调拿到两条**绑定在该事务上**的仓储：它们之间的读能看到彼此未提交的写入，
    /// 因此「改项目行 + 改想法行」这类跨表操作可以是原子的。
    /// </summary>
    /// <param name="work">要同生共死的一组写操作。</param>
    /// <param name="ideaLogger">
    /// 想法仓储的日志器（可空）。事务绑定的想法仓储是这里现造的 ——
    /// 「索引写入被跳过」的 warning 从它那里发，logger 不传进去就永远不响。
    /// </param>
    /// <remarks>
    /// 事务边界由调用方划定：调用方把要同生共死的一组写操作圈进这个回调，
    /// 仓储只负责在给定的事务里执行语句。
    /// <para>
    /// 回收站用它做两件事：删项目时连带软删它下面的想法、
    /// 恢复项目时连带恢复「因删项目而删」的想法 —— 两者都要项目行与想法行同时生效，
    /// 中途失败会留下「项目回来了、想法还在回收站」这种半截状态。
    /// </para>
    /// </remarks>
    public T InTransaction<T>(
        Func<IdeaRepository, ProjectRepository, T> work,
        ILogger<IdeaRepository>? ideaLogger = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var result = work(
            new IdeaRepository(this, connection, transaction, ideaLogger),
            new ProjectRepository(this, connection, transaction));

        transaction.Commit();
        return result;
    }

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

    /// <summary>
    /// 解析数据库路径：相对路径以 <paramref name="baseDirectory"/>（应用目录）为基准。
    /// </summary>
    /// <remarks>
    /// 刻意**不看当前工作目录**：同一份配置在不同的工作目录下必须落在同一个地方
    /// （systemd 不设 <c>WorkingDirectory</c> 时 CWD 是 <c>/</c>）。
    /// </remarks>
    internal static string ResolveDatabasePath(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(path));
        }

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(baseDirectory, path));

        return fullPath;
    }
}
