# Idea 管理工具 · 后端实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 .NET 10 Native AOT 实现 idea 记事本的完整后端：REST API、SQLite 存储、事件总线与 SSE 广播、webhook 异步投递与重试、回收站自动清理。

**Architecture:** ASP.NET Core Minimal API 单项目。手写 SQL + `Microsoft.Data.Sqlite`（不用 ORM，避免 AOT 下的反射问题）。领域服务负责业务规则与事务，事件在**事务提交之后**才写入事件总线；事件总线按 handler 扇出，每个 `IEventHandler` 有独立通道；SSE 与 webhook 投递互不阻塞。

**Tech Stack:** .NET 10.0.113 / Native AOT / ASP.NET Core Minimal API / Microsoft.Data.Sqlite 10.0.14 / Microsoft.AspNetCore.OpenApi 10.0.15 / xunit 2.9.3 + Microsoft.NET.Test.Sdk 17.14.0 + Microsoft.AspNetCore.Mvc.Testing 10.0.14

**Spec:** `docs/superpowers/specs/2026-11-11-idea-manager-design.md`

## Global Constraints

- 目标框架 `net10.0`；发布时 `PublishAot=true`、`InvariantGlobalization=true`、`TrimMode=full`。
- **禁止**反射式序列化：所有 JSON 走 `JsonSerializerContext` 源生成。
- **AOT 下端点返回 JSON 的硬性要求**（Task 1 实测踩出来的）：`Results.Ok(匿名对象)` 在 Native AOT 产物下返回 **500**（没有 `JsonTypeInfo` 元数据）。因此：
  - 所有端点返回**静态类型**的 DTO，不返回匿名对象；
  - `IdeaJsonContext` 必须在 `JsonOptions.SerializerOptions.TypeInfoResolverChain` 中注册（Task 3 负责），此后 `Results.Ok(dto)` 对已登记的 DTO 可用；
  - 创建类响应（201）用 `Results.Json(dto, IdeaJsonContext.Default.XxxDto, statusCode: 201)` 显式带 `JsonTypeInfo`；
  - 任何返回 DTO 的端点都要有一个集成测试断言它返回 200/201 而**不是 500**。
- **禁止** `ActivatorUtilities`、动态代理、运行时表达式编译。
- SQL 手写，不引入 EF Core 或任何 ORM。
- 命名空间根：`IdeaApp`（`Domain` / `Application` / `Infrastructure` / `Contracts` / `Endpoints`）。
- 时间戳一律 `DateTimeOffset`，存库转 UTC ISO-8601 字符串（`yyyy-MM-ddTHH:mm:ss.fffffffZ`）。
- 所有实体 id 用 `Guid.CreateVersion7()`，转字符串时用 32 位无连字符小写 hex；内置收件箱 id 固定为 `inbox`。
- API 路径前缀 `/api`；JSON 字段名 camelCase；错误统一 RFC 9457 ProblemDetails + 扩展字段 `code`。
- 列表排序恒为 `updated_at ASC, id ASC`。
- 事件不参与 SQLite 事务：领域服务先提交事务，再发布事件。
- 每个 task 结束提交一次，commit message 用 Conventional Commits（**中文正文，要有 body 说明 why**）。
- 引用类型可空性全部显式标注；`Directory.Build.props` 开启 `Nullable=enable` 与 `TreatWarningsAsErrors`。
- 集成测试里 `WebApplicationFactory` 必须 `using` 释放（`using var factory = new TestAppFactory();`），每个测试用独立临时库。

## Review Focus

以下五类输入/情形最可能咬人，每类都必须在对应 task 的测试里被钉住：

1. **同刻写入的多条想法** —— `updated_at` 完全相同，列表顺序必须稳定（`id ASC`，UUIDv7 字典序 = 插入序），不能随机跳动。
2. **正文含引号、换行、反斜杠、`<` `>`** —— 存 SQLite 不出错；渲染进 webhook JSON 不破坏 JSON；Telegram 纯文本模式原样送达。
3. **跨项目合并** —— 必须拒绝并返回 `merge_cross_project`，绝不能把内容拼到另一个项目下。
4. **删项目前已被单独删除的想法** —— 恢复项目时**不得**复活它，它仍以顶层条目留在回收站。
5. **webhook 目标不可达 / 超时** —— 新增想法的 HTTP 响应绝不能被拖慢，失败必须落死信且可手动重投。

---

## 文件结构

```
IdeaApp.sln
Directory.Build.props
server/
  IdeaApp/
    IdeaApp.csproj
    Program.cs                     # 组合根：DI 注册、管道、端点映射
    appsettings.json
    Domain/
      Idea.cs                      # 领域记录 + 枚举
      Project.cs
      IdeaEvent.cs                 # 事件记录 + EventTypes 常量
    Contracts/
      Dtos.cs                      # 对外 DTO 记录
      JsonContext.cs               # JsonSerializerContext 源生成
      Requests.cs                  # 请求体记录
    Application/
      IdeaService.cs               # 想法领域服务（事务 + 事件发布）
      ProjectService.cs            # 项目领域服务
      TagService.cs                # 标签字典与补全
      WebhookService.cs            # webhook 配置 CRUD
      DeliveryService.cs           # 投递记录查询与手动重投
      TrashService.cs              # 回收站视图与恢复
      IdeaQuery.cs                 # 查询条件与过滤判定
    Infrastructure/
      AppOptions.cs                # 配置绑定
      SqliteDatabase.cs            # 连接工厂 + 初始化
      schema.sql                   # 建表 DDL（嵌入资源）
      IdeaRepository.cs
      ProjectRepository.cs
      TagRepository.cs
      WebhookRepository.cs
      DeliveryRepository.cs
      SettingRepository.cs
      EventBus.cs                  # 扇出
      SseBroadcaster.cs            # SSE 连接管理 + 帧写入
      SseEventHandler.cs           # IEventHandler 实现
      WebhookEventHandler.cs       # IEventHandler 实现
      WebhookDeliveryWorker.cs     # 投递 + 重试
      TrashCleanupWorker.cs        # 定期清理
      TemplateRenderer.cs          # 占位符渲染
      EventPlaceholders.cs         # 事件 → 占位符字典
      HmacSigner.cs
    Endpoints/
      IdeaEndpoints.cs
      ProjectEndpoints.cs
      TagEndpoints.cs
      WebhookEndpoints.cs
      SettingsEndpoints.cs
      EventEndpoints.cs
      HealthEndpoints.cs
  IdeaApp.Tests/
    IdeaApp.Tests.csproj
    TestAppFactory.cs              # WebApplicationFactory + 临时库
    ...
web/                               # 前端（在另一份计划里）
openapi/idea-v1.json
tools/ExportOpenApi/
```

---

### Task 1: 解决方案骨架与健康检查

**Files:**
- Create: `IdeaApp.sln`、`Directory.Build.props`
- Create: `server/IdeaApp/IdeaApp.csproj`、`server/IdeaApp/Program.cs`、`server/IdeaApp/appsettings.json`
- Create: `server/IdeaApp.Tests/IdeaApp.Tests.csproj`、`server/IdeaApp.Tests/HealthTests.cs`
- Create: `server/IdeaApp/Endpoints/HealthEndpoints.cs`

**Interfaces:**
- Consumes: 无
- Produces: `public partial class Program`（供 `WebApplicationFactory<Program>` 使用）；`GET /api/health` → `200 {"status":"ok"}`；`IdeaApp` 项目可 `dotnet run` 启动。

- [ ] **Step 1: 创建解决方案与项目**

```bash
cd /share/rw/repo/server/kanban
dotnet new sln -n IdeaApp
dotnet new web -o server/IdeaApp -n IdeaApp --no-restore
dotnet new xunit -o server/IdeaApp.Tests -n IdeaApp.Tests --no-restore
dotnet sln add server/IdeaApp server/IdeaApp.Tests
dotnet add server/IdeaApp.Tests reference server/IdeaApp
```

`Directory.Build.props` 内容固定为：

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: 配置 csproj**

`server/IdeaApp/IdeaApp.csproj` 增加：

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.14" />
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.15" />
</ItemGroup>
```

`server/IdeaApp.Tests/IdeaApp.Tests.csproj` 增加：

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.14" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />
  <PackageReference Include="xunit" Version="2.9.3" />
  <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
</ItemGroup>
```

- [ ] **Step 3: 写失败测试**

