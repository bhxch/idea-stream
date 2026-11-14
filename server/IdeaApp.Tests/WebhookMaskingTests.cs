using IdeaApp.Contracts;
using IdeaApp.Infrastructure;

namespace IdeaApp.Tests;

/// <summary>
/// webhook 凭据遮蔽的单元测试。
///
/// <para>
/// 遮蔽函数是**安全边界**：算错了就是把凭据放行 —— Telegram 的 bot token 就在
/// URL 的路径段里、凭据就在请求头的值里，而遮蔽后的响应会原样进 agent 的上下文与日志。
/// </para>
///
/// <para>
/// 这些用例直接测函数本身（不起 HTTP 服务）：集成测试只走了「正常路径」，
/// 解析失败、非默认端口、IPv6、连续斜杠这些分支在那里一个都到不了。
/// </para>
/// </summary>
public sealed class WebhookMaskingTests
{
    /// <summary>
    /// 正常地址：保留 scheme 与 host，路径的每一段换成 <c>***</c>。
    /// </summary>
    /// <remarks>
    /// host 可读是刻意的：调用方要知道「打的是哪个服务」，
    /// 而路径段（token 就在那儿）一律换成 ***。
    /// </remarks>
    [Fact]
    public void MaskUrl_keeps_scheme_and_host_and_masks_every_path_segment()
    {
        var masked = WebhookMasking.MaskUrl("https://api.telegram.org/bot123:ABC/sendMessage");

        Assert.Equal("https://api.telegram.org/***/***", masked);
    }

    /// <summary>
    /// 段数保留：路径有几段，遮蔽后就是几段 ***。
    /// </summary>
    /// <remarks>
    /// 段数是给调用方看形状用的（两段还是三段能看出来），
    /// 但段里的内容一个字都不能剩。
    /// </remarks>
    [Theory]
    [InlineData("https://example.com/a", "https://example.com/***")]
    [InlineData("https://example.com/a/b", "https://example.com/***/***")]
    [InlineData("https://example.com/a/b/c/d", "https://example.com/***/***/***/***")]
    public void MaskUrl_keeps_the_number_of_path_segments(string url, string expected)
    {
        Assert.Equal(expected, WebhookMasking.MaskUrl(url));
    }

