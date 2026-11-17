using IdeaApp.Domain;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 事件 → 占位符字典：把一个 <see cref="IdeaEvent"/> 摊平成模板能用的键值对。
///
/// <para>
/// 键就是占位符的名字（<c>event</c>、<c>idea.content</c>…），值是它们的文本形态。
/// 事件里没有的字段（没有项目、没有想法、没改过东西）对应的值是 <c>null</c> ——
/// 渲染时它渲染成空串，而不是把占位符原样留在正文里。
/// </para>
/// </summary>
public static class EventPlaceholders
{
    /// <summary>
    /// 造一个事件的占位符字典。
    /// </summary>
    /// <param name="evt">事件。</param>
    /// <param name="publicBaseUrl">
    /// 公开访问地址（前端所在处）。它为空时 <c>{{idea.url}}</c> 是空串 ——
    /// 半截链接（<c>/?idea=xxx</c>）既点不开，又比没有链接更难认。
    /// </param>
    public static IReadOnlyDictionary<string, string?> Build(IdeaEvent evt, string? publicBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(evt);

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["event"] = evt.Type,
            ["timestamp"] = evt.Timestamp.ToString("O"),
            ["project"] = evt.Project?.Name,
            ["project.id"] = evt.Project?.Id,
            ["idea.id"] = evt.Idea?.Id,
            ["idea.content"] = evt.Idea?.Content,
            ["idea.tags"] = evt.Idea is null ? null : string.Join(",", evt.Idea.Tags),
            ["idea.done"] = evt.Idea is null ? null : evt.Idea.Done ? "true" : "false",
            ["changed"] = evt.Changed is null ? null : string.Join(",", evt.Changed),
            ["idea.url"] = BuildIdeaUrl(evt.Idea?.Id, publicBaseUrl)
        };
    }

    /// <summary>想法的深链：<c>{公开地址}/?idea={想法 id}</c>。</summary>
    /// <remarks>
    /// 公开地址的**尾部斜杠要裁掉**：<c>https://x.example/</c> 直接拼上
    /// <c>/?idea=…</c> 会得到 <c>https://x.example//?idea=…</c>（双斜杠）。
    /// </remarks>
    private static string BuildIdeaUrl(string? ideaId, string? publicBaseUrl)
    {
        if (string.IsNullOrEmpty(publicBaseUrl) || string.IsNullOrEmpty(ideaId))
        {
            return string.Empty;
        }

        return $"{publicBaseUrl.TrimEnd('/')}/?idea={ideaId}";
    }
}
