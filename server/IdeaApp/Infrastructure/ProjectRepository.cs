using System.Globalization;
using System.Text;
using IdeaApp.Domain;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 项目仓储。手写 SQL，每次操作自己开连接、自己关连接，不自行开事务
/// （事务边界由调用方按业务需要决定）。
/// </summary>
public sealed class ProjectRepository(SqliteDatabase db)
{
    private const string SelectColumns = "id, name, sort_order, created_at, updated_at, deleted_at";

    /// <summary>列出未删除的项目，按 <c>sort_order ASC, id ASC</c> 排序。</summary>
    public IReadOnlyList<Project> ListActive()
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {SelectColumns} FROM project WHERE deleted_at IS NULL ORDER BY sort_order ASC, id ASC;";

        var projects = new List<Project>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(Read(reader));
        }

        return projects;
    }

    /// <summary>按 id 取项目（含回收站里的），找不到返回 <c>null</c>。</summary>
    public Project? Get(string id)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM project WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>新建项目。id 用 UUIDv7（32 位无连字符小写 hex）。</summary>
    public Project Insert(string name, int sortOrder)
    {
        var id = Guid.CreateVersion7().ToString("N");
        var now = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO project(id, name, sort_order, deleted_at, created_at, updated_at) " +
            "VALUES ($id, $name, $sortOrder, NULL, $now, $now);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        var timestamp = SqliteDatabase.ParseTimestamp(now);
        return new Project(id, name, sortOrder, timestamp, timestamp, null);
    }

    /// <summary>改项目名，返回改完之后的项目。</summary>
    public Project Rename(string id, string name)
    {
        var now = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);

        using var connection = db.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE project SET name = $name, updated_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT {SelectColumns} FROM project WHERE id = $id;";
        read.Parameters.AddWithValue("$id", id);

        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"项目不存在，无法改名：{id}");
        }

        return Read(reader);
    }

    /// <summary>软删：行还在，只把 <c>deleted_at</c> 置上。</summary>
    public void SoftDelete(string id, DateTimeOffset at)
    {
        var timestamp = SqliteDatabase.FormatTimestamp(at);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE project SET deleted_at = $at, updated_at = $at WHERE id = $id;";
        command.Parameters.AddWithValue("$at", timestamp);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>从回收站恢复项目（只恢复项目本身，它下面的想法归回收站逻辑管）。</summary>
    public void Restore(string id)
    {
        var now = SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE project SET deleted_at = NULL, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 手动排序：ids 里的项目按给定顺序拿到 <c>sort_order = 索引</c>，
    /// 没被列出的项目保持原来的相对顺序，接在它们后面。
    /// 请求里写了库里不存在的 id 时，那一项被忽略（不占索引）。
    /// </summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        using var connection = db.Open();

        // 先读出全部项目：没被列出的部分要按现有的 (sort_order, id) 顺序接在后面。
        var all = new List<(string Id, int SortOrder)>();
        using (var read = connection.CreateCommand())
        {
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
        var sql = new StringBuilder("UPDATE project SET sort_order = CASE id");
        for (var i = 0; i < assignments.Count; i++)
        {
            sql.Append($" WHEN $p{i} THEN {assignments[i].SortOrder}");
            command.Parameters.AddWithValue($"$p{i}", assignments[i].Id);
        }

        sql.Append(" ELSE sort_order END, updated_at = $now WHERE id IN (");
        for (var i = 0; i < assignments.Count; i++)
        {
            sql.Append(i == 0 ? "$p0" : $",$p{i}");
        }

        sql.Append(");");
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 项目下未删想法里最近一次更新的时间；没有未删想法时返回 <c>null</c>。
    /// </summary>
    public DateTimeOffset? LastActivityAt(string projectId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MAX(updated_at) FROM idea WHERE project_id = $projectId AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var value = command.ExecuteScalar() as string;
        return value is null ? null : SqliteDatabase.ParseTimestamp(value);
    }

    /// <summary>项目下未删想法的数量。</summary>
    public int CountActiveIdeas(string projectId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM idea WHERE project_id = $projectId AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var value = command.ExecuteScalar();
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static Project Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Name: reader.GetString(1),
        SortOrder: reader.GetInt32(2),
        CreatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(3)),
        UpdatedAt: SqliteDatabase.ParseTimestamp(reader.GetString(4)),
        DeletedAt: reader.IsDBNull(5) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(5)));
}
