namespace IdeaApp.Tests;

/// <summary>
/// 「声明 415」与「让 415 送得到」必须成对出现的守卫。
///
/// <para>
/// 415（<c>unsupported_media_type</c>）这个错误码是**双要素**的：
/// <list type="bullet">
/// <item>端点声明 <c>.ProducesApiProblem(Status415UnsupportedMediaType, "unsupported_media_type")</c>
/// —— 契约里才写得出这个 415；</item>
/// <item>端点调用 <c>.RejectsNonJsonBodyWithProblemDetails()</c> —— 运行时才真的回 415
/// （框架在**绑定 body 时**抛 <c>BadHttpRequestException(415)</c>，由
/// <c>ApiExceptionHandler</c> 转成 ProblemDetails）。</item>
/// </list>
///
/// <para>
/// <b>缺一个，契约就在说谎</b>：
/// <list type="bullet">
/// <item>只声明不调用：契约写着「会回 415 + <c>unsupported_media_type</c>」，
/// 运行时其实回的是 <b>404 + <c>api_not_found</c></b> ——
/// .NET 10 的路由层会先按 Content-Type 把端点毙掉，请求落到
/// <c>/api/{**rest}</c> 兜底（"接口不存在"，语义完全不对）；</item>
/// <item>只调用不声明：运行时回 415，契约里却没有这个响应。</item>
/// </list>
///
/// <para>
/// 这类错配**没有任何现有测试能发现**：契约文件与运行期行为不在同一个世界里，
/// 契约里写了 415、运行时回 404，两边各自"自洽"。
/// 所以这里扫**源码**，按端点配对检查。
/// </para>
///
/// <para>
/// 扫描规则放在共享的 <see cref="EndpointScanner"/> 里 ——
/// 另一条守卫（运行期 415 用例清单与源码扫描的交叉核对，见
/// <see cref="UnsupportedMediaTypeCoverageTests"/>）要用**同一份**规则，
/// 两边各扫一遍迟早有一边认不全某种写法。
/// </para>
/// </summary>
public sealed class UnsupportedMediaTypeTests
{
    /// <summary>
    /// 声明了 415 与调用了机制，必须成对出现。
    /// </summary>
    [Fact]
    public void Declaring_415_and_enabling_the_rejection_go_together()
    {
        var endpoints = EndpointScanner.ScanEndpoints();

        // 扫描不能是空的：连一个声明 415 的端点都没扫到，说明扫描规则坏了
        // （端点文件挪了地方、映射写法变了……），那样下面的断言会「什么都没查」就绿。
        Assert.True(
            endpoints.Any(endpoint => endpoint.Declares415),
            $"在 {RepoPaths.Endpoints} 下没扫到任何声明 415 的端点 —— 扫描规则坏了？");

        // 方向一：声明了 415 却没调机制 → 契约说谎（运行时回 404 api_not_found）。
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Declares415 && !endpoint.UsesMechanism))
        {
            Assert.Fail(
                $"{endpoint.Label}：声明了 415（unsupported_media_type），但没有调用 RejectsNonJsonBodyWithProblemDetails()。"
                + "运行时不会回 415（请求会被 /api/{**rest} 兜底接走，回 404 api_not_found），契约说谎。");
        }

        // 方向二：调了机制却没声明 415 → 契约漏了它（运行时回 415，契约里没有）。
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.UsesMechanism && !endpoint.Declares415))
        {
            Assert.Fail(
                $"{endpoint.Label}：调用了 RejectsNonJsonBodyWithProblemDetails()，但没有声明 415（unsupported_media_type）。"
                + "运行时回 415，契约里却写不出这个响应。");
        }
    }
}
