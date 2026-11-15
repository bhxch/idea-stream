using IdeaApp.Contracts;
using IdeaApp.Domain;

namespace IdeaApp.Infrastructure;

/// <summary>
/// 想法命中位置的计算：标题、正文、标签名、项目名四个字段各算一遍。
///
/// <para>
/// 为什么单独一份：<c>GET /api/ideas?q=</c> 与 <c>GET /api/search</c> 都要给
/// 「命中落在哪个字段的哪一段」——<b>两处必须给出一模一样的答案</b>，
/// 否则 agent 在一个端点上看到高亮、在另一个端点上看到没有，两边对不上。
/// 各写一份的话，改了字段名或偏移口径只有一处会跟着改。
/// </para>
///
/// <para>
/// 两条路径的**唯一**差别是项目名从哪来：
/// 列表路径按项目 id 查（同一页里通常只有一两个项目，缓存住即可），
/// 搜索路径从 SQL 的 JOIN 里直接带出来。算命中这件事本身完全一样。
/// </para>
///
/// <para>
/// 命中位置**在原文上**算（见 <see cref="TextTokenizer.FindMatches"/>）：
/// FTS5 没有 <c>offsets()</c>，而 <c>highlight()</c> 输出的是切分后的词元串，
/// 跟原文对不上。
/// </para>
/// </summary>
public static class IdeaMatches
{
    /// <summary>
    /// 算一条想法在某关键词下的命中位置。
    /// </summary>
    /// <param name="idea">那条想法（标签要已从库里读回来）。</param>
    /// <param name="projectName">它所属项目的名字（项目名也在索引里）。</param>
    /// <param name="keyword">用户给的查询串。</param>
    /// <returns>命中区间列表，按「字段的先后、字段内的先后」排列。</returns>
    /// <remarks>
    /// ⚠️ <b>同一个查询可能产出重复或重叠的区间</b>
    /// （见 <see cref="TextTokenizer.FindMatches"/>）：前端渲染高亮时自己合并，
    /// 服务端原样给出 —— 合并口径放在渲染那一侧只有一份。
    /// </remarks>
    public static IReadOnlyList<SearchMatch> For(Idea idea, string projectName, string keyword)
    {
        var matches = new List<SearchMatch>();

        Add(matches, "title", idea.Title, keyword);
        Add(matches, "content", idea.Content, keyword);

        // 标签列的「完整字符串」是**用空格连起来的标签名** ——
        // 与索引里那一列的文本一样，偏移才对得上。
        Add(matches, "tags", string.Join(" ", idea.Tags), keyword);

        Add(matches, "project", projectName, keyword);

        return matches;
    }

    /// <summary>把某个字段里的命中位置收进结果里。</summary>
    /// <param name="matches">收集结果。</param>
    /// <param name="field">字段名（<c>title</c> / <c>content</c> / <c>tags</c> / <c>project</c>）。</param>
    /// <param name="text">该字段的完整文本（可为 null 或空）。</param>
    /// <param name="keyword">用户给的查询串。</param>
    private static void Add(List<SearchMatch> matches, string field, string? text, string keyword)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var match in TextTokenizer.FindMatches(text, keyword))
        {
            matches.Add(new SearchMatch(field, match.Start, match.Length));
        }
    }
}
