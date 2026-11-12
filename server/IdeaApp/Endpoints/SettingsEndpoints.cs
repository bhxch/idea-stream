using IdeaApp.Application;
using IdeaApp.Contracts;
using IdeaApp.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IdeaApp.Endpoints;

/// <summary>
/// 应用偏好（设置）API。
///
/// <para>
/// 响应是**扁平的键值字典**（<c>{"project.sort_mode": "recent"}</c>），不是数组：
/// 键名已经自带命名空间（<c>project.</c> 前缀），数组形式还得再包一层对象名。
/// </para>
/// <para>
/// AOT 硬性要求：返回的静态类型必须在 <see cref="IdeaJsonContext"/> 里有条目，
/// 并且显式带上 <c>JsonTypeInfo</c>。
/// </para>
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (SettingRepository settings) =>
            ApiResults.Run(() => Results.Json(ReadAll(settings), IdeaJsonContext.Default.DictionaryStringString)))
            .Produces<Dictionary<string, string>>(StatusCodes.Status200OK);

        app.MapPut("/api/settings/{key}", (string key, SetSettingRequest request,
                                           SettingRepository settings, ProjectService projects) =>
            ApiResults.Run(() =>
            {
                // 只接受已定义的键：配置文件里的项（回收站保留期等）不可经 API 修改。
                if (!string.Equals(key, SettingKeys.ProjectSortMode, StringComparison.Ordinal))
                {
                    throw new ApiException(400, "setting_key_not_writable", $"设置项不可写：{key}");
                }

                // 值是否合法由服务层判定（排序方式是这个领域的规则）。
                projects.SetSortMode(request.Value ?? string.Empty);

                return Results.Json(ReadAll(settings), IdeaJsonContext.Default.DictionaryStringString);
            }))
            .Produces<Dictionary<string, string>>(StatusCodes.Status200OK)
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest,
                "setting_key_not_writable",
                "invalid_sort_mode");

        return app;
    }

    /// <summary>
    /// 组装设置响应：已知键读不到就回落默认值，未知键不出现在响应里。
    /// </summary>
    private static Dictionary<string, string> ReadAll(SettingRepository settings) =>
        new(StringComparer.Ordinal)
        {
            [SettingKeys.ProjectSortMode] =
                ProjectSortMode.Resolve(settings.Get(SettingKeys.ProjectSortMode))
        };
}
