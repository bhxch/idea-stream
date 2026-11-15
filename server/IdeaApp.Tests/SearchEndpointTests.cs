using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IdeaApp.Tests;

/// <summary>
/// <c>GET /api/ideas</c> 的关键词搜索（<c>q</c>）与往回翻历史的分页
/// （<c>limit</c> / <c>offset</c>）的 HTTP 层测试。
///
/// <para>
/// 语义（规格 §7.1、§11.1）：
/// <list type="bullet">
/// <item>关键词走全文索引，语义是「词元相邻」：中文等价于子串
/// （标点与空白不挡路），英文是词级（搜 <c>hel</c> 搜不到 <c>hello</c>）。</item>
/// <item>多段之间是 AND：<c>咖啡 水温</c> 要求两个都出现，不要求相邻。</item>
/// <item>分页是往回翻历史：<c>offset</c> 从最新一端数，
/// 返回的数组仍是 <c>updated_at</c> 升序。</item>
/// </list>
/// </para>
///
/// <para>
/// 每个测试用 <see cref="TestAppFactory"/> 独占一个临时 SQLite 库。
/// </para>
/// </summary>
public sealed class SearchEndpointTests
{
    /// <summary>中文等价于子串：正文里的「咖啡」搜得到。</summary>
    [Fact]
    public async Task Get_ideas_matches_chinese_substring()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡机滤芯");

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas?q=咖啡"));

