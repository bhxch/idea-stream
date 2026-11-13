using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace IdeaApp.Tests;

/// <summary>
/// 请求体上限的**数字守卫**。
///
/// <para>
/// 「请求体太大」在两条部署路径下有两个**不同的上限**：
/// <list type="bullet">
/// <item>经 nginx（仓库自带的 <c>docs/nginx/idea.conf</c>）→ <c>client_max_body_size</c>；</item>
/// <item>直连 Kestrel → 框架默认的 <c>MaxRequestBodySize</c>（项目自己没配过）。</item>
/// </list>
/// </para>
///
/// <para>
/// 这两个数字**会漂**：改了 nginx 配置或 Kestrel 配置，文档里的数字不会自动跟着变，
/// 于是 skill 说明书开始说假话 —— 上一轮就是这么错的（文档写「413 的响应体是空的」，
/// 而经 nginx 时拿到的其实是 nginx 自己回的东西）。
/// 这里把它们钉住：<b>部署配置 ↔ 设计文档里的片段副本 ↔ SKILL.md 里的数字</b>，
/// 任何一边单独改都会红。
/// </para>
///
/// <para>
/// <b>单位口径（别搞混）：</b>
/// <list type="number">
/// <item>nginx 的 <c>k</c>/<c>m</c>/<c>g</c> 后缀 → <b>1024</b> 进位（nginx 自己的语义）：
/// 所以配置里的 <c>4m</c> = 4 MiB = <b>4194304</b> 字节。</item>
/// <item>文档里的二进制单位（<c>KiB</c>/<c>MiB</c>/<c>GiB</c>）→ 也是 1024 进位。</item>
/// <item>文档里的十进制单位（<c>KB</c>/<c>MB</c>/<c>GB</c>）→ 按惯例是 <b>1000</b> 进位：
/// 「4 MB」说的是 4,000,000 字节，与 nginx 的 <c>4m</c> 不是一回事。
/// 所以守卫要求文档把上限写成**精确字节数**（「4 MiB（4194304 字节）」），
/// 免得读者按 1,000,000 去理解 —— 只写「4 MB」的话，数字对不对根本没法核对。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>生效的上限</b>取配置里所有 <c>client_max_body_size</c> 的<b>最小值</b>：
/// nginx 里 location 层的指令会覆盖 server 层的那条，location 里的更小值才是实际生效的那个。
/// 只看第一条（server 层）的话，将来往某个 location 里加一条更小的上限，守卫就看不见了。
/// </para>
///
/// <para>
/// <b>散文里的上限声明要锚定上下文</b>：只有「在谈上限」且「在谈 nginx」的那一句里的数字
/// 才算数 —— 否则将来 SKILL.md 里多一句无关的体积话（例如「把 body 压到 1MB 以内」）
/// 就会把守卫带偏（误报），或者反过来：把「4 MiB」改成有歧义的「4 MB」它还照样绿（放过）。
/// </para>
///
/// <para>
/// 顺带守住 nginx 那个**结构化 413**：它必须回一个**没有 <c>code</c> 字段**的 JSON ——
/// 有 <c>code</c> 的话 CLI 会拿它当业务错误码，agent 就得为「经 nginx / 直连」
/// 分两条路处理；没有 <c>code</c>，CLI 才会合成 <c>http_413</c>，两条路径同一个码。
/// </para>
/// </summary>
public sealed class RequestBodyLimitGuardTests
{
    /// <summary>仓库自带的 nginx 配置。</summary>
    private static string NginxConfigPath =>
        Path.Combine(RepoPaths.Root, "docs", "nginx", "idea.conf");

    /// <summary>skill 的说明书（agent 看的那份）。</summary>
    private static string SkillDocPath =>
        Path.Combine(RepoPaths.Root, "skills", "idea-manager", "SKILL.md");

    /// <summary>设计文档。</summary>
    private static string DesignDocPath =>
        Path.Combine(
            RepoPaths.Root,
            "docs",
            "superpowers",
            "specs",
            "2026-11-11-idea-manager-design.md");

