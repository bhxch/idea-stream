using IdeaApp.Contracts;
using IdeaApp.Infrastructure;

namespace IdeaApp.Application;

/// <summary>
/// 标签字典服务：字典列表与输入补全。业务规则在这里，仓储只管存。
/// </summary>
public sealed class TagService(TagRepository repo)
{
    /// <summary>标签字典（含使用计数）。</summary>
    public IReadOnlyList<TagDto> List()
    {
        return repo.ListWithCounts();
    }

    /// <summary>
    /// 输入补全：先列当前项目用过的标签，再列全局其它标签。
    /// </summary>
    /// <param name="projectId">当前项目 id；空表示不区分项目。</param>
    /// <param name="prefix">前缀；空表示不过滤前缀。</param>
    /// <remarks>
    /// 前缀匹配大小写不敏感。同一个标签不会列两次
    /// （项目里的标签本来也在全局字典里）。
    /// </remarks>
    public IReadOnlyList<string> Suggest(string? projectId, string prefix)
    {
        var normalizedPrefix = prefix?.Trim() ?? string.Empty;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            AppendMatching(repo.NamesForProject(projectId.Trim()), normalizedPrefix, result, seen);
        }

        AppendMatching(repo.AllNames(), normalizedPrefix, result, seen);

        return result;
    }

    /// <summary>把匹配前缀的标签名追加到结果里，跳过已经列过的。</summary>
    private static void AppendMatching(
        IReadOnlyList<string> names, string prefix, List<string> result, HashSet<string> seen)
    {
        foreach (var name in names)
        {
            if (prefix.Length > 0
                && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }
    }
}
