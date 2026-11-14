# Agent Skill 随服务分发 · 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 agent skill 的真实内容随服务一起分发 —— 服务端发手册与契约，agent 侧只留一个薄壳；同时删掉 933 行的命令行封装，agent 改用 curl + 契约直接调 API。

**Architecture:** 服务端新增一组只读端点 `/skill/idea-manager/**`：一个清单端点（版本 + 文件清单 + 每文件 sha256 + 入口路径）与一个取文件端点；发布产物里带上 `skill/idea-manager/`（内容源的构建副本）。agent 侧安装的是「薄壳」—— 一个 `SKILL.md`（frontmatter + 指针）与一个 `scripts/fetch.py`（取件器，按清单同步到本地缓存、逐文件校验 sha256 后整体切换）。命令层整个删掉：webhook 的 url 与 headers 改为**服务端默认遮蔽**，agent 自己用 curl 调 API。

**Tech Stack:** .NET 10 Native AOT + Minimal API（JSON 源生成上下文，无反射）、SQLite、Vue 3 + TypeScript + Vite、Python 3.9+ 标准库（无第三方依赖）、xUnit（`dotnet test`）、Vitest（`npm test`）。

**Spec:** [`docs/superpowers/specs/2026-11-13-skill-distribution-design.md`](../specs/2026-11-13-skill-distribution-design.md)

---

## Global Constraints

- **只用 Python 标准库**：`fetch.py` 与安装脚本不得引入任何第三方依赖；目标解释器 **Python 3.9+**（不得用 3.10+ 语法：`match`、`X | Y` 形式的运行时求值注解）。
- **Native AOT**：所有端点返回的静态类型必须登记进 `IdeaJsonContext`（`server/IdeaApp/Contracts/JsonContext.cs`），并显式带上 `JsonTypeInfo`。少登记一个，开发期全绿、AOT 产物下 500。
- **契约是构建产物快照**：`openapi/idea-v1.json` 由 `dotnet run --project tools/ExportOpenApi` 导出。改了端点、DTO、查询参数、状态码或错误码之后**必须重新导出并提交**，否则 `OpenApiDocumentTests.Committed_document_matches_the_running_app` 会红。
- **新错误码必须登记进 `ApiErrorCodes.All`**（`server/IdeaApp/Contracts/ApiErrorCodes.cs`）：`ApiErrorCodeTests.Documented_codes_match_the_ones_actually_thrown` 会扫源码核对，漏登记就红；登记了但没人抛也红。
- **相对路径锚在应用目录**（`AppContext.BaseDirectory`），与当前工作目录无关 —— 与 `Database:Path` 同一条约定（见 `SqliteDatabase.AppDirectory`）。
- **端点映射必须在 `MapFallbackToFile("index.html")` 之前**（`server/IdeaApp/Program.cs`）。SPA 兜底是 catch-all，会兜住所有没匹配到的 GET。
- **错误响应一律 RFC 9457 的 ProblemDetails + 扩展字段 `code`**（走 `ApiException` + `ApiResults.Run`）。
- **凭据只从环境变量读**：`IDEA_BASE_URL` / `IDEA_USER` / `IDEA_PASS` / `IDEA_TOKEN`。命令行不接受密码；错误信息与日志一律不得回显凭据（含 URL 的 userinfo）。
- **不提交任何凭据**，也不提交 `__pycache__`（已在 `.gitignore`）。
- **commit 规范**：Conventional Commits（`feat` / `fix` / `docs` / `test` / `refactor` / `chore`），中文正文；**每个 task 结束提交一次**，提交内容只含该 task 的文件（不要 `git add -A`）。

---

## Review Focus

下列五类是 spec 暗示、但没有哪条常规测试能覆盖的失败模式 —— 每一条都在下面的 task 里有对应的测试钉住：

1. **路径穿越**：`/skill/idea-manager/../../etc/passwd` 必须 404，**不能**读到文件内容（Task 1）。
2. **拼错的路径落到 SPA 兜底**：`/skill/idea-manger/SKILL.md`、`/skill` 必须 404 + ProblemDetails，**不能**是 200 + HTML（agent 只看状态码会把 200 当成功）（Task 1）。
3. **半新半旧**：同步中途失败（某个文件的 sha256 对不上）时，旧版本必须原样留在缓存里，**不能**出现「手册是新的、别的是旧的」（Task 2）。
4. **凭据泄漏**：webhook 的 url 里的 token 片段与 headers 的值，默认不得出现在响应体里；取件器的错误信息不得回显密码（Task 3、Task 2）。
5. **特殊字符原样送达**：手册必须给出能照抄的写法（`--data-binary @file` 或 quoted heredoc），否则 agent 会用 shell 拼 JSON，引号/换行/反斜杠会被吃掉（Task 5）。

---

## 文件结构

**新增**

```
server/IdeaApp/Contracts/SkillDtos.cs          清单协议的两个 DTO
server/IdeaApp/Infrastructure/SkillCatalog.cs   分发副本的目录视图：枚举、哈希、清单、路径解析
server/IdeaApp/Endpoints/SkillEndpoints.cs      /skill/** 的三个端点
server/IdeaApp/Infrastructure/WebhookMasking.cs webhook 的 url/headers 遮蔽
server/IdeaApp.Tests/SkillEndpointsTests.cs     /skill/** 的集成测试
server/IdeaApp.Tests/SkillDocGuardTests.cs      手册与契约/端点/错误码的一致性守卫
skills/idea-manager-client/SKILL.md             薄壳（agent 侧安装的那份）
skills/idea-manager-client/scripts/fetch.py     取件器
skills/idea-manager-client/tests/test_fetch.py  取件器的单元测试
skills/idea-manager-client/scripts/run-tests.sh 取件器测试的运行脚本
skills/install-agent-skill.sh                   薄壳安装脚本
skills/idea-manager/references/calling.md       怎么用 curl 调
skills/idea-manager/references/errors.md        错误处理与服务端错误码表
skills/idea-manager/references/webhooks.md      webhook 特有语义
skills/idea-manager/references/api.md           契约位置与端点清单
skills/idea-manager/references/scenarios.md     典型场景
```

**修改**

```
server/IdeaApp/Program.cs                        注册 SkillCatalog + 映射端点
server/IdeaApp/IdeaApp.csproj                    把内容源带进产物
server/IdeaApp/Contracts/JsonContext.cs          登记 SkillManifestDto
server/IdeaApp/Contracts/ApiErrorCodes.cs        三个新错误码 + 注释指针
server/IdeaApp/Contracts/Dtos.cs                 不动（webhook 的 DTO 形状不变）
server/IdeaApp/Endpoints/WebhookEndpoints.cs     unmask 参数 + 默认遮蔽
server/IdeaApp/Infrastructure/AppOptions.cs      SkillOptions
server/IdeaApp.Tests/TestAppFactory.cs           覆盖 Skill:Path
server/IdeaApp.Tests/RepoPaths.cs                仓库内 skill 相关路径
server/IdeaApp.Tests/OpenApiDocumentTests.cs     删掉「skill 契约副本」的字节级测试
server/IdeaApp.Tests/RequestBodyLimitGuardTests.cs  改读 references/errors.md
server/IdeaApp.Tests/PayloadTooLargeTests.cs     注释里的指针
server/IdeaApp.Tests/WebhookApiTests.cs          遮蔽的集成测试
web/src/components/WebhookManager.vue            列表请求带 unmask=true
web/src/pages/WebhooksPage.spec.ts               前端断言
skills/idea-manager/SKILL.md                     改成索引页（≤150 行）
openapi/idea-v1.json                             重新导出（Task 1、Task 3 各一次）
README.md                                         安装方式、agent skill 一节、配置项表
docs/nginx/idea.conf                             注释里的 413 指针
docs/superpowers/specs/2026-11-11-idea-manager-design.md  §17 的交付物描述
```