    /// <summary>query 与 fragment 一律丢弃（里面可能带 token）。</summary>
    [Fact]
    public void MaskUrl_drops_the_query_and_the_fragment()
    {
        var masked = WebhookMasking.MaskUrl("https://api.telegram.org/bot123:ABC/sendMessage?token=SECRET#SECRET2");

        Assert.Equal("https://api.telegram.org/***/***", masked);
        Assert.DoesNotContain("SECRET", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// userinfo（<c>user:password@</c>）一律丢弃。
    /// </summary>
    /// <remarks>
    /// 密码在 URL 的 userinfo 里是常见写法（<c>https://user:pass@host/…</c>），
    /// 遮蔽版里不能有任何痕迹。
    /// </remarks>
    [Fact]
    public void MaskUrl_drops_the_userinfo()
    {
        var masked = WebhookMasking.MaskUrl("https://user:pass@example.com/hook");

        Assert.Equal("https://example.com/***", masked);
        Assert.DoesNotContain("pass", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("user", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// 不是合法绝对地址时，整个 URL 换成 <c>***</c>，原文一个字都不剩。
    /// </summary>
    /// <remarks>
    /// 宁可什么都不给，也不给半截原文：解析不了就说明这个字符串的形态未知，
    /// 没法保证「哪些部分是凭据」。
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("example.com/hook")]
    public void MaskUrl_returns_stars_when_the_url_cannot_be_parsed(string url)
    {
        Assert.Equal("***", WebhookMasking.MaskUrl(url));
    }

    /// <summary>
    /// 以斜杠开头的路径在 Unix 上**是**能解析的（<see cref="Uri"/> 把它当成
    /// <c>file://</c> 地址），所以它不在「解析失败」那一类里。
    /// </summary>
    /// <remarks>
    /// 这条钉的是「解析得了但形态古怪」的情况：路径照样按段遮蔽，原文不漏。
    /// </remarks>
    [Fact]
    public void MaskUrl_masks_the_path_even_when_it_parses_as_a_file_uri()
    {
        var masked = WebhookMasking.MaskUrl("/relative/path");

        Assert.Equal("file:///***/***", masked);
        Assert.DoesNotContain("relative", masked, StringComparison.Ordinal);
    }

    /// <summary>解析失败时不能把原文带出去（原文里可能有 token）。</summary>
    [Fact]
    public void MaskUrl_does_not_echo_the_original_when_parsing_fails()
    {
        const string broken = "not a url with SECRET";

        var masked = WebhookMasking.MaskUrl(broken);

        Assert.Equal("***", masked);
        Assert.DoesNotContain("SECRET", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// 端口：非默认端口保留，默认端口不保留。
    /// </summary>
    /// <remarks>
    /// 非默认端口是信息（同一个 host 上的另一个服务），默认端口（https 的 443、
    /// http 的 80）对调用方没有信息量。
    /// </remarks>
    [Fact]
    public void MaskUrl_keeps_a_non_default_port_and_drops_the_default_one()
    {
        Assert.Equal("https://example.com:8443/***/***",
            WebhookMasking.MaskUrl("https://example.com:8443/a/b"));

        Assert.Equal("https://example.com/***/***",
            WebhookMasking.MaskUrl("https://example.com:443/a/b"));

        Assert.Equal("http://example.com/***/***",
            WebhookMasking.MaskUrl("http://example.com:80/a/b"));
    }

    /// <summary>
    /// IPv6 的 host 保留**方括号**。
    /// </summary>
    /// <remarks>
    /// 依据是 <see cref="Uri.Host"/> 的实际行为：对 IPv6 字面量它返回的是
    /// <c>[::1]</c>（带方括号），不是 <c>::1</c>；而 <see cref="Uri.IdnHost"/>
    /// 才是去掉方括号的那个。所以拼 authority 时用 Host 不会拼出
    /// <c>http://::1:8080</c> 这种坏地址。
    /// </remarks>
    [Fact]
    public void MaskUrl_keeps_the_brackets_of_an_ipv6_host()
    {
        var masked = WebhookMasking.MaskUrl("http://[::1]:8080/x");

        Assert.Equal("http://[::1]:8080/***", masked);
    }

    /// <summary>
    /// 结尾的斜杠与连续的空段原样保留：斜杠的个数不变，段还是段。
    /// </summary>
    /// <remarks>
    /// 遮蔽按「段」做，不按字符做：空段（结尾的斜杠、双斜杠中间的空段）不是段，
    /// 原样留着，路径的形状因此不变。
    /// </remarks>
    [Fact]
    public void MaskUrl_keeps_the_trailing_slash_and_the_empty_segments()
    {
        // 结尾一个斜杠：多出一个空段。
        Assert.Equal("https://example.com/***/***/",
            WebhookMasking.MaskUrl("https://example.com/a/b/"));

        // 中间两个斜杠：中间多出一个空段。
        Assert.Equal("https://example.com/***//***",
            WebhookMasking.MaskUrl("https://example.com/a//b"));

        // 只有根路径：一个空段。
        Assert.Equal("https://example.com/",
            WebhookMasking.MaskUrl("https://example.com"));

        Assert.Equal("https://example.com/",
            WebhookMasking.MaskUrl("https://example.com/"));
    }

    /// <summary>请求头的值全换成 <c>***</c>，键名留着。</summary>
    [Fact]
    public void Mask_masks_the_header_values_and_keeps_their_keys()
    {
        var webhook = SampleWebhook(headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer SECRET",
            ["X-Token"] = "value"
        });

        var masked = WebhookMasking.Mask(webhook);

        Assert.Equal(2, masked.Headers.Count);
        Assert.Equal("***", masked.Headers["Authorization"]);
        Assert.Equal("***", masked.Headers["X-Token"]);

        // 键名留着：调用方要靠它认出「这是哪个请求头」，键名本身不是凭据。
        Assert.True(masked.Headers.ContainsKey("Authorization"));
        Assert.True(masked.Headers.ContainsKey("X-Token"));
    }

    /// <summary>空的请求头字典遮蔽后还是空字典，不是 null。</summary>
    [Fact]
    public void Mask_keeps_an_empty_header_dictionary_empty()
    {
        var webhook = SampleWebhook(headers: new Dictionary<string, string>());

        var masked = WebhookMasking.Mask(webhook);

        Assert.NotNull(masked.Headers);
        Assert.Empty(masked.Headers);
    }

    /// <summary>
    /// 除了 url 与请求头的值，其余字段原样不动。
    /// </summary>
    /// <remarks>
    /// 遮蔽只动凭据：名称、订阅的事件、作用域这些是配置面，
    /// 调用方要靠它们判断「这是哪个 webhook」，动不得。
    /// </remarks>
    [Fact]
    public void Mask_leaves_the_other_fields_untouched()
    {
        var webhook = SampleWebhook(
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET" });

        var masked = WebhookMasking.Mask(webhook);

        Assert.Equal(webhook.Id, masked.Id);
        Assert.Equal(webhook.Name, masked.Name);
        Assert.Equal(webhook.Method, masked.Method);
        Assert.Equal(webhook.BodyTemplate, masked.BodyTemplate);
        Assert.Equal(webhook.Events, masked.Events);
        Assert.Equal(webhook.Scope, masked.Scope);
        Assert.Equal(webhook.ProjectId, masked.ProjectId);
        Assert.Equal(webhook.Enabled, masked.Enabled);
        Assert.Equal(webhook.HasSecret, masked.HasSecret);
        Assert.Equal(webhook.CreatedAt, masked.CreatedAt);
        Assert.Equal(webhook.UpdatedAt, masked.UpdatedAt);
    }

    /// <summary>
    /// 遮蔽不改原记录：传进来的 webhook 原样还在。
    /// </summary>
    /// <remarks>
    /// 遮蔽返回的是**新**记录（<c>with</c> 表达式），原记录里的凭据还在 ——
    /// 这正是「库里存原值、出参给遮蔽版」要成立的前提：
    /// 调用方拿着遮蔽版去改别的东西时，不会把遮蔽值写回库里。
    /// </remarks>
    [Fact]
    public void Mask_does_not_touch_the_input_webhook()
    {
        var webhook = SampleWebhook(headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer SECRET"
        });

        var masked = WebhookMasking.Mask(webhook);

        Assert.NotSame(webhook, masked);
        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", webhook.Url);
        Assert.Equal("Bearer SECRET", webhook.Headers["Authorization"]);

        // 遮蔽版与原版是两份独立的数据：改遮蔽版的字典不会影响原版。
        masked.Headers["Authorization"] = "改过了";
        Assert.Equal("Bearer SECRET", webhook.Headers["Authorization"]);
    }

    /// <summary>
    /// 遮蔽版的 url 走的是同一份规则：请求头换掉了，url 也换掉了。
    /// </summary>
    [Fact]
    public void Mask_masks_the_url_as_well()
    {
        var webhook = SampleWebhook(headers: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer SECRET"
        });

        var masked = WebhookMasking.Mask(webhook);

        Assert.Equal("https://api.telegram.org/***/***", masked.Url);
        Assert.DoesNotContain("SECRET", masked.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一个 webhook 记录（除 url 与请求头外，其余字段都填上，
    /// 好让「其余字段原样不动」这条能真的被验到）。
    /// </summary>
    private static WebhookDto SampleWebhook(Dictionary<string, string> headers) => new(
        Id: "0198f0a1b2c3d4e5f60718293a4b5c6d",
        Name: "Telegram 通知",
        Url: "https://api.telegram.org/bot123:ABC/sendMessage",
        Method: "POST",
        Headers: headers,
        BodyTemplate: """{"text":"{{idea.content}}"}""",
        Events: ["idea.created", "idea.updated"],
        Scope: "global",
        ProjectId: null,
        Enabled: true,
        HasSecret: true,
        CreatedAt: new DateTimeOffset(2026, 11, 14, 1, 2, 3, TimeSpan.Zero),
        UpdatedAt: new DateTimeOffset(2026, 11, 14, 1, 2, 3, TimeSpan.Zero));
}
