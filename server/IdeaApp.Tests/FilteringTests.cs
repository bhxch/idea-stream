using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdeaApp.Domain;

namespace IdeaApp.Tests;

/// <summary>
/// 列表过滤与标签字典的测试。
///
/// 前四条是 <see cref="IdeaFilter"/> 的纯函数单测（不碰数据库），
/// 后几条走 HTTP，验证查询参数、排序、标签字典与补全。
/// 每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </summary>
public sealed class FilteringTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Matches_or_mode_returns_ideas_sharing_any_tag()
    {
        var idea = MakeIdea(tags: new[] { "a", "b" });

        // 选中 {b,c}，命中 {a,b}：有交集就算命中。
        Assert.True(IdeaFilter.Matches(idea, MakeQuery(tags: new[] { "b", "c" }, mode: TagMatchMode.Or)));

        // 完全没交集就不命中。
        Assert.False(IdeaFilter.Matches(idea, MakeQuery(tags: new[] { "c", "d" }, mode: TagMatchMode.Or)));

        // 标签比较大小写不敏感（字典本身是 UNIQUE COLLATE NOCASE）。
        Assert.True(IdeaFilter.Matches(idea, MakeQuery(tags: new[] { "B" }, mode: TagMatchMode.Or)));
    }

    [Fact]
    public void Matches_and_mode_requires_all_selected_tags()
    {
        var idea = MakeIdea(tags: new[] { "a", "b" });

        // 选中 {a,b,c}，想法只有 {a,b}：缺一个就不算命中。
        Assert.False(IdeaFilter.Matches(idea, MakeQuery(tags: new[] { "a", "b", "c" }, mode: TagMatchMode.And)));

        // 选中 {a,b}：想法的标签集合是选中集合的子集 → 命中。
        Assert.True(IdeaFilter.Matches(idea, MakeQuery(tags: new[] { "a", "b" }, mode: TagMatchMode.And)));
    }

    [Fact]
    public void Matches_status_open_excludes_done()
    {
        var done = MakeIdea(tags: Array.Empty<string>(), done: true);

        Assert.False(IdeaFilter.Matches(done, MakeQuery(status: IdeaStatusFilter.Open)));
        Assert.True(IdeaFilter.Matches(done, MakeQuery(status: IdeaStatusFilter.Done)));
        Assert.True(IdeaFilter.Matches(done, MakeQuery(status: IdeaStatusFilter.All)));
    }

    [Fact]
    public void Matches_project_filters_by_project_id()
    {
        var idea = MakeIdea(projectId: "project-a", tags: Array.Empty<string>());

        Assert.True(IdeaFilter.Matches(idea, MakeQuery(projectId: "project-a")));
        Assert.False(IdeaFilter.Matches(idea, MakeQuery(projectId: "project-b")));

        // 没给项目 = 不限项目。
        Assert.True(IdeaFilter.Matches(idea, MakeQuery(projectId: null)));
    }

    [Fact]
    public async Task Get_ideas_returns_bare_array_sorted_by_updated_at_asc()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");
        var third = await CreateIdeaAsync(client, "第三条");

        var response = await client.GetAsync("/api/ideas");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // 裸数组，不是包了一层的对象。
        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        // 排序恒为 updated_at ASC, id ASC：最老的在顶。
        var ids = root.EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();

        Assert.Equal(new[] { first, second, third }, ids);
    }

    [Fact]
    public async Task Get_ideas_tag_mode_query_param_is_honoured()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var onlyA = await CreateIdeaAsync(client, "只有 a", tags: new[] { "a" });
        var both = await CreateIdeaAsync(client, "a 和 b", tags: new[] { "a", "b" });
        var onlyB = await CreateIdeaAsync(client, "只有 b", tags: new[] { "b" });

        // AND：必须同时命中 a 和 b。
        var and = await ReadIdsAsync(await client.GetAsync("/api/ideas?tags=a,b&tagMode=and"));
        Assert.Equal(new[] { both }, and);

        // OR：命中任意一个即可。
        var or = await ReadIdsAsync(await client.GetAsync("/api/ideas?tags=a,b&tagMode=or"));
        Assert.Equal(new[] { onlyA, both, onlyB }, or);
    }

    [Fact]
    public async Task Get_tags_returns_dictionary_with_counts()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateIdeaAsync(client, "带两个标签", tags: new[] { "工作", "紧急" });
        await CreateIdeaAsync(client, "也是工作", tags: new[] { "工作" });

        var counts = await ReadTagCountsAsync(await client.GetAsync("/api/tags"));

        Assert.Equal(2, counts["工作"]);
        Assert.Equal(1, counts["紧急"]);
    }

    [Fact]
    public async Task Get_tags_suggest_prefers_current_project()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectX = await CreateProjectAsync(client, "项目 X");
        var projectY = await CreateProjectAsync(client, "项目 Y");

        await CreateIdeaAsync(client, "X 的想法", projectId: projectX, tags: new[] { "alpha" });
        await CreateIdeaAsync(client, "Y 的想法", projectId: projectY, tags: new[] { "alpine" });

        var names = await ReadStringsAsync(
            await client.GetAsync($"/api/tags/suggest?projectId={projectX}&prefix=al"));

        // 当前项目用过的标签排在前面，全局其它标签接在后面。
        Assert.Equal(new[] { "alpha", "alpine" }, names);
    }

    /// <summary>
    /// 标签在想法被软删除后仍留在字典里（spec §10.3），但计数只算未删的想法：
    /// 筛选栏里的数字得和点进去看到的条数对得上。
    /// </summary>
    [Fact]
    public async Task Get_tags_keeps_soft_deleted_ideas_tags_with_zero_count()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "带标签", tags: new[] { "工作" });

        var deleted = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var counts = await ReadTagCountsAsync(await client.GetAsync("/api/tags"));

        Assert.Equal(0, counts["工作"]);
    }

    /// <summary>
    /// 项目过滤走的是 SQL（<c>AND project_id = $projectId</c>），
    /// 这条钉住端到端的过滤结果。
    /// </summary>
    [Fact]
    public async Task Get_ideas_filters_by_project_id()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectX = await CreateProjectAsync(client, "项目 X");
        var projectY = await CreateProjectAsync(client, "项目 Y");

        var inX = await CreateIdeaAsync(client, "X 的想法", projectId: projectX);
        await CreateIdeaAsync(client, "Y 的想法", projectId: projectY);

        var ids = await ReadIdsAsync(await client.GetAsync($"/api/ideas?projectId={projectX}"));
        Assert.Equal(new[] { inX }, ids);
    }

    /// <summary>状态过滤走的是 SQL（<c>done = 0 / done = 1</c>），这条钉住端到端的过滤结果。</summary>
    [Fact]
    public async Task Get_ideas_filters_by_status()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var open = await CreateIdeaAsync(client, "还没做完");
        var done = await CreateIdeaAsync(client, "做完了");

        var completed = await client.PostAsJsonAsync($"/api/ideas/{done}/complete", new { done = true });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var openIds = await ReadIdsAsync(await client.GetAsync("/api/ideas?status=open"));
        Assert.Equal(new[] { open }, openIds);

        var doneIds = await ReadIdsAsync(await client.GetAsync("/api/ideas?status=done"));
        Assert.Equal(new[] { done }, doneIds);
    }

    /// <summary>
    /// 过滤参数给了不认识的值必须 400：默默当成「不过滤」的话，
    /// 用户以为筛了却拿到全量结果，比报错更难查。
    /// </summary>
    [Fact]
    public async Task Get_ideas_with_invalid_filter_values_returns_400()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var badStatus = await client.GetAsync("/api/ideas?status=archived");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal("invalid_status",
            (await ReadProblemAsync(badStatus)).GetProperty("code").GetString());

        var badMode = await client.GetAsync("/api/ideas?tags=a&tagMode=xor");
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
        Assert.Equal("invalid_tag_mode",
            (await ReadProblemAsync(badMode)).GetProperty("code").GetString());
    }

    /// <summary>
    /// 换标签也是一次写操作，必须刷新 updated_at、把条目挪到列表最下面。
    /// 这条守着 <c>IdeaRepository.ReplaceTags</c> 里刷新 updated_at 的那行。
    /// </summary>
    [Fact]
    public async Task SetTags_moves_idea_to_end_of_list()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.PutAsJsonAsync($"/api/ideas/{first}/tags", new { tags = new[] { "新标签" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas"));
        Assert.Equal(new[] { second, first }, ids);
    }

    /// <summary>
    /// 命中数超过单次返回上限时返回 400（不静默截断），错误码是 too_many_ideas。
    /// </summary>
    [Fact]
    public async Task Get_ideas_over_limit_returns_400_code_too_many_ideas()
    {
        using var factory = new TestAppFactory(maxIdeasPerResponse: 2);
        using var client = factory.CreateClient();

        await CreateIdeaAsync(client, "第一条");
        await CreateIdeaAsync(client, "第二条");
        await CreateIdeaAsync(client, "第三条");

        var response = await client.GetAsync("/api/ideas");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("too_many_ideas", problem.GetProperty("code").GetString());

        // detail 说明命中了多少条、让人知道要收窄条件。
        var detail = problem.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.Contains("3", detail);
    }

    private static Idea MakeIdea(string projectId = "inbox", string[]? tags = null, bool done = false) =>
        new(Id: "0198f0a1b2c3d4e5f60718293a4b5c6d",
            ProjectId: projectId,
            Title: null,
            Content: "内容",
            Tags: tags ?? Array.Empty<string>(),
            Done: done,
            CreatedAt: Timestamp,
            UpdatedAt: Timestamp,
            DeletedAt: null);

    private static IdeaQuery MakeQuery(
        string? projectId = null,
        IdeaStatusFilter status = IdeaStatusFilter.All,
        string[]? tags = null,
        TagMatchMode mode = TagMatchMode.Or) =>
        new(projectId, status, tags ?? Array.Empty<string>(), mode);

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(
        HttpClient client, string content, string? projectId = null, string[]? tags = null)
    {
        var response = await client.PostAsJsonAsync("/api/ideas", new { content, projectId, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>建一个项目并返回它的 id。</summary>
    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>读想法列表里的 id（顺序即返回顺序）。</summary>
    private static async Task<string[]> ReadIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();
    }

    /// <summary>读标签字典：标签名 → 使用计数。</summary>
    private static async Task<Dictionary<string, int>> ReadTagCountsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            counts[element.GetProperty("name").GetString()!] = element.GetProperty("count").GetInt32();
        }

        return counts;
    }

    /// <summary>读字符串数组（补全结果）。</summary>
    private static async Task<string[]> ReadStringsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    /// <summary>读 ProblemDetails 响应体。</summary>
    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // JsonElement 在文档释放后失效，复制一份出去。
        return document.RootElement.Clone();
    }
}