`server/IdeaApp.Tests/HealthTests.cs`：

```csharp
public class HealthTests
{
    [Fact]
    public async Task Health_returns_ok()
    {
        var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }
}
```

`server/IdeaApp.Tests/TestAppFactory.cs`：

```csharp
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
```

- [ ] **Step 4: 运行测试确认失败**

Run: `dotnet test server/IdeaApp.Tests` → Expected: FAIL（找不到 `TestAppFactory` / 端点 404）

- [ ] **Step 5: 实现**

`Program.cs` 用最小 API 组合根，末尾加 `public partial class Program;`。
`Endpoints/HealthEndpoints.cs` 提供 `MapHealthEndpoints(this IEndpointRouteBuilder)`，映射 `GET /api/health` 返回 `Results.Ok(new { status = "ok" })`。

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test server/IdeaApp.Tests` → Expected: PASS

- [ ] **Step 7: 提交**

```bash
git add IdeaApp.sln Directory.Build.props server/
git commit -m "feat(server): 搭建解决方案骨架与健康检查端点"
```

---

### Task 2: SQLite 初始化、schema 与内置收件箱

**Files:**
- Create: `server/IdeaApp/Infrastructure/AppOptions.cs`
- Create: `server/IdeaApp/Infrastructure/SqliteDatabase.cs`
- Create: `server/IdeaApp/Infrastructure/schema.sql`
- Create: `server/IdeaApp.Tests/DatabaseInitTests.cs`
- Modify: `server/IdeaApp/Program.cs`
- Modify: `server/IdeaApp/appsettings.json`
- Modify: `server/IdeaApp/IdeaApp.csproj`（补 `TrimMode` 与运行时包版本）

**Interfaces:**
- Consumes: Task 1 的 `Program`。
- Produces:
  - `public sealed class AppOptions` —— **按配置键的层级用嵌套类**（.NET 的配置绑定器无法把 `Database:Path` 绑到扁平属性 `DatabasePath`）：

    ```csharp
    public sealed class AppOptions
    {
        public DatabaseOptions Database { get; set; } = new();
        public TrashOptions Trash { get; set; } = new();
        public ApiOptions Api { get; set; } = new();
        public WebhookOptions Webhook { get; set; } = new();
        public SseOptions Sse { get; set; } = new();
        public string? PublicBaseUrl { get; set; }
    }
    public sealed class DatabaseOptions { public string Path { get; set; } = "data/idea.db"; }
    public sealed class TrashOptions { public int RetentionDays { get; set; } = 30; }
    public sealed class ApiOptions { public int MaxIdeasPerResponse { get; set; } = 2000; }
    public sealed class WebhookOptions
    {
        public int TimeoutSeconds { get; set; } = 10;
        public int MaxAttempts { get; set; } = 3;
        public int MaxDeliveryRecords { get; set; } = 500;
    }
    public sealed class SseOptions { public int BufferSize { get; set; } = 500; }
    ```

    绑定方式：`builder.Services.Configure<AppOptions>(builder.Configuration)`。
    引用处一律写 `options.Value.Database.Path`、`options.Value.Trash.RetentionDays`、`options.Value.Sse.BufferSize` 这种嵌套形式，**不要写扁平属性名**。

  - `public sealed class SqliteDatabase(string databasePath)`，方法：`SqliteConnection Open()`、`void Initialize()`、`string DatabasePath { get; }`
  - `Open()` 返回的连接已执行 `PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;`
  - DI 注册（Task 15 接线时用，**不能**写 `AddSingleton<SqliteDatabase>()` —— 构造函数要 string，DI 解析不了）：
    ```csharp
    builder.Services.AddSingleton(sp =>
        new SqliteDatabase(sp.GetRequiredService<IOptions<AppOptions>>().Value.Database.Path));
    ```

- [ ] **Step 1: 写失败测试**

`DatabaseInitTests.cs`，断言：

```csharp
[Fact] public void Initialize_creates_all_tables_and_indexes()
// 查询 sqlite_master，断言存在表：project, idea, tag, idea_tag, webhook, webhook_delivery, setting
// 断言存在索引：ix_idea_list, ix_idea_tag_tag, ix_delivery_webhook

[Fact] public void Initialize_inserts_inbox_project()
// SELECT id,name FROM project WHERE id='inbox' → name == "收件箱"

[Fact] public void Initialize_is_idempotent()
// 连续调用 Initialize() 两次不抛异常，收件箱仍只有一条

[Fact] public void Open_enables_wal_and_foreign_keys()
// PRAGMA journal_mode → "wal"；PRAGMA foreign_keys → 1
```

- [ ] **Step 2: 运行测试确认失败** → Run: `dotnet test server/IdeaApp.Tests --filter DatabaseInitTests` → Expected: FAIL（类型不存在）

- [ ] **Step 3: 实现**

`schema.sql` 内容**逐字**采用 spec §9 的 DDL（6 张表 + 全部索引），在 `IdeaApp.csproj` 里以 `<EmbeddedResource Include="Infrastructure/schema.sql" />` 引入。

`SqliteDatabase.Initialize()` 读取嵌入资源、按 `;` 切分语句执行（每条 `CREATE TABLE/INDEX` 都是独立语句，无分号出现在语句内部），随后执行：

```sql
INSERT OR IGNORE INTO project(id, name, sort_order, deleted_at, created_at, updated_at)
VALUES ('inbox', '收件箱', 0, NULL, :now, :now);
```

`server/IdeaApp/appsettings.json` 内容固定为：

```json
{
  "Urls": "http://127.0.0.1:5080",
  "Database": { "Path": "data/idea.db" },
  "Trash": { "RetentionDays": 30 },
  "Api": { "MaxIdeasPerResponse": 2000 },
  "Webhook": { "TimeoutSeconds": 10, "MaxAttempts": 3, "MaxDeliveryRecords": 500 },
  "Sse": { "BufferSize": 500 },
  "PublicBaseUrl": "",
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }
}
```

`SqliteDatabase` 构造时若 `databasePath` 是相对路径，按**当前工作目录**解析；目录不存在要自动创建（`Path.GetDirectoryName` → `Directory.CreateDirectory`）。

**csproj 补两行**（Task 1 评审的 Minor 1 与 2）：

```xml
<TrimMode>full</TrimMode>
<RuntimeFrameworkVersion>10.0.14</RuntimeFrameworkVersion>
```

`RuntimeFrameworkVersion` 是为了让运行时包与 Task 1 已固定的 `KnownILCompilerPack` 10.0.14 同版本（SDK 内置的运行时包是 10.0.13，版本错配会让日后 AOT 下的 SQLite 互操作 / SSE 问题难以归因）。

> 若 `RuntimeFrameworkVersion 10.0.14` 导致 restore 拉不到包（离线环境），改为把 `KnownILCompilerPack` 退回 SDK 内置的 10.0.13，并在 csproj 里对 `Microsoft.DotNet.ILCompiler` 单独抑制 `NU1903`（不要全局关掉 `TreatWarningsAsErrors`）。**选哪种都行，但要在报告里写明选了哪种、为什么。**

- [ ] **Step 4: 运行测试确认通过** → Expected: PASS

- [ ] **Step 5: 提交** → `git commit -m "feat(server): 建立 SQLite schema 与内置收件箱"`

---

### Task 3: 领域模型、对外 DTO 与 JSON 源生成

**Files:**
- Create: `server/IdeaApp/Domain/Idea.cs`、`server/IdeaApp/Domain/Project.cs`
- Create: `server/IdeaApp/Contracts/Dtos.cs`、`server/IdeaApp/Contracts/JsonContext.cs`
- Create: `server/IdeaApp.Tests/JsonSerializationTests.cs`
- Modify: `server/IdeaApp/Endpoints/HealthEndpoints.cs`（把 Task 1 的 `HealthJsonContext` 并入 `IdeaJsonContext`）
- Delete: `server/IdeaApp/Endpoints/HealthJsonContext.cs`（Task 1 建的临时上下文，合并后删掉）

**Interfaces:**
- Produces（**后续所有 task 都依赖这些类型，名字不得改**）:

```csharp
// Domain/Idea.cs
public sealed record Idea(string Id, string ProjectId, string? Title, string Content,
                          IReadOnlyList<string> Tags, bool Done,
                          DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                          DateTimeOffset? DeletedAt);

public enum IdeaStatusFilter { All, Open, Done }
public enum TagMatchMode { Or, And }

