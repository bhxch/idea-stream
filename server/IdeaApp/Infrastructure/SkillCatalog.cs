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
///
/// <para>
/// <b>哪些路径算分发副本的一部分，只有一处定义</b>（<see cref="IsDistributedPath"/>）：
/// 枚举清单与取文件端点都用它。两个地方各写一份时迟早会漂移。
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
    /// <remarks>
    /// **空路径 = 没有分发**，不是错误。
    /// <para>
    /// 早先这里对空路径抛 <see cref="ArgumentException"/>，而注册是**惰性单例** ——
    /// 异常要等到有人第一次访问 <c>/skill/**</c> 才炸出来，那就是 500。
    /// 而「没分发」本来就有专门的错误码（<c>skill_not_distributed</c>），
    /// 规格里也明写「目录缺失时不要 500」。所以这里把空路径记成
    /// 「没配分发目录」，由取用的一方回 404。
    /// </para>
    /// <para>
    /// 也不能让空路径退化成「应用目录」：那样取文件端点会把应用目录里的
    /// 文件（数据库、配置）顺着发出去。
    /// </para>
    /// </remarks>
    public SkillCatalog(string configuredPath)
    {
        Root = string.IsNullOrWhiteSpace(configuredPath)
            ? string.Empty
            : Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    /// <summary>分发副本的根目录（绝对路径）。没配路径时是空串。</summary>
    public string Root { get; }

    /// <summary>
    /// 分发副本在不在（路径配了，且目录存在）。
    /// </summary>
    /// <remarks>
    /// 「没分发」有两种：路径没配（空串）、目录不存在。两者对 agent 是同一件事 ——
    /// 服务端手上没有 skill 内容 —— 所以是同一个错误码。
    /// </remarks>
    public bool IsDistributed => Root.Length > 0 && Directory.Exists(Root);

    /// <summary>「没分发」的说明（进错误详情）。</summary>
    public string NotDistributedDetail =>
        Root.Length == 0
            ? "服务端没有分发 skill 内容：Skill:Path 没有配置（空路径）。"
            : $"服务端没有分发 skill 内容：目录 {Root} 不存在。";

    /// <summary>
    /// 出一份清单：版本、入口、逐个文件的哈希与大小。
    /// </summary>
    /// <remarks>
    /// 版本串是「全部文件的 (路径, 哈希) 排序后拼接再取哈希」：
    /// 同一份内容永远同一个版本串，内容变一个字节版本就变 ——
    /// 取件器拿它判断「要不要重新同步」，不能有第二种含义。
    ///
    /// 版本串是 **64 位小写十六进制**（sha256 的十六进制编码）：
    /// 取件器拿它当缓存目录名，形状必须固定。
    /// </remarks>
    /// <exception cref="ApiException">
    /// 分发副本目录不存在，或里面没有入口文件（<c>SKILL.md</c>）—— 都是
    /// 「服务端没有分发 skill 内容」，回 404 + <c>skill_not_distributed</c>。
    /// </exception>
    public SkillManifestDto BuildManifest()
    {
        if (!IsDistributed)
        {
            throw new ApiException(
                404,
                "skill_not_distributed",
                NotDistributedDetail);
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

        // 版本串 = sha256(「路径 + 换行 + 文件哈希」逐项按路径序拼接) 的十六进制编码。
        //
        // **哈希在这一行显式做**：helper 只负责 hex 编码（名字如实反映它做的事），
        // 少哈希一次的话这里一眼看得见 —— 之前那个叫 `Sha256Hex` 的 helper
        // 其实只做 hex 编码，版本串因此是「原文的十六进制」而不是哈希，
        // 取件器的版本串形状校验（至多 128 字符）当场就把它拒了。
        var version = HexLower(SHA256.HashData(Utf8.GetBytes(string.Join(
            "\n",
            files.Select(file => file.Path + "\n" + file.Sha256)))));

        return new SkillManifestDto(version, entry, files);
    }

    /// <summary>
    /// 把「相对分发副本根目录的路径」解析成磁盘上的绝对路径。
    /// </summary>
    /// <param name="relativePath">相对路径（用 <c>/</c> 分隔）。可以是 null（路径没给到）。</param>
    /// <param name="fullPath">解析成功时给出绝对路径，否则是空串。</param>
    /// <returns>解析成功且文件存在时为 true。</returns>
    /// <remarks>
    /// 拒绝的情形一律返回 false（不回错误、不抛异常）：绝对路径、含 <c>..</c> 段的路径、
    /// 解析后跑到副本目录之外的路径、**不属于分发副本的路径**（见 <see cref="IsDistributedPath"/>），
    /// 以及副本里根本没有的文件。
    /// </remarks>
    public bool TryResolveFile(string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // 没配分发目录：没有根可解析。
        // 这一条不能省 —— 根是空串时 Path.Combine("", 相对路径) 会解析到
        // **当前工作目录**下去，后面的「必须落在根之下」检查也就形同虚设。
        if (Root.Length == 0)
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

        // 不属于分发副本的路径（开发残留目录）：与枚举清单用的是同一条规则 ——
        // 清单里没有的文件，取文件端点也不该给。
        if (!IsDistributedPath(relativePath))
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
            .Where(file => IsDistributedPath(file.RelativePath))
            // 按路径升序：版本串与清单的顺序都取决于它，
            // 必须与取件器那侧一致（那边按字节序排）。
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal);
    }

    /// <summary>
    /// 这个相对路径算不算「分发副本的一部分」。
    /// </summary>
    /// <remarks>
    /// **枚举清单与取文件共用这一条规则**：同一个概念只许有一份定义。
    /// 两处各写一份时，迟早会漂移 —— 清单里有而取文件端点取不到
    /// （agent 按索引页的路由表去取就 404），或者反过来（清单里没有、
    /// 取文件却给得出去）。
    /// <para>
    /// 规则本身：除了开发侧的残留目录（<c>__pycache__</c>），副本里的文件一律算数 ——
    /// 包括点目录里的（<c>.cache/x.md</c>）：产物是 csproj 的 glob 收的，
    /// 它收什么就分发什么。
    /// </para>
    /// </remarks>
    private static bool IsDistributedPath(string relativePath) =>
        !relativePath.Split('/', '\\').Any(segment => segment == ExcludedDirectoryName);

    /// <summary>文件在副本里的相对路径（统一用 <c>/</c> 分隔）。</summary>
    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(Root, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    /// <summary>文件字节的 sha256（十六进制小写）。</summary>
    private static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);

        return HexLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// 字节串的十六进制编码（小写）。
    /// </summary>
    /// <remarks>
    /// **它不哈希** —— 只是把字节编成十六进制。名字如实反映它做的事，
    /// 是为了让人一眼看出「哈希在哪一步」：
    /// 每个调用点自己写 <c>SHA256.HashData(...)</c>，少哈希一次就看得见。
    /// （之前这个 helper 叫 <c>Sha256Hex</c>，名字骗人：它只编码不哈希，
    /// 于是版本串变成了「原文的十六进制」而不是哈希。）
    /// </remarks>
    private static string HexLower(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