**删除**

```
skills/idea-manager/scripts/idea.py              命令层（933 行）
skills/idea-manager/scripts/run-tests.sh
skills/idea-manager/tests/test_cli.py
skills/idea-manager/references/openapi.json      契约副本（服务端实时发 /openapi/v1.json）
```

---

### Task 1: 服务端发 skill 内容

**Files:**
- Create: `server/IdeaApp/Contracts/SkillDtos.cs`
- Create: `server/IdeaApp/Infrastructure/SkillCatalog.cs`
- Create: `server/IdeaApp/Endpoints/SkillEndpoints.cs`
- Modify: `server/IdeaApp/Contracts/JsonContext.cs`
- Modify: `server/IdeaApp/Contracts/ApiErrorCodes.cs`
- Modify: `server/IdeaApp/Infrastructure/AppOptions.cs`
- Modify: `server/IdeaApp/Program.cs`
- Modify: `server/IdeaApp/IdeaApp.csproj`
- Modify: `server/IdeaApp.Tests/TestAppFactory.cs`
- Create: `server/IdeaApp.Tests/SkillEndpointsTests.cs`
- Modify: `openapi/idea-v1.json`（重新导出）

**Interfaces:**
- Produces:
  - `GET /skill/idea-manager/version` → 200，JSON `SkillManifestDto`：
    `{ "version": string, "entry": string, "files": [ { "path": string, "sha256": string, "size": long } ] }`
    - `version`：分发副本全部文件的 `(path, sha256)` 排序后拼接再取 sha256 的十六进制小写串。**同一个内容永远同一个版本串。**
    - `entry`：索引页在分发副本里的相对路径（当前是 `SKILL.md`）。
    - `files`：递归枚举的全部文件（跳过路径里含 `__pycache__` 段的），按 path 升序。
    - `sha256`：文件字节的 sha256 十六进制小写串（与 `fetch.py` 侧算法一致：先算文件字节的 sha256，再对 `(path + "\n" + sha256)` 按 path 升序拼接的字节串取 sha256）。
  - `GET /skill/idea-manager/{**path}` → 200 + 文件内容；`.md` → `text/markdown; charset=utf-8`，`.json` → `application/json; charset=utf-8`，其余 → `text/plain; charset=utf-8`。
    - **分发副本目录不存在** → 404 `skill_not_distributed`；**目录在但文件不在** → 404 `skill_file_not_found`。
      （目录判定由端点单独做：`TryResolveFile` 只管路径解析与文件存在性。）
  - `GET /skill/{**rest}` → 404 + ProblemDetails，`code=skill_endpoint_not_found`。
  - `SkillCatalog`：`public SkillCatalog(string configuredPath)`、`public string Root { get; }`、`public SkillManifestDto BuildManifest()`、`public bool TryResolveFile(string relativePath, out string fullPath)`。
    - `configuredPath` 是相对路径时按 `AppContext.BaseDirectory` 解析。
    - `TryResolveFile` 对绝对路径、含 `..` 的路径、以及解析后不在 `Root` 之下的路径一律返回 false。
  - `SkillOptions.Path`：默认 `"skill/idea-manager"`（相对路径，按应用目录解析）。
  - `ApiErrorCodes.All` 新增三条（404）：`skill_not_distributed`（分发副本目录不存在）、`skill_file_not_found`（副本里没有这个文件，含穿越尝试）、`skill_endpoint_not_found`（`/skill/**` 下没有对应端点）。
  - `TestAppFactory(string? skillPath = null)`：给 `Skill:Path` 覆盖用；不给则默认指向仓库里的 `skills/idea-manager/`（`RepoPaths.Root` 下）。

- Consumes: 无（这是第一个 task）。

- [ ] **Step 1: 写失败的测试**

在 `server/IdeaApp.Tests/SkillEndpointsTests.cs` 写这些用例（xUnit，用 `TestAppFactory`）：

```csharp
[Fact] public async Task Version_endpoint_returns_the_manifest_of_the_skill_directory()
// GET /skill/idea-manager/version → 200；响应是对象，含 version（非空字符串）、
// entry（"SKILL.md"）、files（数组）；files 里有一项 path == "SKILL.md"，
// 它的 sha256 == 该文件字节的 sha256（自己算一遍比对）。

[Fact] public async Task File_endpoint_returns_the_file_with_its_content_type()
// GET /skill/idea-manager/SKILL.md → 200；Content-Type 以 "text/markdown" 开头；
// 响应体与仓库里那份 skills/idea-manager/SKILL.md 逐字相同。

[Fact] public async Task File_endpoint_rejects_path_traversal()
// GET /skill/idea-manager/../../etc/passwd → 404；响应是 ProblemDetails，
// code == "skill_file_not_found"；响应体里不出现 "root:"。

[Fact] public async Task File_endpoint_returns_404_for_a_missing_file()
// GET /skill/idea-manager/nope.md → 404，code == "skill_file_not_found"。

[Fact] public async Task Unmatched_skill_path_returns_404_problem_details_not_html()
// GET /skill/idea-manger/SKILL.md → 404，code == "skill_endpoint_not_found"，
// Content-Type 是 application/problem+json（**不是** text/html）。
// GET /skill → 404，code == "skill_endpoint_not_found"。

[Fact] public async Task Missing_skill_directory_returns_404_not_distributed()
// TestAppFactory(skillPath: 一个不存在的目录) → GET /skill/idea-manager/SKILL.md
// → 404，code == "skill_not_distributed"。

[Fact] public async Task Version_changes_when_the_content_changes()
// skillPath 指向一个临时目录：先放 a.md（内容 "a"），取一次 version；
// 把 a.md 的内容改成 "b"，再取一次 version；两次 version 不相等。
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --filter "FullyQualifiedName~SkillEndpointsTests"
```
Expected: 全部 FAIL（端点还不存在 → 404 或 405）。

- [ ] **Step 3: 实现 DTO 与配置项**

`server/IdeaApp/Contracts/SkillDtos.cs`（新建）：

```csharp
namespace IdeaApp.Contracts;

/// <summary>分发副本的清单：版本、入口、文件清单。</summary>
public sealed record SkillManifestDto(string Version, string Entry, SkillFileDto[] Files);

/// <summary>分发副本里的一个文件。</summary>
public sealed record SkillFileDto(string Path, string Sha256, long Size);
```

`server/IdeaApp/Infrastructure/AppOptions.cs`：加

```csharp
/// <summary>skill 分发副本的位置。</summary>
public sealed class SkillOptions
{
    /// <summary>分发副本的根目录。相对路径按应用目录解析。</summary>
    public string Path { get; set; } = "skill/idea-manager";
}
```

并在 `AppOptions` 上加 `public SkillOptions Skill { get; set; } = new();`。