// Domain/Project.cs
public sealed record Project(string Id, string Name, int SortOrder,
                             DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                             DateTimeOffset? DeletedAt);

// Contracts/Dtos.cs
public sealed record IdeaDto(string Id, string ProjectId, string? Title, string Content,
                             string[] Tags, bool Done,
                             DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ProjectDto(string Id, string Name, int SortOrder, int IdeaCount,
                                 DateTimeOffset? LastActivityAt,
                                 DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TagDto(string Name, int Count);
public sealed record WebhookDto(string Id, string Name, string Url, string Method,
                                Dictionary<string, string> Headers, string BodyTemplate,
                                string[] Events, string Scope, string? ProjectId,
                                bool Enabled, bool HasSecret,
                                DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DeliveryDto(string Id, string WebhookId, string EventId, string EventType,
                                 string Payload, string Status, int Attempts, string? LastError,
                                 DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TrashEntryDto(string Kind, ProjectDto? Project, IdeaDto[] Ideas, IdeaDto? Idea);

// Contracts/JsonContext.cs
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IdeaDto))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(TagDto))]
[JsonSerializable(typeof(WebhookDto))]
[JsonSerializable(typeof(DeliveryDto))]
[JsonSerializable(typeof(TrashEntryDto))]
[JsonSerializable(typeof(IdeaDto[]))]
[JsonSerializable(typeof(ProjectDto[]))]
[JsonSerializable(typeof(TagDto[]))]
[JsonSerializable(typeof(DeliveryDto[]))]
[JsonSerializable(typeof(TrashEntryDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ProblemDetails))]
public sealed partial class IdeaJsonContext : JsonSerializerContext;
```

- [ ] **Step 1: 写失败测试**

`server/IdeaApp.Tests/JsonSerializationTests.cs`：

```csharp
[Fact] public void IdeaDto_serializes_with_camel_case_and_iso_timestamps()
// 序列化一个 IdeaDto，断言 JSON 含 "projectId"、"createdAt"，且时间为 "2026-11-11T09:00:00.0000000+00:00" 形式（DateTimeOffset 默认格式即可）

[Fact] public void IdeaDto_tags_is_empty_array_not_null()
// Tags = [] → JSON 含 "tags":[]

[Fact] public void WebhookDto_omits_secret_value()
// WebhookDto 只含 HasSecret 布尔，不含 secret 明文字段
```

再加一个 **AOT 回归测试**（Task 1 踩出来的坑，必须钉住）：

```csharp
[Fact] public async Task Health_endpoint_returns_200_not_500_under_aot()
// 复用 Task 1 的 /api/health，但断言的是：响应 200 + 用 JsonDocument.Parse 能解析出根对象的 status == "ok"
// 目的：任何返回 DTO 的端点一旦退回匿名对象或漏注册 resolver chain，AOT 下会变成 500
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 按上面的签名逐字建类型，JsonContext 用 `JsonSerializerContext` 源生成。

  另外三件必须做的事：

  1. **把 Task 1 的 `HealthResponse` 挪进 `Contracts/Dtos.cs`，把 `HealthJsonContext` 并进 `IdeaJsonContext`**，删掉 Task 1 建的独立 `HealthJsonContext` 文件，`HealthEndpoints.cs` 改为用 `IdeaJsonContext.Default.HealthResponse`。全项目只留**一个**源生成上下文。
  2. **在组合根把 `IdeaJsonContext` 注册进 JSON 解析链**，否则 `Results.Ok(dto)` 在 AOT 下是 500：
     ```csharp
     builder.Services.ConfigureHttpJsonOptions(o =>
         o.SerializerOptions.TypeInfoResolverChain.Insert(0, IdeaJsonContext.Default));
     ```
  3. `IdeaJsonContext` 上加 `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]`，并把 `HealthResponse` 也登记进去。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 定义领域模型、对外 DTO 与 JSON 源生成上下文"`

---

### Task 4: 项目仓储与项目 API

**Files:**
- Create: `server/IdeaApp/Infrastructure/ProjectRepository.cs`
- Create: `server/IdeaApp/Application/ProjectService.cs`
- Create: `server/IdeaApp/Application/ApiException.cs`
- Create: `server/IdeaApp/Endpoints/ProjectEndpoints.cs`
- Create: `server/IdeaApp.Tests/ProjectApiTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SqliteDatabase`、Task 3 的 DTO/枚举。
- Produces:
  - `public sealed class ApiException(int status, string code, string detail) : Exception` —— `Status`、`Code`、`Detail` 三个只读属性。端点层统一 catch 并转成 ProblemDetails：`new ProblemDetails { Status = ex.Status, Title = "请求失败", Detail = ex.Detail }` 并在 `Extensions["code"] = ex.Code`，`Content-Type: application/problem+json`。**所有 4xx 业务错误都必须通过 ApiException 抛出**，不手写 ProblemDetails。
  - `public sealed class ProjectRepository(SqliteDatabase db)`
    - `IReadOnlyList<Project> ListActive()`
    - `Project? Get(string id)`
    - `Project Insert(string name, int sortOrder)`
    - `Project Rename(string id, string name)`
    - `void SoftDelete(string id, DateTimeOffset at)`
    - `void Restore(string id)`
    - `void Reorder(IReadOnlyList<string> ids)`
    - `DateTimeOffset? LastActivityAt(string projectId)`
    - `int CountActiveIdeas(string projectId)`
  - `public sealed class ProjectService(ProjectRepository repo)`：`IReadOnlyList<ProjectDto> List()`、`ProjectDto Create(string name)`、`ProjectDto Rename(string id, string name)`、`void Delete(string id)`、`ProjectDto Restore(string id)`、`IReadOnlyList<ProjectDto> Reorder(IReadOnlyList<string> ids)`
  - `public static class ProjectEndpoints { public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app); }`
  - 端点：`GET /api/projects`、`POST /api/projects`、`PATCH /api/projects/{id}`、`DELETE /api/projects/{id}`、`POST /api/projects/{id}/restore`、`POST /api/projects/reorder`

- [ ] **Step 1: 写失败测试**

`ProjectApiTests.cs`（用 `TestAppFactory`，每个测试独立临时库）：

```csharp
[Fact] public async Task Create_project_returns_201_and_persists()
// POST /api/projects {"name":"项目A"} → 201，body.name == "项目A"，id 非空且为 32 位小写 hex

[Fact] public async Task List_projects_includes_inbox_and_active_projects()
// 新建两个项目后 GET /api/projects → 含 inbox + 2 个，inbox 排在最前（sortOrder 最小）

[Fact] public async Task Rename_project_updates_name()
// PATCH /api/projects/{id} {"name":"改名"} → 200，GET 列表里是新名

[Fact] public async Task Delete_project_soft_deletes_and_hides_from_list()
// DELETE → 204；GET /api/projects 不含它；GET /api/projects/{id}… （无单查端点，改为直接查库）

[Fact] public async Task Delete_inbox_returns_400_with_code_inbox_not_deletable
// DELETE /api/projects/inbox → 400，ProblemDetails.extensions["code"] == "inbox_not_deletable"

[Fact] public async Task Reorder_persists_sort_order
// POST /api/projects/reorder {"ids":["<b>","<a>","inbox"]} → 200 且返回顺序与 ids 一致

[Fact] public async Task Rename_unknown_project_returns_404_with_code_project_not_found
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL（404）
- [ ] **Step 3: 实现**
  - 仓储用 `SqliteDatabase.Open()` 逐连接开闭；`ProjectRepository` 不自行开事务。
  - `ProjectService.Delete`：`id == "inbox"` 直接抛 `ApiException(400, "inbox_not_deletable", ...)`；否则 `SoftDelete`。
  - 排序：`List()` 按 `sortOrder ASC, id ASC`；`Reorder` 把 ids 按给定顺序写入 `sort_order = 索引`，未列出的项目排在其后（`sort_order = 现有最大值 + 1` 递增）。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现项目仓储与项目 API"`

---

### Task 5: 想法仓储与想法 CRUD API

