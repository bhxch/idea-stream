using System.Globalization;
using System.Text;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 想法仓储。手写 SQL，每次操作自己开连接、自己关连接。
///
/// 一次业务写操作往往要改多张表（idea 行 + idea_tag），这必须是一个事务。
/// <para>
/// <b>事务边界由调用方划定</b>：调用方通过 <see cref="InTransaction{T}"/> 把一组写操作
/// 圈进同一个事务，仓储只负责在给定的事务里执行语句；
/// 单独调用一个多语句写方法时（没有外层事务），仓储自己兜一个事务，
/// 避免出现「写了一半」。
/// </para>
/// <para>
/// <see cref="InTransaction{T}"/> 存在的理由：仓储方法签名里没有连接/事务参数，
/// 要让「更新 idea 行 → 写 idea_tag → 提交」原子完成，唯一的办法就是让调用方
/// 拿到一个绑定在同一事务上的仓储。
/// </para>
/// </summary>
public sealed class IdeaRepository(SqliteDatabase db)
{
    private const string SelectColumns =
        "id, project_id, title, content, done, created_at, updated_at, deleted_at";

    // 事务作用域内复用同一条连接；不在事务里时为 null，各方法自己开连接。
    private readonly SqliteConnection? _connection = null;
    private readonly SqliteTransaction? _transaction = null;

    internal IdeaRepository(SqliteDatabase db, SqliteConnection connection, SqliteTransaction transaction)
        : this(db)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>
    /// 在一个 SQLite 事务里执行一组写操作：全部成功才提交，任一步抛异常则整体回滚。
    /// 回调拿到的仓储绑定在该事务上，事务里的读写互相可见。
    /// </summary>
    public T InTransaction<T>(Func<IdeaRepository, T> work)
    {
        return db.InTransaction((ideas, _) => work(ideas));
    }

    /// <summary>在一个 SQLite 事务里执行一组写操作（无返回值版本）。</summary>
    public void InTransaction(Action<IdeaRepository> work)
    {
        db.InTransaction((ideas, _) =>
        {
            work(ideas);
            return 0;
        });
    }