`server/IdeaApp/Contracts/ApiErrorCodes.cs`：在 `All` 的 404 段追加三条（码名与含义照抄上面 Interfaces 段），
`Meaning` 用中文写清楚「服务端没有分发 skill 内容」「分发副本里没有这个文件」「/skill 下的路径没有对应端点」。

`server/IdeaApp/Contracts/JsonContext.cs`：加 `[JsonSerializable(typeof(SkillManifestDto))]`
与 `[JsonSerializable(typeof(SkillFileDto[]))]`（清单里的 files 是数组，包装类型也要登记）。

- [ ] **Step 4: 实现 SkillCatalog**

`server/IdeaApp/Infrastructure/SkillCatalog.cs`（新建）：

- 构造函数 `SkillCatalog(string configuredPath)`：`Root` = 绝对路径（相对路径按 `AppContext.BaseDirectory` 解析，同 `SqliteDatabase.AppDirectory` 的约定）。
- `BuildManifest()`：
  - `Root` 不存在 → 抛 `ApiException(404, "skill_not_distributed", …)`。
  - 递归枚举 `Root` 下的全部文件，跳过路径里含 `__pycache__` 段的；按相对路径（`/` 分隔）升序。
  - 每个文件算 sha256（`System.Security.Cryptography.SHA256.HashData` 的字节，十六进制小写）。
  - `version` = 对 `string.Join("\n", files.Select(f => f.Path + "\n" + f.Sha256))` 的 UTF-8 字节取 sha256。
  - `entry` = 相对路径恰好是 `SKILL.md` 的那个文件的路径；没有就抛 `ApiException(404, "skill_not_distributed", …)`。
- `TryResolveFile(string relativePath, out string fullPath)`：
  - 空路径、绝对路径（以 `/` 开头）、含 `..` 段的 → false。
  - 组合 `Path.Combine(Root, relativePath)` → `Path.GetFullPath` → 必须以 `Root + Path.DirectorySeparatorChar` 开头 → 否则 false。
  - 通过后还要确认文件存在。

- [ ] **Step 5: 实现端点并接线**

`server/IdeaApp/Endpoints/SkillEndpoints.cs`（新建），仿照 `WebhookEndpoints` 的写法
（`ApiResults.Run(...)` + `Results.Json(dto, IdeaJsonContext.Default.Xxx)`）：

```csharp
public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
```

三个端点：
- `GET /skill/idea-manager/version` → `Results.Json(catalog.BuildManifest(), IdeaJsonContext.Default.SkillManifestDto)`，`.Produces<SkillManifestDto>(200)`。
- `GET /skill/idea-manager/{**path}` → 先判分发副本目录在不在（不在 → `ApiException(404, "skill_not_distributed", …)`）；
  再 `TryResolveFile`：找到就 `Results.Text(内容, 内容类型)`；找不到就抛 `ApiException(404, "skill_file_not_found", …)`。
  `.Produces<string>(200)` 与 `.ProducesApiProblem(404, "skill_file_not_found", "skill_not_distributed")`。
- `GET /skill/{**rest}` → 抛 `ApiException(404, "skill_endpoint_not_found", …)`。

`server/IdeaApp/Program.cs`：
- 在 `builder.Services.AddSingleton<WebhookService>();` 之后注册
  `builder.Services.AddSingleton(sp => new SkillCatalog(sp.GetRequiredService<IOptions<AppOptions>>().Value.Skill.Path));`
- 在 `app.MapWebhookEndpoints();` 之后加 `app.MapSkillEndpoints();`（**必须在 `app.MapFallbackToFile("index.html");` 之前**）。

`server/IdeaApp/IdeaApp.csproj`：加一个 `ItemGroup`：

```xml
<!--
  skill 分发副本：内容源（仓库里的 skills/idea-manager/）随产物一起走。
  显式列出要带的文件，不整目录拷 —— 内容源里可能混着开发侧的残留
  （__pycache__、测试），整目录拷会把它们一起发出去。
  Link 用正斜杠：MSBuild 在 Unix 上不保证把反斜杠转成目录分隔符。
-->
<ItemGroup>
  <Content Include="../../skills/idea-manager/SKILL.md"
           Link="skill/idea-manager/SKILL.md"
           CopyToOutputDirectory="PreserveNewest"
           CopyToPublishDirectory="PreserveNewest" />
  <Content Include="../../skills/idea-manager/references/**/*.md"
           Link="skill/idea-manager/references/%(RecursiveDir)%(Filename)%(Extension)"
           CopyToOutputDirectory="PreserveNewest"
           CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 6: 让测试能覆盖 Skill:Path**

`server/IdeaApp.Tests/TestAppFactory.cs`：构造函数加 `string? skillPath = null` 参数与对应属性；
在 `ConfigureAppConfiguration` 的字典里加 `["Skill:Path"] = SkillPath`。
`SkillPath` 为 null 时用 `Path.Combine(RepoPaths.Root, "skills", "idea-manager")`。

- [ ] **Step 7: 跑测试确认通过**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --filter "FullyQualifiedName~SkillEndpointsTests"
```
Expected: 全部 PASS。

- [ ] **Step 8: 重新导出契约并跑全量测试**

```bash
dotnet run --project tools/ExportOpenApi
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj
```
Expected: 契约文件被更新（`git status` 里 `openapi/idea-v1.json` 有改动），全量测试 PASS。

- [ ] **Step 9: 给 AOT 冒烟加两条检查**

`server/scripts/aot-smoke.sh` 末尾（`exit 0` 之前）加：

```bash
echo "==> GET /skill/idea-manager/version"
# 清单端点：返回静态类型 SkillManifestDto。AOT 下少登记它，这里就是 500。
check_endpoint "/skill/idea-manager/version" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) && version.GetString() != null && root.TryGetProperty("entry", out var entry) && entry.GetString() == "SKILL.md" && root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array'

echo "==> GET /skill/idea-manager/SKILL.md"
# 内容端点返回的是 markdown（不是 JSON），用 curl 直接断言状态码与内容类型。
SKILL_CODE="$(curl -sS -o "$WORK_DIR/skill.md" -w '%{http_code}' "$BASE_URL/skill/idea-manager/SKILL.md")"
[[ "$SKILL_CODE" == "200" ]] || fail "/skill/idea-manager/SKILL.md 期望 HTTP 200，实际 $SKILL_CODE"
grep -q '^---' "$WORK_DIR/skill.md" || fail "取到的 SKILL.md 不像 markdown（没有 frontmatter）"
```

跑一遍：

```bash
bash server/scripts/aot-smoke.sh
```
Expected: 输出 `PASS: AOT 产物下所有被检查的端点都返回预期状态码与响应体`。

- [ ] **Step 10: 提交**

```bash
git add server/IdeaApp/Contracts/SkillDtos.cs server/IdeaApp/Infrastructure/SkillCatalog.cs \
        server/IdeaApp/Endpoints/SkillEndpoints.cs server/IdeaApp/Contracts/JsonContext.cs \
        server/IdeaApp/Contracts/ApiErrorCodes.cs server/IdeaApp/Infrastructure/AppOptions.cs \
        server/IdeaApp/Program.cs server/IdeaApp/IdeaApp.csproj \
        server/IdeaApp.Tests/SkillEndpointsTests.cs server/IdeaApp.Tests/TestAppFactory.cs \
        server/scripts/aot-smoke.sh openapi/idea-v1.json
git commit -m "feat(skill): 服务端发 skill 分发副本与清单

agent 手上的说明书会随服务更新而过期，agent 于是按旧语义操作。
让服务端持有唯一权威：/skill/idea-manager/version 给清单（版本 + 文件清单 +
每文件 sha256），/skill/idea-manager/{path} 取文件，/skill/** 兜底 404
（拼错的路径不能落到 SPA 兜底变成 200 + HTML）。

路径穿越一律 404；分发副本缺失回 skill_not_distributed 而不是 500。"
```

