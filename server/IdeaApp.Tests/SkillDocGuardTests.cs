using System.Text.Json;
using System.Text.RegularExpressions;
using IdeaApp.Contracts;

namespace IdeaApp.Tests;

/// <summary>
/// skill 手册的**漂移守卫**。
///
/// <para>
/// 内容从服务端来之后，漂移不会自己暴露 —— 手册写错了不会有编译错误，
/// 也没有别的测试会红，只会让 agent 拿着一份过期的说明书按旧语义调接口
/// （上一轮就是这么错的：以为「覆盖式设标签」，实际早已改成并集追加）。
/// 这几条守卫把「手册 ↔ 契约 ↔ 错误码 ↔ 薄壳」钉死：任何一边单独改，
/// 都会有一条守卫变红。
/// </para>
///
/// <para>
/// <b>语料</b> = 内容源的索引页 + <c>references/*.md</c> 的全文。
/// 薄壳不算 —— 它只有指针，不复述手册内容（它另有一条守卫）。
/// </para>
///
/// <para>
/// <b>权威来源</b>：
/// <list type="bullet">
/// <item>端点：仓库里的契约文件 <c>openapi/idea-v1.json</c>（构建产物快照，
/// <c>OpenApiDocumentTests</c> 守着它与运行中的应用一致）—— 这里不再自己扫一遍源码。</item>
/// <item>错误码：<see cref="ApiErrorCodes.All"/>（业务错误码的权威清单）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SkillDocGuardTests
{
    /// <summary>OpenAPI 的 path item 里表示 HTTP 方法的属性名（小写）。</summary>
    private static readonly string[] HttpMethodNames =
        ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    /// <summary>
    /// 手册里「方法 + 路径」的写法：<c>GET /api/ideas</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只认「方法紧跟路径」这一种形态：方法与路径之间是空白，路径以 <c>/api/</c> 开头。
    /// </para>
    /// <para>
    /// curl 样板里的 <c>-X POST "$IDEA_BASE_URL/api/ideas"</c> **不算** ——
    /// 那里的方法与路径之间隔着一整个 base url，说的是「怎么调」而不是「有哪些端点」。
    /// </para>
    /// <para>
    /// 路径里可以带 <c>{}</c>（<c>/api/ideas/{id}</c>、<c>{deliveryId}</c>）。
    /// </para>
    /// </remarks>
    private static readonly Regex MethodPathPattern = new(
        @"(?<method>GET|POST|PUT|PATCH|DELETE)\s+(?<path>/api/[^\s`）)、，,。]+)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 手册里把路径条数写死的样子：<c>29 条路径</c>、<c>二十九个端点</c>。
    /// </summary>
    /// <remarks>
    /// 阿拉伯数字与中文数字两种写法都要认 —— 手写的人两种都写得出来。
    /// 量词只跟数字走（「每个端点」不是写死了条数）。
    /// </remarks>
    private static readonly Regex HardcodedCountPattern = new(
        @"(?<count>[0-9]+|[零一二三四五六七八九十百两]+)\s*(?:条|个|项|支)\s*(?:路径|端点|接口|路由)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 契约里的每个数据 API 端点，手册里都要提到。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 服务端加了能力而手册没写，agent 就不知道有这条路 ——
    /// 它只会照着手册里那份旧清单调。这条守的是「服务端 → 手册」这一向。
    /// </para>
    /// <para>
    /// 端点集合取自仓库里的契约文件（构建产物快照），不扫源码：
    /// 契约文件与运行中的应用一致这件事，<c>OpenApiDocumentTests</c> 已经守着。
    /// </para>
    /// <para>
    /// 比的是 **(方法, 路径) 二元组**，不是子串：<c>GET /api/ideas</c> 是
    /// <c>GET /api/ideas/{id}</c> 的前缀，只做子串匹配的话，
    /// 「能力清单里整行删掉了一条端点」这种漂移会漏过去。
    /// </para>
    /// </remarks>
    [Fact]
    public void Manual_mentions_every_api_path_in_the_contract()
    {
        var endpoints = ContractEndpoints();

        Assert.True(
            endpoints.Count > 0,
            "契约文件里一条业务端点都没有 —— 守卫没有东西可守，先查契约文件。");

        var mentioned = Mentions()
            .Select(mention => (mention.Method, mention.Path))
            .ToHashSet();

        foreach (var (method, path) in endpoints
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Method, StringComparer.Ordinal))
        {
            Assert.True(
                mentioned.Contains((method, path)),
                $"契约里的「{method} {path}」在手册里没有出现 —— 服务端有这条端点，"
                + "agent 却在手册里找不到怎么调它。（契约文件是权威来源：端点变了就改手册。）"
                + $"手册里要写成「{method} {path}」这样的形态：方法与路径之间一个空白。");
        }
    }

    /// <summary>
    /// 手册里提到的每个端点，契约里都要有。
    /// </summary>
    /// <remarks>
    /// 手册描述了一条不存在（或已改名）的接口，agent 照着调只会拿到 404，
    /// 还会以为是自己参数写错了。这条守的是「手册 → 服务端」这一向。
    /// </remarks>
    [Fact]
    public void Every_api_path_in_the_manual_exists_in_the_contract()
    {
        var endpoints = ContractEndpoints();
        var mentions = Mentions();

        // 防假绿：正则一条都没扫到时，下面那个循环等于没跑，测试会绿得莫名其妙。
        Assert.True(
            mentions.Count > 0,
            "手册里一条「方法 + 路径」都没扫到 —— 写法被守卫的正则漏掉了，"
            + "这条守卫会假绿。请检查手册的写法（方法与路径之间一个空白，路径以 /api/ 开头）。");

        foreach (var mention in mentions
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Method, StringComparer.Ordinal))
        {
            Assert.True(
                endpoints.Contains((mention.Method, mention.Path)),
                $"手册的 {mention.File} 第 {mention.Line} 行写了「{mention.Method} {mention.Path}」，"
                + "但契约里没有这条端点 —— 手册说的是不存在（或已改）的接口。");
        }
    }

    /// <summary>
    /// 手册「## 服务端错误码」那张表的 code 集合，必须恰好等于 <see cref="ApiErrorCodes.All"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 错误码表是手抄的，抄漏一条与抄多一条同样致命：agent 按 <c>code</c> 分支处理，
    /// 表里没有的码它就没有分支可走。
    /// </para>
    /// <para>
    /// <b>只比第一张表</b>（服务端那 29 条）：errors.md 里还有第二张「取件器的错误码」，
    /// 那张表里的码是取件器自己产的（<c>not_found</c>、<c>network_error</c> 之类），
    /// 不在业务错误码清单里。第二张表里有几条与第一张重名，按标题定位才不会混。
    /// </para>
    /// <para>
    /// <b><c>internal_error</c> 不进表格行</b>：它是服务端的兜底码
    /// （<c>ApiErrorCodes.All</c> 只登记业务错误码），在正文里说明即可。
    /// 放进表格行，这张表的集合就对不上了。
    /// </para>
    /// </remarks>
    [Fact]
    public void Manual_error_code_table_matches_the_registry()
    {
        var documented = ServiceErrorCodeTable();
        var registry = ApiErrorCodes.All
            .Select(code => code.Code)
            .ToHashSet(StringComparer.Ordinal);

        var missing = registry.Except(documented).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        var extra = documented.Except(registry).OrderBy(code => code, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            "手册「## 服务端错误码」那张表与 ApiErrorCodes.All 对不上："
            + (missing.Length > 0 ? $"少了 {string.Join("、", missing)}；" : string.Empty)
            + (extra.Length > 0 ? $"多了 {string.Join("、", extra)}。" : string.Empty)
            + "表里只放业务错误码；internal_error 是服务端的兜底码（在正文里说明，别放进表格行）。");
    }

    /// <summary>
    /// 薄壳的 frontmatter 必须与内容源索引页的 frontmatter **逐字相同**。
    /// </summary>
    /// <remarks>
    /// 薄壳是 agent 侧唯一要维护的东西（正文只有指针）：它的 description 决定了
    /// agent 会不会想起这个 skill。内容源改了 description 而薄壳没跟上，
    /// agent 看到的还是旧描述 —— 而「薄壳与内容源解耦」说的是**正文**解耦，
    /// frontmatter 本来就该同步。
    /// </remarks>
    [Fact]
    public void Shell_frontmatter_matches_the_content_source()
    {
        var source = FrontmatterOf(File.ReadAllText(RepoPaths.SkillIndex), "内容源的索引页");
        var shell = FrontmatterOf(File.ReadAllText(RepoPaths.SkillShell), "薄壳");

        Assert.True(
            source == shell,
            "薄壳与内容源的 frontmatter 不是逐字相同 —— "
            + "薄壳是 agent 侧唯一要维护的东西，它的 description 不该悄悄过期。"
            + Environment.NewLine + "内容源：" + Environment.NewLine + source
            + Environment.NewLine + "薄壳：" + Environment.NewLine + shell);
    }

    /// <summary>
    /// 薄壳的正文（frontmatter 之后）里不许出现服务端的东西。
    /// </summary>
    /// <remarks>
    /// 薄壳在取到正文之前是 agent 唯一读到的东西，它必须与内容源解耦：
    /// 服务端哪天改了拆分（改了文件名、挪了目录），写死的指针会指向不存在的文件。
    /// 所以薄壳只许说「本文件」与「scripts/fetch.py」——
    /// 服务端有哪些文件、有哪些端点，由服务端自己说（路由表在服务端的索引页里）。
    /// </remarks>
    [Fact]
    public void Shell_body_contains_no_server_file_names()
    {
        var body = BodyOf(File.ReadAllText(RepoPaths.SkillShell), "薄壳");

        foreach (var forbidden in new[] { "/api/", "references/", "SKILL.md" })
        {
            Assert.False(
                body.Contains(forbidden, StringComparison.Ordinal),
                $"薄壳正文里出现了「{forbidden}」—— 薄壳必须与内容源解耦："
                + "服务端有哪些文件、哪些端点由服务端自己说（薄壳只说「先取索引页」）。");
        }
    }

    /// <summary>
    /// 索引页最多 150 行。
    /// </summary>
    /// <remarks>
    /// 渐进式披露的自我约束：索引页是 agent 每次触发 skill 都会读的那一份，
    /// 它的体量直接进 agent 的上下文。膨胀了就该把细节拆进 <c>references/</c>。
    /// </remarks>
    [Fact]
    public void Index_page_is_at_most_150_lines()
    {
        var lines = File.ReadAllLines(RepoPaths.SkillIndex).Length;

        Assert.True(
            lines <= 150,
            $"索引页 {lines} 行，超了 150 行 —— 索引页膨胀了就该把细节拆进 references/。");
    }

    /// <summary>
    /// 内容源里不许再有契约副本。
    /// </summary>
    /// <remarks>
    /// 服务端本来就实时发 <c>/openapi/v1.json</c>。手抄一份进内容源，
    /// 就是把「会漂的东西」又制造了一份 —— 上一轮手册里的契约副本就是这么过期掉的。
    /// </remarks>
    [Fact]
    public void Skill_directory_has_no_contract_copy()
    {
        var copy = Path.Combine(RepoPaths.SkillReferences, "openapi.json");

        Assert.False(
            File.Exists(copy),
            $"内容源里又有契约副本了（{copy}）—— 副本会漂："
            + "服务端本来就实时发 /openapi/v1.json，手册应当指向它。");
    }

    /// <summary>
    /// 内容源里只允许「SKILL.md」与「references/」目录，references/ 下只允许 .md。
    /// </summary>
    /// <remarks>
    /// 命令层（<c>scripts/idea.py</c> 那套命令行封装）与契约副本都已经删掉了：
    /// 它们是会漂的第二份真相。内容源是**分发给 agent 的纯文档**，
    /// 混进别的东西就等于把漂移的源头又装回去了。
    /// </remarks>
    [Fact]
    public void Content_source_contains_only_the_shipped_files()
    {
        foreach (var entry in Directory
            .EnumerateFileSystemEntries(RepoPaths.SkillDocs)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);

            if (name == "SKILL.md" || (name == "references" && Directory.Exists(entry)))
            {
                continue;
            }

            Assert.Fail(
                $"内容源里多了不该有的东西：{name} —— "
                + "只允许「SKILL.md」与「references」目录。");
        }

        foreach (var file in Directory
            .EnumerateFiles(RepoPaths.SkillReferences, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            Assert.True(
                file.EndsWith(".md", StringComparison.Ordinal),
                $"references/ 下只允许 .md：{Path.GetRelativePath(RepoPaths.Root, file)} —— "
                + "命令层与契约副本都已经删掉了，references/ 是分发给 agent 的纯文档。");
        }
    }

    /// <summary>
    /// 手册里不许把路径条数写死。
    /// </summary>
    /// <remarks>
    /// 「契约里有 32 条路径」这种话，契约一改它就过期 —— 而且过期得**看不出来**
    /// （数字还在，只是不准了）。手册应该说「完整契约见 <c>/openapi/v1.json</c>」。
    /// </remarks>
    [Fact]
    public void Manual_does_not_hardcode_the_path_count()
    {
        foreach (var (name, text) in Corpus())
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                var match = HardcodedCountPattern.Match(lines[index]);

                if (!match.Success)
                {
                    continue;
                }

                Assert.Fail(
                    $"{name} 第 {index + 1} 行写了「{match.Value}」—— "
                    + "手册里不写死路径条数：契约一变它就过期。写「完整契约见 /openapi/v1.json」。");
            }
        }
    }

    /// <summary>
    /// 手册必须给出能原样送达特殊字符的写法。
    /// </summary>
    /// <remarks>
    /// agent 用 shell 拼 JSON 的话，引号、换行、反斜杠会被 shell 吃掉 ——
    /// 正文就这么没了。手册必须给出能照抄的写法（请求体走文件）。
    /// </remarks>
    [Fact]
    public void Manual_shows_how_to_send_special_characters()
    {
        var corpus = string.Join("\n", Corpus().Select(item => item.Text));

        Assert.True(
            corpus.Contains("--data-binary @", StringComparison.Ordinal),
            "手册里没有给出能原样送达特殊字符的写法（--data-binary @文件）—— "
            + "agent 用 shell 拼 JSON 的话，引号、换行、反斜杠会被吃掉。");
    }

    /// <summary>
    /// 两条正则得认得出它们该认的写法 —— 否则守卫是**哑的**。
    /// </summary>
    /// <remarks>
    /// 「手册里没有那种写法」与「正则没认出那种写法」在测试里长得一模一样：
    /// 都是绿。所以这里直接把两种形状喂给正则，钉住它的判别力。
    /// </remarks>
    [Fact]
    public void The_detectors_recognize_the_shapes_they_are_meant_to_catch()
    {
        var match = MethodPathPattern.Match("| GET /api/ideas | 列表 |");

        Assert.True(match.Success, "「方法 + 路径」的正则认不出表格里的写法。");
        Assert.Equal("GET", match.Groups["method"].Value);
        Assert.Equal("/api/ideas", match.Groups["path"].Value);

        Assert.True(
            MethodPathPattern.IsMatch("POST /api/webhooks/{id}/deliveries/{deliveryId}/retry"),
            "「方法 + 路径」的正则认不出带路径参数的写法。");

        Assert.False(
            MethodPathPattern.IsMatch("curl -sS -X POST \"$IDEA_BASE_URL/api/ideas\""),
            "「方法 + 路径」的正则把 curl 样板里的 -X POST 当成了端点提及。");

        Assert.True(
            HardcodedCountPattern.IsMatch("契约里有 29 条路径"),
            "「写死条数」的正则认不出阿拉伯数字的写法。");

        Assert.True(
            HardcodedCountPattern.IsMatch("一共二十九个端点"),
            "「写死条数」的正则认不出中文数字的写法。");
    }

    /// <summary>语料：索引页 + references/ 下的全部 .md。</summary>
    private static List<(string Name, string Text)> Corpus()
    {
        var paths = new List<string> { RepoPaths.SkillIndex };

        paths.AddRange(Directory
            .EnumerateFiles(RepoPaths.SkillReferences, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal));

        return paths
            .Select(path => (
                Name: Path.GetRelativePath(RepoPaths.Root, path),
                Text: File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>契约文件里的数据 API 端点（方法 + 路径）。</summary>
    /// <remarks>
    /// <c>/skill/**</c> 那三条是**分发端点**（取清单、取文件），不是业务端点；
    /// <c>/openapi/**</c> 是契约自己。两者都不参与「手册 ↔ 端点」的比对。
    /// </remarks>
    private static HashSet<(string Method, string Path)> ContractEndpoints()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Contract));

        var endpoints = new HashSet<(string Method, string Path)>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (path.Name.StartsWith("/skill/", StringComparison.Ordinal)
                || path.Name.StartsWith("/openapi/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!HttpMethodNames.Contains(operation.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                endpoints.Add((operation.Name.ToUpperInvariant(), path.Name));
            }
        }

        return endpoints;
    }

    /// <summary>手册里提到的「方法 + 路径」（同一个只留第一次出现的位置）。</summary>
    private static List<Mention> Mentions()
    {
        var found = new Dictionary<(string Method, string Path), Mention>();

        foreach (var (name, text) in Corpus())
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in MethodPathPattern.Matches(lines[index]))
                {
                    var method = match.Groups["method"].Value;
                    var path = NormalizePath(match.Groups["path"].Value);

                    found.TryAdd((method, path), new Mention(method, path, name, index + 1));
                }
            }
        }

        return found.Values.ToList();
    }

    /// <summary>
    /// 把从手册里抽出来的路径归一化：去掉查询串（<c>?</c> 之后的那一段）。
    /// </summary>
    /// <remarks>
    /// 「GET /api/ideas?status=open」说的是同一条端点 —— 查询参数是另一回事，
    /// 端点的身份只由路径决定。不归一化的话，这条提及会被当成一条不存在的端点。
    /// </remarks>
    private static string NormalizePath(string path)
    {
        var query = path.IndexOf('?');

        return query < 0 ? path : path[..query];
    }

    /// <summary>
    /// 手册「## 服务端错误码」那张表里的 code 集合。
    /// </summary>
    /// <remarks>
    /// 按**标题**定位那张表，不扫全文的表格行 —— errors.md 里有两张表，
    /// 第二张（取件器的错误码）里有重名的 code，扫全文会混进来。
    /// </remarks>
    private static HashSet<string> ServiceErrorCodeTable()
    {
        var lines = File.ReadAllLines(RepoPaths.SkillErrorsDoc)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

        var heading = Array.FindIndex(lines, line => line.Trim() == "## 服务端错误码");
        Assert.True(
            heading >= 0,
            $"{RepoPaths.SkillErrorsDoc} 里找不到「## 服务端错误码」这一节 —— 守卫定位不到那张表。");

        var codes = new List<string>();
        var inTable = false;
        var firstRow = true;

        for (var index = heading + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();

            if (!line.StartsWith('|'))
            {
                // 表到这儿就结束了（后面是散文或空行）。
                if (inTable)
                {
                    break;
                }

                continue;
            }

            inTable = true;

            var cells = line.Split('|');
            if (cells.Length < 2)
            {
                continue;
            }

            // 表格的第一格是 code：`| `code` | 含义 | agent 该怎么办 |`。
            var code = cells[1].Trim();
            if (code.Length == 0)
            {
                continue;
            }

            // 表头行与分隔行（`|---|---|`）不是数据行。
            if (firstRow)
            {
                firstRow = false;
                continue;
            }

            if (IsSeparatorRow(cells))
            {
                continue;
            }

            codes.Add(code.Trim('`'));
        }

        return codes.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>这一行是不是 markdown 表格的分隔行（<c>|---|---|</c>）。</summary>
    private static bool IsSeparatorRow(string[] cells)
    {
        foreach (var cell in cells)
        {
            var trimmed = cell.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            foreach (var character in trimmed)
            {
                if (character is not ('-' or ':'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>取 markdown 文件开头的 frontmatter 块（不含分隔用的 --- 行）。</summary>
    private static string FrontmatterOf(string text, string origin)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var start = Array.FindIndex(lines, line => line.Trim() == "---");
        Assert.True(start >= 0, $"{origin} 的开头没有 frontmatter（第一行不是 ---）。");

        var end = -1;
        for (var index = start + 1; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
            {
                end = index;
                break;
            }
        }

        Assert.True(end > start, $"{origin} 的 frontmatter 没有闭合（后面没有第二个 ---）。");

        return string.Join("\n", lines.Skip(start + 1).Take(end - start - 1));
    }

    /// <summary>取 markdown 文件里 frontmatter 之后的正文。</summary>
    private static string BodyOf(string text, string origin)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var start = Array.FindIndex(lines, line => line.Trim() == "---");
        Assert.True(start >= 0, $"{origin} 的开头没有 frontmatter（第一行不是 ---）。");

        var end = -1;
        for (var index = start + 1; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
            {
                end = index;
                break;
            }
        }

        Assert.True(end > start, $"{origin} 的 frontmatter 没有闭合（后面没有第二个 ---）。");

        return string.Join("\n", lines.Skip(end + 1));
    }

    /// <summary>手册里的一次「方法 + 路径」提及。</summary>
    private sealed record Mention(string Method, string Path, string File, int Line);
}
