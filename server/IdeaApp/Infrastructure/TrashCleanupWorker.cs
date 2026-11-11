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
    /// 定时清理：启动时立刻跑一次，之后每小时一次。
    /// </summary>
    /// <remarks>
    /// 启动时先跑一次：进程可能停了很久（机器休眠、容器被暂停），
    /// 启动时补一次才对得上「到期即清理」的约定。
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunCleanup();

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
    /// </remarks>
    public int Cleanup()
    {
        var retentionDays = Math.Max(0, options.Value.Trash.RetentionDays);
        var cutoff = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-retentionDays));

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        // 1) 过期的顶层想法：已删，且它的项目没被删
        //    （项目被删时它下面的想法在容器里，随容器一起清理）。
        var topLevelIdeas = Execute(connection, transaction,
            "DELETE FROM idea WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff " +
            "AND project_id NOT IN (SELECT id FROM project WHERE deleted_at IS NOT NULL);",
            cutoff);

        // 2) 过期容器项目下的全部想法（想法的删除时刻可能比项目新：
        //    比如想法被单独恢复过又删掉，但项目已经过期 —— 容器没了，想法也不能留下）。
        var containerIdeas = Execute(connection, transaction,
            "DELETE FROM idea WHERE project_id IN " +
            "(SELECT id FROM project WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff);",
            cutoff);

        // 3) 过期的容器项目行（该项目下的 webhook 靠外键的 ON DELETE CASCADE 带走）。
        var projects = Execute(connection, transaction,
            "DELETE FROM project WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff;",
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