---

### Task 2: 取件器、薄壳与安装脚本

**Files:**
- Create: `skills/idea-manager-client/scripts/fetch.py`
- Create: `skills/idea-manager-client/tests/test_fetch.py`
- Create: `skills/idea-manager-client/scripts/run-tests.sh`
- Create: `skills/idea-manager-client/SKILL.md`
- Create: `skills/install-agent-skill.sh`

**Interfaces:**
- Consumes: Task 1 的 `GET /skill/idea-manager/version` 与 `GET /skill/idea-manager/{path}`。

- Produces:
  - `fetch.py` 的命令行契约：
    | 用法 | 行为 |
    |---|---|
    | `fetch.py` | 同步后打印索引页（清单里的 `entry`） |
    | `fetch.py <相对路径>` | 同步后打印该文件（相对分发副本根目录） |
    | `fetch.py /<绝对路径>` | 打印服务端的绝对路径（`/openapi/v1.json`） |
  - 环境变量：`IDEA_BASE_URL`（必填）、`IDEA_USER` / `IDEA_PASS`（basic auth）、`IDEA_TOKEN`（`X-Api-Key`）、`IDEA_SKILL_CACHE_DIR`（可选，覆盖缓存根目录；测试用）。
  - 缓存根目录：`$IDEA_SKILL_CACHE_DIR`，未设时用 `${XDG_CACHE_HOME:-~/.cache}/idea-skill`；缓存副本放在 `<缓存根>/<version>/`。
  - 错误：一行 `code: detail` 到 stderr。`missing_config` / `invalid_config` / `auth_error` / `not_found` / `skill_integrity_error` → 退出码 1；`network_error` → 退出码 2；argparse 的用法错误 → 退出码 2。
  - 薄壳 `skills/idea-manager-client/SKILL.md`：frontmatter（与 `skills/idea-manager/SKILL.md` 逐字相同）+ 指针正文。

- [ ] **Step 1: 写失败的测试**

`skills/idea-manager-client/tests/test_fetch.py`（unittest，标准库；用一个 `http.server.ThreadingHTTPServer` 起假服务端）：

```python
class FetchTest(unittest.TestCase):
    def test_prints_the_entry_document(self)          # 清单的 entry 指向 SKILL.md → 打印它
    def test_prints_a_reference_file(self)           # fetch.py references/errors.md
    def test_prints_an_absolute_server_path(self)    # fetch.py /openapi/v1.json → 打服务端那个路径
    def test_syncs_a_new_version_and_replaces_the_cache(self)
        # 服务端换版本（内容变了 → version 变了）→ 再取一次拿到新内容，
        # 且**当前版本目录**里是新版内容。
        # 不断言「缓存里只有一个版本目录」：旧版本要留着（同步失败时还要靠它退）。
    def test_keeps_the_old_version_when_a_hash_does_not_match(self)
        # 服务端返回的 sha256 与实际内容对不上 → 退出码 1，stderr 有 skill_integrity_error，
        # 且旧版本目录仍在、内容没被覆盖。
    def test_reports_missing_config_without_the_base_url(self)
        # 不设 IDEA_BASE_URL → 退出码 1，stderr 以 "missing_config: " 开头。
    def test_reports_skill_not_distributed_on_404(self)
        # 清单端点回 404 + code=skill_not_distributed → stderr 有 skill_not_distributed。
    def test_never_echoes_credentials(self)
        # IDEA_PASS 是个可辨认的串；服务端回 401 时，stderr 与 stdout 都不含它。
```

跑法：

```bash
IDEA_SKILL_CACHE_DIR="$(mktemp -d)" python3 -m unittest discover -s skills/idea-manager-client/tests -v
```
Expected: FAIL（`fetch.py` 还不存在）。

- [ ] **Step 2: 实现 fetch.py**

`skills/idea-manager-client/scripts/fetch.py`（新建，纯标准库，Python 3.9+）：

- 读环境变量；`IDEA_BASE_URL` 缺失 → 打印 `missing_config: IDEA_BASE_URL is required` 到 stderr，退出 1；
  URL 非法（无 scheme / host）→ `invalid_config: …`，退出 1。
- 认证：`IDEA_USER` 与 `IDEA_PASS` 都非空时发 `Authorization: Basic …`；
  `IDEA_TOKEN` 非空时发 `X-Api-Key`。**凭据不出现在任何输出里。**
- `GET {base}/skill/idea-manager/version` → 清单 JSON。
  - HTTP 401/403 → `auth_error`，退出 1。
  - HTTP 404 → 读响应体的 `code` 字段：`skill_not_distributed` → 同名错误码；
    其他 → `not_found`。退出 1。
  - 连接失败/超时 → `network_error: …`，退出 2。
- 同步：
  - 当前缓存目录 = `<缓存根>/<清单的 version>`；存在且完整 → 直接用。
  - 否则：建 `<缓存根>/.tmp-<version>/`，按 `files` 逐个下载并**逐个校验 sha256**
    （不一致 → 删掉临时目录，打印 `skill_integrity_error: …`，退出 1，**旧版本原样留着**）。
  - 全部校验通过 → 把临时目录 `os.replace` 成 `<缓存根>/<version>`（同目录下的 rename 是原子的）。
  - 旧的版本目录按 mtime 保留最近 2 个，其余删掉。
- 打印：
  - 无参数 → 打印缓存里的 `entry` 文件内容。
  - 相对路径 → 打印缓存里对应的文件；不在清单里 → `not_found`，退出 1。
  - 以 `/` 开头 → 直接 `GET {base}{路径}` 并打印响应体（用于 `/openapi/v1.json`）。
- **绝不在错误信息里回显**：`IDEA_PASS`、`IDEA_TOKEN`、带 userinfo 的 URL、请求头。

- [ ] **Step 3: 跑测试确认通过**

```bash
IDEA_SKILL_CACHE_DIR="$(mktemp -d)" python3 -m unittest discover -s skills/idea-manager-client/tests -v
```
Expected: 全部 PASS。

- [ ] **Step 4: 写薄壳**

`skills/idea-manager-client/SKILL.md`（新建）。frontmatter 与 `skills/idea-manager/SKILL.md` 的 frontmatter
**逐字相同**（当前是 `name: idea-manager` + 那句中文 description）。

正文只允许出现：frontmatter、三种取件用法、`IDEA_*` 环境变量表、取件失败的排查提示。
**不许出现**：任何服务端文件名（`SKILL.md`、`references/…`）、任何 `/api/…` 路径、任何子命令名。

正文骨架：

