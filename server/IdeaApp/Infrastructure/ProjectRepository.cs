using System.Globalization;
using System.Text;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 项目仓储。手写 SQL，每次操作自己开连接、自己关连接；
/// 被绑定到事务上时（见 <see cref="SqliteDatabase.InTransaction{T}"/>）复用那条连接。
/// </summary>
public sealed class ProjectRepository(SqliteDatabase db)
{
    private const string SelectColumns = "id, name, sort_order, created_at, updated_at, deleted_at";

    // 事务作用域内复用同一条连接；不在事务里时为 null，各方法自己开连接。
    private readonly SqliteConnection? _connection = null;
    private readonly SqliteTransaction? _transaction = null;

    internal ProjectRepository(SqliteDatabase db, SqliteConnection connection, SqliteTransaction transaction)
        : this(db)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>列出未删除的项目，按 <c>sort_order ASC, id ASC</c> 排序。</summary>
    public IReadOnlyList<Project> ListActive()
    {
        return Query((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT {SelectColumns} FROM project WHERE deleted_at IS NULL ORDER BY sort_order ASC, id ASC;";

            var projects = new List<Project>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(Read(reader));
            }

            return projects;
        });
    }

    /// <summary>
    /// 列出回收站里的项目（在回收站里的），按 <c>deleted_at ASC, id ASC</c> 排序
    /// （先进回收站的排前面）。
    /// </summary>
    public IReadOnlyList<Project> ListDeleted()
    {
        return Query((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT {SelectColumns} FROM project WHERE deleted_at IS NOT NULL ORDER BY deleted_at ASC, id ASC;";

            var projects = new List<Project>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(Read(reader));
            }

            return projects;
        });
    }

    /// <summary>按 id 取项目（含回收站里的），找不到返回 <c>null</c>。</summary>
    public Project? Get(string id)
    {
        return Query((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {SelectColumns} FROM project WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });
    }

    /// <summary>新建项目。id 用单调 UUIDv7（32 位无连字符小写 hex）。</summary>
    /// <remarks>
    /// 索引行与业务行在**同一个事务**里写（规格 §10.1）：项目名是要能搜到的。
    /// </remarks>
    public Project Insert(string name, int sortOrder)
    {
        var id = MonotonicGuid.NewUuid7().ToString("N");
        var now = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);

        return Write((connection, transaction) =>
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO project(id, name, sort_order, deleted_at, created_at, updated_at) " +
                    "VALUES ($id, $name, $sortOrder, NULL, $now, $now);";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$sortOrder", sortOrder);
                command.Parameters.AddWithValue("$now", now);
                command.ExecuteNonQuery();
            }

            SearchIndex.UpsertProject(connection, transaction, id, name);

            var timestamp = SqliteDatabase.ParseTimestamp(now);
            return new Project(id, name, sortOrder, timestamp, timestamp, null);
        });
    }

    /// <summary>改项目名，返回改完之后的项目。</summary>
    /// <remarks>
    /// 改名要重建该项目下所有想法的索引行：项目名进了想法的索引列
    /// （搜项目名要能搜到想法），不重建的话搜旧项目名还能搜到、搜新项目名搜不到。
    /// </remarks>
    public Project Rename(string id, string name)
    {
        return Write((connection, transaction) =>
        {
            var now = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE project SET name = $name, updated_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            // 索引行：项目名换了，索引里得跟着换。
            SearchIndex.UpsertProject(connection, transaction, id, name);

            // 该项目下所有想法的索引行（项目名列变了）。
            IdeaRepository.ReindexProjectIdeas(connection, transaction, id, name);

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"SELECT {SelectColumns} FROM project WHERE id = $id;";
            read.Parameters.AddWithValue("$id", id);

            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"项目不存在，无法改名：{id}");
            }

            return Read(reader);
        });
    }

    /// <summary>软删：行还在，只把 <c>deleted_at</c> 置上。</summary>
    public void SoftDelete(string id, DateTimeOffset at)
    {
        Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE project SET deleted_at = $at, updated_at = $at WHERE id = $id;";
            command.Parameters.AddWithValue("$at", SqliteDatabase.FormatTimestamp(at));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>从回收站恢复项目（只恢复项目本身，它下面的想法归回收站逻辑管）。</summary>
    public void Restore(string id)
    {
        Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE project SET deleted_at = NULL, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// 手动排序：ids 里的项目按给定顺序拿到 <c>sort_order = 索引</c>，
    /// 没被列出的项目保持原来的相对顺序，接在它们后面。
    /// 请求里写了库里不存在的 id 时，那一项被忽略（不占索引）。
    /// </summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        Write((connection, transaction) =>
        {
            // 先读出全部项目：没被列出的部分要按现有的 (sort_order, id) 顺序接在后面。
            var all = new List<(string Id, int SortOrder)>();
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT id, sort_order FROM project ORDER BY sort_order ASC, id ASC;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    all.Add((reader.GetString(0), reader.GetInt32(1)));
                }
            }

            var existing = new HashSet<string>(all.Select(p => p.Id), StringComparer.Ordinal);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            var assignments = new List<(string Id, int SortOrder)>();
            foreach (var id in ids)
            {
                if (existing.Contains(id) && listed.Add(id))
                {
                    assignments.Add((id, assignments.Count));
                }
            }

            var tail = assignments.Count;
            foreach (var project in all)
            {
                if (!listed.Contains(project.Id))
                {
                    assignments.Add((project.Id, tail));
                    tail++;
                }
            }

            if (assignments.Count == 0)
            {
                return;
            }

            // 一条 UPDATE 写完所有 sort_order：SQLite 的隐式事务保证它要么全成要么全不成。
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var sql = new StringBuilder("UPDATE project SET sort_order = CASE id");
            for (var i = 0; i < assignments.Count; i++)
            {
                sql.Append($" WHEN $p{i} THEN {assignments[i].SortOrder}");
                command.Parameters.AddWithValue($"$p{i}", assignments[i].Id);
            }

            // 只写 sort_order，不动 updated_at：updated_at 是「这个项目最近有变化」的时间，
            // 也是没有想法时 recent 排序的回落值，被排序动作刷新的话，用户手动调一次
            // 顺序排序结果就变了。
            sql.Append(" ELSE sort_order END WHERE id IN (");
            for (var i = 0; i < assignments.Count; i++)
            {
                sql.Append(i == 0 ? "$p0" : $",$p{i}");
            }

            sql.Append(");");
            command.CommandText = sql.ToString();
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// 全表（含回收站里的项目）里最大的 <c>sort_order</c>；表为空时返回 -1。
    /// 新建项目拿它 +1 当作自己的 <c>sort_order</c>。
    /// </summary>
    public int MaxSortOrder()
    {
        return Query((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) FROM project;";

            var value = command.ExecuteScalar();
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        });
    }

    /// <summary>
    /// 项目的最近活动时间：项目下**未删**想法里最大的 <c>updated_at</c>；
    /// 没有未删想法时回落到项目自身的 <c>updated_at</c>。
    /// </summary>
    /// <remarks>
    /// 契约：**返回非空**（项目不存在时抛 <see cref="InvalidOperationException"/>）。
    /// 调用方可以直接依赖这个值，不要在别处再写一份
    /// 「没有未删想法就回落项目自身 <c>updated_at</c>」的规则 ——
    /// 两处口径分叉的话，前端显示的「最近活动」和 recent 排序会对不上。
    /// </remarks>
    public DateTimeOffset LastActivityAt(string projectId)
    {
        return Query((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT COALESCE(" +
                "(SELECT MAX(updated_at) FROM idea WHERE project_id = $projectId AND deleted_at IS NULL), " +
                "(SELECT updated_at FROM project WHERE id = $projectId));";
            command.Parameters.AddWithValue("$projectId", projectId);

            var value = command.ExecuteScalar() as string
                ?? throw new InvalidOperationException($"项目不存在，无法取最近活动时间：{projectId}");

            return SqliteDatabase.ParseTimestamp(value);
        });
    }

    /// <summary>项目下未删想法的数量。</summary>
    public int CountActiveIdeas(string projectId)
    {
        return Query((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT COUNT(*) FROM idea WHERE project_id = $projectId AND deleted_at IS NULL;";
            command.Parameters.AddWithValue("$projectId", projectId);

            var value = command.ExecuteScalar();
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        });
    }

    private static Project Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Name: reader.GetString(1),
        SortOrder: reader.GetInt32(2),
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(3)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(4)),
        DeletedAt: reader.IsDBNull(5) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(5)));

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