**Files:**
- Create: `server/IdeaApp/Infrastructure/IdeaRepository.cs`
- Create: `server/IdeaApp/Application/IdeaService.cs`
- Create: `server/IdeaApp/Endpoints/IdeaEndpoints.cs`
- Create: `server/IdeaApp.Tests/IdeaApiTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `Idea`/`IdeaDto`。
- Produces:
  - `public sealed class IdeaRepository(SqliteDatabase db)`
    - `Idea? Get(string id)`
    - `Idea Insert(Idea idea)`
    - `Idea UpdateContent(string id, string? title, string content)`
    - `Idea SetDone(string id, bool done)`
    - `void SoftDelete(string id, DateTimeOffset at)`
    - `void Restore(string id)`
    - `void HardDelete(string id)`
    - `IReadOnlyList<string> GetTagNames(string ideaId)`
    - `void ReplaceTags(string ideaId, IReadOnlyList<string> tags)`
  - `public sealed class IdeaService(IdeaRepository repo, ProjectRepository projects)`：
    - `IdeaDto Create(string projectId, string content, string? title, IReadOnlyList<string> tags)`
    - `IdeaDto Update(string id, string? title, string? content)`
    - `IdeaDto SetTags(string id, IReadOnlyList<string> tags)`
    - `IdeaDto SetDone(string id, bool done)`
    - `void Delete(string id)`
    - `IdeaDto Restore(string id)`
  - `public static class IdeaEndpoints { public static IEndpointRouteBuilder MapIdeaEndpoints(this IEndpointRouteBuilder app); }`
  - 端点：`POST /api/ideas`、`PATCH /api/ideas/{id}`、`PUT /api/ideas/{id}/tags`、`POST /api/ideas/{id}/complete`、`DELETE /api/ideas/{id}`、`POST /api/ideas/{id}/restore`

- [ ] **Step 1: 写失败测试**

`IdeaApiTests.cs`：

```csharp
[Fact] public async Task Create_idea_defaults_to_inbox_and_returns_201()
// POST /api/ideas {"content":"第一条"} → 201，projectId == "inbox"，tags == []

[Fact] public async Task Create_idea_with_empty_content_returns_400_code_invalid_content
// content 为 "" 或全空白 → 400，code == "invalid_content"

[Fact] public async Task Create_idea_with_unknown_project_returns_404_code_project_not_found

[Fact] public async Task Update_idea_bumps_updated_at_and_moves_to_end_of_list
// 建 A、B 两条；改 A；GET /api/ideas 返回顺序应为 [B, A]（A updated_at 更新 → 靠下）

[Fact] public async Task Same_timestamp_ideas_keep_insertion_order
// 直接往库里插两条 updated_at 完全相同的 idea → GET /api/ideas 返回顺序按 id ASC（后插入的在下）

[Fact] public async Task SetTags_replaces_existing_tags_case_insensitively
// PUT /api/ideas/{id}/tags {"tags":["A","a","B"]} → tags == ["A","B"]（保留首次出现的大小写）

[Fact] public async Task SetDone_marks_done_and_bumps_updated_at

[Fact] public async Task Delete_idea_soft_deletes_it
// DELETE → 204；GET /api/ideas 不含它

[Fact] public async Task Restore_idea_brings_it_back_and_puts_it_at_end

[Fact] public async Task Idea_content_with_quotes_newlines_is_stored_verbatim
// content 含 "引号"、\n、反斜杠、<b> → GET 返回内容逐字相同

[Fact] public async Task Update_unknown_idea_returns_404_code_idea_not_found
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `IdeaService` 里所有写操作包在一个 SQLite 事务：更新 idea 行 → 写 `idea_tag` → 提交。
  - `Update`：`title` 传 null 表示不改标题，传字符串（含空串）表示改为空标题；`content` 为 null 表示不改内容。
  - `SetTags`：按大小写不敏感去重，保留首次出现的原始写法；复用/新建 `tag` 行。
  - 所有写操作把 `updated_at` 设为当前 UTC。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现想法仓储与想法 CRUD API"`

---

### Task 6: 标签字典与列表过滤

**Files:**
- Create: `server/IdeaApp/Infrastructure/TagRepository.cs`
- Create: `server/IdeaApp/Application/TagService.cs`
- Create: `server/IdeaApp/Application/IdeaQuery.cs`
- Modify: `server/IdeaApp/Infrastructure/IdeaRepository.cs`（加 `List(IdeaQuery)`）
- Modify: `server/IdeaApp/Endpoints/IdeaEndpoints.cs`（加 `GET /api/ideas`）
- Create: `server/IdeaApp/Endpoints/TagEndpoints.cs`
- Create: `server/IdeaApp.Tests/FilteringTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `IdeaRepository`。
- Produces:
  - `public sealed record IdeaQuery(string? ProjectId, IdeaStatusFilter Status, IReadOnlyList<string> Tags, TagMatchMode TagMode)`
  - `public static class IdeaFilter { public static bool Matches(Idea idea, IdeaQuery q); }`
  - `public sealed class TagRepository(SqliteDatabase db)`：`IReadOnlyList<TagDto> ListWithCounts()`、`IReadOnlyList<string> AllNames()`、`IReadOnlyList<string> NamesForProject(string projectId)`
  - `public sealed class TagService(TagRepository repo)`：`IReadOnlyList<TagDto> List()`、`IReadOnlyList<string> Suggest(string? projectId, string prefix)`
  - 端点：`GET /api/ideas?projectId=&status=&tags=&tagMode=`、`GET /api/tags`、`GET /api/tags/suggest?projectId=&prefix=`

- [ ] **Step 1: 写失败测试**

`FilteringTests.cs`：

```csharp
[Fact] public void Matches_or_mode_returns_ideas_sharing_any_tag()
// idea 标签 {a,b}，q.Tags={b,c}, TagMode=Or → true

[Fact] public void Matches_and_mode_requires_all_selected_tags
// idea 标签 {a,b}，q.Tags={a,b,c}, TagMode=And → false；q.Tags={a,b} → true

[Fact] public void Matches_status_open_excludes_done
// done=true 的 idea，Status=Open → false

[Fact] public void Matches_project_filters_by_project_id

[Fact] public async Task Get_ideas_returns_bare_array_sorted_by_updated_at_asc
// 建 3 条（间隔足够时间），GET /api/ideas → JSON 是数组，最早的在前

[Fact] public async Task Get_ideas_tag_mode_query_param_is_honoured
// ?tags=a,b&tagMode=and 与 ?tagMode=or 结果不同

[Fact] public async Task Get_tags_returns_dictionary_with_counts

[Fact] public async Task Get_tags_suggest_prefers_current_project
// ?projectId=X&prefix=a → 先列项目 X 的标签，再列全局其它
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `IdeaFilter.Matches` 是纯函数，先判 project，再判 status，再判标签。标签比较用 `StringComparer.OrdinalIgnoreCase`。
  - `IdeaRepository.List(query)` 一次 SQL 取候选集（project + deleted_at IS NULL + status），再在内存里用 `IdeaFilter.Matches` 判标签，最后按 `updated_at ASC, id ASC` 排序。
  - `GET /api/ideas` 的结果条数超过 `AppOptions.Api.MaxIdeasPerResponse` 时返回 **400**，ProblemDetails 的 `code` 为 `too_many_ideas`，`detail` 说明命中了多少条、建议收窄条件。**不静默截断。**
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现标签字典与列表过滤"`

---

### Task 7: 批量操作与合并

**Files:**
- Create: `server/IdeaApp/Endpoints/BulkIdeaEndpoints.cs`
- Modify: `server/IdeaApp/Application/IdeaService.cs`（加批量与合并方法）
- Create: `server/IdeaApp.Tests/BulkAndMergeTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `IdeaService`、`IdeaRepository`。
- Produces:
  - `IdeaService` 新增：`IReadOnlyList<IdeaDto> BulkSetDone(IReadOnlyList<string> ids, bool done)`、`IReadOnlyList<IdeaDto> BulkSetTags(IReadOnlyList<string> ids, IReadOnlyList<string> tags, bool replace)`、`void BulkDelete(IReadOnlyList<string> ids)`、`IdeaDto Merge(IReadOnlyList<string> ids, string? content, string? title)`
  - 端点：`POST /api/ideas/bulk/complete`（body `{ids[], done}`）、`POST /api/ideas/bulk/tags`（body `{ids[], tags[], replace?}`）、`POST /api/ideas/bulk/delete`（body `{ids[]}`）、`POST /api/ideas/merge`（body `{ids[], content?, title?}`）

- [ ] **Step 1: 写失败测试**

`BulkAndMergeTests.cs`：

```csharp
[Fact] public async Task Bulk_complete_marks_all_and_bumps_updated_at