        Assert.Equal(new[] { id }, ids);
    }

    /// <summary>
    /// 词元相邻才算命中：「咖啡的水温」搜「咖啡水温」**搜不到** ——
    /// 中间隔着一个「的」，那不是「咖啡水温」这个词组。
    /// </summary>
    [Fact]
    public async Task Get_ideas_does_not_match_across_a_chinese_character()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡的水温");

        // 想法确实在库里（不然下面的「搜不到」说明不了任何问题）。
        Assert.Equal(new[] { id }, await ReadIdsAsync(await client.GetAsync("/api/ideas")));

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas?q=咖啡水温"));

        Assert.Empty(ids);
    }

    /// <summary>英文大小写不敏感：正文里的「Weather」搜得到「weather」。</summary>
    [Fact]
    public async Task Get_ideas_ignores_case_for_english()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "Weather today");

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas?q=weather"));

        Assert.Equal(new[] { id }, ids);
    }

    /// <summary>英文是词级：搜「hel」搜不到「hello」。</summary>
    [Fact]
    public async Task Get_ideas_does_not_match_english_prefix()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "hello");

        Assert.Equal(new[] { id }, await ReadIdsAsync(await client.GetAsync("/api/ideas?q=hello")));
        Assert.Empty(await ReadIdsAsync(await client.GetAsync("/api/ideas?q=hel")));
    }

    /// <summary>标题也在索引里：正文没有那个词，标题有，照样搜得到。</summary>
    [Fact]
    public async Task Get_ideas_matches_title()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "完全无关的正文", title: "咖啡清单");

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas?q=咖啡"));

        Assert.Equal(new[] { id }, ids);
    }

    /// <summary>标签名也在索引里：标签叫「咖啡」的想法搜得到。</summary>
    [Fact]
    public async Task Get_ideas_matches_tag_name()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "完全无关的正文", tags: new[] { "咖啡" });

        var response = await client.GetAsync("/api/ideas?q=咖啡");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var idea = document.RootElement[0];
        Assert.Equal(id, idea.GetProperty("id").GetString());

        // 命中落在标签名上：字段是 tags，偏移相对「用空格连起来的标签名」。
        var match = idea.GetProperty("matches")[0];
        Assert.Equal("tags", match.GetProperty("field").GetString());
        Assert.Equal(0, match.GetProperty("start").GetInt32());
        Assert.Equal(2, match.GetProperty("length").GetInt32());
    }

    /// <summary>项目名也在索引里：搜项目名能搜到那个项目下的想法。</summary>
    [Fact]
    public async Task Get_ideas_matches_project_name()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "工作复盘");
        var id = await CreateIdeaAsync(client, "完全无关的正文", projectId: projectId);

        var response = await client.GetAsync("/api/ideas?q=工作复盘");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var idea = document.RootElement[0];
        Assert.Equal(id, idea.GetProperty("id").GetString());

        // 命中落在项目名上：字段是 project，偏移相对项目名本身。
        var match = idea.GetProperty("matches")[0];
        Assert.Equal("project", match.GetProperty("field").GetString());
        Assert.Equal(0, match.GetProperty("start").GetInt32());
        Assert.Equal(4, match.GetProperty("length").GetInt32());
    }

    /// <summary>多段之间是 AND：两个词都出现就命中，不要求相邻。</summary>
    [Fact]
    public async Task Get_ideas_with_multiple_terms_matches_both()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡中间隔着一段与查询完全无关的文字水温");

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas?q=咖啡 水温"));

        Assert.Equal(new[] { id }, ids);
    }

    /// <summary>
    /// 查询里全是标点 = 没有关键词 = 不过滤：返回全部，不报错。
    /// </summary>
    [Fact]
    public async Task Get_ideas_with_punctuation_only_query_returns_all()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var response = await client.GetAsync("/api/ideas?q=...");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var ids = document.RootElement.EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();

        Assert.Equal(new[] { first, second }, ids);

        // 纯标点 = 没有关键词：响应形状与**不带 q** 时一样（没有 matches 键）。
        // 服务层与仓储层各判一次的话，这里会多出一个空的 "matches": [] ——
        // 同一语义两种响应形状。
        Assert.False(document.RootElement[0].TryGetProperty("matches", out _));
    }

    /// <summary>查询只有空白 = 没有关键词 = 不过滤：返回全部。</summary>
    [Fact]
    public async Task Get_ideas_with_blank_query_returns_all()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var ids = await ReadIdsAsync(await client.GetAsync("/api/ideas?q=%20%20"));

        Assert.Equal(new[] { first, second }, ids);
    }

    /// <summary>给了 limit 就返回**最新**的那一页（数组仍是 updated_at 升序）。</summary>
    [Fact]
    public async Task Get_ideas_limit_returns_the_newest_page()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await CreateIdeaAsync(client, $"第{i + 1}条"));
        }

        var page = await ReadIdsAsync(await client.GetAsync("/api/ideas?limit=2"));

        // 最新两条：第 4、5 条（id 单调递增，创建顺序即列表顺序）。
        Assert.Equal(new[] { ids[3], ids[4] }, page);
    }

    /// <summary>offset 从最新一端数：offset=2 是「跳过最新的两条」。</summary>
    [Fact]
    public async Task Get_ideas_offset_walks_backwards()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await CreateIdeaAsync(client, $"第{i + 1}条"));
        }

        var newest = await ReadIdsAsync(await client.GetAsync("/api/ideas?limit=2"));
        var older = await ReadIdsAsync(await client.GetAsync("/api/ideas?limit=2&offset=2"));

        // 第 3、4 新（第 3 新 = ids[2]，第 4 新 = ids[1]），仍是升序。
        Assert.Equal(new[] { ids[1], ids[2] }, older);

        // 两页不重叠。
        Assert.Empty(newest.Intersect(older));
    }

    /// <summary>
    /// 给了 limit 就不再报 too_many_ideas：明确要了一页，不该被顶回来。
    /// </summary>
    [Fact]
    public async Task Get_ideas_limit_bypasses_too_many_ideas()
    {
        using var factory = new TestAppFactory(maxIdeasPerResponse: 2);
        using var client = factory.CreateClient();

        await CreateIdeaAsync(client, "第一条");
        await CreateIdeaAsync(client, "第二条");
        await CreateIdeaAsync(client, "第三条");

        // 不给 limit：命中数超过单次返回上限 → 400。
        var tooMany = await client.GetAsync("/api/ideas");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("too_many_ideas",
            (await ReadProblemAsync(tooMany)).GetProperty("code").GetString());

        // 给了 limit：正常返回那一页。
        var page = await ReadIdsAsync(await client.GetAsync("/api/ideas?limit=2"));
        Assert.Equal(2, page.Length);
    }

    /// <summary>
    /// 切片在**内存标签过滤之后**：一页要拿满 limit 条。
    /// 先切页再过滤的话，这一页会被滤掉一部分，
    /// 前端按「返回条数 == limit+1」判定没更多，后面的命中就永远看不到了。
    /// </summary>
    [Fact]
    public async Task Get_ideas_with_tags_and_limit_returns_a_full_page()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 三条带标签的先建。
        var tagged = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            tagged.Add(await CreateIdeaAsync(client, $"带标签的第{i + 1}条", tags: new[] { "咖啡" }));
        }

        // **没有标签的那条最后建**（= 最新的一条）。
        // 先切页再过滤的话，「最新 2 条」是 [t3, 无标签]，内存过滤后只剩 1 条 ——
        // 下面的断言会红。
        // 无标签那条若建在最老的一端，它天然落在「最新 2 条」之外，
        // 这条测试对它想守的顺序就不敏感了。
        await CreateIdeaAsync(client, "没有标签的");

        var page = await ReadIdsAsync(await client.GetAsync("/api/ideas?tags=咖啡&limit=2"));

        // 过滤后剩 [t1, t2, t3]，取最新 2 条 → [t2, t3]（页内仍是升序）。
        Assert.Equal(new[] { tagged[1], tagged[2] }, page);
    }

    /// <summary>命中位置要给得出：偏移切出来的正好是命中词。</summary>
    [Fact]
    public async Task Get_ideas_returns_matches_offsets()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateIdeaAsync(client, "咖啡机滤芯");

        var response = await client.GetAsync("/api/ideas?q=咖啡");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var idea = document.RootElement[0];
        var match = idea.GetProperty("matches")[0];

        Assert.Equal("content", match.GetProperty("field").GetString());

        var start = match.GetProperty("start").GetInt32();
        var length = match.GetProperty("length").GetInt32();

        var content = idea.GetProperty("content").GetString()!;
        Assert.Equal("咖啡", content.Substring(start, length));
    }

    /// <summary>不带 q 时响应里**没有** matches 键（属性级 JsonIgnore 生效）。</summary>
    [Fact]
    public async Task Get_ideas_without_query_omits_matches()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        await CreateIdeaAsync(client, "咖啡机滤芯");

        var response = await client.GetAsync("/api/ideas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // matches 与 trashed 都得省掉：
        // 少了任何一个的属性级 JsonIgnore，响应就会多出那个键 ——
        // 「不带关键词的响应与今天逐字节一样」这条约束就破了。
        Assert.False(document.RootElement[0].TryGetProperty("matches", out _));
        Assert.False(document.RootElement[0].TryGetProperty("trashed", out _));
    }

    /// <summary>分页参数越界一律 400（invalid_pagination）。</summary>
    [Fact]
    public async Task Get_ideas_with_invalid_limit_returns_400()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        foreach (var query in new[] { "limit=0", "limit=100000", "offset=-1" })
        {
            var response = await client.GetAsync($"/api/ideas?{query}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_pagination",
                (await ReadProblemAsync(response)).GetProperty("code").GetString());
        }
    }

    /// <summary>offset 单独给没有意义（翻历史必须知道翻多宽）：400。</summary>
    [Fact]
    public async Task Get_ideas_with_offset_but_no_limit_returns_400()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ideas?offset=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_pagination",
            (await ReadProblemAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// 非数字的 limit → 400 <c>invalid_pagination</c>，而且**带 code**。
    /// </summary>
    /// <remarks>
    /// 实测（跑真实应用）：这两个参数绑成 <c>int?</c> 时，<c>?limit=abc</c>
    /// 在参数绑定阶段失败后，框架**只设一个 400 状态码就返回** ——
    /// 空 body、没有 code，进不了 <c>ApiExceptionHandler</c>。
    /// agent 拿到的就是一个空响应，连「是哪儿错了」都读不出来。
    /// <para>
    /// 现在绑的是 <c>string?</c>，由服务层自己解析、自己判范围 ——
    /// 非法一律 <c>invalid_pagination</c> 并带说得清楚的 detail。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Get_ideas_with_non_numeric_limit_returns_400_code_invalid_pagination()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ideas?limit=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_pagination", problem.GetProperty("code").GetString());

        // detail 说清楚收到的是什么。
        Assert.Contains("limit", problem.GetProperty("detail").GetString()!);
    }

    /// <summary>offset 同理：不是数字也是 <c>invalid_pagination</c>。</summary>
    [Fact]
    public async Task Get_ideas_with_non_numeric_offset_returns_400_code_invalid_pagination()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ideas?offset=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("invalid_pagination", problem.GetProperty("code").GetString());

        Assert.Contains("offset", problem.GetProperty("detail").GetString()!);
    }

    /// <summary>建一条想法并返回它的 id。</summary>
    private static async Task<string> CreateIdeaAsync(
        HttpClient client,
        string content,
        string? title = null,
        string? projectId = null,
        string[]? tags = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/ideas",
            new { content, title, projectId, tags });

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

    /// <summary>读 ProblemDetails 响应体。</summary>
    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // body 不能是空的：框架对「查询串绑定失败」只设一个 400 状态码就返回，
        // 响应体是空的、没有 code —— agent 连「是哪儿错了」都读不出来。
        // 这条断言给那个坑守着。
        Assert.NotEmpty(body);

        using var document = JsonDocument.Parse(body);

        // JsonElement 在文档释放后失效，复制一份出去。
        return document.RootElement.Clone();
    }
}
