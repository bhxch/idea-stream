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
/// <b>「什么算分发副本的一部分」只有一处定义</b>，两问都在这里：
/// <list type="bullet">
/// <item><b>哪些路径算</b>（<see cref="IsDistributedPath"/>）；</item>
/// <item><b>多大算</b>（<see cref="IsDistributedSize"/>）。</item>
/// </list>
/// 枚举清单与取文件端点都用它们。两处各写一份时迟早会漂移 ——
/// 清单里列着一份取文件端点取不到的文件，取件器照清单逐个下载、
/// 拿到 404 → 整次同步作废 → agent 手上**一份手册都拿不到**
/// （不是「少一个文件」）。
/// </para>
/// </summary>
public sealed class SkillCatalog
{
    /// <summary>入口文件名（索引页）。清单里的 <c>entry</c> 就是它。</summary>
    private const string EntryFileName = "SKILL.md";

    /// <summary>开发侧的残留目录：不出现在清单里，也不该被分发出去。</summary>
    private const string ExcludedDirectoryName = "__pycache__";

    /// <summary>
    /// 分发副本里一份文件的大小上限（4 MiB）。
    /// </summary>
    /// <remarks>
    /// 「多大算分发副本的一部分」与「哪些路径算」是同一个问题的两面，
    /// 所以两者住在同一个地方（这个类）。
    /// <para>
    /// 分发副本里每份都是几十 KB 的文档，4 MiB 已经是宽得离谱的上限：
    /// 超了的那一份**不该在分发副本里**，按「副本里没有这个文件」处理
    /// （清单里不列它、取文件端点回 404）。
    /// </para>
    /// </remarks>
    public const long MaxFileBytes = 4 * 1024 * 1024;

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
    ///
    /// <para>
    /// 清单里**只列取得到的文件**：枚举之后读不到的那一份（被删、被换、被占着）
    /// 按「不在分发副本里」处理 —— 让它抛出去就是 500，整个清单端点挂掉，
    /// agent 一份手册都拿不到（见 <see cref="DescribeFile"/>）。
    /// </para>
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

        // 枚举之后那一份**可能已经不在副本里了**（被删、被换、权限被抽掉）：
        // 那时量大小 / 算哈希都会抛 IOException → 500 internal_error
        // → 整个清单端点挂掉 → agent 一份手册都拿不到。
        // 所以按「读不到 = 不在副本里」的口径跳过那一份（与超限跳过同一类处理）。
        var files = EnumerateFiles()
            .Select(file => DescribeFile(file.RelativePath, file.FullPath))
            .OfType<SkillFileDto>()
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
    /// 一份文件在清单里的样子（相对路径 + 哈希 + 大小）；读不到就是 null（不在副本里）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>枚举与这里之间有缝</b>（TOCTOU）：文件可能在这中间被删掉、被换掉、
    /// 权限被抽掉、被别的进程独占着。早先造清单那一侧是**裸读**
    /// （量大小、算哈希都是直接读），读失败抛 <c>IOException</c> →
    /// 500 <c>internal_error</c> → 整个清单端点挂掉 → agent 一份手册都拿不到。
    /// </para>
    /// <para>
    /// 所以这里按「<b>读不到 = 不在副本里</b>」处理 —— 与超限的那一份同一类：
    /// 清单里只列**取得到**的文件，取不到的那一份就当它不在副本里。
    /// </para>
    /// </remarks>
    internal static SkillFileDto? DescribeFile(string relativePath, string fullPath)
    {
        var length = LengthOf(fullPath);

        // 量不到大小 → 读不到 → 不在副本里。
        if (length < 0)
        {
            return null;
        }

        // 超限的那一份**不在分发副本里**（与枚举清单同一条判据）：
        // 清单里永远不会出现取文件端点取不到的文件。
        if (!IsDistributedSize(length))
        {
            return null;
        }

        var sha256 = Sha256HexOfFileOrNull(fullPath);

        if (sha256 is null)
        {
            return null;
        }

        return new SkillFileDto(relativePath, sha256, length);
    }

    /// <summary>
    /// 把「相对分发副本根目录的路径」解析成磁盘上的绝对路径。
    /// </summary>
    /// <param name="relativePath">相对路径（用 <c>/</c> 分隔）。可以是 null（路径没给到）。</param>
    /// <param name="fullPath">解析成功时给出绝对路径，否则是空串。</param>
    /// <returns>解析成功且文件存在时为 true。</returns>
    /// <remarks>
    /// 拒绝的情形一律返回 false（不回错误、不抛异常）：绝对路径、含 <c>..</c> 段的路径、
    /// 解析后跑到副本目录之外的路径、**不属于分发副本的路径或大小**
    /// （见 <see cref="IsDistributedPath"/> 与 <see cref="IsDistributedSize"/>），
    /// 以及副本里根本没有的文件。
    /// </remarks>
    public bool TryResolveFile(string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (!TryResolveCandidate(relativePath, out var candidate))
        {
            return false;
        }

        // 超限的那一份**不在分发副本里**（与枚举清单同一条判据）：
        // 清单里永远不会出现取文件端点取不到的文件。
        if (!IsDistributedSizeOf(candidate))
        {
            return false;
        }

        fullPath = candidate;

        return true;
    }