[Fact] public async Task Bulk_tags_appends_by_default_and_replaces_when_flagged

[Fact] public async Task Bulk_delete_soft_deletes_all

[Fact] public async Task Merge_creates_new_idea_with_concatenated_content
// 三条 content "A"、"B"、"C" → 合并结果 content == "A\n\nB\n\nC"（按 updated_at 升序）

[Fact] public async Task Merge_result_takes_union_of_tags
// {a} {b} {a} → ["a","b"]

[Fact] public async Task Merge_result_takes_title_of_first_source

[Fact] public async Task Merge_hard_deletes_sources
// 合并后 GET /api/ideas 只剩合并结果，源 id 查库也查不到（硬删）

[Fact] public async Task Merge_across_projects_returns_400_code_merge_cross_project
// 两条分属不同项目 → 400，code == "merge_cross_project"，且不产生任何改动

[Fact] public async Task Merge_with_fewer_than_two_ids_returns_400_code_invalid_merge_selection

[Fact] public async Task Merge_preserves_verbatim_special_characters
// 源 content 含 "引号"\n\\<b> → 合并结果逐字保持
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - 合并在一个事务里完成：校验 → 插新 idea → 写并集标签 → 硬删源 idea（`idea_tag` 靠 FK CASCADE 清理）。
  - 源排序按 `updated_at ASC, id ASC`。
  - 合并结果的 `title` 取第一条源的 title；请求体带了 `title` 则用请求体的。
  - 合并结果的 `content`：请求体带 `content` 用它，否则拼接。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现批量操作与想法合并"`

---

### Task 8: 事件模型与事件总线（扇出）

**Files:**
- Create: `server/IdeaApp/Domain/IdeaEvent.cs`
- Create: `server/IdeaApp/Infrastructure/EventBus.cs`
- Create: `server/IdeaApp.Tests/EventBusTests.cs`

**Interfaces:**
- Consumes: 无（纯基础设施）
- Produces:
  - `public sealed record ProjectSnapshot(string Id, string Name);`
  - `public sealed record IdeaSnapshot(string Id, string ProjectId, string? Title, string Content, string[] Tags, bool Done, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);`
  - `public sealed record IdeaEvent(string Id, string Type, DateTimeOffset Timestamp, ProjectSnapshot? Project, IdeaSnapshot? Idea, string[]? Changed, string[]? MergedFrom);`
  - `public static class EventTypes { public const string IdeaCreated = "idea.created"; IdeaUpdated = "idea.updated"; IdeaDeleted = "idea.deleted"; IdeaRestored = "idea.restored"; IdeaMerged = "idea.merged"; ProjectCreated = "project.created"; ProjectUpdated = "project.updated"; ProjectDeleted = "project.deleted"; public static readonly string[] All; }`
  - `public interface IEventHandler { ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct); }`
  - `public sealed class EventBus : IAsyncDisposable`，构造 `EventBus(IEnumerable<IEventHandler> handlers, ILogger<EventBus> logger)`，方法 `void Publish(IdeaEvent evt)`、`ValueTask DisposeAsync()`
  - `public static class EventFactory`，方法 `IdeaEvent Create(string type, ProjectSnapshot? project, IdeaSnapshot? idea, string[]? changed = null, string[]? mergedFrom = null)` —— id 用 `Guid.CreateVersion7().ToString("N")`，timestamp 为当前 UTC。

- [ ] **Step 1: 写失败测试**

`EventBusTests.cs`：

```csharp
[Fact] public async Task Publish_delivers_to_every_handler
// 两个 handler 各收到同一实例（同一 Id），不是各拿一半

[Fact] public async Task Publish_does_not_block_when_handler_is_slow
// handler 阻塞 200ms，Publish 立即返回（<50ms）

[Fact] public async Task Publish_is_silent_when_a_handler_throws
// handler 抛异常，Publish 不抛，且另一个 handler 仍收到事件

[Fact] public void EventFactory_creates_monotonic_ids
// 连续创建 100 个事件，Id 严格递增（字符串比较）
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `EventBus` 为每个 `IEventHandler` 建一条 `Channel<IdeaEvent>`，容量 1000，`BoundedChannelFullMode.Wait`；`Publish` 用 `TryWrite`，返回 false 时记 `LogWarning` 并丢弃该 handler 的这条事件。
  - 每个通道一个后台消费循环，`await handler.HandleAsync(evt, ct)` 包 try/catch，异常记日志。
  - `EventBus` 实现 `IAsyncDisposable`，释放时完成所有通道。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现事件模型与扇出式事件总线"`

---

### Task 9: SSE 端点（心跳、Last-Event-ID 补发、resync）

**Files:**
- Create: `server/IdeaApp/Infrastructure/SseBroadcaster.cs`
- Create: `server/IdeaApp/Infrastructure/SseEventHandler.cs`
- Create: `server/IdeaApp/Endpoints/EventEndpoints.cs`
- Create: `server/IdeaApp.Tests/SseTests.cs`

**Interfaces:**
- Consumes: Task 8 的 `EventBus`、`IEventHandler`、`IdeaEvent`。
- Produces:
  - `public sealed class SseBroadcaster(int bufferSize, int heartbeatSeconds = 25)`：`int BufferSize`、`int HeartbeatSeconds`、`void Append(IdeaEvent evt)`、`IReadOnlyList<IdeaEvent> Since(string? lastEventId)`、`SseSubscription Subscribe()`、`bool TryCompleteSubscription(...)`
  - `public sealed record SseSubscription(Guid Id, Channel<IdeaEvent> Channel);`
  - `public sealed class SseEventHandler(SseBroadcaster broadcaster) : IEventHandler` —— `HandleAsync` 把事件 `Append` 进 broadcaster。
  - 端点 `GET /api/events` → `text/event-stream`，帧格式：
    ```
    id: <event.Id>
    event: <event.Type>
    data: <json 单行>

    ```
    心跳每 25 秒发 `: ping\n\n`；补发越界时先发 `event: resync\ndata: {}\n\n`。

- [ ] **Step 1: 写失败测试**

`SseTests.cs`：

```csharp
[Fact] public async Task Sse_stream_emits_created_event_after_write
// 连上 GET /api/events，POST /api/ideas，读第一帧，断言含 "event:idea.created" 与该 idea 的 id

[Fact] public async Task Sse_frame_carries_event_id_and_single_line_json
// data 行只有一行，JSON 可被 JsonDocument 解析

[Fact] public async Task Sse_reconnect_with_last_event_id_replays_missed_events
// 断开 → POST 一条 → 带 Last-Event-ID 重连 → 先收到那条

[Fact] public async Task Sse_resync_frame_sent_when_last_event_id_too_old
// 用一个不存在的 Last-Event-ID → 首帧是 "event:resync"

[Fact] public async Task Sse_heartbeat_is_emitted
// 25 秒太长不好测 → 把心跳间隔做成可注入（SseBroadcaster 构造参数 heartbeatSeconds，默认 25），测试用 1 秒
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `SseBroadcaster` 持有一个 `List<IdeaEvent>` 环形缓冲（容量 = bufferSize）与订阅列表。
  - SSE 端点：`Last-Event-ID` 头存在且能在缓冲里找到 → 先补发其后的全部事件；找不到 → 发 `resync` 帧后只推新事件。
  - 响应头：`Content-Type: text/event-stream`、`Cache-Control: no-cache`、`X-Accel-Buffering: no`。
  - 客户端断开时取消订阅。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现 SSE 事件流与断线补发"`

---

### Task 10: 回收站视图、恢复语义与自动清理

**Files:**
- Create: `server/IdeaApp/Application/TrashService.cs`
- Create: `server/IdeaApp/Infrastructure/TrashCleanupWorker.cs`
- Create: `server/IdeaApp/Endpoints/TrashEndpoints.cs`
- Create: `server/IdeaApp.Tests/TrashTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SqliteDatabase`、Task 4/5 的仓储、Task 3 的 `TrashEntryDto`。
- Produces:
  - `public sealed class TrashService(IdeaRepository ideas, ProjectRepository projects)`
    - `IReadOnlyList<TrashEntryDto> List()`
    - `IdeaDto RestoreIdea(string id)`
    - `ProjectDto RestoreProject(string id)`
  - `public sealed class TrashCleanupWorker(SqliteDatabase db, IOptions<AppOptions> options, ILogger<TrashCleanupWorker> logger) : BackgroundService` —— 保留期读 `options.Value.Trash.RetentionDays`。
  - 端点：`GET /api/trash`、`POST /api/ideas/{id}/restore`（Task 5 已有，改为走 TrashService）、`POST /api/projects/{id}/restore`（Task 4 已有，改为走 TrashService）