    /// <summary>按 id 取一条想法（含回收站里的），带上它的标签。找不到返回 null。</summary>
    public Idea? Get(string id)
    {
        return Query((connection, transaction) =>
        {
            Idea? idea = null;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT {SelectColumns} FROM idea WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    idea = Read(reader);
                }
            }

            return idea is null ? null : idea with { Tags = ReadTagNames(connection, transaction, id) };
        });
    }

    /// <summary>
    /// 列出未删除的想法，按 <c>updated_at ASC, id ASC</c> 排序（最老的在顶、最新的在底）。
    /// </summary>
    /// <remarks>
    /// 这是不带过滤条件的那个重载，等价于 <see cref="List(IdeaQuery)"/> 传
    /// <see cref="IdeaQuery.Everything"/>。
    /// <para>
    /// 排序是全序：<c>updated_at</c> 相同时按 <c>id ASC</c> 兜底，同一批数据的顺序永远一致。
    /// <c>id</c> 是自实现的**单调** UUIDv7（见 <see cref="Domain.MonotonicGuid"/>）——
    /// 同一毫秒内生成的 id 严格递增，所以 <c>id ASC</c> 就是插入先后，
    /// <c>updated_at</c> 打平时也不会出现顺序随机跳动。
    /// </para>
    /// </remarks>
    public IReadOnlyList<Idea> List()
    {
        return List(IdeaQuery.Everything);
    }

    /// <summary>
    /// 按查询条件列出想法，按 <c>updated_at ASC, id ASC</c> 排序（最老的在顶、最新的在底）。
    /// </summary>
    /// <remarks>
    /// 一次 SQL 取候选集（项目 + 未删 + 状态），再在内存里用
    /// <see cref="IdeaFilter.Matches"/> 判标签 —— 标签是「想法的标签集合」与
    /// 「选中的标签集合」的关系，交给内存判比拼 SQL 简单得多。
    /// </remarks>
    public IReadOnlyList<Idea> List(IdeaQuery query)
    {
        return Query((connection, transaction) =>
        {
            var (where, parameters) = BuildFilter(query);

            var ideas = new List<Idea>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"SELECT {SelectColumns} FROM idea WHERE {where} ORDER BY updated_at ASC, id ASC;";

                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ideas.Add(Read(reader));
                }
            }

            // 标签一次性批量取，避免每条想法各查一次。
            var tags = ReadTagNamesOfCandidates(connection, transaction, where, parameters);

            return ideas
                .Select(idea => idea with
                {
                    Tags = tags.TryGetValue(idea.Id, out var names) ? names : Array.Empty<string>()
                })
                .Where(idea => IdeaFilter.Matches(idea, query))
                .ToList();
        });
    }

    /// <summary>
    /// 列出某个项目下**仍在回收站里**的想法（<c>deleted_at</c> 非空），
    /// 按 <c>updated_at ASC, id ASC</c> 排序。
    /// </summary>
    /// <remarks>
    /// 回收站的容器条目用它：已删项目下挂的是它下面**仍在回收站里**的想法 ——
    /// 无论它们是随项目一起删的，还是在项目被删之前就被单独删掉的。
    /// <para>
    /// **不含从未删除的想法**：项目还在回收站时被单独恢复的想法已经回到正常列表，
    /// 它还杵在容器里的话，用户会以为恢复没生效。
    /// </para>
    /// </remarks>
    public IReadOnlyList<Idea> ListDeletedByProject(string projectId)
    {
        return Query((connection, transaction) => ReadByProject(connection, transaction, projectId)
            .Where(idea => idea.DeletedAt is not null)
            .ToList());
    }

    /// <summary>
    /// 回收站里的**顶层**想法：已删，且它的项目**没有**被删。
    /// </summary>
    /// <remarks>
    /// 项目被删时，它下面的想法一律以容器条目的形式出现（不论何时删除），
    /// 这里不再重复列一遍 —— 同一个想法不会在两处同时出现。
    /// </remarks>
    public IReadOnlyList<Idea> ListTopLevelTrash()
    {
        return Query((connection, transaction) =>
        {
            // 顶层 = 已删，且它的项目不在回收站里。
            const string where =
                "deleted_at IS NOT NULL AND project_id NOT IN " +
                "(SELECT id FROM project WHERE deleted_at IS NOT NULL)";

            var ideas = new List<Idea>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"SELECT {SelectColumns} FROM idea WHERE {where} ORDER BY deleted_at ASC, id ASC;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ideas.Add(Read(reader));
                }
            }

            var tags = ReadTagNamesOfCandidates(
                connection,
                transaction,
                where,
                new Dictionary<string, object>(StringComparer.Ordinal));

            return ideas
                .Select(idea => idea with
                {
                    Tags = tags.TryGetValue(idea.Id, out var names) ? names : Array.Empty<string>()
                })
                .ToList();
        });
    }

    /// <summary>
    /// 把项目下**未删**的想法一并软删（<c>deleted_at</c> 置为同一时刻），
    /// 返回被软删的想法（软删之后的样子）。
    /// </summary>
    /// <remarks>
    /// 「未删」是硬要求：项目被删之前就被单独删掉的想法带着自己的删除时刻，
    /// 恢复项目时靠这个时刻区分「是不是随项目一起删的」。
    /// </remarks>
    public IReadOnlyList<Idea> SoftDeleteByProject(string projectId, DateTimeOffset at)
    {
        return Write((connection, transaction) =>
        {
            var affected = ReadByProject(connection, transaction, projectId)
                .Where(idea => idea.DeletedAt is null)
                .ToList();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE idea SET deleted_at = $at, updated_at = $at " +
                    "WHERE project_id = $projectId AND deleted_at IS NULL;";
                command.Parameters.AddWithValue("$at", SqliteDatabase.FormatTimestamp(at));
                command.Parameters.AddWithValue("$projectId", projectId);
                command.ExecuteNonQuery();
            }

            // UPDATE 写的就是 deleted_at / updated_at，回读一次不必要：
            // 直接把这两个字段换成写入的值，结果与回读一致。
            return affected
                .Select(idea => idea with { DeletedAt = at, UpdatedAt = at })
                .ToList();
        });
    }

    /// <summary>
    /// 恢复项目时连带恢复「因删项目而删」的想法，返回被恢复的想法。
    /// </summary>
    /// <remarks>
    /// 判定依据是想法的 <c>deleted_at</c> 与项目的 <c>deleted_at</c> **相同**
    /// （两者在删除项目时被写成同一时刻）。
    /// 在项目被删之前就被单独删掉的想法不复活 —— 它们仍以顶层条目的形式留在回收站里。
    /// </remarks>
    public IReadOnlyList<Idea> RestoreWithProject(string projectId, DateTimeOffset now)
    {
        return Write((connection, transaction) =>
        {
            var projectDeletedAt = ReadProjectDeletedAt(connection, transaction, projectId);

            var affected = ReadByProject(connection, transaction, projectId)
                .Where(idea => projectDeletedAt is not null && idea.DeletedAt == projectDeletedAt)
                .ToList();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE idea SET deleted_at = NULL, updated_at = $now " +
                    "WHERE project_id = $projectId AND deleted_at IS NOT NULL " +
                    "AND deleted_at = (SELECT deleted_at FROM project WHERE id = $projectId);";
                command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(now));
                command.Parameters.AddWithValue("$projectId", projectId);
                command.ExecuteNonQuery();
            }

            return affected
                .Select(idea => idea with { DeletedAt = null, UpdatedAt = now })
                .ToList();
        });
    }

    /// <summary>
    /// 把查询条件翻译成 SQL 的 WHERE 子句与参数。
    /// 候选集与候选集的标签用同一份 WHERE，保证两边口径一致。
    /// </summary>
    private static (string Where, Dictionary<string, object> Parameters) BuildFilter(IdeaQuery query)
    {
        var where = new StringBuilder("deleted_at IS NULL");
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(query.ProjectId))
        {
            where.Append(" AND project_id = $projectId");
            parameters["$projectId"] = query.ProjectId;
        }

        switch (query.Status)
        {
            case IdeaStatusFilter.Open:
                where.Append(" AND done = 0");
                break;
            case IdeaStatusFilter.Done:
                where.Append(" AND done = 1");
                break;
        }

        return (where.ToString(), parameters);
    }

    /// <summary>
    /// 插入一条想法，并写入它的标签链接与全文索引行。
    /// 返回的想法带库里实际的标签名 —— 标签字典按大小写不敏感唯一，
    /// 复用的是字典里最早出现的那一行，写法以它为准。
    /// </summary>
    /// <remarks>
    /// 索引行与业务行在**同一个事务**里写（规格 §10.1）：索引落后就是搜不到，
    /// 索引超前就是搜到不该搜到的，两种偏差都得靠同事务杜绝。
    /// </remarks>
    public Idea Insert(Idea idea)
    {
        return Write((connection, transaction) =>
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO idea(id, project_id, title, content, done, deleted_at, created_at, updated_at) " +
                    "VALUES ($id, $projectId, $title, $content, $done, $deletedAt, $createdAt, $updatedAt);";
                command.Parameters.AddWithValue("$id", idea.Id);
                command.Parameters.AddWithValue("$projectId", idea.ProjectId);
                command.Parameters.AddWithValue("$title", Parameter(idea.Title));
                command.Parameters.AddWithValue("$content", idea.Content);
                command.Parameters.AddWithValue("$done", idea.Done ? 1 : 0);
                command.Parameters.AddWithValue("$deletedAt",
                    idea.DeletedAt is null ? DBNull.Value : SqliteDatabase.FormatTimestamp(idea.DeletedAt.Value));
                command.Parameters.AddWithValue("$createdAt", SqliteDatabase.FormatTimestamp(idea.CreatedAt));
                command.Parameters.AddWithValue("$updatedAt", SqliteDatabase.FormatTimestamp(idea.UpdatedAt));
                command.ExecuteNonQuery();
            }

            WriteTagLinks(connection, transaction, idea.Id, idea.Tags);

            // 标签名用**库里读回来的排序版**，不是调用方给的那份：
            // 老库回填是把标签名按名称排序后拼的（见 SearchIndexInitializer），
            // 增量写必须逐字节一致 —— 否则同一条想法在两条路径下写出的 tags 文本不同，
            // 普通匹配不受影响，但跨两个标签的短语查询会时灵时不灵。
            var tags = ReadTagNames(connection, transaction, idea.Id);

            // 索引行与业务行在同一个事务里写（规格 §10.1）：索引落后就是搜不到，
            // 索引超前就是搜到不该搜到的，两种偏差都得靠同事务杜绝。
            SearchIndex.UpsertIdea(
                connection,
                transaction,
                idea.Id,
                idea.Title,
                idea.Content,
                tags,
                ReadProjectName(connection, transaction, idea.ProjectId));

            return idea with { Tags = tags };
        });
    }

    /// <summary>
    /// 改正文与标题，并刷新 <c>updated_at</c>；索引行跟着更新（标题与正文两列）。
    /// <paramref name="title"/> 为 null 表示把标题写成「无标题」（NULL）。
    /// </summary>
    /// <remarks>
    /// 索引写的是**刚落库的值**：正文改了，索引里就不能还留着旧 token
    /// （旧的搜得到、新的搜不到，比整条搜不到更难查）。
    /// </remarks>
    public Idea UpdateContent(string id, string? title, string content)
    {
        return Write((connection, transaction) =>
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE idea SET title = $title, content = $content, updated_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$title", Parameter(title));
                command.Parameters.AddWithValue("$content", content);
                command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            // 回读一次：索引行要用刚落库的标题与正文，标签与项目名沿用库里的现值。
            var idea = ReadIdea(connection, transaction, id);

            SearchIndex.UpsertIdea(
                connection,
                transaction,
                id,
                idea.Title,
                idea.Content,
                idea.Tags,
                ReadProjectName(connection, transaction, idea.ProjectId));

            return idea;
        });
    }

    /// <summary>设置完成标记，并刷新 <c>updated_at</c>。</summary>
    public Idea SetDone(string id, bool done)
    {
        return Write((connection, transaction) =>
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE idea SET done = $done, updated_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$done", done ? 1 : 0);
                command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            return ReadIdea(connection, transaction, id);
        });
    }

    /// <summary>软删：行还在，只把 <c>deleted_at</c> 置上（并刷新 <c>updated_at</c>）。</summary>
    public void SoftDelete(string id, DateTimeOffset at)
    {
        Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE idea SET deleted_at = $at, updated_at = $at WHERE id = $id;";
            command.Parameters.AddWithValue("$at", SqliteDatabase.FormatTimestamp(at));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>从回收站恢复（只恢复想法本身，它所属项目的恢复归回收站逻辑管）。</summary>
    public void Restore(string id)
    {
        Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE idea SET deleted_at = NULL, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// 彻底删除一行（idea_tag 靠外键 CASCADE 一起清掉），索引行一起删。
    /// </summary>
    /// <remarks>
    /// 索引行必须**一起删**（规格 §10.3）：<c>idea</c> 表没有显式整数主键，
    /// rowid 会被复用 —— 留着孤儿索引行的话，下一条新想法可能顶到它的位置上，索引就串了。
    /// <para>
    /// 两条语句在**同一个事务**里：索引删了、业务行没删的话，那条想法就再也搜不到了
    /// （没有异常、没有日志）。
    /// </para>
    /// </remarks>
    public void HardDelete(string id)
    {
        Write((connection, transaction) =>
        {
            // 顺序不能反：DeleteIdea 靠 idea 行取 rowid，idea 行没了就取不到。
            SearchIndex.DeleteIdea(connection, transaction, id);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM idea WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>取一条想法的标签名（按名称排序，大小写不敏感）。</summary>
    public IReadOnlyList<string> GetTagNames(string ideaId)
    {
        return Query((connection, transaction) => ReadTagNames(connection, transaction, ideaId));
    }

    /// <summary>
    /// 覆盖式设置标签：清掉原有链接后写入新的一批，并刷新 <c>updated_at</c>
    /// （换标签也是一次写操作，必须把条目挪到列表底部）；索引行的标签列跟着更新。
    /// </summary>
    /// <remarks>
    /// 新建的标签还要进标签索引（<c>tag_fts</c>）—— 标签名是要能搜到的。
    /// </remarks>
    public void ReplaceTags(string ideaId, IReadOnlyList<string> tags)
    {
        Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM idea_tag WHERE idea_id = $ideaId;";
                delete.Parameters.AddWithValue("$ideaId", ideaId);
                delete.ExecuteNonQuery();
            }

            WriteTagLinks(connection, transaction, ideaId, tags);

            // 标签名用**库里读回来的排序版**，不是入参那份（那是用户给的顺序）：
            // 回填路径按名称排序后拼，两条路径必须逐字节一致。
            var idea = ReadIdea(connection, transaction, ideaId);

            SearchIndex.UpsertIdea(
                connection,
                transaction,
                ideaId,
                idea.Title,
                idea.Content,
                idea.Tags,
                ReadProjectName(connection, transaction, idea.ProjectId));

            using var touch = connection.CreateCommand();
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE idea SET updated_at = $now WHERE id = $ideaId;";
            touch.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            touch.Parameters.AddWithValue("$ideaId", ideaId);
            touch.ExecuteNonQuery();
        });
    }

    /// <summary>读一条想法（含标签）；不存在时抛异常 —— 调用方应先确认它存在。</summary>
    private static Idea ReadIdea(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        Idea? idea = null;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {SelectColumns} FROM idea WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                idea = Read(reader);
            }
        }

        if (idea is null)
        {
            throw new InvalidOperationException($"想法不存在，无法读取：{id}");
        }

        return idea with { Tags = ReadTagNames(connection, transaction, id) };
    }

    /// <summary>读一条想法的标签名。</summary>
    private static IReadOnlyList<string> ReadTagNames(
        SqliteConnection connection, SqliteTransaction? transaction, string ideaId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT t.name FROM idea_tag it JOIN tag t ON t.id = it.tag_id " +
            "WHERE it.idea_id = $ideaId ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;";
        command.Parameters.AddWithValue("$ideaId", ideaId);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// 读项目名；项目不存在时返回空串。
    /// </summary>
    /// <remarks>
    /// 项目名进了想法的索引列（搜项目名要能搜到想法），
    /// 查不到就写空串 —— 索引里不该出现字典外的项目名。
    /// </remarks>
    private static string ReadProjectName(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM project WHERE id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);

        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>
    /// 重建某个项目下**全部**想法的索引行（项目改名时用）。
    /// </summary>
    /// <param name="connection">数据库连接。</param>
    /// <param name="transaction">所在事务（可为 null = 不在事务里）。</param>
    /// <param name="projectId">项目 id。</param>
    /// <param name="projectName">项目名（写进索引的项目名列）。</param>
    /// <remarks>
    /// 项目名进了想法的索引列，所以项目改名必须把该项目下所有想法的索引行重建一遍：
    /// 不重建的话，搜旧项目名还能搜到、搜新项目名搜不到。
    /// <para>
    /// 回收站里的想法也要重建：软删不动索引，它们照样在索引里。
    /// </para>
    /// <para>
    /// 想法数量是几百到几千级，逐条重建的成本可忽略。
    /// </para>
    /// </remarks>
    internal static void ReindexProjectIdeas(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        string projectName)
    {
        foreach (var ideaId in ReadIdeaIdsByProject(connection, transaction, projectId))
        {
            // 标题与正文从库里读，标签用读回来的排序版（与回填路径一致）。
            var idea = ReadIdea(connection, transaction, ideaId);

            SearchIndex.UpsertIdea(
                connection,
                transaction,
                ideaId,
                idea.Title,
                idea.Content,
                idea.Tags,
                projectName);
        }
    }

    /// <summary>读某个项目下全部想法的 id（含回收站里的）。</summary>
    private static IReadOnlyList<string> ReadIdeaIdsByProject(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM idea WHERE project_id = $projectId ORDER BY rowid;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// 读某个项目下的**全部**想法（含回收站里的），带上标签，
    /// 按 <c>updated_at ASC, id ASC</c> 排序。
    /// </summary>
    private static IReadOnlyList<Idea> ReadByProject(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        var ideas = new List<Idea>();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT {SelectColumns} FROM idea WHERE project_id = $projectId " +
                "ORDER BY updated_at ASC, id ASC;";
            command.Parameters.AddWithValue("$projectId", projectId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ideas.Add(Read(reader));
            }
        }

        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["$projectId"] = projectId
        };

        var tags = ReadTagNamesOfCandidates(connection, transaction, "project_id = $projectId", parameters);

        return ideas
            .Select(idea => idea with
            {
                Tags = tags.TryGetValue(idea.Id, out var names) ? names : Array.Empty<string>()
            })
            .ToList();
    }

    /// <summary>读项目的删除时刻；项目不在回收站里（或不存在）时返回 null。</summary>
    private static DateTimeOffset? ReadProjectDeletedAt(
        SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT deleted_at FROM project WHERE id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var value = command.ExecuteScalar() as string;
        return value is null ? null : SqliteDatabase.ParseTimestamp(value);
    }

    /// <summary>一次性读完候选想法的标签：想法 id → 标签名列表。</summary>
    private static Dictionary<string, string[]> ReadTagNamesOfCandidates(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string where,
        Dictionary<string, object> parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT i.id, t.name FROM idea i " +
            "JOIN idea_tag it ON it.idea_id = i.id " +
            "JOIN tag t ON t.id = it.tag_id " +
            $"WHERE {where} " +
            "ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;";

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ideaId = reader.GetString(0);
            if (!result.TryGetValue(ideaId, out var names))
            {
                names = new List<string>();
                result[ideaId] = names;
            }

            names.Add(reader.GetString(1));
        }

        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// 写入标签链接。标签名在字典里按大小写不敏感唯一：
    /// 已存在就复用那一行（保留它最初的写法），不存在就新建。
    /// </summary>
    private static void WriteTagLinks(
        SqliteConnection connection, SqliteTransaction? transaction, string ideaId, IReadOnlyList<string> tags)
    {
        foreach (var name in tags)
        {
            var tagId = EnsureTag(connection, transaction, name);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR IGNORE INTO idea_tag(idea_id, tag_id) VALUES ($ideaId, $tagId);";
            command.Parameters.AddWithValue("$ideaId", ideaId);
            command.Parameters.AddWithValue("$tagId", tagId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 取标签行的 id；没有就新建一行。
    /// 标签名按大小写不敏感唯一（字典约束），所以复用已有行 —— 写法以字典里最早那次为准。
    /// </summary>
    /// <remarks>
    /// 新建的标签要顺带写进标签索引（<c>tag_fts</c>）：标签名是要能搜到的。
    /// 复用已有行就不必再写 —— 那一行的索引早就在了。
    /// </remarks>
    private static long EnsureTag(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        var existing = FindTagId(connection, transaction, name);
        if (existing is not null)
        {
            return existing.Value;
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            // 带上 OR IGNORE：万一并发插了同一行（名字大小写不同也算同一行），
            // 这里忽略掉，下面统一按名字回查，拿到的一定是字典里那一行。
            insert.CommandText = "INSERT OR IGNORE INTO tag(name) VALUES ($name);";
            insert.Parameters.AddWithValue("$name", name);
            insert.ExecuteNonQuery();
        }

        // 标签索引行与字典行在同一个事务里写（规格 §10.2）。
        // OR IGNORE 让并发插入（上面那句 INSERT 被忽略）的情况也无害：
        // 同一行写两次是幂等的。
        SearchIndex.UpsertTag(connection, transaction, name);

        return FindTagId(connection, transaction, name)
               ?? throw new InvalidOperationException($"标签写入后拿不到 id：{name}");
    }

    /// <summary>按名字（大小写不敏感）查标签行的 id；没有返回 null。</summary>
    private static long? FindTagId(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT id FROM tag WHERE name = $name COLLATE NOCASE;";
        lookup.Parameters.AddWithValue("$name", name);

        var existing = lookup.ExecuteScalar();
        return existing is null ? null : Convert.ToInt64(existing, CultureInfo.InvariantCulture);
    }

    private static Idea Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        ProjectId: reader.GetString(1),
        Title: reader.IsDBNull(2) ? null : reader.GetString(2),
        Content: reader.GetString(3),
        Tags: Array.Empty<string>(),
        Done: reader.GetInt64(4) != 0,
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(5)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(6)),
        DeletedAt: reader.IsDBNull(7) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(7)));

    /// <summary>可空文本按 SQL 的 NULL 传参。</summary>
    private static object Parameter(string? value) => value is null ? DBNull.Value : value;

    /// <summary>执行一条查询。</summary>
    private T Query<T>(Func<SqliteConnection, SqliteTransaction?, T> action)
    {
        if (_connection is not null)
        {
            return action(_connection, _transaction);
        }

        using var connection = db.Open();
        return action(connection, null);
    }

    /// <summary>执行一条写操作（单语句，自带隐式提交）。</summary>
    private void Execute(Action<SqliteConnection, SqliteTransaction?> action)
    {
        if (_connection is not null)
        {
            action(_connection, _transaction);
            return;
        }

        using var connection = db.Open();
        action(connection, null);
    }

    /// <summary>
    /// 执行一组必须同生共死的写操作。
    /// 已在事务里就并入那个事务，否则自己开一个 —— 绝不出现「写了一半」。
    /// </summary>
    private T Write<T>(Func<SqliteConnection, SqliteTransaction?, T> action)
    {
        if (_connection is not null)
        {
            return action(_connection, _transaction);
        }

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        var result = action(connection, transaction);

        transaction.Commit();
        return result;
    }

    /// <summary>执行一组必须同生共死的写操作（无返回值版本）。</summary>
    private void Write(Action<SqliteConnection, SqliteTransaction?> action)
    {
        if (_connection is not null)
        {
            action(_connection, _transaction);
            return;
        }

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        action(connection, transaction);

        transaction.Commit();
    }
}