```markdown
# idea-manager（薄壳 · 说明书在服务端）

本 skill 的说明书由 idea 服务端分发，**不在本目录里**。本目录只有两个文件：
本文件（指针）与 `scripts/fetch.py`（取件器）。

## 第一步：取说明书

    scripts/fetch.py                      # 索引页：有哪些能力、怎么调、细节去哪找
    scripts/fetch.py <路径>               # 按索引页里的路由表取某一份细节
    scripts/fetch.py /openapi/v1.json     # 完整接口契约

## 第二步：按索引页给的姿势调 API

索引页里有 curl 模板与能力清单（能力 → 方法 + 路径）。契约在 `/openapi/v1.json`。

## 连接配置（只读环境变量）

| 变量 | 必填 | 说明 |
|---|---|---|
| `IDEA_BASE_URL` | 是 | 实例地址，如 `https://ideas.example.com` |
| `IDEA_USER` | 否 | nginx basic auth 用户名（与密码同时存在时才发） |
| `IDEA_PASS` | 否 | nginx basic auth 密码 |
| `IDEA_TOKEN` | 否 | 可选。存在时额外发 `X-Api-Key` 头 |
| `IDEA_SKILL_CACHE_DIR` | 否 | 缓存根目录（默认 `${XDG_CACHE_HOME:-~/.cache}/idea-skill`） |

## 取件失败怎么查

- `missing_config` / `invalid_config`：`IDEA_BASE_URL` 没设或写错了（要带 scheme）。
- `auth_error`：basic auth 的用户名或密码不对。
- `network_error`：网络不可达或超时。
- `skill_not_distributed`：服务端没有分发 skill 内容（部署漏了 `skill/` 目录）。
- `not_found`：要取的文件不存在。
- `skill_integrity_error`：取到的内容与服务端清单对不上（多半是传输中被改了），重试一次。
- 服务端暂时连不上、但本地有缓存时：stderr 会有一行 `warning:`，这时拿到的是**上一版**手册。
```

- [ ] **Step 5: 写安装脚本**

`skills/install-agent-skill.sh`（新建，bash）：

- 用法：`bash skills/install-agent-skill.sh [目标目录]`；默认目标 `~/.zcode/skills/idea-manager`。
- 幂等：目标目录已存在就覆盖 `SKILL.md` 与 `scripts/fetch.py`。
- 从 `skills/idea-manager-client/` 拷 `SKILL.md` 与 `scripts/fetch.py`（后者 `chmod +x`）。
- 结束时打印：装到了哪里、下一步要设哪几个环境变量。
- 脚本自身不读凭据、不写凭据。

- [ ] **Step 6: 写取件器的测试运行脚本**

`skills/idea-manager-client/scripts/run-tests.sh`（新建，bash）：起一个假的 HTTP 服务端
（用 `python3 -m http.server` 之类的临时目录即可），跑 `python3 -m unittest discover -s skills/idea-manager-client/tests -v`，
用测试的退出码作为脚本退出码。**不复用**旧的 `skills/idea-manager/scripts/run-tests.sh`（那份随 CLI 一起删）。

- [ ] **Step 7: 手工验一次**

```bash
bash skills/install-agent-skill.sh /tmp/idea-skill-test
IDEA_BASE_URL="http://127.0.0.1:5080" /tmp/idea-skill-test/scripts/fetch.py
```
Expected: 打印索引页内容（服务端没起时会打印 `network_error`，那也算符合预期 ——
要真的拿到内容，需要一个跑着的实例）。

- [ ] **Step 8: 提交**

```bash
git add skills/idea-manager-client skills/install-agent-skill.sh
git commit -m "feat(skill): 取件器与薄壳

agent 侧只留两个文件：SKILL.md（frontmatter + 指针）与 scripts/fetch.py（取件器）。
取件器按服务端的清单同步内容，逐文件校验 sha256 后整体切换缓存 ——
同步中途失败时旧版本原样留着，不会出现半新半旧。

凭据只从环境变量读，错误信息一律不回显。"
```

---

### Task 3: webhook 凭据默认遮蔽

**Files:**
- Create: `server/IdeaApp/Infrastructure/WebhookMasking.cs`
- Modify: `server/IdeaApp/Endpoints/WebhookEndpoints.cs`
- Modify: `server/IdeaApp.Tests/WebhookApiTests.cs`
- Modify: `web/src/components/WebhookManager.vue`
- Modify: `web/src/pages/WebhooksPage.spec.ts`
- Modify: `openapi/idea-v1.json`（重新导出）

**Interfaces:**
- Consumes: 无。

- Produces:
  - `WebhookMasking`（`server/IdeaApp/Infrastructure/WebhookMasking.cs`）：
    - `public static WebhookDto Mask(WebhookDto webhook)` → 返回新记录：`Url` 走 `MaskUrl`，`Headers` 的每个值换成 `***`（键名保留），其余字段原样。
    - `public static string MaskUrl(string url)` → 保留 `scheme://host[:port]`；路径的**每一段**换成 `***`（空段保留为空，即连续的 `/` 不动）；query、fragment、userinfo 一律丢弃；解析失败返回 `"***"`。
      - 例：`https://api.telegram.org/bot123:ABC/sendMessage?x=1` → `https://api.telegram.org/***/***`
  - `GET /api/webhooks` 与 `GET /api/webhooks/{id}`：新增查询参数 `unmask`（bool，缺省 false）。
    - 缺省 → 返回遮蔽版；`?unmask=true` → 返回原值。
  - `POST /api/webhooks` 与 `PATCH /api/webhooks/{id}`：**响应一律遮蔽**（写操作的响应不带完整凭据）。
  - `GET /api/webhooks/templates/telegram`：**不遮蔽**（url 里是 `<BOT_TOKEN>` 占位符，不是凭据）。
  - `WebhookDto` 的形状**不变**（`Url` 与 `Headers` 仍是字符串 / 字典，只是值不同）。

- [ ] **Step 1: 写失败的测试**

`server/IdeaApp.Tests/WebhookApiTests.cs` 追加：

```csharp
[Fact] public async Task List_returns_masked_url_and_header_keys_by_default()
// 建一个 url = "https://api.telegram.org/bot123456:SECRET/sendMessage"、
// headers = { "Authorization": "Bearer SECRET2" } 的全局 webhook；
// GET /api/webhooks → 200；那一项的 url 以 "https://api.telegram.org/" 开头，
// 且**不含** "123456" 与 "SECRET"；headers 的键是 "Authorization"，值是 "***"。

[Fact] public async Task List_returns_the_raw_values_when_unmask_is_true()
// GET /api/webhooks?unmask=true → url 与 headers 的值都是原值。

[Fact] public async Task Get_returns_masked_url_by_default()
// GET /api/webhooks/{id} → 404 之外的情形：url 是遮蔽版。
// GET /api/webhooks/{id}?unmask=true → 原值。

[Fact] public async Task Create_response_is_masked()
// POST /api/webhooks → 201；响应的 url 是遮蔽版（不含 token 片段）。

[Fact] public async Task Update_response_is_masked()
// PATCH /api/webhooks/{id}（改 name）→ 200；响应的 url 是遮蔽版。

[Fact] public async Task Masked_url_keeps_scheme_and_host()
// MaskUrl("https://api.telegram.org/botX/sendMessage") 的结果是
// "https://api.telegram.org/***/***"（段数保留，host 不遮）。
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --filter "FullyQualifiedName~WebhookApiTests"
```
Expected: 新增用例 FAIL（现在返回的是原值）。

- [ ] **Step 3: 实现遮蔽**

`server/IdeaApp/Infrastructure/WebhookMasking.cs`（新建）：