- [ ] **Step 1: 写失败测试**

`TrashTests.cs`：

```csharp
[Fact] public async Task Trash_lists_deleted_ideas_and_deleted_projects
// 删一条 idea、删一个项目 → GET /api/trash 有两项：Kind=="project" 的容器 + Kind=="idea" 的顶层

[Fact] public async Task Trash_project_container_lists_its_ideas
// 容器项的 Ideas 数组含该项目下全部 idea（含先前单独删掉的那条）

[Fact] public async Task Trash_does_not_list_same_idea_twice
// 先单独删 idea，再删它所在项目 → 顶层 idea 条目里不含它，只在容器里出现一次

[Fact] public async Task Restore_project_does_not_revive_previously_deleted_idea
// 先单独删 X，再删项目，再恢复项目 → X 仍在回收站顶层，恢复项目后 GET /api/trash 里 X 还在

[Fact] public async Task Restore_project_revives_ideas_deleted_with_the_project
// 删项目 → 恢复项目 → 容器消失，其 idea 回到正常列表

[Fact] public async Task Cleanup_deletes_entries_older_than_retention_days
// 直接改库把 deleted_at 改成 39 天前，跑清理 → 条目消失

[Fact] public async Task Cleanup_keeps_entries_within_retention_days
// deleted_at 改成 29 天前 → 仍在

[Fact] public async Task Cleanup_deletes_project_together_with_its_ideas_and_webhooks
// 过期项目 + 其 idea + 其 webhook 一起被删
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `TrashService.List()`：先查已删项目（`project.deleted_at IS NOT NULL`），每个项目取其**全部** idea（含已删）作为容器；再查顶层 idea = `idea.deleted_at IS NOT NULL AND (所属项目未删 OR idea.deleted_at != project.deleted_at)`。容器排在顶层之前。
  - `RestoreProject`：`UPDATE idea SET deleted_at=NULL, updated_at=now WHERE project_id=? AND deleted_at IS NOT NULL AND deleted_at = (SELECT deleted_at FROM project WHERE id=?)` —— 只恢复「因删项目而删」的。
  - `TrashCleanupWorker`：启动时立刻跑一次，之后每小时一次。判定 `deleted_at < now - RetentionDays`。
  - 清理项目时先删该项目下 idea 行，再删项目行（webhook 靠 FK CASCADE 带走）。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现回收站视图、恢复语义与到期清理"`

---

### Task 11: Webhook 配置 API 与 Telegram 模板预设

**Files:**
- Create: `server/IdeaApp/Domain/Webhook.cs`
- Create: `server/IdeaApp/Contracts/Requests.cs`
- Create: `server/IdeaApp/Infrastructure/WebhookRepository.cs`
- Create: `server/IdeaApp/Application/WebhookService.cs`
- Create: `server/IdeaApp/Endpoints/WebhookEndpoints.cs`
- Create: `server/IdeaApp.Tests/WebhookApiTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `WebhookDto`、Task 8 的 `EventTypes`。
- Produces:
  - `public sealed class WebhookRepository(SqliteDatabase db)`：`IReadOnlyList<Webhook> List(string? projectId)`、`Webhook? Get(string id)`、`Webhook Insert(Webhook w)`、`Webhook Update(Webhook w)`、`void Delete(string id)`、`IReadOnlyList<Webhook> ListEnabledFor(string? projectId)`
  - `public sealed record Webhook(string Id, string Name, string Url, string Method, Dictionary<string,string> Headers, string BodyTemplate, string[] Events, string Scope, string? ProjectId, string? Secret, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);`（放在 `Domain/Webhook.cs`）
  - `public sealed class WebhookService(WebhookRepository repo)`
    - `IReadOnlyList<WebhookDto> List(string? projectId)`
    - `WebhookDto Create(WebhookCreateRequest req)`
    - `WebhookDto Update(string id, WebhookUpdateRequest req)`
    - `void Delete(string id)`
  - `public static class TelegramTemplate`：`public const string Url = "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage";` `public const string BodyTemplate = """{"chat_id":-1003800671233,"message_thread_id":89,"text":"[{{project}}] {{event}}\n{{idea.title}}\n{{idea.content}}\n#{{idea.tags}}"}""";`
  - `server/IdeaApp/Contracts/Requests.cs`（请求体记录，供源生成序列化）：
    ```csharp
    public sealed record CreateProjectRequest(string Name);
    public sealed record RenameProjectRequest(string Name);
    public sealed record ReorderProjectsRequest(string[] Ids);
    public sealed record CreateIdeaRequest(string ProjectId, string Content, string? Title, string[]? Tags);
    public sealed record UpdateIdeaRequest(string? Title, string? Content);
    public sealed record SetTagsRequest(string[] Tags);
    public sealed record SetDoneRequest(bool Done);
    public sealed record BulkCompleteRequest(string[] Ids, bool Done);
    public sealed record BulkTagsRequest(string[] Ids, string[] Tags, bool? Replace);
    public sealed record BulkIdsRequest(string[] Ids);
    public sealed record MergeIdeasRequest(string[] Ids, string? Content, string? Title);
    public sealed record UpdateSettingRequest(string Value);
    public sealed record WebhookCreateRequest(string Name, string Url, string Method,
        Dictionary<string,string>? Headers, string BodyTemplate, string[]? Events,
        string Scope, string? ProjectId, string? Secret);
    public sealed record WebhookUpdateRequest(string? Name, string? Url, string? Method,
        Dictionary<string,string>? Headers, string? BodyTemplate, string[]? Events,
        bool? Enabled, string? Secret);
    ```
    这些 record 要加进 `IdeaJsonContext` 的 `[JsonSerializable]` 列表。
  - `public static class WebhookEndpoints { public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app); }`
  - 端点：`GET /api/webhooks?projectId=`、`POST /api/webhooks`、`GET /api/webhooks/{id}`、`PATCH /api/webhooks/{id}`、`DELETE /api/webhooks/{id}`、`GET /api/webhooks/templates/telegram`

- [ ] **Step 1: 写失败测试**

`WebhookApiTests.cs`：

```csharp
[Fact] public async Task Create_global_webhook_is_disabled_by_default
// POST /api/webhooks {scope:"global", ...} → 201，Enabled == false

[Fact] public async Task Create_project_webhook_is_enabled_by_default
// POST /api/webhooks {scope:"project", projectId:"<id>", ...} → 201，Enabled == true

[Fact] public async Task Create_webhook_never_returns_secret_plaintext
// 创建带 secret → 返回体里 HasSecret == true，且响应 JSON 不含 secret 字符串

[Fact] public async Task List_webhooks_filters_by_project_id

[Fact] public async Task Get_telegram_template_returns_preset_with_message_thread_id
// GET /api/webhooks/templates/telegram → body 含 "message_thread_id" 与 "{{idea.content}}"

[Fact] public async Task Create_webhook_with_unknown_event_type_returns_400_code_invalid_event_type

[Fact] public async Task Update_webhook_can_change_enabled_and_events
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `WebhookService.Create`：`scope == "global"` → `Enabled = false`（忽略请求里的 enabled）；`scope == "project"` → `Enabled = true`。
  - `Events` 校验：每个值必须在 `EventTypes.All` 里，否则 400 `invalid_event_type`。
  - `Headers`/`Events` 以 JSON 字符串存库，映射时用 `IdeaJsonContext`。
  - `WebhookDto.HasSecret` = `!string.IsNullOrEmpty(Secret)`，**不回传 secret 明文**。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现 webhook 配置 API 与 Telegram 模板预设"`

---

### Task 12: 占位符模板渲染与 HMAC 签名

**Files:**
- Create: `server/IdeaApp/Infrastructure/TemplateRenderer.cs`
- Create: `server/IdeaApp/Infrastructure/EventPlaceholders.cs`
- Create: `server/IdeaApp/Infrastructure/HmacSigner.cs`
- Create: `server/IdeaApp.Tests/TemplateRendererTests.cs`

