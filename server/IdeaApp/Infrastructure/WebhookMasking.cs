using IdeaApp.Contracts;

namespace IdeaApp.Infrastructure;

/// <summary>
/// webhook 凭据的遮蔽。
///
/// <para>
/// 命令层删掉之后，脱敏的归处就是服务端：裸 curl 拿到的响应会原样进 agent 的
/// 上下文与日志，而 Telegram 的 bot token 就在 URL 的路径段里、凭据就在请求头的值里。
/// </para>
///
/// <para>
/// 规则是刻意简单的：**host 可读、凭据不可读**。
/// URL 只留 <c>scheme://host[:port]</c>，路径的每一段换成 <c>***</c>，
/// query、fragment、userinfo 一律丢弃；请求头只留键名，值一律换成 <c>***</c>。
/// </para>
/// </summary>
public static class WebhookMasking
{
    /// <summary>遮蔽值：固定三个星号。</summary>
    private const string Masked = "***";

    /// <summary>
    /// 遮蔽一个 webhook：URL 走 <see cref="MaskUrl"/>，请求头的值一律换成 <c>***</c>。
    /// </summary>
    /// <remarks>
    /// 键名留着：调用方要靠它认出「这是哪个请求头」（比如 <c>Authorization</c>），
    /// 而键名本身不是凭据。请求头为空字典时返回的还是空字典，不是 null。
    /// </remarks>
    public static WebhookDto Mask(WebhookDto webhook) => webhook with
    {
        Url = MaskUrl(webhook.Url),
        Headers = webhook.Headers.ToDictionary(pair => pair.Key, _ => Masked)
    };

    /// <summary>
    /// 遮蔽 URL：只留 <c>scheme://host[:port]</c>，路径的每一段换成 <c>***</c>。
    /// </summary>
    /// <remarks>
    /// 路径按 <c>/</c> 切成段，非空的段换成 <c>***</c>，空段留着 ——
    /// 连续的 <c>/</c>（结尾的斜杠、双斜杠）原样保留，段数不变。
    /// <para>
    /// 解析不了就整个换成 <c>***</c>：宁可什么都不给，也不给半截原文。
    /// </para>
    /// </remarks>
    public static string MaskUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Masked;
        }

        // 端口只在非默认时带：默认端口（https 的 443）对调用方没有信息量。
        var authority = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";

        var maskedPath = string.Join('/',
            uri.AbsolutePath.Split('/').Select(segment => segment.Length == 0 ? string.Empty : Masked));

        return authority + maskedPath;
    }
}
