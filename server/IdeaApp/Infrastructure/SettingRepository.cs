using Microsoft.Data.Sqlite;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 设置仓储：极小的键值偏好存储（表 <c>setting</c>）。
/// </summary>
/// <remarks>
/// 只存**应用偏好**（如项目排序方式），不碰配置文件里的任何项 ——
/// 回收站保留期、webhook 配置、公共地址都只能从配置读，
/// 给它们开 API 入口就等于绕开了「不可逆删除由配置说了算」这条约束。
/// </remarks>
public sealed class SettingRepository(SqliteDatabase db)
{
    /// <summary>读一个设置值；没有这个键时返回 <c>null</c>。</summary>
    public string? Get(string key)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        return command.ExecuteScalar() as string;
    }

    /// <summary>写一个设置值（键已存在就覆盖）。</summary>
    public void Set(string key, string value)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO setting(key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        command.ExecuteNonQuery();
    }
}

/// <summary>设置项的键名。</summary>
public static class SettingKeys
{
    /// <summary>项目排序方式（<c>recent</c> / <c>manual</c>）。</summary>
    public const string ProjectSortMode = "project.sort_mode";
}

/// <summary>项目排序方式的取值。</summary>
public static class ProjectSortMode
{
    /// <summary>最近活动的项目在前。</summary>
    public const string Recent = "recent";

    /// <summary>按手动排序（<c>sort_order</c>）。</summary>
    public const string Manual = "manual";

    /// <summary>
    /// 把存库的值收敛成合法的排序方式：只有 <c>manual</c> 算 manual，
    /// 其它（没存这一行、值非法）一律当 <c>recent</c>。
    /// </summary>
    /// <remarks>
    /// 默认值**不播种到库里**：读不到就回落，而不是启动时写一行。
    /// 播种过一次，以后想改默认值就得写迁移；回落则不用。
    /// </remarks>
    public static string Resolve(string? stored) => stored == Manual ? Manual : Recent;
}
