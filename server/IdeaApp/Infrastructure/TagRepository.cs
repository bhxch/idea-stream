using IdeaApp.Contracts;
using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 标签字典仓储。标签是自由文本、全局共享的字典：
/// 字典行按大小写不敏感唯一（<c>tag.name UNIQUE COLLATE NOCASE</c>），
/// 名字被软删的想法用过的标签也留在字典里（硬删想法时才随外键级联清掉）。
/// </summary>
public sealed class TagRepository(SqliteDatabase db)
{
    /// <summary>
    /// 列出字典里的全部标签及其使用计数。
    /// </summary>
    /// <remarks>
    /// 计数只算**未删**的想法：想法列表里看不到回收站中的条目，
    /// 计数却把它们算进去的话，筛选栏里的数字会跟点进去看到的条数对不上。
    /// 只被回收站里的想法用过的标签仍在字典里，计数为 0。
    /// </remarks>
    public IReadOnlyList<TagDto> ListWithCounts()
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.name, " +
            "(SELECT COUNT(*) FROM idea_tag it JOIN idea i ON i.id = it.idea_id " +
            " WHERE it.tag_id = t.id AND i.deleted_at IS NULL) " +
            "FROM tag t " +
            "ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;";

        var result = new List<TagDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TagDto(reader.GetString(0), reader.GetInt32(1)));
        }

        return result;
    }

    /// <summary>字典里的全部标签名（按名称排序，大小写不敏感）。</summary>
    public IReadOnlyList<string> AllNames()
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM tag ORDER BY name COLLATE NOCASE ASC, name ASC;";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// 某个项目下**未删**想法用过的标签名（按名称排序，大小写不敏感）。
    /// 项目不存在时返回空列表。
    /// </summary>
    public IReadOnlyList<string> NamesForProject(string projectId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.name FROM tag t " +
            "JOIN idea_tag it ON it.tag_id = t.id " +
            "JOIN idea i ON i.id = it.idea_id " +
            "WHERE i.project_id = $projectId AND i.deleted_at IS NULL " +
            "GROUP BY t.id, t.name " +
            "ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;";
        command.Parameters.AddWithValue("$projectId", projectId);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
