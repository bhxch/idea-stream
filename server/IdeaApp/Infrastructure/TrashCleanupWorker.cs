using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 回收站自动清理：到期条目唯一的出口。
///
/// <para>
/// 保留期只从配置读（<c>Trash:RetentionDays</c>，默认 30 天）：
/// <b>页面与 API 都不提供设置入口</b> —— 保留期是唯一会导致不可逆删除的开关，
/// 绕开它就等于绕开了「不能丢数据」这条约束。
/// </para>
///
/// <para>
/// 清理<b>不产生事件</b>：对象已经不存在了，通知没有意义。
/// </para>
/// </summary>
public sealed class TrashCleanupWorker(
    SqliteDatabase db,
    IOptions<AppOptions> options,
    ILogger<TrashCleanupWorker> logger) : BackgroundService
{
    /// <summary>清理的间隔：每小时一次。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// 保留期的下限：一天。
    /// </summary>
    /// <remarks>
    /// 保留期是本应用**唯一不可逆**的开关：配成 0 等于「下一小时把整个回收站清空」。
    /// 非正值按下限 1 天处理，并在启动时打一条警告让用户知道自己配错了。
    /// </remarks>
    private const int MinimumRetentionDays = 1;

    /// <summary>
    /// 启动：告警 + 立刻清一次。
    /// </summary>
    /// <remarks>
    /// 这两件事放在 <see cref="StartAsync"/> 而不是 <see cref="ExecuteAsync"/>：
    /// .NET 10 的 <see cref="BackgroundService"/> 用 <c>Task.Run</c> 调度 ExecuteAsync，
    /// 它跑在池线程上、与宿主启动并行 ——
    /// <list type="bullet">
    /// <item>清理的写事务会跟第一个请求抢同一个 SQLite 文件（SQLITE_BUSY 抖动）；</item>
    /// <item>警告可能晚到，甚至在紧接着停机时一次都没打出来
    /// （停机取消得比池线程开跑还快时，那个任务压根不会执行）。</item>
    /// </list>
    /// 在 StartAsync 里同步做完：宿主还没开始收请求，状态是确定的，警告也确定发得出去。
    /// </remarks>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        WarnIfRetentionDaysMisconfigured();

        // 启动时先清一次：进程可能停了很久（机器休眠、容器被暂停），
        // 启动时补一次才对得上「到期即清理」的约定。
        RunCleanup();

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// 定时清理：之后每小时一次。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RunCleanup();
            }
        }
        catch (OperationCanceledException)
        {
            // 应用要停了：正常收工。
        }
    }

    /// <summary>保留期配成非正数时提醒用户：那个值没有意义，我们按下限处理了。</summary>
    private void WarnIfRetentionDaysMisconfigured()
    {
        var configured = options.Value.Trash.RetentionDays;

        if (configured <= 0)
        {
            logger.LogWarning(
                "回收站保留期配成了 {RetentionDays}，非正数没有意义 —— 按 {Minimum} 天处理",
                configured,
                MinimumRetentionDays);
        }
    }

    /// <summary>
    /// 三条清理条件。索引删除与业务删除是两条独立的语句，但条件必须出自同一份文本 ——
    /// 各写一遍的话，改一处忘另一处就会留下孤儿索引行，而孤儿行占着 rowid、
    /// rowid 会被复用，后果是索引串行（规格 §10.3）。
    /// </summary>
    private static class CleanupConditions
    {
        /// <summary>过期的顶层想法：已删，且它的项目没被删（对 idea 表）。</summary>
        public const string TopLevelIdeas =
            "deleted_at IS NOT NULL AND deleted_at < $cutoff " +
            "AND project_id NOT IN (SELECT id FROM project WHERE deleted_at IS NOT NULL)";

        /// <summary>过期容器项目下的全部想法（对 idea 表；想法的删除时刻可能比项目新）。</summary>
        public const string ContainerIdeas =
            "project_id IN (SELECT id FROM project WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff)";

        /// <summary>过期的项目行（对 project 表）。</summary>
        public const string ExpiredProjects =
            "deleted_at IS NOT NULL AND deleted_at < $cutoff";
    }

    /// <summary>
    /// 执行一次清理，返回被删除的条目数（想法 + 项目）。
    /// </summary>
    /// <remarks>
    /// 判定标准是删除时刻早于「当前时间 − 保留期」。
    /// <para>
    /// 清理分两批：过期的<b>顶层想法</b>（所属项目还活着的那些）与过期的<b>容器项目</b>
    /// （连同它下面的全部想法）。容器项目必须先删想法再删项目行 ——
    /// <c>idea.project_id</c> 没有 <c>ON DELETE CASCADE</c>，不先删想法项目行就删不掉；
    /// 项目行删掉后，该项目下的 webhook 靠外键的 <c>ON DELETE CASCADE</c> 一起带走。
    /// </para>
    /// <para>
    /// 三条业务 <c>DELETE</c> 之前各有一条删索引的 <c>DELETE</c>：清理是裸 SQL，
    /// 绕过仓储层，不会触发仓储里的索引同步逻辑。索引行必须一起删 ——
    /// 孤儿索引行占着 rowid，而 rowid 会被复用（规格 §10.3）。
    /// 两类语句的条件出自同一份常量（见 <see cref="CleanupConditions"/>）。
    /// </para>
    /// </remarks>
    public int Cleanup()
    {
        // 非正值按下限 1 天处理：0 天等于「下一小时把整个回收站清空」。
        var retentionDays = Math.Max(MinimumRetentionDays, options.Value.Trash.RetentionDays);
        var cutoff = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-retentionDays));

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        // 1) 过期的顶层想法：已删，且它的项目没被删
        //    （项目被删时它下面的想法在容器里，随容器一起清理）。
        //
        //    索引行**先删**：它靠 idea 行取 rowid，idea 行没了就取不到。
        //    清理是绕过仓储层的裸 SQL，不走仓储的索引同步逻辑，所以要单独补上。
        //    留着孤儿索引行不只是脏数据 —— 它占着 rowid，而 idea 表没有显式整数主键、
        //    rowid 会被复用，下一条新想法可能顶到它的位置上，索引就串了（规格 §10.3）。
        Execute(connection, transaction,
            $"DELETE FROM idea_fts WHERE rowid IN (SELECT rowid FROM idea WHERE {CleanupConditions.TopLevelIdeas});",
            cutoff);

        var topLevelIdeas = Execute(connection, transaction,
            $"DELETE FROM idea WHERE {CleanupConditions.TopLevelIdeas};",
            cutoff);

        // 2) 过期容器项目下的全部想法（想法的删除时刻可能比项目新：
        //    比如想法被单独恢复过又删掉，但项目已经过期 —— 容器没了，想法也不能留下）。
        Execute(connection, transaction,
            $"DELETE FROM idea_fts WHERE rowid IN (SELECT rowid FROM idea WHERE {CleanupConditions.ContainerIdeas});",
            cutoff);

        var containerIdeas = Execute(connection, transaction,
            $"DELETE FROM idea WHERE {CleanupConditions.ContainerIdeas};",
            cutoff);

        // 3) 过期的容器项目行（该项目下的 webhook 靠外键的 ON DELETE CASCADE 带走）。
        //    索引行同样先删：它靠 project 行取 rowid。
        Execute(connection, transaction,
            $"DELETE FROM project_fts WHERE rowid IN (SELECT rowid FROM project WHERE {CleanupConditions.ExpiredProjects});",
            cutoff);

        var projects = Execute(connection, transaction,
            $"DELETE FROM project WHERE {CleanupConditions.ExpiredProjects};",
            cutoff);

        transaction.Commit();

        logger.LogInformation(
            "回收站清理完成：删除 {IdeaCount} 条想法、{ProjectCount} 个项目（保留期 {RetentionDays} 天）",
            topLevelIdeas + containerIdeas,
            projects,
            retentionDays);

        return topLevelIdeas + containerIdeas + projects;
    }

    /// <summary>跑一次清理；失败只记日志，不让后台任务把应用带崩。</summary>
    private void RunCleanup()
    {
        try
        {
            Cleanup();
        }
        catch (Exception ex)
        {
            // 条目还在回收站里，下次再试：清理失败不该影响服务可用性。
            logger.LogError(ex, "回收站自动清理失败");
        }
    }

    /// <summary>执行一条删除语句，返回删掉的行数。</summary>
    private static int Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql, string cutoff)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }
}
