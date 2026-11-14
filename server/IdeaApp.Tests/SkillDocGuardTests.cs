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
/// <b>语料</b> = 内容源的索引页 + <c>references/</c> 下（递归）的全部 <c>.md</c>。
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
    /// <summary>
    /// 开发侧的残留目录名：服务端 <c>SkillCatalog</c> 枚举分发副本时排除这一种段
    /// （别的文件一律进清单 —— 垃圾文件不进分发副本靠的是 csproj 的 <c>*.md</c> glob）。
    /// </summary>
    private const string ExcludedDirectoryName = "__pycache__";

    /// <summary>操作系统留下的垃圾文件名（不进分发副本）。</summary>
    private const string ResidueFileName = "Thumbs.db";

    /// <summary>
    /// OpenAPI 的 path item 里表示 HTTP 方法的属性名（小写）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="MethodPathPattern"/> 认的那几种方法**对齐** ——
    /// 契约里多出一种方法而手册的写法不认它，守卫 1 会永远红（响亮的失败也没用）。
    /// </remarks>
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
    /// <para>
    /// 路径字符集只收 ASCII：中文不属于路径 ——
    /// 「GET /api/ideas这个端点」里的路径是 <c>/api/ideas</c>，
    /// 不是 <c>/api/ideas这个端点</c>（后者会被当成一条不存在的端点）。
    /// </para>
    /// </remarks>
    private static readonly Regex MethodPathPattern = new(
        @"(?<method>GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|TRACE)\s+(?<path>/api/[^\s`\)\],.\u0080-\uFFFF]+)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 「路径到此为止」的边界：后面紧跟这些字符，说明路径还在继续（是另一条路径）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只收 ASCII</b>：路径后面紧跟一个中文字母是中文手册的常态
    /// （「取 references/errors.md即可」），不能因此把整条提及作废。
    /// </para>
    /// <para>
    /// 「点 + 字母数字」也算继续（<c>references/api.md.bak</c> 是另一份文档），
    /// 但光秃秃的句点不算（<c>见 references/api.md.</c> 里那是散文里的句号）。
    /// </para>
    /// <para>
    /// 前向（抽提及）与后向（找提及）用**同一把尺子** ——
    /// 否则会出现「前向说提到了、后向说没提到」的错位。
    /// </para>
    /// </remarks>
    private const string MentionBoundary = @"(?!(?:[A-Za-z0-9_-]|\.[A-Za-z0-9_-]))";

    /// <summary>
    /// 索引页里提到细节文档的形态：<c>references/api.md</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 字符集收的是**字母、数字、字母记号**与 <c>. _ / -</c>：
    /// 非 ASCII 的字母也在内（中文文件名 <c>references/接口.md</c> 要抽得出来），
    /// 而空白与标点（反引号、括号、句号、逗号……）一律不收 ——
    /// 路径尾部那个标点不能被吃进路径。
    /// </para>
    /// <para>
    /// 惰性匹配：路径在**第一个** <c>.md</c> 处收尾 ——
    /// 「references/api.md 与 references/calling.md」不会被吃成一长串。
    /// 末尾的边界（<see cref="MentionBoundary"/>）保证 <c>api.mdx</c> 这种不算数
    /// （它不是一份 <c>.md</c> 文档），而 <c>references/api.md是</c> 里的路径
    /// 仍然是 <c>references/api.md</c>（中文手册里这种写法很常见）。
    /// </para>
    /// </remarks>
    private static readonly Regex ReferenceDocPattern = new(
        @"(?<path>references/[\p{L}\p{N}\p{M}._/-]+?\.md)" + MentionBoundary,
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 手册里把路径条数写死的样子：<c>29 条路径</c>、<c>二十九个端点</c>、<c>32 条 API 路径</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 量词与名词之间允许夹一个 <c>API</c> / <c>HTTP</c> / <c>REST</c>
    /// （「32 条 API 路径」也是写死了条数）。
    /// </para>
    /// <para>
    /// 计数要 &gt;= 2：中文「一」与阿拉伯「1」不算计数 ——
    /// 「只有一条路径」「每次只发一个接口」说的是「一次一个」，
    /// 不是「路径总条数」的陈述，不是漂移标记。
    /// </para>
    /// </remarks>
    private static readonly Regex HardcodedCountPattern = new(
        @"(?<count>[0-9]+|[零二三四五六七八九十百两]+)\s*(?:条|个|项|支)\s*(?:API|HTTP|REST)?\s*(?:路径|端点|接口|路由)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    /// <para>
    /// 薄壳是 agent 侧唯一要维护的东西（正文只有指针）：它的 description 决定了
    /// agent 会不会想起这个 skill。内容源改了 description 而薄壳没跟上，
    /// agent 看到的还是旧描述 —— 而「薄壳与内容源解耦」说的是**正文**解耦，
    /// frontmatter 本来就该同步。
    /// </para>
    /// <para>
    /// 还要各自带得住 <c>name</c> 与 <c>description</c>：两侧同时被清空时，
    /// 「逐字相同」照样成立，但 agent 侧拿不到 description，**skill 直接不被触发**。
    /// </para>
    /// </remarks>
    [Fact]
    public void Shell_frontmatter_matches_the_content_source()
    {
        var source = SplitFrontmatter(File.ReadAllText(RepoPaths.SkillIndex), "内容源的索引页");
        var shell = SplitFrontmatter(File.ReadAllText(RepoPaths.SkillShell), "薄壳");

        Assert.True(
            source.Frontmatter == shell.Frontmatter,
            "薄壳与内容源的 frontmatter 不是逐字相同 —— "
            + "薄壳是 agent 侧唯一要维护的东西，它的 description 不该悄悄过期。"
            + Environment.NewLine + "内容源：" + Environment.NewLine + source.Frontmatter
            + Environment.NewLine + "薄壳：" + Environment.NewLine + shell.Frontmatter);

        foreach (var (block, origin) in new[] { (source, "内容源的索引页"), (shell, "薄壳") })
        {
            foreach (var key in new[] { "name", "description" })
            {
                var value = FrontmatterValue(block.Frontmatter, key);

                Assert.True(
                    !string.IsNullOrWhiteSpace(value),
                    $"{origin} 的 frontmatter 里没有 {key}: —— "
                    + "agent 侧靠它认出这个 skill（description 缺了，skill 就不会被触发）。");
            }
        }
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
        var body = SplitFrontmatter(File.ReadAllText(RepoPaths.SkillShell), "薄壳").Body;

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
    /// <para>
    /// 命令层（<c>scripts/idea.py</c> 那套命令行封装）与契约副本都已经删掉了：
    /// 它们是会漂的第二份真相。内容源是**分发给 agent 的纯文档**，
    /// 混进别的东西就等于把漂移的源头又装回去了。
    /// </para>
    /// <para>
    /// <b>references/ 是平铺的</b>：手册就那么几份细节文档，索引页的路由表按文件名指它们。
    /// 子目录会让「索引页 ↔ 文档」的对应关系散掉，
    /// 也会让子目录里的文档脱离「平铺 + 只允许 .md」的约束。
    /// </para>
    /// <para>
    /// <b>残留放过</b>：<c>__pycache__</c> 目录与点开头的目录/文件
    /// （<c>.cache/</c>、<c>.vscode/</c>、<c>.DS_Store</c>、<c>.gitignore</c> 之类）
    /// 是编辑器与工具留下的，别人的机器上会有 —— 守卫红一次，人就学会无视守卫。
    /// 垃圾文件不进**分发副本**靠的是 csproj 的 <c>references/**/*.md</c> glob
    /// （<c>SkillCatalog</c> 只排除 <c>__pycache__</c> 段，其余文件一律进清单）。
    /// </para>
    /// </remarks>
    [Fact]
    public void Content_source_contains_only_the_shipped_files()
    {
        foreach (var entry in Directory
            .EnumerateFileSystemEntries(RepoPaths.SkillDocs)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (IsResiduePath(entry))
            {
                continue;
            }

            var name = Path.GetFileName(entry);

            if (name == "SKILL.md" || (name == "references" && Directory.Exists(entry)))
            {
                continue;
            }

            Assert.Fail(
                $"内容源里多了不该有的东西：{name} —— "
                + "只允许「SKILL.md」与「references」目录。");
        }

        foreach (var directory in Directory
            .EnumerateDirectories(RepoPaths.SkillReferences, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (IsResiduePath(directory))
            {
                continue;
            }

            Assert.Fail(
                $"references/ 下不许有子目录：{Path.GetRelativePath(RepoPaths.Root, directory)} —— "
                + "手册的 references 是平铺的几份文档，索引页的路由表按文件名指它们。");
        }

        foreach (var file in Directory
            .EnumerateFiles(RepoPaths.SkillReferences, "*", SearchOption.AllDirectories)
            .Where(path => !IsResiduePath(path))
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            Assert.True(
                file.EndsWith(".md", StringComparison.Ordinal),
                $"references/ 下只允许 .md：{Path.GetRelativePath(RepoPaths.Root, file)} —— "
                + "命令层与契约副本都已经删掉了，references/ 是分发给 agent 的纯文档。");
        }
    }

    /// <summary>
    /// 索引页提到的每一份细节文档都要在，<c>references/</c> 下的每一份也要被提到。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 索引页（尤其是那张路由表）是 agent 取细节的**指引**：它照着指引去取，
    /// 指引里那份文档不在了就是 404 —— 而且 agent 只知道自己「按指引取」，
    /// 不会想到是指引错了。这种悬空指针最难查。
    /// </para>
    /// <para>
    /// 双向：前向（「提到的必须存在」）关掉悬空指针 ——
    /// 不管文件被挪到哪儿（哪怕挪进点目录），索引页的指针断了就红；
    /// 后向（「每份都被提到」）关掉「多了一份文档但索引页不知道」——
    /// agent 照索引页取不到它，那份文档等于不存在。
    /// </para>
    /// <para>
    /// <b>正文里任何一处提到都算</b>（不限是不是在路由表那一节）：
    /// 路由表是给人看的指引，而 agent 照**任何**指引去取都要能取到 ——
    /// 所以判据是「索引页提过这份文档」，不是「路由表里有这一行」。
    /// </para>
    /// <para>
    /// 点目录里的 <c>.md</c> 也算：它们会被分发（csproj 的 glob 收）、也会进语料，
    /// 所以同样得在索引页里有位置。
    /// </para>
    /// <para>
    /// <b>已知限制</b>（两个方向并不对称，别当成对称的）：
    /// <list type="bullet">
    /// <item><b>前向看不见含空白或标点的文件名</b>（字符集不含它们）：
    /// 「references/我的 笔记.md」这种名字只有**后向**认得
    /// （后向按整条路径找，不靠正则切）。</item>
    /// <item><b>比较是大小写不敏感的</b>：<c>OrdinalIgnoreCase</c> ——
    /// 枚举的 glob 在 Windows 上不分大小写、在 Linux 上分，
    /// 比较若分大小写，同一份文档换个机器就红。
    /// 代价是 Linux 上两个只差大小写的文件（<c>api.md</c> 与 <c>API.md</c>）
    /// 会被当成同一份，一个提及顶替两份 —— 那是枚举侧固有的歧义，
    /// 不是这条守卫的错。</item>
    /// </list>
    /// </para>
    /// </remarks>
    [Fact]
    public void The_index_page_points_at_exactly_the_reference_docs()
    {
        var docs = ReferenceDocs();
        var text = IndexPageText();

        // 后向：每份文档都得在索引页里被提到。
        // 按**整条路径**找，不靠正则切 —— 文件名带空格（references/我的 笔记.md）
        // 或带中文（references/接口.md）时也认得出来。
        // 但要带边界：references/api.mdx 里的 references/api.md 不算提及
        // （那是指着另一份不存在的文档）。
        var unmentioned = docs
            .Where(doc => !Mentions(text, doc))
            .OrderBy(doc => doc, StringComparer.Ordinal)
            .ToArray();

        // 前向：索引页提到的每条路径都得存在。
        var dangling = ReferenceDocPattern.Matches(text)
            .Select(match => match.Groups["path"].Value)
            .Where(mention => !docs.Any(doc => string.Equals(doc, mention, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            dangling.Length == 0 && unmentioned.Length == 0,
            "索引页与 references/ 对不上："
            + (dangling.Length > 0 ? $"索引页提到的 {string.Join("、", dangling)} 不在那儿（指针悬空）；" : string.Empty)
            + (unmentioned.Length > 0 ? $"{string.Join("、", unmentioned)} 没被索引页提到。" : string.Empty)
            + "索引页是 agent 取细节的指引，两边必须一一对应。");
    }

    /// <summary>
    /// 语料的文件集合必须**恰好**是「索引页 + references/ 下递归的全部 .md」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 语料既要**全**（少读一份文档 = 那一整份不受任何守卫管），
    /// 也要**确定**（glob 改坏了，守卫会集体变哑 ——
    /// 只扫索引页时，守卫 1/2/9/10 一条都不会红）。
    /// </para>
    /// <para>
    /// 期望值独立算出来（枚举目录下所有文件再按扩展名筛），
    /// 不复用 <see cref="Corpus"/> 的 glob —— 那样这条守卫只是把自己抄了一遍。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_corpus_is_exactly_the_index_page_plus_the_reference_docs()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { RepoPaths.SkillIndex };

        foreach (var file in Directory
            .EnumerateFiles(RepoPaths.SkillReferences, "*", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(path)))
        {
            if (file.EndsWith(".md", StringComparison.Ordinal))
            {
                expected.Add(file);
            }
        }

        var actual = Corpus()
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);

        var missing = expected.Except(actual).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected).OrderBy(path => path, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            "语料的文件集合不对："
            + (missing.Length > 0 ? $"少了 {string.Join("、", missing.Select(Relative))}；" : string.Empty)
            + (extra.Length > 0 ? $"多了 {string.Join("、", extra.Select(Relative))}。" : string.Empty)
            + "语料必须恰好是「索引页 + references/ 下递归的全部 .md」—— 少一份就有一份文档不受守卫管。");
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
        foreach (var file in Corpus())
        {
            var lines = file.Text.Replace("\r\n", "\n").Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var match in HardcodedCounts(lines[index]))
                {
                    Assert.Fail(
                        $"{file.Name} 第 {index + 1} 行写了「{match.Value}」—— "
                        + "手册里不写死路径条数：契约一变它就过期。写「完整契约见 /openapi/v1.json」。");
                }
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
        var corpus = string.Join("\n", Corpus().Select(file => file.Text));

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

        // 中文不属于路径。
        var afterChinese = MethodPathPattern.Match("GET /api/ideas这个端点");

        Assert.True(afterChinese.Success, "「方法 + 路径」的正则认不出路径后面跟着中文的写法。");
        Assert.Equal("/api/ideas", afterChinese.Groups["path"].Value);

        // 「写死条数」：认得出两种数字写法，也认得出量词与名词之间夹着 API。
        Assert.Single(HardcodedCounts("契约里有 29 条路径"));
        Assert.Single(HardcodedCounts("一共二十九个端点"));
        Assert.Single(HardcodedCounts("契约里有 32 条 API 路径"));

        // 「一」是量词不是计数：这种话不是「路径总条数」的陈述。
        Assert.Empty(HardcodedCounts("每次只发一个接口"));
        Assert.Empty(HardcodedCounts("只有一条路径"));

        // 细节文档的提及：认得出中文文件名与带点的名字，也不把标点吃进路径。
        Assert.Equal(
            "references/api.md",
            ReferenceDocPattern.Match("取哪份 | `references/api.md` |").Groups["path"].Value);

        Assert.Equal(
            "references/.api/api.md",
            ReferenceDocPattern.Match("`references/.api/api.md`").Groups["path"].Value);

        Assert.Equal(
            "references/接口.md",
            ReferenceDocPattern.Match("见 `references/接口.md`。").Groups["path"].Value);

        Assert.Equal(
            "references/api.v2.md",
            ReferenceDocPattern.Match("`references/api.v2.md`").Groups["path"].Value);

        // 两个提及挨着时，路径在第一个 .md 处就收尾。
        Assert.Equal(
            "references/api.md",
            ReferenceDocPattern.Match("`references/api.md` 与 `references/calling.md`").Groups["path"].Value);

        // 路径后面紧跟中文字母：中文手册里「取 references/errors.md即可」很常见，
        // 不能因此把整条提及作废。
        Assert.Equal(
            "references/api.md",
            ReferenceDocPattern.Match("取 references/api.md即可").Groups["path"].Value);

        // 后面是 ASCII 的 x：那不是一份 .md 文档。
        Assert.False(
            ReferenceDocPattern.IsMatch("`references/api.mdx`"),
            "「细节文档提及」的正则把 .mdx 当成了一份 .md 文档。");

        // 后向的边界与前向同一把尺子。
        Assert.True(Mentions("见 `references/api.md`。", "references/api.md"));
        Assert.True(Mentions("取 references/api.md即可", "references/api.md"));
        Assert.False(Mentions("见 `references/api.mdx`", "references/api.md"));
    }

    /// <summary>语料：索引页 + references/ 下（递归）的全部 .md。</summary>
    /// <remarks>
    /// <para>
    /// 递归：references/ 下的子目录里也是手册的一部分 ——
    /// 少读一份，等于有一整份文档不受任何守卫管。
    /// </para>
    /// <para>
    /// 跳过 <c>__pycache__</c> 段：服务端 <c>SkillCatalog</c> 枚举分发副本时
    /// 排除的也是这一种段（别的文件一律进清单）。
    /// </para>
    /// </remarks>
    private static List<CorpusFile> Corpus()
    {
        var paths = new List<string> { RepoPaths.SkillIndex };

        paths.AddRange(Directory
            .EnumerateFiles(RepoPaths.SkillReferences, "*.md", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(path))
            .OrderBy(path => path, StringComparer.Ordinal));

        return paths
            .Select(path => new CorpusFile(
                path,
                Path.GetRelativePath(RepoPaths.Root, path),
                File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>索引页的全文（frontmatter 与正文都算）。</summary>
    /// <remarks>
    /// spec §14 那行的措辞是「在索引页被提到」—— 不限定在哪一节。
    /// </remarks>
    private static string IndexPageText() => File.ReadAllText(RepoPaths.SkillIndex);

    /// <summary>索引页里有没有提到这份文档。</summary>
    /// <remarks>
    /// 判据：路径**整条**出现在索引页里，且紧跟其后的字符不会把路径**延长**成另一条
    /// （见 <see cref="MentionBoundary"/>）：<c>references/api.mdx</c> 里的
    /// <c>references/api.md</c> 不算提及，<c>`references/api.md`</c> 与
    /// <c>references/api.md。</c> 都算。
    /// <para>
    /// 比较不分大小写 —— 与前向、与枚举口径一致（见守卫 remarks 里的「已知限制」）。
    /// </para>
    /// </remarks>
    private static bool Mentions(string text, string doc)
    {
        return Regex.IsMatch(
            text,
            Regex.Escape(doc) + MentionBoundary,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary><c>references/</c> 下的全部 .md（相对内容源根的路径，正斜杠分隔）。</summary>
    /// <remarks>
    /// <para>
    /// 与索引页里提到的形态同一个坐标系（都是相对内容源根）：
    /// 索引页写的是 <c>references/api.md</c>，这里也得是它。
    /// </para>
    /// <para>
    /// 与语料同一口径：递归、跳过 <c>__pycache__</c> 段，
    /// **点目录里的 .md 也算**（它们会被分发、也会进语料）。
    /// </para>
    /// <para>
    /// 比较用**大小写不敏感**：枚举的 glob 在 Windows 上不分大小写、在 Linux 上分，
    /// 比较若分大小写，同一份文档换个机器就红。集合本身按大小写敏感存
    /// （两个只差大小写的文件都在），但比较时它们互相顶替 ——
    /// 一个提及顶替两份，这是比较口径的代价（见守卫 remarks 里的「已知限制」）。
    /// </para>
    /// </remarks>
    private static HashSet<string> ReferenceDocs()
    {
        return Directory
            .EnumerateFiles(RepoPaths.SkillReferences, "*.md", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(path))
            .Select(path => Path.GetRelativePath(RepoPaths.SkillDocs, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>这条路径上有没有开发侧的残留目录（<c>__pycache__</c>）。</summary>
    /// <remarks>
    /// <c>SkillCatalog</c> 枚举分发副本时也只排除这一种段（别的文件一律进清单）。
    /// </remarks>
    private static bool HasExcludedSegment(string path)
    {
        return SegmentsOf(path).Any(segment => segment == ExcludedDirectoryName);
    }

    /// <summary>
    /// 这条路径是不是「残留」—— 内容源里不该有、也不该让形状守卫红的东西。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据：<b>相对于内容源根</b>的路径里，有任何一段以点开头，或有 <c>__pycache__</c> 段。
    /// 点开头的（<c>.cache/</c>、<c>.vscode/</c>、<c>.DS_Store</c>、<c>.gitignore</c>）
    /// 是编辑器与工具留下的：**在形状上**不算内容源的一部分（所以形状检查放过它们）。
    /// </para>
    /// <para>
    /// 但它们里面的 <c>.md</c> **会**被分发（csproj 的 <c>references/**/*.md</c> glob 收）、
    /// **也会**进语料，所以仍受手册的各条守卫管 ——
    /// 「点目录不是内容」只对形状成立。
    /// </para>
    /// <para>
    /// <b>普通目录不放行</b>（<c>scripts/</c>、<c>tests/</c>、<c>notes/</c> 那些）：
    /// 那些是真该被红的东西 —— 守卫红一次，人就学会无视守卫。
    /// </para>
    /// <para>
    /// 必须按**相对路径**判：仓库自己就躺在 <c>.worktrees/</c> 里，
    /// 拿绝对路径判点开头会把整棵树都当成残留。
    /// </para>
    /// </remarks>
    private static bool IsResiduePath(string path)
    {
        var segments = SegmentsOf(path);

        // 不在内容源里的路径（相对路径带 ..）：不当残留处理 ——
        // 那样会被静默跳过，而这条判据说的是「内容源里的残留」。
        if (segments.Length > 0 && segments[0] == "..")
        {
            return false;
        }

        return segments.Any(segment =>
            segment == ExcludedDirectoryName
            || segment == ResidueFileName
            || segment.StartsWith('.'));
    }

    /// <summary>路径相对内容源根之后的各段（相对路径，绕开仓库自己所在的 <c>.worktrees/</c>）。</summary>
    private static string[] SegmentsOf(string path)
    {
        return Path.GetRelativePath(RepoPaths.SkillDocs, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>路径相对仓库根的样子（报错时看的是它，不是绝对路径）。</summary>
    private static string Relative(string path) => Path.GetRelativePath(RepoPaths.Root, path);

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

        foreach (var file in Corpus())
        {
            var lines = file.Text.Replace("\r\n", "\n").Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in MethodPathPattern.Matches(lines[index]))
                {
                    var method = match.Groups["method"].Value;
                    var path = NormalizePath(match.Groups["path"].Value);

                    found.TryAdd((method, path), new Mention(method, path, file.Name, index + 1));
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

    /// <summary>一段文字里把路径条数写死的地方。</summary>
    /// <remarks>
    /// 「一」不算：<see cref="IsCountAtLeastTwo"/> 说了算。
    /// </remarks>
    private static List<Match> HardcodedCounts(string text)
    {
        return HardcodedCountPattern.Matches(text)
            .Where(match => IsCountAtLeastTwo(match.Groups["count"].Value))
            .ToList();
    }

    /// <summary>这个计数是不是「至少两个」（一不算）。</summary>
    private static bool IsCountAtLeastTwo(string count)
    {
        if (count.All(char.IsAsciiDigit))
        {
            return int.TryParse(count, out var value) && value >= 2;
        }

        // 中文数字：字符集里已经去掉了「一」，
        // 只要还有一个非「零」的字符（二三四五六七八九十百两），计数就 >= 2。
        return count.Any(character => character is not '零');
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

    /// <summary>markdown 文件开头的 frontmatter 块与它之后的正文。</summary>
    private sealed record FrontmatterBlock(string Frontmatter, string Body);

    /// <summary>把文件切成「frontmatter」与「正文」两半。</summary>
    private static FrontmatterBlock SplitFrontmatter(string text, string origin)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        // frontmatter 的起始 --- 必须是文件开头的第一个非空行：
        // 否则正文里的水平分割线会被误当成 frontmatter 的起点。
        var start = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim().Length == 0)
            {
                continue;
            }

            if (lines[index].Trim() == "---")
            {
                start = index;
            }

            break;
        }

        Assert.True(
            start >= 0,
            $"{origin} 里找不到 frontmatter 的起始 ---（它得是文件开头的第一个非空行）。");

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

        return new FrontmatterBlock(
            string.Join("\n", lines.Skip(start + 1).Take(end - start - 1)),
            string.Join("\n", lines.Skip(end + 1)));
    }

    /// <summary>frontmatter 里某个键的值（没有这个键就返回 null）。</summary>
    private static string? FrontmatterValue(string frontmatter, string key)
    {
        foreach (var line in frontmatter.Split('\n'))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }

            return line[(key.Length + 1)..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>语料里的一份文件。</summary>
    private sealed record CorpusFile(string Path, string Name, string Text);

    /// <summary>手册里的一次「方法 + 路径」提及。</summary>
    private sealed record Mention(string Method, string Path, string File, int Line);
}
