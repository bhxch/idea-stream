using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IdeaApp.Tests;

/// <summary>
/// 搜索端点的 HTTP 层测试：<c>GET /api/ideas</c> 的关键词搜索（<c>q</c>）
/// 与往回翻历史的分页（<c>limit</c> / <c>offset</c>），
/// 以及 <c>GET /api/search</c> 的全库搜索（想法 / 项目 / 标签三组）。
///
/// <para>
/// 语义（规格 §7.1、§11.1、§11.3）：
/// <list type="bullet">
/// <item>关键词走全文索引，语义是「词元相邻」：中文等价于子串
/// （标点与空白不挡路），英文是词级（搜 <c>hel</c> 搜不到 <c>hello</c>）。</item>
/// <item>多段之间是 AND：<c>咖啡 水温</c> 要求两个都出现，不要求相邻。</item>
/// <item><c>/api/ideas</c> 的分页是往回翻历史：<c>offset</c> 从最新一端数，
/// 返回的数组仍是 <c>updated_at</c> 升序。</item>
/// <item><c>/api/search</c> 的分页是翻相关度页：<c>offset</c> 从**最相关**一端数，
/// 想法组按 <c>bm25()</c> 排（标题命中比正文命中更相关）。</item>
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
    /// 分页参数与过滤参数**都**非法时，报分页的那个。
    /// </summary>
    /// <remarks>
    /// 顺序是定下来的：服务层先判分页、再判状态/标签。
    /// 报哪个不能看谁先炸 —— agent 拿到的是一个**稳定**的错误码才好处理。
    /// </remarks>
    [Fact]
    public async Task Get_ideas_with_invalid_pagination_and_status_reports_invalid_pagination()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/ideas?limit=abc&status=bogus");

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

    /// <summary>
    /// 全库搜索：一次请求拿到想法、项目、标签三组命中，<c>query</c> 原样回显。
    /// </summary>
    [Fact]
    public async Task Search_returns_three_groups()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "工作");
        var ideaId = await CreateIdeaAsync(
            client, "咖啡机滤芯", projectId: projectId, tags: new[] { "烘焙" });

        var response = await client.GetAsync("/api/search?q=咖啡");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // query 回显：agent 拿到的响应里能看见自己搜的是什么。
        Assert.Equal("咖啡", root.GetProperty("query").GetString());

        // 三组都在（每组都是 { items, total, offset, limit, hasMore }）。
        var ideas = root.GetProperty("ideas");
        var projects = root.GetProperty("projects");
        var tags = root.GetProperty("tags");

        Assert.Equal(JsonValueKind.Array, ideas.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Array, projects.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Array, tags.GetProperty("items").ValueKind);

        // 想法组里就是那条想法（正文命中）。
        Assert.Equal(ideaId, ideas.GetProperty("items")[0].GetProperty("id").GetString());
    }

    /// <summary>
    /// 想法组按**相关度**排：标题命中的那条排在正文命中的前面。
    /// </summary>
    /// <remarks>
    /// 两条的创建顺序是「正文命中在前」：相关度打平时按 id 升序兜底，
    /// 那时正文命中的那条会排在前面 —— 这条用例要守的正是「相关度说了算」。
    /// </remarks>
    [Fact]
    public async Task Search_ideas_group_is_sorted_by_relevance()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var inContent = await CreateIdeaAsync(client, "咖啡机滤芯");
        var inTitle = await CreateIdeaAsync(client, "完全不相关的正文", title: "咖啡清单");

        var ids = await ReadIdeaIdsAsync(await client.GetAsync("/api/search?q=咖啡"));

        Assert.Equal(new[] { inTitle, inContent }, ids);
    }

    /// <summary>
    /// 项目与标签各自成组：搜项目名得到项目，搜标签名得到标签。
    /// </summary>
    [Fact]
    public async Task Search_finds_projects_and_tags()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "工作");
        await CreateIdeaAsync(client, "一条想法", projectId: projectId, tags: new[] { "烘焙" });

        var byProject = await ReadSearchAsync(await client.GetAsync("/api/search?q=工作"));
        var projectNames = byProject.GetProperty("projects").GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("工作", projectNames);

        var byTag = await ReadSearchAsync(await client.GetAsync("/api/search?q=烘焙"));
        var tagNames = byTag.GetProperty("tags").GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("烘焙", tagNames);
    }

    /// <summary>
    /// 默认**含**回收站：软删的想法照样搜得到，并且标上 <c>trashed</c>。
    /// </summary>
    [Fact]
    public async Task Search_includes_trash_by_default()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡机滤芯");

        var deleted = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var root = await ReadSearchAsync(await client.GetAsync("/api/search?q=咖啡"));
        var item = root.GetProperty("ideas").GetProperty("items")[0];

        Assert.Equal(id, item.GetProperty("id").GetString());
        Assert.True(item.GetProperty("trashed").GetBoolean());
    }

    /// <summary>
    /// <c>includeTrash=false</c>：回收站里的不收。
    /// </summary>
    [Fact]
    public async Task Search_excludes_trash_when_asked()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var id = await CreateIdeaAsync(client, "咖啡机滤芯");

        var deleted = await client.DeleteAsync($"/api/ideas/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var root = await ReadSearchAsync(await client.GetAsync("/api/search?q=咖啡&includeTrash=false"));

        Assert.Empty(root.GetProperty("ideas").GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// 三组各自分页：<c>limit</c> / <c>offset</c> 各自生效，
    /// <c>total</c> 是命中总数（不受分页影响），<c>hasMore</c> 说明还有没有。
    /// </summary>
    [Fact]
    public async Task Search_paginates_each_group()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 每组造两个命中。想法带的标签名里也有「咖啡」——
        // 那只影响它**命中在哪个字段**，不影响命中了几条（一条想法就是一条）。
        await CreateIdeaAsync(client, "咖啡第一条", tags: new[] { "咖啡豆" });
        await CreateIdeaAsync(client, "咖啡第二条", tags: new[] { "咖啡粉" });
        await CreateProjectAsync(client, "咖啡甲");
        await CreateProjectAsync(client, "咖啡乙");

        var root = await ReadSearchAsync(await client.GetAsync("/api/search?q=咖啡&limit=1&offset=1"));

        foreach (var group in new[] { "ideas", "projects", "tags" })
        {
            var page = root.GetProperty(group);

            Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal(2, page.GetProperty("total").GetInt32());
            Assert.Equal(1, page.GetProperty("offset").GetInt32());
            Assert.Equal(1, page.GetProperty("limit").GetInt32());

            // offset + items.Length < total → 1 + 1 < 2 不成立 → 没有更多了。
            Assert.False(page.GetProperty("hasMore").GetBoolean());
        }
    }

    /// <summary>
    /// <c>offset</c> 的语义是「**跳过最相关的前 N 条**」。
    /// </summary>
    /// <remarks>
    /// 这与 <c>GET /api/ideas</c> 的 <c>offset</c> **相反**（那边是往回翻历史、
    /// 从最新一端数），是 agent 翻页的唯一依据 —— 只断言「返回了几条」的话，
    /// 「永远返回第一页」的实现照样能过。
    /// <para>
    /// 期望顺序不写死：先取全量（那就是 bm25 的顺序：最相关 → 最不相关），
    /// 再断言每一页正好是全量里对应位置的那条。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Search_offset_skips_the_most_relevant_ones()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        // 三条命中，相关度各不相同（标题 / 正文 / 正文）。
        await CreateIdeaAsync(client, "完全不相关的正文", title: "咖啡清单");
        await CreateIdeaAsync(client, "咖啡机滤芯");
        await CreateIdeaAsync(client, "手冲咖啡的要点");

        // 先取全量：bm25 的顺序就是「最相关 → 最不相关」。
        var full = await ReadSearchAsync(await client.GetAsync("/api/search?q=咖啡&limit=10"));
        var ordered = ReadGroupIds(full, "ideas");

        Assert.Equal(3, ordered.Length);

        // offset=N → 跳过最相关的前 N 条，返回第 N+1 条。
        for (var offset = 0; offset < ordered.Length; offset++)
        {
            var page = await ReadSearchAsync(
                await client.GetAsync($"/api/search?q=咖啡&limit=1&offset={offset}"));

            var ids = ReadGroupIds(page, "ideas");

            Assert.Single(ids);
            Assert.Equal(ordered[offset], ids[0]);
        }
    }

    /// <summary>
    /// 项目组与想法组**同一个回收站口径**：默认含回收站（并标 <c>trashed</c>），
    /// <c>includeTrash=false</c> 时不含。
    /// </summary>
    /// <remarks>
    /// 规格 §11.6：「<c>true</c> = 软删的**全部**都收（想法、项目都算）」。
    /// 软删不动索引（项目名还在 <c>project_fts</c> 里），所以它默认就该出现在结果里。
    /// </remarks>
    [Fact]
    public async Task Search_projects_follow_the_trash_rule()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var projectId = await CreateProjectAsync(client, "咖啡项目");

        var deleted = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // 默认（含回收站）：软删的项目在 projects 组里，并标 trashed。
        var root = await ReadSearchAsync(await client.GetAsync("/api/search?q=咖啡"));
        var projects = root.GetProperty("projects").GetProperty("items").EnumerateArray().ToArray();

        var item = Assert.Single(projects, project =>
            project.GetProperty("id").GetString() == projectId);

        Assert.True(item.GetProperty("trashed").GetBoolean());

        // includeTrash=false：不在。
        var excluded = await ReadSearchAsync(
            await client.GetAsync("/api/search?q=咖啡&includeTrash=false"));

        Assert.Empty(excluded.GetProperty("projects").GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// 标签组的 <c>count</c> **只数活着的想法**，且不随 <c>includeTrash</c> 变化。
    /// </summary>
    /// <remarks>
    /// 规格 §11.3：「<c>tags.items[].count</c> 的口径与 <c>GET /api/tags</c> 保持一致
    /// （只数**未删除**的想法），**不随 <c>includeTrash</c> 变化** ——
    /// 它回答的是「这个标签现在被用着多少次」，不是「搜索结果里出现了多少次」。
    /// </remarks>
    [Fact]
    public async Task Search_tags_count_only_live_ideas()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var live = await CreateIdeaAsync(client, "活着的想法", tags: new[] { "烘焙" });
        var dead = await CreateIdeaAsync(client, "删掉的想法", tags: new[] { "烘焙" });

        var deleted = await client.DeleteAsync($"/api/ideas/{dead}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var root = await ReadSearchAsync(await client.GetAsync("/api/search?q=烘焙"));

        var tag = Assert.Single(root.GetProperty("tags").GetProperty("items").EnumerateArray());

        Assert.Equal("烘焙", tag.GetProperty("name").GetString());

        // 只数活着的想法（两条里有一条被软删了）。
        Assert.Equal(1, tag.GetProperty("count").GetInt32());

        // 口径与 GET /api/tags 一致：那边报的是同一个数。
        var tags = await client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);

        using var document = JsonDocument.Parse(await tags.Content.ReadAsStringAsync());
        var listed = Assert.Single(document.RootElement.EnumerateArray(), item =>
            item.GetProperty("name").GetString() == "烘焙");

        Assert.Equal(1, listed.GetProperty("count").GetInt32());

        // includeTrash=true 也不变：它是「这个标签现在被用着多少次」。
        var withTrash = await ReadSearchAsync(
            await client.GetAsync("/api/search?q=烘焙&includeTrash=true"));

        Assert.Equal(1, withTrash.GetProperty("tags").GetProperty("items")[0]
            .GetProperty("count").GetInt32());

        // 想法组：活着的想法 trashed=false，软删的那条 trashed=true。
        var ideas = root.GetProperty("ideas").GetProperty("items").EnumerateArray().ToArray();

        var liveItem = Assert.Single(ideas, idea => idea.GetProperty("id").GetString() == live);
        var deadItem = Assert.Single(ideas, idea => idea.GetProperty("id").GetString() == dead);

        Assert.False(liveItem.GetProperty("trashed").GetBoolean());
        Assert.True(deadItem.GetProperty("trashed").GetBoolean());
    }

    /// <summary>
    /// <c>q</c> 缺失、给空串、给纯空白 → 400 <c>invalid_search_query</c>。
    /// </summary>
    [Fact]
    public async Task Search_with_empty_query_returns_400()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        foreach (var url in new[] { "/api/search", "/api/search?q=", "/api/search?q=%20%20" })
        {
            var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_search_query",
                (await ReadProblemAsync(response)).GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// <c>limit</c> 越界 → 400 <c>invalid_pagination</c>（范围 1..100）。
    /// </summary>
    [Fact]
    public async Task Search_with_out_of_range_limit_returns_400()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        foreach (var query in new[] { "limit=0", "limit=101" })
        {
            var response = await client.GetAsync($"/api/search?q=咖啡&{query}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_pagination",
                (await ReadProblemAsync(response)).GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// 全是标点的查询 = 没有关键词 = **不过滤**（与 <c>/api/ideas</c> 同一口径）。
    /// </summary>
    /// <remarks>
    /// 规格 §9.2：「所有段都空 → 等价于「没有关键词」→ 不过滤，返回全部」；
    /// 计划里也写明「两处口径必须一致」。所以这里返回全部，
    /// 而且响应里**没有** matches 键（没有关键词就没有命中位置）。
    /// </remarks>
    [Fact]
    public async Task Search_with_punctuation_only_query_returns_everything()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();

        var first = await CreateIdeaAsync(client, "第一条");
        var second = await CreateIdeaAsync(client, "第二条");

        var root = await ReadSearchAsync(await client.GetAsync("/api/search?q=..."));

        var ids = root.GetProperty("ideas").GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        Assert.Equal(new[] { first, second }, ids);

        // 没有关键词 = 没有命中位置：响应里不该多出一个空的 matches。
        Assert.False(root.GetProperty("ideas").GetProperty("items")[0].TryGetProperty("matches", out _));
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

    /// <summary>读想法 id 列表（顺序即返回顺序）。</summary>
    private static async Task<string[]> ReadIdeaIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("ideas").GetProperty("items")
            .EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();
    }

    /// <summary>读全库搜索里某一组的 id（顺序即返回顺序）。</summary>
    /// <param name="root">搜索响应的根对象。</param>
    /// <param name="group">组名（<c>ideas</c> / <c>projects</c> —— 两组的条目都有 id；
    /// 标签组只有 name 与 count）。</param>
    private static string[] ReadGroupIds(JsonElement root, string group)
    {
        return root.GetProperty(group).GetProperty("items")
            .EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()!)
            .ToArray();
    }

    /// <summary>读全库搜索的响应体（根对象）。</summary>
    private static async Task<JsonElement> ReadSearchAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // JsonElement 在文档释放后失效，复制一份出去。
        return document.RootElement.Clone();
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
