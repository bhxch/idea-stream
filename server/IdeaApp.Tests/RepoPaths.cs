namespace IdeaApp.Tests;

/// <summary>
/// 仓库里的关键路径。
///
/// <para>
/// 测试要读仓库根下的契约文件、**skill 说明书**（索引页与 references/）与**源码**
/// （错误码清单要与实际抛出的错误码一致），
/// 所以得先找到仓库根。认 <c>IdeaApp.sln</c>：逐级上溯找
/// <c>openapi/idea-v1.json</c> 会命中别处的同名目录，那就找错文件了。
/// </para>
/// </summary>
internal static class RepoPaths
{
    /// <summary>仓库根目录。</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>应用的源码目录。</summary>
    public static string AppSource { get; } = Path.Combine(Root, "server", "IdeaApp");

    /// <summary>提交进仓库的 OpenAPI 契约文件。</summary>
    public static string Contract { get; } = Path.Combine(Root, "openapi", "idea-v1.json");

    /// <summary>
    /// skill 的**内容源**目录（<c>skills/idea-manager</c>）：服务端分发的说明书。
    /// </summary>
    /// <remarks>
    /// 里面只有索引页（<see cref="SkillIndex"/>）与 <c>references/</c>
    /// （<see cref="SkillReferences"/>）—— 没有命令层，也没有契约副本。
    /// </remarks>
    public static string SkillDocs { get; } = Path.Combine(Root, "skills", "idea-manager");

    /// <summary>内容源的索引页（agent 触发 skill 后必然读的那一页）。</summary>
    public static string SkillIndex { get; } = Path.Combine(SkillDocs, "SKILL.md");

    /// <summary>内容源的细节目录（按需取用）。</summary>
    public static string SkillReferences { get; } = Path.Combine(SkillDocs, "references");

    /// <summary>错误码与 413 说明所在的那一份（<c>references/errors.md</c>）。</summary>
    public static string SkillErrorsDoc { get; } =
        Path.Combine(SkillReferences, "errors.md");

    /// <summary>
    /// agent 侧安装的**薄壳**（<c>skills/idea-manager-client/SKILL.md</c>）。
    /// </summary>
    /// <remarks>
    /// 薄壳只有 frontmatter 与指针：它的 frontmatter 必须与
    /// <see cref="SkillIndex"/> 的 frontmatter **逐字相同**。
    /// </remarks>
    public static string SkillShell { get; } =
        Path.Combine(Root, "skills", "idea-manager-client", "SKILL.md");

    /// <summary>应用的端点源码目录。</summary>
    public static string Endpoints { get; } = Path.Combine(AppSource, "Endpoints");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IdeaApp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"找不到仓库根：从 {AppContext.BaseDirectory} 往上找不到 IdeaApp.sln。");
    }

    /// <summary>目录下所有的 .cs 源码文件（跳过 obj/bin）。</summary>
    public static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);
}