**Interfaces:**
- Consumes: Task 8 的 `IdeaEvent`。
- Produces:
  - `public static class TemplateRenderer`
    - `string RenderJson(string jsonTemplate, IReadOnlyDictionary<string, string?> values)` —— 解析 JSON 模板，对每个字符串值做占位符替换并按 JSON 字符串上下文转义，输出序列化后的 JSON（缩进 2 空格）
    - `string RenderPlain(string template, IReadOnlyDictionary<string, string?> values)` —— 纯文本替换，不转义
  - `public static class EventPlaceholders`：`IReadOnlyDictionary<string, string?> Build(IdeaEvent evt, string? publicBaseUrl)`
  - `public static class HmacSigner`：`string Sign(string secret, string payload)` —— 返回 `"sha256=" + 小写 hex`

- [ ] **Step 1: 写失败测试**

`TemplateRendererTests.cs`：

```csharp
[Fact] public void Render_json_substitutes_placeholders()
// {"text":"[{{project}}] {{event}}"} + {project:"项目A", event:"idea.created"} → 解析后 text == "[项目A] idea.created"

[Fact] public void Render_json_escapes_special_characters_in_values
// 值含 "引号"、换行、反斜杠 → 渲染结果是合法 JSON，解析回来值逐字相同

[Fact] public void Render_json_keeps_unknown_placeholder_verbatim
// 模板里 "{{unknown}}" → 输出仍是 "{{unknown}}"

[Fact] public void Render_json_throws_on_invalid_json_template
// 模板 "not json" → 抛 ArgumentException

[Fact] public void Build_placeholders_contains_all_documented_keys
// 键集合含 event,timestamp,project,project.id,idea.id,idea.title,idea.content,idea.tags,idea.done,changed,idea.url

[Fact] public void Build_idea_url_uses_public_base_url
// publicBaseUrl="https://x.example" → idea.url == "https://x.example/?idea=<id>"

[Fact] public void Build_idea_tags_is_comma_separated

[Fact] public void Sign_is_deterministic_and_prefixed
// Sign("k","p") == Sign("k","p")，以 "sha256=" 开头，hex 长度 64
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `RenderJson` 用 `JsonNode.Parse` 解析模板，递归遍历：`JsonValue` 且底层是 string → 替换占位符（值用 `JsonEncodedText.Encode(value).ToString()` 转义）；对象/数组递归；其余原样。找不到的占位符保留原文本。
  - 占位符语法：`{{name}}`，name 允许含 `.`（如 `idea.title`）。用正则 `\{\{([a-zA-Z0-9_.]+)\}\}` 匹配。
  - `EventPlaceholders.Build` 的 `{{idea.done}}` 输出 `"true"` / `"false"` 字符串。
  - `{{idea.url}}`：`publicBaseUrl` 为 **null 或空串**（`AppOptions.PublicBaseUrl` 从 appsettings 绑出来默认是空串，不是 null —— 判断要用 `string.IsNullOrEmpty`）时输出**空串**，不要输出 `/?idea=xxx` 这种半截链接。
  - `{{changed}}` 输出逗号分隔的字段名；`{{mergedFrom}}` 不在占位符清单里，不提供。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现占位符渲染与 HMAC 签名"`

---

### Task 13: 设置 API 与项目排序方式

**Files:**
- Create: `server/IdeaApp/Infrastructure/SettingRepository.cs`
- Create: `server/IdeaApp/Endpoints/SettingsEndpoints.cs`
- Create: `server/IdeaApp.Tests/SettingsTests.cs`
- Modify: `server/IdeaApp/Application/ProjectService.cs`（排序方式）

**Interfaces:**
- Consumes: Task 4 的 `ProjectService`。
- Produces:
  - `public sealed class SettingRepository(SqliteDatabase db)`：`string? Get(string key)`、`void Set(string key, string value)`
  - `public static class SettingKeys { public const string ProjectSortMode = "project.sort_mode"; }`
  - `public static class ProjectSortMode { public const string Recent = "recent"; public const string Manual = "manual"; }`
  - 端点：`GET /api/settings` → `Dictionary<string,string>`；`PUT /api/settings/{key}` body `{value}` → 200 `Dictionary<string,string>`
  - `ProjectService.List()` 按 `project.sort_mode` 排序：`recent` → 按最近活动时间倒序（活动时间为项目下未删 idea 的最大 `updated_at`，无 idea 时取项目 `updated_at`），同值按 `id ASC`；`manual` → 按 `sort_order ASC, id ASC`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task Get_settings_returns_empty_or_defaults
// 初始只有 project.sort_mode=recent

[Fact] public async Task Put_settings_changes_project_sort_mode
// PUT /api/settings/project.sort_mode {"value":"manual"} → 200，之后 GET 返回 manual

[Fact] public async Task Put_settings_rejects_unknown_key_with_code_setting_key_not_writable
// PUT /api/settings/Trash.RetentionDays → 400，code == "setting_key_not_writable"

[Fact] public async Task Put_settings_rejects_illegal_sort_mode_value
// {"value":"whatever"} → 400，code == "invalid_sort_mode"

[Fact] public async Task Recent_sort_mode_puts_most_recently_active_project_first
// 两个项目各有一条 idea，先后更新 → recent 模式下列表里最近活动的在前

[Fact] public async Task Manual_sort_mode_uses_sort_order
// 改 manual 并 reorder → 列表顺序按 sort_order
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— `PUT` 只接受 `SettingKeys.ProjectSortMode`，值只接受 `recent`/`manual`。

  **默认值不播种到库里**：`project.sort_mode` 这个键**不预置行**。`GET /api/settings` 组装响应时，对这个键做「读不到就回落默认值 `recent`」—— 即响应里一定有 `project.sort_mode: "recent"` 这一项，但 `setting` 表里可能没有对应的行。这样避免了「播种一次、以后改默认值要迁移」的问题。其它未知键不出现在响应里。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现设置 API 与项目排序方式"`

---

### Task 14: Webhook 投递、重试、死信与手动重投

**Files:**
- Create: `server/IdeaApp/Domain/Delivery.cs`
- Create: `server/IdeaApp/Infrastructure/WebhookEventHandler.cs`
- Create: `server/IdeaApp/Infrastructure/WebhookDeliveryWorker.cs`
- Create: `server/IdeaApp/Infrastructure/DeliveryRepository.cs`
- Create: `server/IdeaApp/Application/DeliveryService.cs`
- Modify: `server/IdeaApp/Endpoints/WebhookEndpoints.cs`（加投递记录与重投端点）
- Create: `server/IdeaApp.Tests/WebhookDeliveryTests.cs`

**Interfaces:**
- Consumes: Task 11 的 `WebhookService`/`WebhookRepository`、Task 12 的 `TemplateRenderer`/`EventPlaceholders`/`HmacSigner`、Task 8 的 `IEventHandler`。
- Produces:
  - `public sealed record WebhookDeliveryJob(Webhook Webhook, IdeaEvent Event, string Payload);`
  - `public sealed class DeliveryRepository(SqliteDatabase db)`
    - `DeliveryRecord Insert(WebhookDeliveryJob job)`
    - `DeliveryRecord? Get(string id)`
    - `void Update(DeliveryRecord record)`
    - `IReadOnlyList<DeliveryRecord> ListForWebhook(string webhookId, int limit)`
    - `void Trim(string webhookId, int keep)`
  - `public sealed record DeliveryRecord(string Id, string WebhookId, string EventId, string EventType, string Payload, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);`（放 `Domain/Delivery.cs`）
  - `public sealed class WebhookDeliveryWorker(WebhookRepository webhooks, DeliveryRepository deliveries, EventBus bus, ILogger<...>) : BackgroundService`
    - `bool Enqueue(WebhookDeliveryJob job)` —— 投进内部 `Channel<WebhookDeliveryJob>`（容量 1000，`BoundedChannelFullMode.Wait`）；`TryWrite` 返回 false（队列满）时记日志并返回 false，**不阻塞调用方**。
    - 公开方法 `ValueTask DeliverAsync(WebhookDeliveryJob job, CancellationToken ct)`（便于测试直接调用）
  - `public sealed class DeliveryService(DeliveryRepository deliveries, WebhookRepository webhooks, WebhookDeliveryWorker worker)` —— 手动重投时要重新取 webhook 配置（签名要用当前 secret 重算），所以依赖 `WebhookRepository`。
    - `IReadOnlyList<DeliveryDto> ListForWebhook(string webhookId)`
    - `DeliveryDto Retry(string webhookId, string deliveryId)`
  - 端点：`GET /api/webhooks/{id}/deliveries`、`POST /api/webhooks/{id}/deliveries/{deliveryId}/retry`