    /// <summary>nginx 指令：<c>client_max_body_size 4m;</c>。</summary>
    private static readonly Regex NginxLimitPattern = new(
        @"client_max_body_size\s+(?<value>\d[\d,]*[a-zA-Z]*)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 文档里的 nginx 指令写法：<c>client_max_body_size 4m</c>。
    /// </summary>
    /// <remarks>
    /// 只认**带单位**的写法：文档里那句「改了 nginx 的 <c>client_max_body_size</c> 或 …」
    /// 后面跟的不是数字，不会被误当成一个上限。
    /// </remarks>
    private static readonly Regex SkillNginxDirectivePattern = new(
        @"client_max_body_size\s+(?<value>\d[\d,]*[a-zA-Z]*)",
        RegexOptions.CultureInvariant);

    /// <summary>文档里的散文写法（大小）：<c>4 MiB</c> / <c>4MB</c>。</summary>
    private static readonly Regex SkillProseSizePattern = new(
        @"(?<number>\d[\d,]*)\s*(?<unit>KiB|MiB|GiB|KB|MB|GB)(?![A-Za-z])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 文档里的**精确写法**：<c>4 MiB（4194304 字节）</c>。
    /// </summary>
    /// <remarks>
    /// 大小紧跟着精确字节数 —— 只有这种写法才核得对，
    /// 也只有这种写法才认得出「4 MB」到底说的是 4,000,000 还是 4,194,304。
    /// </remarks>
    private static readonly Regex SkillPreciseSizePattern = new(
        @"(?<number>\d[\d,]*)\s*(?<unit>KiB|MiB|GiB|KB|MB|GB)\s*[（(]\s*(?<bytes>\d[\d,]*)\s*字节",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 「上限」这类词：说明这一句在谈上限，才算「上限声明」。
    /// </summary>
    /// <remarks>
    /// 用来把「把 body 压到 1MB 以内」这种无关的体积话挡在外面（误报）。
    /// </remarks>
    private static readonly Regex LimitLanguagePattern = new(
        @"上限|限额|限制|最多|不超过|最大|limit|max",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// SKILL.md 里 **Kestrel 那一行**上的字节数：<c>Kestrel … 30,000,000 字节</c>。
    /// </summary>
    /// <remarks>
    /// 必须写在**同一行**上：这样才能把 Kestrel 的上限与 nginx 的上限分开取。
    /// </remarks>
    private static readonly Regex SkillKestrelBytesPattern = new(
        @"Kestrel[^\n]*?(?<value>\d[\d,]*)\s*字节",
        RegexOptions.CultureInvariant);

    /// <summary>大小字面量：数字 + 单位。</summary>
    private static readonly Regex SizeLiteralPattern = new(
        @"^(?<number>\d[\d,]*)\s*(?<unit>[a-zA-Z]*)$",
        RegexOptions.CultureInvariant);

    /// <summary>nginx 的 <c>error_page 413 = @名字;</c>。</summary>
    private static readonly Regex ErrorPagePattern = new(
        @"error_page\s+413\s*=\s*@(?<name>[\w-]+)",
        RegexOptions.CultureInvariant);

    /// <summary>named location 里的 <c>return 413 '…';</c>。</summary>
    private static readonly Regex ReturnJsonPattern = new(
        @"return\s+(?<status>\d{3})\s+'(?<json>[^']*)'",
        RegexOptions.CultureInvariant);

    /// <summary>有序列表项（<c>1. </c>）。</summary>
    private static readonly Regex OrderedListItemPattern = new(
        @"^\d+[.)]\s",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 部署配置里的 nginx 上限 ↔ 设计文档 §16.3 片段里的同一个值。
    /// </summary>
    /// <remarks>
    /// 设计文档里存的是**片段副本**（少了 upstream、SSE 段与 <c>error_page</c>），
    /// 两份天然会漂。片段里的「请求体上限」这一项由这里守住。
    /// </remarks>
    [Fact]
    public void The_nginx_body_limit_in_the_deploy_config_matches_the_design_doc()
    {
        var configured = ReadNginxLimit(NginxConfigPath);
        var documented = ParseNginxLimit(
            StripHashComments(DesignNginxSnippet()),
            "设计文档 §16.3 的 nginx 片段");

        Assert.True(
            configured == documented,
            $"设计文档 §16.3 的 nginx 片段里的 client_max_body_size（{documented} 字节）"
            + $"与 {NginxConfigPath} 里的（{configured} 字节）不一致。"
            + "请同步这两处：片段是副本，以 docs/nginx/idea.conf 为准。");
    }

    /// <summary>
    /// 部署配置里的 nginx 上限 ↔ SKILL.md 里 agent 看到的那个数字。
    /// </summary>
    /// <remarks>
    /// SKILL.md 里 nginx 的上限可以写成两种形式，都认：
    /// nginx 自己的指令写法（<c>client_max_body_size 4m</c>），
    /// 与散文写法（<c>4 MiB（4194304 字节）</c>）。
    /// 散文写法必须带精确字节数 —— 见
    /// <see cref="The_skill_doc_states_the_nginx_limit_with_exact_bytes"/>。
    /// </remarks>
    [Fact]
    public void The_nginx_body_limit_in_the_deploy_config_matches_the_skill_doc()
    {
        var configured = ReadNginxLimit(NginxConfigPath);
        var claims = ReadSkillNginxClaims();

        Assert.True(
            claims.Count > 0,
            $"在 {SkillDocPath} 里找不到 nginx 的请求体上限 —— "
            + $"请在讲 nginx 上限的那一句里写出上限（当前 {NginxConfigPath} 里生效的上限是 {configured} 字节），"
            + "写成「4 MiB（4194304 字节）」这样的形式（带精确字节数），"
            + "否则 agent 看到的部署上限是空的。");

        foreach (var claim in claims)
        {
            var written = claim.ExactBytes ?? claim.DirectiveBytes;

            Assert.True(
                written.HasValue,
                $"SKILL.md 第 {claim.Line} 行写的 nginx 请求体上限（{claim.Raw}）没有精确字节数 —— "
                + $"请写成「4 MiB（{configured} 字节）」这样的形式，"
                + "别只写「4 MB」这种有歧义的写法（有人会按 1,000,000 理解，那就错了）。"
                + $"原文：{Snippet(claim)}");

            Assert.True(
                configured == written,
                $"SKILL.md 第 {claim.Line} 行写的 nginx 请求体上限（{claim.Raw} = {written} 字节）"
                + $"与 {NginxConfigPath} 里生效的上限（{configured} 字节）不一致，请同步。"
                + $"原文：{Snippet(claim)}");
        }
    }

    /// <summary>
    /// SKILL.md 里 nginx 那个上限必须写成**精确字节数**（<c>4 MiB（4194304 字节）</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「4 MiB」还行（MiB 就是 1024 进位），「4 MB」就有歧义了 ——
    /// 按惯例那是 4,000,000，与 nginx 的 <c>4m</c>（4,194,304）不是一回事。
    /// 守卫认「单位 + 精确字节数」这一种写法，并把两者的进位核对一遍：
    /// 单位说 1000 进位、字节数却是 1024 进位的结果 —— 自相矛盾，红。
    /// </para>
    /// <para>
    /// 没有精确字节数的话，人根本没法核对那个数字对不对（也核不了），
    /// 所以宁可要求写精确值。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_skill_doc_states_the_nginx_limit_with_exact_bytes()
    {
        var claims = ReadSkillNginxClaims();
        var prose = claims.Where(claim => claim.IsProse).ToList();

        Assert.True(
            prose.Count > 0,
            $"在 {SkillDocPath} 里找不到 nginx 请求体上限的散文写法 —— "
            + "请在讲 nginx 上限的那一句里写成「4 MiB（4194304 字节）」这样的形式。");

        foreach (var claim in prose)
        {
            Assert.True(
                claim.ExactBytes.HasValue,
                $"SKILL.md 第 {claim.Line} 行写 nginx 的请求体上限只写了「{claim.Raw}」，没有精确字节数 —— "
                + "请写成「4 MiB（4194304 字节）」这样的形式：只写「4 MB」是有歧义的"
                + "（有人按 1,000,000 理解，那就错了），写精确字节数才核得对。"
                + $"原文：{Snippet(claim)}");

            var expected = LabelBytes(claim.Number, claim.Unit);

            Assert.True(
                expected == claim.ExactBytes,
                $"SKILL.md 第 {claim.Line} 行的「{claim.Raw}（{claim.ExactBytes} 字节）」自相矛盾 —— "
                + $"「{claim.Unit}」按 {LabelBase(claim.Unit)} 进位是 {expected} 字节，"
                + $"和写出来的字节数对不上。nginx 的 `m` 是 1024 进位，请写成「4 MiB（4194304 字节）」。"
                + $"原文：{Snippet(claim)}");
        }
    }

    /// <summary>
    /// 应用**自己**没有改请求体上限 —— 直连路径的上限就是框架默认值。
    /// </summary>
    /// <remarks>
    /// 应用一旦调了 <c>MaxRequestBodySize</c> 或 <c>ConfigureKestrel</c>，
    /// 直连 Kestrel 的上限就不再是 30,000,000 字节，
    /// SKILL.md 里写的那个数字（以及下一条守卫）就全错了。
    /// </remarks>
    [Fact]
    public void The_app_does_not_configure_its_own_request_body_limit()
    {
        var offenders = new List<string>();

        foreach (var file in RepoPaths.SourceFiles(RepoPaths.AppSource))
        {
            var text = SourceText.StripComments(File.ReadAllText(file));
            var origin = Path.GetRelativePath(RepoPaths.Root, file);

            foreach (var token in new[] { "MaxRequestBodySize", "ConfigureKestrel" })
            {
                var index = text.IndexOf(token, StringComparison.Ordinal);

                if (index < 0)
                {
                    continue;
                }

                offenders.Add(
                    $"{origin}:{SourceText.LineNumber(text, index)} 出现了 {token}"
                    + " —— 应用一旦自己设了请求体上限，直连路径的上限就不再是框架默认值，"
                    + "SKILL.md 里写的数字与「框架默认上限」那条守卫都要跟着改。");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("；", offenders));
    }

    /// <summary>
    /// 应用**也没有在配置文件里**改请求体上限。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只扫源码是不够的：将来有人用 <c>appsettings.json</c> 的
    /// <c>Kestrel:Limits:MaxRequestBodySize</c> 改上限（这是 .NET 里最常见的写法），
    /// 源码里一个关键字都没有，SKILL.md 里那句「框架默认值」就成了假话。
    /// </para>
    /// <para>
    /// 所以这里把 <c>server/IdeaApp</c> 下的 <c>appsettings*.json</c> 也扫一遍：
    /// 只要出现 <c>Kestrel</c> 配置段或 <c>MaxRequestBodySize</c>，就红。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_app_does_not_configure_the_request_body_limit_in_appsettings()
    {
        var offenders = new List<string>();

        foreach (var file in AppSettingsFiles())
        {
            var origin = Path.GetRelativePath(RepoPaths.Root, file);
            var text = File.ReadAllText(file);
            JsonElement root;

            try
            {
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                offenders.Add(
                    $"{origin} 不是合法 JSON（{error.Message}）—— 守卫读不了它，"
                    + "没法确认里面有没有配请求体上限。");

                continue;
            }

            foreach (var hit in FindKestrelSettings(root))
            {
                offenders.Add(
                    $"{origin} 里出现了 {hit} —— 上限改成用配置配了，"
                    + "直连 Kestrel 的上限就不再是框架默认值（30,000,000 字节），"
                    + "SKILL.md 里写的数字要跟着改，"
                    + "「框架默认上限」那条守卫也只认框架默认值。");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("；", offenders));
    }

    /// <summary>
    /// SKILL.md 里 Kestrel 那个上限 == 框架默认值（<c>MaxRequestBodySize</c>）。
    /// </summary>
    /// <remarks>
    /// 框架默认值是 <b>精确字节数</b>（30000000），不是「约 30MB」——
    /// 精确值才能被这条断言钉住；写「约 30MB」的话没人能核对它还对不对。
    /// </remarks>
    [Fact]
    public void The_skill_doc_states_the_framework_default_request_body_limit()
    {
        var frameworkDefault = new KestrelServerOptions().Limits.MaxRequestBodySize;

        Assert.True(
            frameworkDefault == 30_000_000L,
            $"Kestrel 的框架默认请求体上限变成了 {frameworkDefault} 字节（原来 30000000）。"
            + "若确实如此，请更新 SKILL.md 里写的数字，并同步修改这条断言里的期望值。");

        var text = File.ReadAllText(SkillDocPath);
        var documented = new List<(long Value, string Source)>();

        foreach (Match match in SkillKestrelBytesPattern.Matches(text))
        {
            if (!TryParseNumber(match.Groups["value"].Value, out var value))
            {
                continue;
            }

            documented.Add((value, match.Groups["value"].Value));
        }

        Assert.True(
            documented.Count > 0,
            $"在 {SkillDocPath} 里找不到 Kestrel 的请求体上限 —— "
            + "请写成精确的字节数（如「30,000,000 字节」），别写「约 30MB」："
            + "模糊的数字没人能核对，也没法被守卫钉住。");

        foreach (var (value, source) in documented)
        {
            Assert.True(
                frameworkDefault == value,
                $"SKILL.md 写的 Kestrel 请求体上限（{source} 字节）与框架默认值"
                + $"（{frameworkDefault} 字节）不一致，请同步 —— "
                + "直连 Kestrel 时的请求体上限就是这个框架默认值。");
        }
    }

    /// <summary>
    /// nginx 遇到超限请求要回**结构化 JSON**，而且**刻意不带 <c>code</c> 字段**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 少了这个 <c>error_page</c>，nginx 会回自己的 HTML 错误页 →
    /// CLI 只能报 <c>invalid_response</c>（响应不是 JSON），
    /// agent 拿不到「是 body 太大」这个信号，只能按文案猜。
    /// </para>
    /// <para>
    /// 带上了它，nginx 回的是 ProblemDetails 形状的 JSON。**没有 <c>code</c> 字段**
    /// 是关键：CLI 的 <c>_error_for</c> 遇到「有 JSON 但没有 code」的响应会合成
    /// <c>http_413</c> —— 与直连 Kestrel 时「空响应体」那条路径**同一个码**，
    /// agent 不必分支两条部署路径。
    /// </para>
    /// </remarks>
    [Fact]
    public void The_nginx_config_answers_an_oversized_body_with_structured_json()
    {
        var text = File.ReadAllText(NginxConfigPath);
        var stripped = StripHashComments(text);

        var errorPage = ErrorPagePattern.Match(stripped);

        Assert.True(
            errorPage.Success,
            $"{NginxConfigPath} 里没有 `error_page 413 = @…;` —— "
            + "缺了它，超限时 nginx 会回自己的 HTML 错误页，"
            + "CLI 只能报 invalid_response（响应不是 JSON），agent 拿不到「body 太大」这个信号。");

        var name = errorPage.Groups["name"].Value;
        var block = ExtractBlock(stripped, $"location @{name}");

        Assert.True(
            block.Length > 0,
            $"{NginxConfigPath} 里 `error_page 413 = @{name};` 指向的 named location 找不到。");

        var returned = ReturnJsonPattern.Match(block);

        Assert.True(
            returned.Success,
            $"{NginxConfigPath} 的 `location @{name}` 里没有 `return 413 '<JSON>';` —— "
            + "结构化的 413 响应体没了。");

        Assert.True(
            returned.Groups["status"].Value == "413",
            $"{NginxConfigPath} 的 `location @{name}` 回的状态码不是 413。");

        var json = returned.Groups["json"].Value;
        var parsed = TryParseJson(json);

        Assert.True(
            parsed.HasValue,
            $"{NginxConfigPath} 的 `location @{name}` 回的不是合法 JSON：{json}"
            + " —— agent 拿到它只能报 invalid_response（响应不是 JSON）。");

        var root = parsed!.Value;

        Assert.True(
            root.ValueKind == JsonValueKind.Object,
            $"{NginxConfigPath} 的 `location @{name}` 回的 JSON 不是对象：{json}");

        Assert.True(
            root.TryGetProperty("status", out var status) && status.GetInt32() == 413,
            $"{NginxConfigPath} 的 `location @{name}` 回的 JSON 里没有 status=413：{json}");

        Assert.True(
            root.TryGetProperty("detail", out var detail)
            && !string.IsNullOrWhiteSpace(detail.GetString()),
            $"{NginxConfigPath} 的 `location @{name}` 回的 JSON 里没有 detail —— "
            + "agent 只看得到码也得知道「是 body 太大」。");

        Assert.False(
            root.TryGetProperty("code", out _),
            $"{NginxConfigPath} 的 `location @{name}` 回的 JSON 里带 code 字段 —— "
            + "CLI 会把它当成业务错误码，agent 就得为「经 nginx / 直连」分支处理；"
            + "要两条路径同一个码（http_413），这里必须**不带** code。");
    }

    /// <summary>SKILL.md 里 nginx 上限的一条声明。</summary>
    /// <param name="Line">行号（从 1 开始）。</param>
    /// <param name="Clause">出处（那一小段文字）。</param>
    /// <param name="IsProse">是不是散文写法（<c>4 MiB</c>），而不是指令写法。</param>
    /// <param name="ExactBytes">散文写法旁边写出的精确字节数（没写就是 <c>null</c>）。</param>
    /// <param name="DirectiveBytes">指令写法（<c>client_max_body_size 4m</c>）里的值。</param>
    /// <param name="Raw">原始写法。</param>
    /// <param name="Number">原始写法里的数字。</param>
    /// <param name="Unit">原始写法里的单位。</param>
    private sealed record NginxClaim(
        int Line,
        string Clause,
        bool IsProse,
        long? ExactBytes,
        long? DirectiveBytes,
        string Raw,
        string Number,
        string Unit);

    /// <summary>读部署配置里的 nginx 上限（字节）。</summary>
    /// <remarks>
    /// 取的是所有 <c>client_max_body_size</c> 里的**最小值**：nginx 里 location 层的指令
    /// 覆盖 server 层的那条，location 里的更小值才是实际生效的上限。
    /// </remarks>
    private static long ReadNginxLimit(string path) =>
        ParseNginxLimit(StripHashComments(File.ReadAllText(path)), path);

    /// <summary>从一段 nginx 配置文本里读出生效的 <c>client_max_body_size</c>（字节）。</summary>
    private static long ParseNginxLimit(string text, string origin)
    {
        var limits = new List<(long Bytes, string Raw)>();

        foreach (Match match in NginxLimitPattern.Matches(text))
        {
            var raw = match.Groups["value"].Value;

            Assert.True(
                TryParseSize(raw, out var bytes),
                $"{origin} 里的 client_max_body_size 的值认不出来：{raw}");

            limits.Add((bytes, raw));
        }

        Assert.True(
            limits.Count > 0,
            $"{origin} 里找不到 `client_max_body_size` —— "
            + "守卫对请求体上限失明了，请确认指令还在。");

        // 生效的上限 = 最小的那条（location 里的更小值覆盖 server 层的）。
        return limits.Min(limit => limit.Bytes);
    }

    /// <summary>
    /// 读 SKILL.md 里 nginx 的那些上限声明。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **锚定上下文**：一条声明要么是 nginx 自己的指令写法（<c>client_max_body_size 4m</c>），
    /// 要么是散文写法（<c>4 MiB</c>）—— 而散文写法还要同时满足两个条件：
    /// </para>
    /// <list type="number">
    /// <item>它所在的**块**（列表项 / 段落 / 表格行）里提到过 nginx；</item>
    /// <item>它所在的**那一段**在谈上限（「上限」「不超过」这类词）。</item>
    /// </list>
    /// <para>
    /// 这样「把 body 压到 1MB 以内」这种无关的体积话不会被当成上限声明（不误报），
    /// 而「上限是 4 MB」这种真的在说 nginx 上限的话一定会被认出来（不放过）。
    /// </para>
    /// </remarks>
    private static List<NginxClaim> ReadSkillNginxClaims()
    {
        var lines = File.ReadAllLines(SkillDocPath);
        var claims = new List<NginxClaim>();

        foreach (var block in TextBlocks(lines))
        {
            // 这个块压根没提 nginx → 与 nginx 的上限无关。
            if (!MentionsNginx(block.Text))
            {
                continue;
            }

            for (var index = block.Start; index <= block.End; index++)
            {
                var line = lines[index];

                foreach (var unit in ContextUnits(line))
                {
                    var talksAboutLimit = LimitLanguagePattern.IsMatch(unit);

                    foreach (var clause in SplitClauses(unit))
                    {
                        var directive = SkillNginxDirectivePattern.Match(clause);

                        if (directive.Success)
                        {
                            var raw = directive.Groups["value"].Value;

                            if (TryParseSize(raw, out var directiveBytes))
                            {
                                claims.Add(
                                    new NginxClaim(
                                        index + 1,
                                        clause,
                                        false,
                                        null,
                                        directiveBytes,
                                        raw,
                                        SizeNumber(raw),
                                        SizeUnit(raw)));
                            }
                        }

                        foreach (Match size in SkillProseSizePattern.Matches(clause))
                        {
                            // 这一段在谈上限吗？这一小句在谈 nginx 吗？
                            if (!talksAboutLimit || !MentionsNginx(clause))
                            {
                                continue;
                            }

                            var precise = SkillPreciseSizePattern.Match(clause, size.Index);
                            long? exact = null;

                            if (precise.Success
                                && precise.Index == size.Index
                                && TryParseNumber(precise.Groups["bytes"].Value, out var bytes))
                            {
                                exact = bytes;
                            }

                            claims.Add(
                                new NginxClaim(
                                    index + 1,
                                    clause,
                                    true,
                                    exact,
                                    null,
                                    size.Value.Trim(),
                                    size.Groups["number"].Value,
                                    size.Groups["unit"].Value));
                        }
                    }
                }
            }
        }

        return claims;
    }

    /// <summary>应用的配置文件（<c>appsettings*.json</c>，跳过 bin/obj）。</summary>
    private static IEnumerable<string> AppSettingsFiles() =>
        Directory
            .EnumerateFiles(RepoPaths.AppSource, "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>在 JSON 里找 Kestrel 相关的配置。</summary>
    /// <remarks>
    /// 认 <c>Kestrel</c> 配置段（哪怕里面还没写上限）与 <c>MaxRequestBodySize</c>。
    /// </remarks>
    private static IEnumerable<string> FindKestrelSettings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "Kestrel", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return "Kestrel 配置段";
                    }
                    else if (string.Equals(
                        property.Name,
                        "MaxRequestBodySize",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        yield return "MaxRequestBodySize";
                    }

                    foreach (var nested in FindKestrelSettings(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in FindKestrelSettings(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>设计文档 §16.3 里的 nginx 片段（代码块内容）。</summary>
    private static string DesignNginxSnippet()
    {
        var text = File.ReadAllText(DesignDocPath);
        var heading = text.IndexOf("### 16.3 nginx", StringComparison.Ordinal);

        Assert.True(
            heading >= 0,
            $"{DesignDocPath} 里找不到 §16.3 的标题 —— 守卫对设计文档失明了。");

        var fence = text.IndexOf("```nginx", heading, StringComparison.Ordinal);

        Assert.True(
            fence >= 0,
            $"{DesignDocPath} 的 §16.3 里没有 nginx 代码块 —— 守卫对设计文档失明了。");

        var start = text.IndexOf('\n', fence) + 1;
        var end = text.IndexOf("```", start, StringComparison.Ordinal);

        Assert.True(
            end > start,
            $"{DesignDocPath} 的 §16.3 里的 nginx 代码块没有闭合 —— 守卫对设计文档失明了。");

        return text[start..end];
    }

    /// <summary>去掉 nginx 配置里的注释行（<c>#</c> 开头）。</summary>
    /// <remarks>
    /// 注释掉的指令不算生效的指令 —— 否则守卫会把「注释里的旧数字」当成真的上限。
    /// </remarks>
    private static string StripHashComments(string text) =>
        string.Join(
            "\n",
            text.Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#'))
                .ToArray());

    /// <summary>文档里的一段文字（列表项 / 段落 / 表格行）。</summary>
    /// <param name="Start">起始行号（从 0 开始）。</param>
    /// <param name="End">结束行号（从 0 开始，含）。</param>
    /// <param name="Text">整段文字。</param>
    private sealed record TextBlock(int Start, int End, string Text);

    /// <summary>
    /// 把文档切成「块」：列表项、段落、表格行各算一块。
    /// </summary>
    /// <remarks>
    /// 块是「这一段在谈什么」的上下文边界：
    /// 讲 nginx 上限的那个列表项自成一个块，与其它段落分开。
    /// </remarks>
    private static List<TextBlock> TextBlocks(string[] lines)
    {
        var blocks = new List<TextBlock>();
        var start = -1;

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            var blank = trimmed.Length == 0;

            var startsBlock = blank
                || trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("+ ", StringComparison.Ordinal)
                || trimmed.StartsWith("|", StringComparison.Ordinal)
                || trimmed.StartsWith("#", StringComparison.Ordinal)
                || OrderedListItemPattern.IsMatch(trimmed);

            if (startsBlock)
            {
                if (start >= 0)
                {
                    blocks.Add(new TextBlock(start, index - 1, string.Join("\n", lines[start..index])));
                }

                start = blank ? -1 : index;
                continue;
            }

            start = start < 0 ? index : start;
        }

        if (start >= 0)
        {
            blocks.Add(new TextBlock(start, lines.Length - 1, string.Join("\n", lines[start..])));
        }

        return blocks;
    }

    /// <summary>
    /// 把一行拆成「上下文单元」：普通行就是一整行，表格行按**单元格**拆。
    /// </summary>
    /// <remarks>
    /// 表格行必须按单元格拆：一格里前半段在讲 nginx 的上限、后半段在讲别的，
    /// 不拆开的话后半段那句无关的体积话会被前半段的 nginx 语境带偏（误报）。
    /// </remarks>
    private static IEnumerable<string> ContextUnits(string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("|", StringComparison.Ordinal))
        {
            return trimmed
                .Split('|')
                .Select(cell => cell.Trim())
                .Where(cell => cell.Length > 0);
        }

        return new[] { line };
    }

    /// <summary>
    /// 把一行切成「小句」（逗号、顿号、句号……）。
    /// </summary>
    /// <remarks>
    /// 刻意**不**按半角逗号切：<c>30,000,000</c> 里的逗号是千分位，不是断句。
    /// </remarks>
    private static IEnumerable<string> SplitClauses(string line) =>
        line.Split(
            new[] { '，', '、', '；', '。', '！', '？', ';', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>这段话里提到 nginx 吗（大小写不敏感）。</summary>
    private static bool MentionsNginx(string text) =>
        text.Contains("nginx", StringComparison.OrdinalIgnoreCase);

    /// <summary>出处那一小段（截断一下，够定位就行）。</summary>
    private static string Snippet(NginxClaim claim) =>
        claim.Clause.Length <= 80 ? claim.Clause : claim.Clause[..80] + "…";

    /// <summary>
    /// 取出 named location 的块内容（<c>{ … }</c> 之间的部分）。
    /// </summary>
    /// <remarks>
    /// 引号里的括号不算（<c>return 413 '{…}';</c> 里的 JSON 也带花括号）。
    /// </remarks>
    private static string ExtractBlock(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
        {
            return string.Empty;
        }

        var open = text.IndexOf('{', index);

        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        var quote = '\0';

        for (var cursor = open; cursor < text.Length; cursor++)
        {
            var current = text[cursor];

            if (quote != '\0')
            {
                if (current == '\\')
                {
                    cursor++;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return text[(open + 1)..cursor];
                }
            }
        }

        return string.Empty;
    }

    /// <summary>解析一段 JSON；不是合法 JSON 时返回 <c>null</c>。</summary>
    private static JsonElement? TryParseJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把 nginx 的大小字面量归一成字节（<b>1024</b> 进位，与 nginx 的 <c>k</c>/<c>m</c>/<c>g</c> 一致）。
    /// </summary>
    private static bool TryParseSize(string raw, out long bytes)
    {
        bytes = 0;

        var match = SizeLiteralPattern.Match(raw.Trim());

        if (!match.Success)
        {
            return false;
        }

        if (!TryParseNumber(match.Groups["number"].Value, out var number))
        {
            return false;
        }

        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "" or "b" => 1L,
            "k" or "kb" or "kib" => 1024L,
            "m" or "mb" or "mib" => 1024L * 1024,
            "g" or "gb" or "gib" => 1024L * 1024 * 1024,
            _ => 0
        };

        if (multiplier == 0)
        {
            return false;
        }

        bytes = number * multiplier;

        return true;
    }

    /// <summary>大小字面量里的数字部分。</summary>
    private static string SizeNumber(string raw)
    {
        var match = SizeLiteralPattern.Match(raw.Trim());

        return match.Success ? match.Groups["number"].Value : raw.Trim();
    }

    /// <summary>大小字面量里的单位部分。</summary>
    private static string SizeUnit(string raw)
    {
        var match = SizeLiteralPattern.Match(raw.Trim());

        return match.Success ? match.Groups["unit"].Value : string.Empty;
    }

    /// <summary>
    /// 散文里的单位按**惯例**折成多少字节。
    /// </summary>
    /// <remarks>
    /// 二进制单位（<c>KiB</c>/<c>MiB</c>/<c>GiB</c>）是 1024 进位，
    /// 十进制单位（<c>KB</c>/<c>MB</c>/<c>GB</c>）按惯例是 1000 进位 ——
    /// 「4 MB」说的是 4,000,000，不是 4,194,304。
    /// </remarks>
    private static long LabelBytes(string number, string unit)
    {
        if (!TryParseNumber(number, out var value))
        {
            return 0;
        }

        return unit.ToLowerInvariant() switch
        {
            "kib" => value * 1024L,
            "mib" => value * 1024L * 1024,
            "gib" => value * 1024L * 1024 * 1024,
            "kb" => value * 1000L,
            "mb" => value * 1000L * 1000,
            "gb" => value * 1000L * 1000 * 1000,
            _ => value
        };
    }

    /// <summary>这个单位是 1024 进位还是 1000 进位（写给失败信息看）。</summary>
    private static string LabelBase(string unit) =>
        unit.ToLowerInvariant() switch
        {
            "kib" or "mib" or "gib" => "1024",
            "kb" or "mb" or "gb" => "1000",
            _ => "1"
        };

    /// <summary>解析带千分位的数字（<c>30,000,000</c>）。</summary>
    private static bool TryParseNumber(string raw, out long value) =>
        long.TryParse(
            raw.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
}
