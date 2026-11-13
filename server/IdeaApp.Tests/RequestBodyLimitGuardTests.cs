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
/// 归一约定：nginx 的 <c>k</c>/<c>m</c>/<c>g</c> 后缀与文档里的
/// <c>KB</c>/<c>MB</c>/<c>GB</c>/<c>KiB</c>/<c>MiB</c>/<c>GiB</c> 一律按
/// <b>1024 进位</b>算（与 nginx 的 <c>m</c> 一致），
/// 所以「4m」与「4 MiB」归一后都是 4194304 字节，可以直接比。
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

    /// <summary>文档里的散文写法：<c>4 MiB</c> / <c>4MB</c>。</summary>
    private static readonly Regex SkillProseSizePattern = new(
        @"(?<value>\d[\d,]*\s*(?:KiB|MiB|GiB|KB|MB|GB))(?![A-Za-z])",
        RegexOptions.CultureInvariant);

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
    [Fact]
    public void The_nginx_body_limit_in_the_deploy_config_matches_the_skill_doc()
    {
        var configured = ReadNginxLimit(NginxConfigPath);
        var documented = ReadSkillNginxLimits();

        Assert.True(
            documented.Count > 0,
            $"在 {SkillDocPath} 里找不到 nginx 的请求体上限 —— "
            + $"请写成 `client_max_body_size <值>` 的形式（当前 {NginxConfigPath} 里的是 {configured} 字节），"
            + "或直接写「4 MiB」这样的散文写法，否则 agent 看到的部署上限是空的。");

        foreach (var (written, source) in documented)
        {
            Assert.True(
                configured == written,
                $"SKILL.md 写的 nginx 请求体上限（{source} = {written} 字节）"
                + $"与 docs/nginx/idea.conf 的 client_max_body_size（{configured} 字节）不一致，请同步。");
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

    /// <summary>读部署配置里的 nginx 上限（字节）。</summary>
    private static long ReadNginxLimit(string path) =>
        ParseNginxLimit(StripHashComments(File.ReadAllText(path)), path);

    /// <summary>从一段 nginx 配置文本里读出 <c>client_max_body_size</c>（字节）。</summary>
    private static long ParseNginxLimit(string text, string origin)
    {
        var match = NginxLimitPattern.Match(text);

        Assert.True(
            match.Success,
            $"{origin} 里找不到 `client_max_body_size` —— "
            + "守卫对请求体上限失明了，请确认指令还在。");

        Assert.True(
            TryParseSize(match.Groups["value"].Value, out var bytes),
            $"{origin} 里的 client_max_body_size 的值认不出来：{match.Groups["value"].Value}");

        return bytes;
    }

    /// <summary>
    /// 读 SKILL.md 里 nginx 的那个上限（字节）。
    /// </summary>
    /// <remarks>
    /// 两处写法都认：nginx 自己的指令写法（<c>client_max_body_size 4m</c>）
    /// 与散文写法（<c>4 MiB</c> / <c>4MB</c>）。两处必须归一成同一个值。
    /// </remarks>
    private static List<(long Value, string Source)> ReadSkillNginxLimits()
    {
        var text = File.ReadAllText(SkillDocPath);
        var limits = new List<(long Value, string Source)>();

        foreach (Match match in SkillNginxDirectivePattern.Matches(text))
        {
            if (TryParseSize(match.Groups["value"].Value, out var bytes))
            {
                limits.Add((bytes, match.Groups["value"].Value));
            }
        }

        foreach (Match match in SkillProseSizePattern.Matches(text))
        {
            if (TryParseSize(match.Groups["value"].Value, out var bytes))
            {
                limits.Add((bytes, match.Groups["value"].Value));
            }
        }

        return limits;
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

    /// <summary>把大小字面量归一成字节（1024 进位）。</summary>
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

    /// <summary>解析带千分位的数字（<c>30,000,000</c>）。</summary>
    private static bool TryParseNumber(string raw, out long value) =>
        long.TryParse(
            raw.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
}