- `MaskUrl`：用 `Uri.TryCreate(url, UriKind.Absolute, out var uri)` 解析；失败返回 `"***"`。
  - 组成 `scheme://host[:port]`（`uri.IsDefaultPort` 时不带端口）。
  - 路径：`uri.AbsolutePath` 按 `/` 切成段，每段非空则换成 `***`，用 `/` 拼回去（保留开头的 `/`）。
  - query / fragment / userinfo 一律不取。
- `Mask`：`webhook with { Url = MaskUrl(webhook.Url), Headers = webhook.Headers.ToDictionary(kv => kv.Key, kv => "***") }`
  （`Headers` 为空字典时返回空字典，不要是 null。）

- [ ] **Step 4: 端点接线**

`server/IdeaApp/Endpoints/WebhookEndpoints.cs`：

- `GET /api/webhooks`：签名加 `bool? unmask`；返回 `service.List(projectId)` 后，
  `unmask == true ? 原值 : 列表里每项都 Mask(...)`。
- `GET /api/webhooks/{id}`：同理加 `bool? unmask`。
- `POST /api/webhooks`：响应对 `service.Create(request)` 的结果调 `Mask(...)`。
- `PATCH /api/webhooks/{id}`：响应对 `service.Update(id, request)` 的结果调 `Mask(...)`。
- `GET /api/webhooks/templates/telegram`：**不动**。

- [ ] **Step 5: 前端跟着改**

`web/src/components/WebhookManager.vue`：

- `load()`：两次 `apiGet` 都要带 `unmask: 'true'`：
  ```ts
  await apiGet<Webhook[]>('/webhooks', { projectId: props.projectId, unmask: 'true' })
  await apiGet<Webhook[]>('/webhooks', { unmask: 'true' })
  ```
  （`apiGet` 的第二参是 `Record<string, string | undefined>` ✓）
- `toggleEnabled()`：PATCH 的响应现在是遮蔽版，不能拿它覆盖列表里的行 ——
  改成 PATCH 成功后 `await load()`（多一次 GET，换来列表状态一致）。

`web/src/pages/WebhooksPage.spec.ts`：

- 改现有的「列出全局作用域的 webhook」用例：断言列表请求的 query 里有 `unmask=true`。
- 改现有的「启用/禁用：PATCH 只带 enabled」用例：断言 PATCH 之后**又发了一次列表请求**
  （即 toggle 之后重新 load）。

- [ ] **Step 6: 跑测试确认通过**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --filter "FullyQualifiedName~WebhookApiTests"
cd web && npm test && npm run typecheck
```
Expected: 全部 PASS。

- [ ] **Step 7: 重新导出契约并跑全量**

```bash
dotnet run --project tools/ExportOpenApi
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj
```
Expected: `openapi/idea-v1.json` 更新（两个 GET 端点多了 `unmask` 参数），全量 PASS。

- [ ] **Step 8: 提交**

```bash
git add server/IdeaApp/Infrastructure/WebhookMasking.cs server/IdeaApp/Endpoints/WebhookEndpoints.cs \
        server/IdeaApp.Tests/WebhookApiTests.cs web/src/components/WebhookManager.vue \
        web/src/pages/WebhooksPage.spec.ts openapi/idea-v1.json
git commit -m "feat(api): webhook 的 url 与 headers 默认遮蔽回传

命令层删掉之后，CLI 的脱敏就没有归处了 —— 裸 curl 会把 Telegram 的 bot token
与 headers 里的 Bearer 令牌原样带进 agent 的上下文与日志。
脱敏搬到服务端：默认返回遮蔽版，要完整值得显式 ?unmask=true（页面用）。
写接口的响应一律遮蔽。

