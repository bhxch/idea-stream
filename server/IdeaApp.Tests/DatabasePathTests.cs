using IdeaApp.Infrastructure;

namespace IdeaApp.Tests;

/// <summary>
/// 数据库路径解析的测试。
///
/// <para>
/// 文档承诺的是「<c>Database:Path</c> 的相对路径以**应用目录**为基准」，
/// 而代码曾经按**当前工作目录**解析 —— 两者不是一回事：
/// 用 systemd 跑且没设 <c>WorkingDirectory</c> 时 CWD 是 <c>/</c>，
/// 库文件会落到 <c>/data/idea.db</c>（不可预期、可能没权限、也可能误伤别的数据）。
/// </para>
/// </summary>
public sealed class DatabasePathTests
{
    /// <summary>
    /// 相对路径挂在**应用目录**下，不挂在当前工作目录下。
    /// </summary>
    [Fact]
    public void Relative_path_is_anchored_to_the_app_directory()
    {
        var appDirectory = Path.Combine(Path.GetTempPath(), "idea-app-dir");

        var resolved = SqliteDatabase.ResolveDatabasePath("data/idea.db", appDirectory);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(appDirectory, "data", "idea.db")),
            resolved);
    }

    /// <summary>
    /// 解析结果不随当前工作目录变化。
    /// </summary>
    /// <remarks>
    /// 解析函数压根不读 CWD（只认显式给出的基准目录）。
    /// 这里**不改**进程的 CWD（那是进程级的，xUnit 会并行跑别的测试类，
    /// 改它可能让别的用例的宿主拿到另一个内容根）—— 改成断言结果不挂在 CWD 下。
    /// </remarks>
    [Fact]
    public void Relative_path_does_not_depend_on_the_current_working_directory()
    {
        var appDirectory = Path.Combine(Path.GetTempPath(), "idea-app-dir");
        var workingDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());

        var resolved = SqliteDatabase.ResolveDatabasePath("data/idea.db", appDirectory);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(appDirectory, "data", "idea.db")),
            resolved);

        // 两个目录不重合时，结果里不会出现工作目录那一段。
        if (!workingDirectory.StartsWith(Path.GetFullPath(appDirectory), StringComparison.Ordinal))
        {
            Assert.DoesNotContain(workingDirectory, resolved);
        }
    }

    /// <summary>
    /// 绝对路径不受影响：照旧按它自己解析。
    /// </summary>
    [Fact]
    public void Absolute_path_is_used_as_is()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "idea-absolute", "idea.db");

        var resolved = SqliteDatabase.ResolveDatabasePath(absolute, "/opt/idea");

        Assert.Equal(Path.GetFullPath(absolute), resolved);
        Assert.DoesNotContain("/opt/idea", resolved);
    }

    /// <summary>
    /// 锚定实际行为：基准目录就是应用目录（<c>AppContext.BaseDirectory</c>）。
    /// </summary>
    /// <remarks>
    /// AOT 单文件发布下它是**可执行文件所在的目录**（例如 <c>/opt/idea/</c>）；
    /// <c>dotnet run</c> 下它是 <c>server/IdeaApp/bin/Debug/net10.0/</c>。
    /// 两种情况的落点都写进了 README 的「配置项」一节。
    /// </remarks>
    [Fact]
    public void The_base_directory_is_the_app_directory()
    {
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(SqliteDatabase.AppDirectory));

        var resolved = SqliteDatabase.ResolveDatabasePath("data/idea.db", SqliteDatabase.AppDirectory);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "idea.db")),
            resolved);
    }
}