- [ ] **Step 1: 写失败测试**

`WebhookDeliveryTests.cs`（用 `HttpListener` 或 `WireMock` 不可用 → **用 `TestServer`/本地 `HttpListener` 起一个假端点**）：

```csharp
[Fact] public async Task Delivery_posts_rendered_template_to_target()
// 假端点收到 POST，body 是渲染后的 JSON，Content-Type 为 application/json

[Fact] public async Task Delivery_sends_custom_headers_and_hmac_signature
// 配置了 header 与 secret → 假端点收到该 header 与 "X-Idea-Signature: sha256=..."

[Fact] public async Task Failed_delivery_retries_three_times_then_records_failed
// 假端点返回 500 → attempts == 3，status == "failed"，last_error 非空

[Fact] public async Task Successful_delivery_records_delivered_with_attempts_one
// 200 → status == "delivered"，attempts == 1

[Fact] public async Task Manual_retry_resends_the_stored_payload
// 改掉模板后手动重投 → 假端点收到的仍是旧 payload（不按新模板重渲染）

[Fact] public async Task Webhook_receives_only_subscribed_events
// 只订阅 idea.created → 触发 idea.updated 不投递

[Fact] public async Task Global_webhook_disabled_receives_nothing
// global 且 enabled=false → 不投递

[Fact] public async Task Project_webhook_receives_only_its_project_events
// 绑定项目 A 的 webhook 不收到项目 B 的事件

[Fact] public async Task Slow_or_unreachable_target_does_not_block_the_api_response
// 假端点 sleep 5 秒；同时 POST /api/ideas 断言 1 秒内返回 201
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
  - `WebhookEventHandler : IEventHandler` —— `HandleAsync` 查出匹配且启用的 webhook，逐个用 `EventPlaceholders.Build` + `TemplateRenderer.RenderJson` 渲染 payload，构造 `WebhookDeliveryJob` 投进 worker 的 `Channel`（容量 1000，满则 `TryWrite` 失败 → 记日志丢弃）。
  - `WebhookDeliveryWorker` 用 `Parallel.ForEachAsync` 消费，`MaxDegreeOfParallelism = 8`；每个 job 内部：插入/取投递记录 → 尝试 POST → 成功记 delivered，失败按 1s/5s/30s 退避重试，共 `MaxAttempts` 次（**含首次**）→ 仍失败记 failed 与 `last_error`。
  - HTTP 客户端用 `IHttpClientFactory` 创建的 `HttpClient`，超时由 `Webhook:TimeoutSeconds` 设定（通过 `HttpClient.Timeout`）。
  - 匹配规则：webhook 启用的前提下，`global` 作用域全部收；`project` 作用域只收 `evt.Project?.Id == webhook.ProjectId` 的事件；再叠加 `Events` 包含事件类型。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现 webhook 异步投递、重试与死信"`

---

### Task 15: OpenAPI 导出、部署文档与整体接线

**Files:**
- Create: `tools/ExportOpenApi/ExportOpenApi.csproj`、`tools/ExportOpenApi/Program.cs`
- Create: `server/IdeaApp/Endpoints/` 各端点文件的 `MapXxx` 全部挂到组合根
- Create: `docs/nginx/idea.conf`
- Create: `README.md`
- Create: `openapi/idea-v1.json`（生成物，提交进仓库）
- Modify: `server/IdeaApp/Program.cs`
- Modify: `server/IdeaApp/appsettings.json`

**Interfaces:**
- Consumes: 全部前置 task 的端点。
- Produces: `dotnet run --project tools/ExportOpenApi` 在仓库根生成 `openapi/idea-v1.json`。

- [ ] **Step 1: 接线组合根**

`Program.cs` **整体替换**为下面这份最终形态。**注意**：Task 2 已经加过 `builder.Services.Configure<AppOptions>(...)`、`builder.Services.AddSingleton(sp => new SqliteDatabase(...))` 和 `SqliteDatabase.Initialize()` 的调用 —— 替换时要保留它们，**不要重复注册**（重复注册 `Configure<AppOptions>` 会导致配置被绑定两次，重复注册 `SqliteDatabase` 会造出两个实例、指向两个不同的库文件）。

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("IDEA_");   // IDEA_ 前缀；双下划线表示层级
builder.Services.Configure<AppOptions>(builder.Configuration);
builder.Services.AddSingleton(sp =>
    new SqliteDatabase(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppOptions>>().Value.Database.Path));
builder.Services.AddSingleton<ProjectRepository>();
builder.Services.AddSingleton<IdeaRepository>();
builder.Services.AddSingleton<TagRepository>();
builder.Services.AddSingleton<WebhookRepository>();
builder.Services.AddSingleton<DeliveryRepository>();
builder.Services.AddSingleton<SettingRepository>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<IdeaService>();
builder.Services.AddSingleton<TagService>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<DeliveryService>();
builder.Services.AddSingleton<TrashService>();
builder.Services.AddSingleton(sp =>
    new SseBroadcaster(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppOptions>>().Value.Sse.BufferSize));
builder.Services.AddSingleton<IEventHandler, SseEventHandler>();
builder.Services.AddSingleton<IEventHandler, WebhookEventHandler>();
// EventBus 必须用工厂：它要在所有 IEventHandler 注册完成后再构造
builder.Services.AddSingleton(sp => new EventBus(
    sp.GetServices<IEventHandler>(),
    sp.GetRequiredService<ILogger<EventBus>>()));
builder.Services.AddSingleton<WebhookDeliveryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryWorker>());
builder.Services.AddHostedService<TrashCleanupWorker>();
builder.Services.AddHttpClient();
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseStaticFiles();                 // Vite 产物在 wwwroot/assets/，没有它静态资源 404

// 建库必须在 Run 之前，且不能被这段替换丢掉
app.Services.GetRequiredService<SqliteDatabase>().Initialize();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapHealthEndpoints();
app.MapProjectEndpoints();
app.MapIdeaEndpoints();
app.MapBulkIdeaEndpoints();
app.MapTrashEndpoints();
app.MapTagEndpoints();
app.MapWebhookEndpoints();
app.MapSettingsEndpoints();
app.MapEventEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program;
```

> **验收这一段**：`dotnet run` 后库文件必须存在且 7 张表齐全 —— 少一次 `Initialize()` 调用，spec §16.2 的「首次启动自动建库」就失效，后续所有仓储的集成测试会集体报 `no such table: project`。写完这一段后跑一次 `dotnet run` + `curl /api/health`，并确认数据文件已建出来。

- [ ] **Step 2: 写导出工具**

`tools/ExportOpenApi/Program.cs`：用 `WebApplication.CreateBuilder` + `AddOpenApi` + 挂同样的端点映射，然后 `app.Services.GetRequiredService<DocumentService>()` 取文档并写入 `openapi/idea-v1.json`。

- [ ] **Step 3: 生成并核对**

Run: `dotnet run --project tools/ExportOpenApi` → Expected: 仓库根出现 `openapi/idea-v1.json`
Run: `python3 -c "import json;d=json.load(open('openapi/idea-v1.json'));print(d['openapi']);print(len(d['paths']))"` → Expected: `3.1.1` 与路径数 ≥ 25

- [ ] **Step 4: 写部署文档**

`docs/nginx/idea.conf` = spec §16.3 的完整配置（含 basic auth、`proxy_buffering off`）。
`README.md` 写清：目录结构、前端构建命令、后端发布命令、运行命令（环境变量前缀 `IDEA_`、双下划线表示层级）、首次启动自动建库、nginx 配置位置、skill 安装方式。

- [ ] **Step 5: 全量测试**

Run: `dotnet test server/IdeaApp.Tests` → Expected: 全部 PASS

- [ ] **Step 6: 验证 AOT 发布**

Run: `dotnet publish server/IdeaApp -c Release -r linux-x64 -p:PublishAot=true -p:InvariantGlobalization=true` → Expected: 成功产出可执行文件
Run: 启动产物，`curl http://127.0.0.1:5080/api/health` → Expected: `{"status":"ok"}`

- [ ] **Step 7: 提交**

```bash
git add openapi/ tools/ docs/nginx/ README.md server/IdeaApp/Program.cs server/IdeaApp/appsettings.json
git commit -m "feat(server): 接线全部端点、导出 OpenAPI 文档与部署说明"
```
