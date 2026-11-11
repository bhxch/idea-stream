namespace IdeaApp.Application;

/// <summary>
/// 业务错误。所有 4xx 都通过它抛出，端点层统一捕获并转成
/// RFC 9457 的 ProblemDetails（扩展字段 <c>code</c>）。
/// </summary>
public sealed class ApiException(int status, string code, string detail) : Exception(detail)
{
    /// <summary>HTTP 状态码。</summary>
    public int Status { get; } = status;

    /// <summary>稳定的机器可读错误码，如 <c>project_not_found</c>。</summary>
    public string Code { get; } = code;

    /// <summary>给人看的说明。</summary>
    public string Detail { get; } = detail;
}
