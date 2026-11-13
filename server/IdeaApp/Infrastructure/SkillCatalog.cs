using System.Security.Cryptography;
using System.Text;
using IdeaApp.Application;
using IdeaApp.Contracts;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 分发副本的目录视图：枚举文件、算哈希、出清单、解析取文件的路径。
///
/// <para>
/// 「分发副本」是随产物一起走的 skill 内容目录（默认 <c>skill/idea-manager/</c>）。
/// 服务端是它唯一的权威来源：agent 按清单同步到本地缓存，
/// 清单里的每个哈希都对应分发副本里的一个文件。
/// </para>
/// </summary>
public sealed class SkillCatalog
{
    /// <summary>入口文件名（索引页）。清单里的 <c>entry</c> 就是它。</summary>
    private const string EntryFileName = "SKILL.md";

    /// <summary>开发侧的残留目录：不出现在清单里，也不该被分发出去。</summary>
    private const string ExcludedDirectoryName = "__pycache__";

    /// <summary>UTF-8（不带 BOM）：版本串是**字节**的哈希，编码必须定死。</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 建一个指向分发副本目录的视图。
    /// </summary>
    /// <param name="configuredPath">
    /// 分发副本的根目录。相对路径以**应用目录**（<see cref="AppContext.BaseDirectory"/>）为基准，
    /// 与当前工作目录无关 —— 与 <c>Database:Path</c> 同一条约定
    /// （见 <see cref="SqliteDatabase.AppDirectory"/>）；绝对路径原样使用。
    /// </param>
    public SkillCatalog(string configuredPath)
    {
        // 空路径会让 Root 变成应用目录本身 —— 那样这个端点会把应用目录里的
        // 文件（数据库、配置）顺着取文件端点发出去。路径写错是部署问题，
        // 不能默默变成「把应用目录当分发副本」（与 SqliteDatabase 同一个口径：
        // 数据库路径为空时直接报错）。
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException("skill 分发副本的路径不能为空。", nameof(configuredPath));
        }

        Root = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    /// <summary>分发副本的根目录（绝对路径）。</summary>
    public string Root { get; }

    /// <summary>
    /// 出一份清单：版本、入口、逐个文件的哈希与大小。
    /// </summary>
    /// <remarks>
    /// 版本串是「全部文件的 (路径, 哈希) 排序后拼接再取哈希」：
    /// 同一份内容永远同一个版本串，内容变一个字节版本就变 ——
    /// 取件器拿它判断「要不要重新同步」，不能有第二种含义。
    /// </remarks>
    /// <exception cref="ApiException">
    /// 分发副本目录不存在，或里面没有入口文件（<c>SKILL.md</c>）—— 都是
    /// 「服务端没有分发 skill 内容」，回 404 + <c>skill_not_distributed</c>。
    /// </exception>
    public SkillManifestDto BuildManifest()
    {
        if (!Directory.Exists(Root))
        {
            throw new ApiException(
                404,
                "skill_not_distributed",
                $"服务端没有分发 skill 内容：目录 {Root} 不存在。");
        }

        var files = EnumerateFiles()
            .Select(file => new SkillFileDto(
                file.RelativePath,
                Sha256HexOfFile(file.FullPath),
                new FileInfo(file.FullPath).Length))
            .ToArray();

        var entry = files.FirstOrDefault(file => file.Path == EntryFileName)?.Path;

        if (entry is null)
        {
            throw new ApiException(
                404,
                "skill_not_distributed",
                $"分发副本里没有入口文件 {EntryFileName}：目录 {Root}。");
        }

        var version = Sha256Hex(Utf8.GetBytes(string.Join(
            "\n",
            files.Select(file => file.Path + "\n" + file.Sha256))));

        return new SkillManifestDto(version, entry, files);
    }

    /// <summary>
    /// 把「相对分发副本根目录的路径」解析成磁盘上的绝对路径。
    /// </summary>
    /// <param name="relativePath">相对路径（用 <c>/</c> 分隔）。</param>
    /// <param name="fullPath">解析成功时给出绝对路径，否则是空串。</param>
    /// <returns>解析成功且文件存在时为 true。</returns>
    /// <remarks>
    /// 拒绝的情形一律返回 false（不回错误、不抛异常）：绝对路径、含 <c>..</c> 段的路径、
    /// 解析后跑到副本目录之外的路径、以及副本里根本没有的文件。
    /// </remarks>
    public bool TryResolveFile(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // 绝对路径：分发副本里的文件都是相对路径，一概拒绝。
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        // 含 ".." 段：直接拒绝，不去「先解析再判断」——
        // 解析后的形态依赖规范化规则，多绕一圈就多一个能出错的环节。
        if (relativePath.Split('/', '\\').Any(segment => segment == ".."))
        {
            return false;
        }

        string candidate;

        try
        {
            candidate = Path.GetFullPath(Path.Combine(Root, relativePath));
        }
        catch (ArgumentException)
        {
            // 路径里有非法字符（控制字符、NUL 等）：当作「副本里没有这个文件」。
            // 解析失败不是服务端故障，不能让请求变成 500。
            return false;
        }
        catch (IOException)
        {
            // 路径太长：同上。
            return false;
        }

        // 双保险：解析之后必须还在副本目录之下。
        if (!candidate.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        if (!File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;

        return true;
    }

    /// <summary>
    /// 递归枚举副本里的全部文件（跳过开发侧的残留目录），按相对路径升序。
    /// </summary>
    private IEnumerable<(string RelativePath, string FullPath)> EnumerateFiles()
    {
        return Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(path => (RelativePath: ToRelativePath(path), FullPath: path))
            .Where(file => !file.RelativePath
                .Split('/')
                .Any(segment => segment == ExcludedDirectoryName))
            // 按路径升序：版本串与清单的顺序都取决于它，
            // 必须与取件器那侧一致（那边按字节序排）。
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal);
    }

    /// <summary>文件在副本里的相对路径（统一用 <c>/</c> 分隔）。</summary>
    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(Root, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    /// <summary>文件字节的 sha256（十六进制小写）。</summary>
    private static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);

        return Sha256Hex(SHA256.HashData(stream));
    }

    /// <summary>字节串的 sha256（十六进制小写）。</summary>
    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
