using System.Reflection;
using System.Text;

namespace IdeaApp.Tests;

/// <summary>
/// 运行期 415 用例清单 与 源码扫描 的**交叉核对**守卫。
///
/// <para>
/// 关于 415 有两条守卫，各管一半，谁都看不见另一半：
/// <list type="bullet">
/// <item><see cref="UnsupportedMediaTypeTests"/>：扫源码 ——
/// 「声明了 415」与「调了 <c>RejectsNonJsonBodyWithProblemDetails()</c>」必须成对出现；</item>
/// <item>运行期用例（<see cref="ErrorContractTests"/> 里那份手写清单）：
/// 真的发一个 <c>Content-Type: text/plain</c> 的请求，断言真的回 415 + ProblemDetails +
/// <c>unsupported_media_type</c>。</item>
/// </list>
///
/// <para>
/// 合起来仍有洞：<b>新增</b>一个带请求体的端点时，静态守卫会确认它
/// 「声明了 415 + 调了机制」✓，但**运行期**的 415 用例不会覆盖它 ——
/// 而「真的回 415 + code」这件事恰恰只在运行期发生。
/// 更糟的是源码扫描本身也可能**部分失灵**（某个文件整体没被扫到），
/// 那样连静态守卫都看不见那个新端点。
/// </para>
///
/// <para>
/// 所以这里让两个**互相独立**的来源交叉核对：
/// <b>手写清单</b>（人写的、运行期真跑的那批用例） ↔ <b>源码扫描</b>（扫代码算出来的）。
/// <b>任一方向不一致都要红</b>：
/// <list type="bullet">
/// <item>扫描有、手写没有 → 新增了带 body 的端点但忘了补运行期用例 → 红；</item>
/// <item>手写有、扫描没有 → 扫描器漏扫了（或清单写重了/写错了）→ 红。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>不要改成「运行期用例集直接由扫描器生成」</b>：那样扫描器漏扫就静默少测了，
/// 两个来源塌成一个，交叉核对的意义正好没了。
/// </para>
/// </summary>
public sealed class UnsupportedMediaTypeCoverageTests
{
    /// <summary>
    /// 手写清单必须与源码扫描**逐条对得上**。
    /// </summary>
    [Fact]
    public void The_runtime_cases_cover_exactly_the_endpoints_the_source_scan_finds()
    {
        // 扫描侧：「带 body 的端点」= 源码里调了 RejectsNonJsonBodyWithProblemDetails() 的端点。
        // 那正是「这个端点收 JSON body」的标记 —— 框架只会给带 JSON body 的端点
        // 自动加那条 AcceptsMetadata，而调用这个机制就是把那条元数据摘掉、
        // 改成在绑定 body 阶段抛 415。所以「调了机制」= 「运行期会回 415」，
        // 每个这样的端点都必须有一条运行期用例。
        //
        // 注意：这里用的判据是「调了机制」，比「声明了 415 且调了机制」更宽 ——
        // 后者在「新增端点既没声明也没调机制」时会漏掉它。
        // 两个判据在一个健康的代码库里等价（配对守卫保证声明与机制成对出现），
        // 但宽的那个能多抓一类漏。
        var scanned = EndpointScanner.ScanEndpoints()
            .Where(endpoint => endpoint.UsesMechanism)
            .Select(endpoint => endpoint.Route)
            .ToList();

        var handwritten = HandwrittenRoutes();

        // 方向一：扫描有、手写没有 → 新增了端点但忘了补运行期用例。
        var missing = Difference(scanned, handwritten);

        // 方向二：手写有、扫描没有 → 扫描器漏扫了（或清单写重了/写错了）。
        var extra = Difference(handwritten, scanned);

        if (missing.Count == 0 && extra.Count == 0)
        {
            return;
        }

        var message = new StringBuilder("运行期 415 用例清单与源码扫描对不上。");

        if (missing.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("扫描到、但手写清单里没有（新增了带 body 的端点，却忘了补运行期用例）：");

            foreach (var route in missing)
            {
                message.AppendLine($"  - {route}");
            }
        }

        if (extra.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("手写清单里有、但扫描没扫到（清单写重了？路径写错了？扫描器漏扫了？）：");

            foreach (var route in extra)
            {
                message.AppendLine($"  - {route}");
            }
        }

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// 手写清单：运行期 415 用例方法上的 <c>[InlineData]</c> 属性。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么用**反射读 InlineData**，而不是把清单抽成一个静态数组、用
    /// <c>[MemberData]</c> 引用它：
    /// <list type="number">
    /// <item>InlineData 就长在**真正会跑的那个用例方法上** —— 清单与用例是同一处东西，
    /// 新增端点只需在那儿加一行；不会出现「数组改了、方法上没改」的两份清单。</item>
    /// <item>要比的正是「运行期真跑的那批用例」，读属性等于直接读运行期输入本身，
    /// 而不是读一份副本。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 反射拿不到时（方法改名了）直接抛错，不静默当成空清单 ——
    /// 空清单会让「手写有、扫描没有」那个方向全红，仍然是红的，不会静默放过。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> HandwrittenRoutes()
    {
        var method = typeof(ErrorContractTests).GetMethod(
            nameof(ErrorContractTests.A_body_endpoint_rejects_a_non_json_content_type_with_415),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "找不到运行期 415 用例的方法（ErrorContractTests."
                + nameof(ErrorContractTests.A_body_endpoint_rejects_a_non_json_content_type_with_415)
                + "）—— 改名了？交叉核对守卫要读它上面的 InlineData。");

        var routes = new List<string>();

        foreach (var inlineData in method.GetCustomAttributes<InlineDataAttribute>())
        {
            var row = inlineData.GetData(method).Single();

            routes.Add($"{row[0]} {row[1]}");
        }

        return routes;
    }

    /// <summary>两个集合的差集（按**出现次数**算，不是按去重后的集合）。</summary>
    /// <param name="left">被减的集合。</param>
    /// <param name="right">减数集合。</param>
    /// <returns>只在 <paramref name="left"/> 里出现的元素。</returns>
    private static List<string> Difference(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var remaining = new List<string>(right);
        var result = new List<string>();

        foreach (var item in left)
        {
            var index = remaining.IndexOf(item);

            if (index < 0)
            {
                result.Add(item);
                continue;
            }

            remaining.RemoveAt(index);
        }

        return result;
    }
}