    /// <summary>
    /// 这个相对路径是不是「副本里确实有、但超了大小上限」。
    /// </summary>
    /// <param name="relativePath">相对路径（用 <c>/</c> 分隔）。</param>
    /// <param name="length">超限那份的实际大小（不是超限时是 0）。</param>
    /// <returns>副本里有它、但它超了上限时为 true。</returns>
    /// <remarks>
    /// 给取文件端点把 404 的详情说清楚用：「副本里没有这个文件」与
    /// 「副本里有，但它超了上限」是两回事 —— 后者要让人知道该去改哪一头
    /// （把那份文件改小，或者从分发副本里拿掉）。
    /// <para>
    /// 解析与判定都复用同一处实现（<see cref="TryResolveCandidate"/> 与
    /// <see cref="IsDistributedSize"/>）：规则只有一份，详情只是多问一句。
    /// </para>
    /// </remarks>
    public bool IsOverSizeLimit(string? relativePath, out long length)
    {
        length = 0;

        if (!TryResolveCandidate(relativePath, out var candidate))
        {
            return false;
        }

        var size = LengthOf(candidate);

        if (size <= MaxFileBytes)
        {
            return false;
        }

        length = size;

        return true;
    }

    /// <summary>
    /// 把相对路径解析成副本里的绝对路径（<b>只管路径</b>，不管大小）。
    /// </summary>
    /// <param name="relativePath">相对路径（用 <c>/</c> 分隔）。</param>
    /// <param name="candidate">解析成功时给出绝对路径，否则是空串。</param>
    /// <returns>路径合法、落在副本目录之下、且副本里确实有这个文件时为 true。</returns>
    /// <remarks>
    /// 「路径」与「大小」是同一个问题的两面，但判据不同：
    /// 解析只回答「这个路径是不是副本里的一个文件」，大小由
    /// <see cref="IsDistributedSize"/> 单独回答。
    /// </remarks>
    private bool TryResolveCandidate(string? relativePath, out string candidate)
    {
        candidate = string.Empty;

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

        string resolved;

        try
        {
            resolved = Path.GetFullPath(Path.Combine(Root, relativePath));
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
        if (!resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        if (!File.Exists(resolved))
        {
            return false;
        }

        candidate = resolved;

        return true;
    }

    /// <summary>
    /// 递归枚举副本里的全部文件（跳过开发侧的残留目录与超限的那一份），按相对路径升序。
    /// </summary>
    /// <remarks>
    /// <b>超限的那一份不进清单</b>：清单里永远不会出现取文件端点取不到的文件 ——
    /// 取件器照清单逐个下载，拿到 404 就整次同步作废（agent 手上**一份手册都拿不到**，
    /// 不是「少一个文件」）。
    /// </remarks>
    private IEnumerable<(string RelativePath, string FullPath)> EnumerateFiles()
    {
        return Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(path => (RelativePath: ToRelativePath(path), FullPath: path))
            .Where(file => IsDistributedPath(file.RelativePath))
            .Where(file => IsDistributedSizeOf(file.FullPath))
            // 按路径升序：版本串与清单的顺序都取决于它，
            // 必须与取件器那侧一致（那边按字节序排）。
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal);
    }

    /// <summary>
    /// 这个相对路径算不算「分发副本的一部分」。
    /// </summary>
    /// <remarks>
    /// <b>枚举清单与取文件共用这一条规则</b>：同一个概念只许有一份定义。
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

    /// <summary>这份文件的大小算不算「分发副本的一部分」（超上限就不算）。</summary>
    /// <remarks>
    /// 与 <see cref="IsDistributedPath"/> 是一对：「哪些路径算」与「多大算」合起来
    /// 才是「什么算分发副本的一部分」。两问都只有一处定义（这里）。
    /// </remarks>
    public static bool IsDistributedSize(long length) => length <= MaxFileBytes;

    /// <summary>这份文件（按大小）算不算分发副本的一部分；量不到大小就不算。</summary>
    /// <remarks>
    /// 量不到大小（枚举到它之后它刚好被删掉 / 权限被抽掉）时当「不在分发副本里」：
    /// 那就是「副本里没有这个文件」，不是服务端故障。
    /// </remarks>
    private static bool IsDistributedSizeOf(string fullPath)
    {
        var length = LengthOf(fullPath);

        return length >= 0 && IsDistributedSize(length);
    }

    /// <summary>这份文件的大小（字节）；量不到就是 -1。</summary>
    private static long LengthOf(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>文件在副本里的相对路径（统一用 <c>/</c> 分隔）。</summary>
    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(Root, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    /// <summary>文件字节的 sha256（十六进制小写）；读不到就是 null。</summary>
    /// <remarks>
    /// 读不到（文件在枚举之后被删掉 / 被换掉 / 权限被抽掉 / 被别的进程独占着）时
    /// 返回 null，而不是让 <c>IOException</c> 冒出去 ——
    /// 那会让整个清单端点变成 500（agent 一份手册都拿不到）。
    /// </remarks>
    private static string? Sha256HexOfFileOrNull(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            return HexLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
