using System.Security.Cryptography;
using System.Text;

namespace IdeaApp.Infrastructure;

/// <summary>
/// HMAC-SHA256 签名：webhook 配了 secret 就给请求体加一个签名，
/// 接收方拿同一个 secret 重算一遍就能确认「这请求确实是我们发的、且没被改过」。
///
/// <para>
/// 签名的形态是 <c>sha256=&lt;小写 hex&gt;</c>（64 位十六进制），
/// 密钥与正文都按 UTF-8 编码。
/// </para>
/// </summary>
public static class HmacSigner
{
    /// <summary>签名头里的算法前缀。</summary>
    public const string Algorithm = "sha256";

    /// <summary>
    /// 给一段正文签名。
    /// </summary>
    /// <param name="secret">签名密钥。</param>
    /// <param name="payload">要签名的正文（请求体）。</param>
    /// <returns><c>sha256=</c> 加小写十六进制摘要。</returns>
    public static string Sign(string secret, string payload)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(payload);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return $"{Algorithm}={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