更新接口本来��是三态（url 传 null = 不改），agent 读到遮蔽值也能只改别的东西。"
```

---

### Task 4: 手册重写成 curl 形态，删掉命令层

**Files:**
- Modify: `skills/idea-manager/SKILL.md`
- Create: `skills/idea-manager/references/{calling,errors,webhooks,api,scenarios}.md`
- Delete: `skills/idea-manager/scripts/idea.py`、`skills/idea-manager/scripts/run-tests.sh`、
  `skills/idea-manager/tests/test_cli.py`、`skills/idea-manager/references/openapi.json`
- Modify: `server/IdeaApp.Tests/RequestBodyLimitGuardTests.cs`
- Modify: `server/IdeaApp.Tests/OpenApiDocumentTests.cs`
- Modify: `server/IdeaApp.Tests/RepoPaths.cs`
- Modify: `server/IdeaApp.Tests/PayloadTooLargeTests.cs`
- Modify: `server/IdeaApp/Contracts/ApiErrorCodes.cs`（注释里的指针）

**Interfaces:**
- Consumes: Task 1（服务端会发 `skills/idea-manager/` 下的文件）、Task 3（遮蔽行为已实现）。

- Produces:
  - 内容源目录的**最终形状**：`skills/idea-manager/SKILL.md`（索引页）+ `skills/idea-manager/references/*.md`。
    目录里**不再有** `scripts/`、`tests/`、`references/openapi.json`。
  - `skills/idea-manager/SKILL.md` ≤ 150 行，章节固定为：
    1. `## 这是什么`
    2. `## 连接配置`
    3. `## curl 模板`
    4. `## 能力清单`（表格：能力 → `方法 /api/路径`）
    5. `## 路由表`（要什么 → 读哪份 reference）
    6. `## 最易错的坑`
    7. `## 错误的最短约定`
    8. `## 契约位置`
  - `skills/idea-manager/references/errors.md` 里有两个固定标题（守卫按它们定位表格）：
    - `## 服务端错误码` —— 表一的 code 集合必须**恰好**等于 `ApiErrorCodes.All` 的 code 集合。
    - `## 取件器的错误码` —— 取件器自己的码（`missing_config` / `invalid_config` /
      `network_error` / `auth_error` / `not_found` / `skill_integrity_error`）。
  - `RepoPaths` 新增：`SkillDocs`（`skills/idea-manager`）、`SkillIndex`（`skills/idea-manager/SKILL.md`）、
    `SkillReferences`（`skills/idea-manager/references`）、`SkillErrorsDoc`
    （`skills/idea-manager/references/errors.md`）、`SkillShell`（`skills/idea-manager-client/SKILL.md`）。
    **删掉** `SkillContract`。

- [ ] **Step 1: 搬内容**

把现有 `skills/idea-manager/SKILL.md` 的内容按下面的去处拆开（内容尽量逐字保留，
措辞可以改成 curl 口径）：

| 现有章节 | 去处 |
|---|---|
| frontmatter | `skills/idea-manager/SKILL.md` 的 frontmatter（逐字保留，薄壳要跟它一致） |
| 能力清单（子命令表） | 改成「能力 → 方法 + 路径」表，留在索引页 |
| 各命令的参数 | `references/commands` 的内容并入 `references/calling.md`（按端点组织，不是按子命令） |
| 典型场景 | `references/scenarios.md`（curl 版） |
| 输出 / 脱敏规则 | `references/output` 的内容并入 `references/webhooks.md`（脱敏现在是服务端做的，改写成「默认遮蔽 + unmask」） |
| 错误处理 + 413 | `references/errors.md`（**413 那两段原文一字不改地搬过去** —— 有守卫盯着） |
| 错误码对照表 | `references/errors.md` 的 `## 服务端错误码` 表 |
| 8 种事件类型 | `references/webhooks.md` |
| 完整接口契约 | `references/api.md` |

**必须搬过去的具体内容**（漏了守卫会红）：

- 413 的两段：nginx 的 4 MiB（4194304 字节）与 Kestrel 的 30,000,000 字节，
  以及「经 nginx 回结构化 JSON（**没有 code 字段**）／直连 Kestrel 回空响应体」那两段。
- 错误码对照表里的**全部** code（`ApiErrorCodes.All` 的每一条都要在表里）。

- [ ] **Step 2: 写索引页**

`skills/idea-manager/SKILL.md`（改写，≤150 行）：

- `## 这是什么`：一句话 + 「本文件由服务端分发，内容与服务端同版本」。
- `## 连接配置`：四个 `IDEA_*` 变量。
- `## curl 模板`：三个能照抄的样板（GET / POST / PATCH），POST 与 PATCH 用 `--data-binary @body.json`。
- `## 能力清单`：表格，一行一个能力，列是「能力 | 方法 + 路径」。
  **必须覆盖契约里的每一个 `/api/**` 路径**（逐条核对 `openapi/idea-v1.json` 的 `paths` 段，
  排除 `/skill/**`；每个路径写全它的每个方法）。
- `## 路由表`：五行的「要什么 → 读哪份」。
- `## 最易错的坑`：六条 —
  1. 覆盖式设标签是 `PUT /api/ideas/{id}/tags`；并集追加是 `POST /api/ideas/bulk/tags`
     （但它给 `replace: true` 时也是覆盖式 —— `tags` 漏了就把标签清空）。
  2. `PATCH` 只发要改的字段：`null` = 不改；空串 / 空数组**常常**是「真的清空」——
     但想法的 `content` 与项目的 `name` 给空串是 400，项目的 `name` 还**必填**。
  3. `POST /api/webhooks` 必须给 body 模板；`scope=global` 不能带 `projectId`，
     `scope=project` 必须带一个存在的项目 id。
  4. `POST /api/ideas/merge` 会**硬删**源想法（不在回收站里）。
  5. webhook 的 url 与 headers 默认是**遮蔽版**，拿到的不是原值。
  6. `done` **必填**（`POST /api/ideas/{id}/complete` 与 `POST /api/ideas/bulk/complete`）：
     不给 = `false` = **取消完成**，服务端不报错。
- `## 错误的最短约定`：4xx/5xx 回 ProblemDetails，带机器可读的 `code`；按 `code` 分支，别匹配 `detail` 文字。
- `## 契约位置`：`/openapi/v1.json`（取法：`scripts/fetch.py /openapi/v1.json`）。

- [ ] **Step 3: 写五份 reference**

- `references/calling.md`：认证怎么带（有凭据才加 `-u "$IDEA_USER:$IDEA_PASS"`；
  `IDEA_TOKEN` 有值时加 `-H "X-Api-Key: $IDEA_TOKEN"`；**别把凭据写进 URL**）；
  `Content-Type: application/json` 什么时候要带；请求体怎么写（`--data-binary @body.json`
  与 quoted heredoc 两种）；特殊字符（引号 / 换行 / 反斜杠 / `<b>`）怎么原样送达；
  GET / POST / PATCH / DELETE 的样板；怎么读响应（裸数组 vs 对象；204 没有响应体）。
- `references/errors.md`：ProblemDetails 的形状；`## 服务端错误码` 表（**全集**）；
  `## 取件器的错误码` 表；413 的两段（原文搬运）；「不要去匹配 detail 文字」。
- `references/webhooks.md`：遮蔽规则与 `?unmask=true`；scope 与 projectId 的对齐；
  body 模板必须给；secret 三态（`null` 不改 / `""` 清空 / 非空替换）；8 种事件类型；
  headers 只在创建/更新时给。
- `references/api.md`：契约在 `/openapi/v1.json`；索引页没列的那些端点（设置项、项目重排、
  webhook 详情与投递记录、手动重投、SSE）逐条列出方法 + 路径；契约里每个路径的完整参数与 schema 在契约文件里。
- `references/scenarios.md`：至少五个能照抄的完整例子（记一条想法、查未完成、按标签筛、
  批量标记完成、合并几条、恢复误删的、配一个 Telegram webhook），每个都是一段完整的 curl。

- [ ] **Step 4: 删掉命令层与契约副本**

```bash
git rm skills/idea-manager/scripts/idea.py skills/idea-manager/scripts/run-tests.sh \
       skills/idea-manager/tests/test_cli.py skills/idea-manager/references/openapi.json
```
（`scripts/` 与 `tests/` 目录随之消失；`skills/idea-manager/scripts/__pycache__/` 是未跟踪的残留，
一并删掉，但**不要**提交它。）

- [ ] **Step 5: 改守卫的读取路径**

`server/IdeaApp.Tests/RepoPaths.cs`：删 `SkillContract`，加 `SkillDocs` / `SkillIndex` /
`SkillReferences` / `SkillErrorsDoc` / `SkillShell`（见 Interfaces）。

`server/IdeaApp.Tests/RequestBodyLimitGuardTests.cs`：
`SkillDocPath` 改成 `RepoPaths.SkillErrorsDoc`（413 的说明搬到 `references/errors.md` 了）。
方法名与注释里提到「SKILL.md」的地方一并改成「references/errors.md」。

`server/IdeaApp.Tests/OpenApiDocumentTests.cs`：删掉
`The_skill_copy_of_the_contract_is_byte_for_byte_the_same_file` 整个用例
（它守的副本已经不存在了）与它用到的 `SkillContractPath()`。

`server/IdeaApp.Tests/PayloadTooLargeTests.cs` 与
`server/IdeaApp/Contracts/ApiErrorCodes.cs`：注释里指向
`skills/idea-manager/SKILL.md` 的指针改成 `skills/idea-manager/references/errors.md`。

- [ ] **Step 6: 跑测试确认通过**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj
```
Expected: 全量 PASS（413 守卫从 `references/errors.md` 里读到了那两段数字）。
**注意**：`RequestBodyLimitGuardTests` 里如果还有别的用例依赖 SKILL.md 的结构，一并改到新位置。

- [ ] **Step 7: 提交**

```bash
git add skills/idea-manager server/IdeaApp.Tests server/IdeaApp/Contracts/ApiErrorCodes.cs
git commit -m "refactor(skill): 手册改成 curl 形态并删掉命令层

命令层重复实现了契约里已有的信息（25 个子命令的参数与语义），
两份会漂 —— 那是整个 skill 里最大的一块维护面。
agent 现在用 curl + 服务端实时发的 /openapi/v1.json 直接调 API。

手册按渐进式披露拆开：索引页（能力清单 + 路由表 + 最易错的坑 + curl 模板）
与 references/ 五份细节。契约副本删掉，改指向实时契约。"
```

---

### Task 5: 文档守卫

**Files:**
- Create: `server/IdeaApp.Tests/SkillDocGuardTests.cs`
- Modify: `server/IdeaApp.Tests/RepoPaths.cs`（若 Task 4 已加齐就不用改）

**Interfaces:**
- Consumes: Task 4 的手册形状与 Task 1 的契约文件。

- Produces: 一组把「手册 ↔ 契约 ↔ 错误码 ↔ 薄壳」钉死的守卫测试。

- [ ] **Step 1: 写守卫测试**

`server/IdeaApp.Tests/SkillDocGuardTests.cs`（新建）：

```csharp
[Fact] public void Manual_mentions_every_api_path_in_the_contract()
// 读 openapi/idea-v1.json 的 paths（排除 /skill/ 与 /openapi/ 前缀）；
// 语料 = 索引页 + references/*.md 的全文；
// 对每个 (方法, 路径)，语料里必须出现 "方法 /路径" 这个 token。

[Fact] public void Every_api_path_in_the_manual_exists_in_the_contract()
// 扫语料里的 token：正则 (?<method>GET|POST|PUT|PATCH|DELETE)\s+(?<path>/api/[^\s`）)、，,。]+)
// 每个 (方法, 路径) 都必须在契约里存在。

[Fact] public void Manual_error_code_table_matches_the_registry()
// errors.md 的「## 服务端错误码」表里**表格行**的 code 集合
// 必须恰好等于 ApiErrorCodes.All 的 code 集合（不多不少）。
// internal_error 是服务端兜底码（不在 ApiErrorCodes.All 里），在正文里说明即可，
// 不要放进这张表的表格行 —— 否则集合对不上。

[Fact] public void Shell_frontmatter_matches_the_content_source()
// 薄壳与索引页的 frontmatter 块逐字相同。

[Fact] public void Shell_body_contains_no_server_file_names()
// 薄壳 frontmatter 之后的正文里不出现三个串：
//   "/api/"（API 路径）、"references/"（服务端的目录名）、"SKILL.md"（服务端的入口文件名）。
// 薄壳只许说「本文件」与「scripts/fetch.py」—— 服务端有哪些文件由服务端自己说。

[Fact] public void Index_page_is_at_most_150_lines()
// 索引页行数 ≤ 150。

[Fact] public void Skill_directory_has_no_contract_copy()
// skills/idea-manager/references/openapi.json 不存在。

[Fact] public void Content_source_contains_only_the_shipped_files()
// skills/idea-manager 的直接子项只允许「SKILL.md」与「references」目录；
// references 下的文件只允许 .md。不允许 scripts/、tests/ 或任何非 .md 文件。

[Fact] public void Manual_does_not_hardcode_the_path_count()
// 语料里不出现 \d+\s*条路径 这类数字。

[Fact] public void Manual_shows_how_to_send_special_characters()
// 语料里含 "--data-binary @"（手册必须给出能原样送达特殊字符的写法）。
```

- [ ] **Step 2: 跑测试确认通过（或按失败项补手册）**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --filter "FullyQualifiedName~SkillDocGuardTests"
```
Expected: PASS。若某条红，**改手册**（不是改守卫的判定口径）——
守卫的判定口径就是手册该满足的契约。

- [ ] **Step 3: 确认守卫真的会红**

故意把索引页里某个能力的方法改错（例如 `POST /api/ideas` 写成 `POST /api/idea`），
跑一次上面那条命令，确认 `Every_api_path_in_the_manual_exists_in_the_contract` 变红；
然后改回来。**这一步是验证守卫有效，不能跳过。**

- [ ] **Step 4: 提交**

```bash
git add server/IdeaApp.Tests/SkillDocGuardTests.cs server/IdeaApp.Tests/RepoPaths.cs
git commit -m "test(skill): 加手册与契约/端点/错误码的一致性守卫

内容从服务端来之后，漂移不会自己暴露 —— 手册写错了也不会有编译错误。
这几条守卫把「手册提到的路径必须存在」「存在的端点必须被提到」
「错误码表必须等于 ApiErrorCodes」「薄壳不许出现服务端的东西」钉死。"
```

---

### Task 6: 文档收尾与全量验证

**Files:**
- Modify: `README.md`
- Modify: `docs/nginx/idea.conf`（注释）
- Modify: `docs/superpowers/specs/2026-11-11-idea-manager-design.md`（§17 的交付物描述）

**Interfaces:**
- Consumes: Task 1~5 的全部产出。

- Produces: 与新形态一致的文档。

- [ ] **Step 1: 改 README**

- 「目录结构」一节：把 `skills/idea-manager/  agent skill（说明书 + 命令行封装 + 契约副本）`
  改成 `skills/idea-manager/  agent skill 的内容源（索引页 + references，服务端分发）`，
  并加上 `skills/idea-manager-client/  agent 侧安装的薄壳（指针 + 取件器）`。
- 「配置项」表：加一行 `Skill:Path`（默认 `skill/idea-manager`，相对路径按应用目录解析）。
- 「Agent skill」一节整节重写：
  - 交付形态：服务端持有权威内容，agent 侧只有薄壳。
  - 安装：`bash skills/install-agent-skill.sh`（不再是软链 `skills/idea-manager/`）。
  - 取件：`scripts/fetch.py` 的三种用法。
  - 调用：curl + 契约，不再有命令行封装。
  - 端到端测试：`bash skills/idea-manager-client/scripts/run-tests.sh`。
- 删掉 README 里所有 `scripts/idea.py` 的命令示例，换成 curl 示例。

- [ ] **Step 2: 改 nginx 注释与总设计 §17**

`docs/nginx/idea.conf`：注释里「改这个数字前先看 skills/idea-manager/SKILL.md 的「HTTP 413」说明」
改成 `skills/idea-manager/references/errors.md`。

`docs/superpowers/specs/2026-11-11-idea-manager-design.md` §17「Agent Skill 交付物」：
改成指向本文档（`docs/superpowers/specs/2026-11-13-skill-distribution-design.md`），
并说明交付物变成「内容源（服务端分发）+ 薄壳（agent 侧）」。

- [ ] **Step 3: 全量验证**

```bash
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj
cd web && npm test && npm run typecheck && npm run build && cd ..
bash server/scripts/aot-smoke.sh
bash skills/idea-manager-client/scripts/run-tests.sh
```
Expected: 全部通过；`npm run build` 的产物落在 `server/IdeaApp/wwwroot/`（被 gitignore，不提交）。

- [ ] **Step 4: 提交**

```bash
git add README.md docs/nginx/idea.conf docs/superpowers/specs/2026-11-11-idea-manager-design.md
git commit -m "docs: 把文档改成 skill 随服务分发后的形态

安装方式、配置项、agent 调用姿势都变了；nginx 注释与总设计 §17 里
指向 SKILL.md 的指针也一并改到 references/errors.md。"
```

---

## 自查

- **spec 覆盖**：§9.1（端点/清单/错误码/穿越防护）→ Task 1；§9.2（分发副本、Skill:Path）→ Task 1；
  §9.3（契约副本删掉）→ Task 4；§9.4（webhook 遮蔽）→ Task 3；§10（薄壳、取件器、安装）→ Task 2；
  §11（删命令层）→ Task 4；§12（认证）→ Task 2 + Task 4；§13（错误约定）→ Task 2 + Task 4；
  §14（守卫）→ Task 5 + Task 1；§15（迁移顺序与指针清单）→ Task 4 + Task 6；§17（验收）→ 各 task 的验证步骤 + Task 6。
- **类型一致**：`SkillManifestDto.Version/Entry/Files`、`SkillFileDto.Path/Sha256/Size`、
  `SkillCatalog.Root/BuildManifest/TryResolveFile`、`SkillOptions.Path`、
  `WebhookMasking.Mask/MaskUrl` —— 每个名字只在它的产出 task 里定义一次，后续 task 只引用。
- **比例**：计划比 spec 短，代码块只给了 DTO 形状、遮蔽规则与测试断言的关键值，没有抄实现。
