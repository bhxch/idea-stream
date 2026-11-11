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
  - **每个端点返回的静态类型（含集合包装类型，如 `List<IdeaDto>`、`WebhookDto[]`）必须在 `IdeaJsonContext` 里有对应的 `[JsonSerializable]`** —— 白名单没登记的类型在 AOT 下必然 500；
  - `IdeaJsonContext` 必须注册为 `JsonOptions.SerializerOptions.TypeInfoResolver`（**直接赋值，不是插进 `TypeInfoResolverChain`** —— 链条里那个默认反射 resolver 会让未登记的类型在开发期照样序列化，测试因此测不出退化，形成「开发全绿、AOT 产物全挂」）；此后 `Results.Ok(dto)` 对已登记的 DTO 可用，未登记的类型在**开发期**就抛 `NoMetadataForType`；
  - 创建类响应（201）用 `Results.Json(dto, IdeaJsonContext.Default.XxxDto, statusCode: 201)` 显式带 `JsonTypeInfo`；
  - `Microsoft.AspNetCore.Mvc.ProblemDetails` 是 .NET 10 里唯一存在的 ProblemDetails 类型（**没有** `Microsoft.AspNetCore.Http.ProblemDetails`），只有它带 `Extensions` 字段，能放 `code`。`HttpValidationProblemDetails`（`Results.ValidationProblem` 的实际类型）也要单独登记 —— 子类元数据不会从基类自动继承。
  - **AOT 冒烟脚本** `server/scripts/aot-smoke.sh`：`dotnet publish -r linux-x64 -c Release` → 启动产物 → 打 `/api/health` 与至少一个返回 DTO 的端点，断言是 200/201 而**不是 500**。Task 3 建它，Task 15 复用。普通 `dotnet test` 宿主**测不出**「退回匿名对象」这一类退化（非 AOT 下默认反射 resolver 兜底，输出逐字节相同），只有真 AOT 产物能测出来。
- **禁止** `ActivatorUtilities`、动态代理、运行时表达式编译。
- SQL 手写，不引入 EF Core 或任何 ORM。
- 命名空间根：`IdeaApp`（`Domain` / `Application` / `Infrastructure` / `Contracts` / `Endpoints`）。
- 时间戳一律 `DateTimeOffset`，存库转 UTC ISO-8601 字符串（`yyyy-MM-ddTHH:mm:ss.fffffffZ`）。
- 所有实体 id 用**单调 UUIDv7**（自实现的 `MonotonicGuid.NewUuid7()`，字节布局见 Task 5），转字符串时用 32 位无连字符小写 hex；内置收件箱 id 固定为 `inbox`。
  > **不要用 .NET 的 `Guid.CreateVersion7()`** —— 实测它同一毫秒内完全无序（30 万样本 149,895 次相邻逆序，单毫秒最大桶 2422 个），且 .NET 10 只有 `CreateVersion7()` / `CreateVersion7(DateTimeOffset)` 两个重载，没有单调版本。用它会让「同刻写入的多条想法」顺序随机跳动，直接破坏「最近更新的在最下边」这个核心承诺。
- API 路径前缀 `/api`；JSON 字段名 camelCase；错误统一 RFC 9457 ProblemDetails + 扩展字段 `code`。
- 列表排序恒为 `updated_at ASC, id ASC`。
- 事件不参与 SQLite 事务：领域服务先提交事务，再发布事件。
- 每个 task 结束提交一次，commit message 用 Conventional Commits（**中文正文，要有 body 说明 why**）。
- 引用类型可空性全部显式标注；`Directory.Build.props` 开启 `Nullable=enable` 与 `TreatWarningsAsErrors`。
- 集成测试里 `WebApplicationFactory` 必须 `using` 释放（`using var factory = new TestAppFactory();`），每个测试用独立临时库。

## Review Focus

以下五类输入/情形最可能咬人，每类都必须在对应 task 的测试里被钉住：

1. **同刻写入的多条想法** —— `updated_at` 完全相同，列表顺序必须稳定（`id ASC`，而 `id` 必须是**单调** UUIDv7），不能随机跳动。
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
      IdeaQuery.cs                 # 查询条件与过滤判定（Domain 层）
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
- Create: `server/scripts/aot-smoke.sh`（AOT 产物冒烟脚本，见 Step 3 第 4 条）
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

再加一个 **JSON 解析链回归测试**：

```csharp
[Fact] public void Json_context_is_registered_in_the_resolver_chain()
// 断言 IdeaJsonContext.Default 在 JsonOptions.SerializerOptions.TypeInfoResolverChain 里
// 注释里必须写明：端点若退回匿名对象，普通测试宿主有反射 resolver 兜底，这里测不出来；
// 那一半由 server/scripts/aot-smoke.sh 在真 AOT 产物上守
```

`/api/health` 本身返回 200、`status == "ok"`、根对象有且仅有 `status` 一个属性的断言保留在 `HealthTests.cs` 里。

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 按上面的签名逐字建类型，JsonContext 用 `JsonSerializerContext` 源生成。

  另外五件必须做的事：

  1. **把 Task 1 的 `HealthResponse` 挪进 `Contracts/Dtos.cs`，把 `HealthJsonContext` 并进 `IdeaJsonContext`**，删掉 Task 1 建的独立 `HealthJsonContext` 文件，`HealthEndpoints.cs` 改为用 `IdeaJsonContext.Default.HealthResponse`。全项目只留**一个**源生成上下文。
  2. **在组合根把 `IdeaJsonContext` 注册进 JSON 解析链**，否则 `Results.Ok(dto)` 在 AOT 下是 500：
     ```csharp
     builder.Services.ConfigureHttpJsonOptions(o =>
         o.SerializerOptions.TypeInfoResolverChain.Insert(0, IdeaJsonContext.Default));
     ```
  3. `IdeaJsonContext` 上加 `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]`，并把 `HealthResponse` 也登记进去。
  4. **建 `server/scripts/aot-smoke.sh`**（可执行）：`dotnet publish server/IdeaApp -c Release -r linux-x64` → 用临时库启动产物 → `curl /api/health` 断言 200 且 body 能 `JsonDocument.Parse` 出 `status == "ok"` → 关进程 → 退出码反映结果。**这个脚本是唯一能测出「端点退回匿名对象」这类退化的手段**（非 AOT 测试宿主测不出来：默认反射 resolver 兜底，输出逐字节相同）。
  5. **把集合包装类型也登记进 `IdeaJsonContext`**：`List<IdeaDto>`、`List<ProjectDto>`、`List<TagDto>`、`List<WebhookDto>`、`List<DeliveryDto>`、`List<TrashEntryDto>`、`WebhookDto[]`、`HttpValidationProblemDetails`。少登记一个，后续用到它的端点在 AOT 下就是 500，而普通 `dotnet test` 照样全绿。
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
    - `DateTimeOffset LastActivityAt(string projectId)` —— **不返回 null**：未删想法的最大 `updated_at`，无未删想法时回落项目自身的 `updated_at`
    - `int CountActiveIdeas(string projectId)`
  - `public sealed class ProjectService(ProjectRepository repo)`：`IReadOnlyList<ProjectDto> List()`、`ProjectDto Create(string name)`、`ProjectDto Rename(string id, string name)`、`void Delete(string id)`、`ProjectDto Restore(string id)`、`IReadOnlyList<ProjectDto> Reorder(IReadOnlyList<string>? ids)` —— `ids` 为 null 或空数组时抛 `ApiException(400, "invalid_reorder_ids", ...)`
  - `public sealed class ProjectRepository(SqliteDatabase db)` 另加 `int MaxSortOrder()` —— 全表（含回收站）最大 `sort_order`，无行时返回 -1。**Create 的新项目 `sort_order` 用它 +1**，与 Reorder 的全表口径一致，删项目后新建不撞号。
  - `public static class ProjectEndpoints { public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app); }`
  - 端点：`GET /api/projects`、`POST /api/projects`、`PATCH /api/projects/{id}`、`DELETE /api/projects/{id}`、`POST /api/projects/{id}/restore`、`POST /api/projects/reorder`

**本 task 额外要做的两件事（Task 3 复审带出来的）**

1. **把 `server/scripts/aot-smoke.sh` 扩展到检查一个返回 DTO 的业务端点。** Task 3 建的脚本目前只打 `/api/health`，而健康端点返回的是 `HealthResponse`（不是业务 DTO）。你这个 task 是第一个有业务 DTO 端点的 task，脚本要能守住「业务端点在 AOT 下不是 500」。脚本要改成可复用形式（一个 `check_endpoint <路径> <期望状态码>` 函数），并在末尾追加对 `GET /api/projects` 的检查 —— **该端点返回 `List<ProjectDto>`，正好验证集合包装类型的 JSON 元数据在 AOT 下可用**。
2. **脚本的 publish 命令要显式带 `-p:PublishAot=true`**（不要只依赖 csproj 里的 `PublishAot=true`）。否则将来有人删掉 csproj 那一行，脚本会静默退化成非 AOT 发布，从此再也测不出退化，而且脚本仍然 PASS。

**顺带裁定（来自 Task 3 复审的范围外观察）**：把组合根里的

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, IdeaJsonContext.Default));
```

改成

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolver = IdeaJsonContext.Default);
```

**理由**：链条里那个默认反射 resolver 是「开发全绿、AOT 产物全挂」的根源 —— 开发宿主下未登记的类型照样能序列化，测试因此测不出退化。直接把 `TypeInfoResolver` 设成源生成上下文，让**开发环境与 AOT 行为一致**：任何未登记的类型在开发期就抛 `NoMetadataForType`，问题在 `dotnet test` 阶段就暴露，不必等到发布。

**代价**：`Results.Ok(匿名对象)` 这类写法在开发期也会失败 —— 这正是我们要的。未登记的根类型一律报错，而不是静默降级。

改完后 `HealthTests.cs` 里那条 `Json_context_is_registered_in_the_resolver_chain` 断言要同步调整（它现在查的是 `TypeInfoResolverChain` 里是否含 `IdeaJsonContext.Default`）。

- [ ] **Step 1: 写失败测试**

`ProjectApiTests.cs`（用 `TestAppFactory`，每个测试独立临时库）：

```csharp
[Fact] public async Task Create_project_returns_201_and_persists()
// POST /api/projects {"name":"项目A"} → 201，body.name == "项目A"，id 非空且为 32 位小写 hex

[Fact] public async Task List_projects_includes_inbox_and_active_projects()
// 新建两个项目后 GET /api/projects → 含 inbox + 2 个，inbox 排在最前（sortOrder 最小）
// 这一条同时守住：List<ProjectDto> 的 JSON 元数据在开发期可解析（TypeInfoResolver 直接赋值后，
// 未登记的类型会在这里抛 NoMetadataForType 而不是静默降级）

[Fact] public async Task Rename_project_updates_name()
// PATCH /api/projects/{id} {"name":"改名"} → 200，GET 列表里是新名

[Fact] public async Task Delete_project_soft_deletes_and_hides_from_list()
// DELETE → 204；GET /api/projects 不含它；直接查库确认 deleted_at 非空、行还在（软删）

[Fact] public async Task Delete_inbox_returns_400_with_code_inbox_not_deletable
// DELETE /api/projects/inbox → 400，ProblemDetails.extensions["code"] == "inbox_not_deletable"

[Fact] public async Task Reorder_persists_sort_order
// POST /api/projects/reorder {"ids":["<b>","<a>","inbox"]} → 200 且返回顺序与 ids 一致

[Fact] public async Task Rename_unknown_project_returns_404_with_code_project_not_found
```

再加一条 **AOT 冒烟**：

```bash
bash server/scripts/aot-smoke.sh
```
→ 脚本要检查 `GET /api/projects` 返回 200 且 body 能解析出数组（不是 500）。

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
  - **时间戳必须走 `SqliteDatabase.FormatTimestamp` / `ParseTimestamp`** —— `idea.updated_at` 要能被 SQL 的 `MAX()` 正确比较（定宽 `yyyy-MM-ddTHH:mm:ss.fffffffZ`，字典序 == 时间序）并被 `ParseExact` 正确解析。**不要在 `IdeaRepository` 里自己写时间戳格式**，否则 `ProjectRepository.LastActivityAt` 的 `MAX(idea.updated_at)` 排序会错、`ParseExact` 会抛 `FormatException` 变成 500。
  - **`public static class MonotonicGuid`（新建 `server/IdeaApp/Domain/MonotonicGuid.cs`）**

    ```csharp
    public static class MonotonicGuid
    {
        public static Guid NewUuid7();                 // 用当前 UTC 毫秒
        internal static Guid NewUuid7(long unixMs);    // 测试用：注入固定毫秒
    }
    ```

    **字节布局（RFC 9562，大端 16 字节）**：

    ```
    bytes[0..6]   unix_ts_ms（48 位，大端）
    bytes[6]      0x70 | (rand_a >> 8)        // ver = 7
    bytes[7]      rand_a & 0xFF
    bytes[8]      0x80 | (rand_b >> 56)       // var = 0b10
    bytes[9..16]  rand_b 的低 56 位（大端）
    ```

    **计数器**：一个 74 位无符号计数器 `C`，`rand_a = (C >> 62) & 0xFFF`（12 位）、`rand_b = C & 0x3FFF_FFFF_FFFF_FFFF`（62 位）。

    ```
    lock 保护 (lastMs, C)：
      ms = 当前 UTC 毫秒
      若 ms != lastMs：lastMs = ms; C = 0        // 毫秒占最高位，ms 变大时 UUID 必然变大
      否则：C += 1
    ```

    构造：`new Guid(bytes, bigEndian: true)`（.NET 8+ 的重载），`ToString("N")` 即得 RFC 顺序的 32 位小写 hex。

    **必须钉住的测试**：
    - 已知答案向量：`NewUuid7(0)` → `00000000-0000-7000-8000-000000000000`；`NewUuid7(0x0123456789AB)` → `01234567-89ab-7000-8000-000000000000`。
    - 同一毫秒内连调 10000 次，`ToString("N")` 的 `string.CompareOrdinal` **严格递增**。
    - 毫秒递增时也严格递增。
    - 线程安全：多线程各生成若干个，全部不重复且整体有序（用并发调用验证不抛异常、id 不重复）。
  - **所有生成 id 的地方都要改用 `MonotonicGuid.NewUuid7().ToString("N")`** —— 包括 `IdeaService.Create`（**不是** `IdeaRepository.Insert`：仓储的 `Insert(Idea idea)` 收的是已带 id 的记录，id 由调用方生成）、Task 4 已有的 `ProjectRepository.Insert`（一行改动）、以及 Task 8 的 `EventFactory`。
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
- Create: `server/IdeaApp/Domain/IdeaQuery.cs`（**放 `Domain` 不放 `Application`** —— 它是查询规格，被 `Infrastructure` 的仓储与 `Application` 的服务同时引用，放 `Application` 会让 Infrastructure 反向依赖 Application）
- Create: `server/IdeaApp/Endpoints/ApiResults.cs`（**抽取共用的异常包装**，见下）
- Modify: `server/IdeaApp/Infrastructure/IdeaRepository.cs`（加 `List(IdeaQuery)`；修类注释；补一条置底回归测试）
- Modify: `server/IdeaApp/Endpoints/IdeaEndpoints.cs`（**改造** Task 5 已有的 `GET /api/ideas`，加上过滤参数 —— **不要新增第二个同路径端点**，否则路由歧义）
- Modify: `server/IdeaApp/Endpoints/ProjectEndpoints.cs`（改用 `ApiResults.Run`）
- Create: `server/IdeaApp/Endpoints/TagEndpoints.cs`
- Create: `server/IdeaApp.Tests/FilteringTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `IdeaRepository`。
- Produces:
  - `public sealed record IdeaQuery(string? ProjectId, IdeaStatusFilter Status, IReadOnlyList<string> Tags, TagMatchMode TagMode)` —— 命名空间 `IdeaApp.Domain`（`IdeaStatusFilter` / `TagMatchMode` 也在 `Domain/Idea.cs`）
  - `public static class IdeaFilter { public static bool Matches(Idea idea, IdeaQuery q); }` —— 也在 `IdeaApp.Domain`
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

  另外四件必须做的事：

  1. **抽出 `Endpoints/ApiResults.cs`**：`IdeaEndpoints.cs` 与 `ProjectEndpoints.cs` 里那个把 `ApiException` 转成 ProblemDetails 的 `Run` 包装是**逐字重复**的，Task 11 之后还会再复制两三份。抽成共用的（静态类或扩展方法），两个端点文件都改用它。行为必须一字不变（同样的 `Status`/`Title`/`Detail`/`Extensions["code"]`/`application/problem+json`）。
  2. **补一条置底回归测试**：`PUT /api/ideas/{id}/tags` 之后该条目必须挪到列表最底部。现在 `IdeaRepository.ReplaceTags` 刷新 `updated_at` 的那行**没有测试钉住** —— 它是实施者自查时才发现并修掉的 bug，而核心承诺「任何写操作都刷新 `updated_at`」的这个入口目前无保护。建 A、B 两条 → 改 A 的标签 → 断言 `GET /api/ideas` 返回顺序是 `[B, A]`。
  3. **修 `IdeaRepository` 的类注释**：现在写「仓储自己不隐式开事务」，但代码里 `Write` 在没有外层事务时**自己兜一个事务**。注释会误导 Task 7/10 的实施者（他们要在同一事务里做合并与恢复）。改成「事务边界由调用方划定；单独调用多语句写方法时仓储自己兜一个事务」。
  4. **补齐入参 null 语义的测试**：`title=""`（清空标题）与 `content=null`（不改正文）这两条分支现在只有实现、没有测试（`title=null` 那条有）。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现标签字典与列表过滤"`

---

### Task 7: 批量操作与合并

**Files:**
- Create: `server/IdeaApp/Endpoints/BulkIdeaEndpoints.cs`
- Modify: `server/IdeaApp/Application/IdeaService.cs`（加批量与合并方法）
- Create: `server/IdeaApp.Tests/BulkAndMergeTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `IdeaService`、`IdeaRepository`、Task 6 的 `ApiResults`。
- Produces:
  - `IdeaService` 新增：`IReadOnlyList<IdeaDto> BulkSetDone(IReadOnlyList<string> ids, bool done)`、`IReadOnlyList<IdeaDto> BulkSetTags(IReadOnlyList<string> ids, IReadOnlyList<string> tags, bool replace)`、`IReadOnlyList<IdeaDto> BulkDelete(IReadOnlyList<string> ids)`、`IdeaDto Merge(IReadOnlyList<string> ids, string? content, string? title)`
  - **三个批量端点一律返回 `200` + 受影响实体的数组**（含 `bulk/delete`）。spec §13.5 的「删除返回 204」只适用于 HTTP 方法 `DELETE`（`DELETE /api/ideas/{id}`），**不适用于 `POST /api/ideas/bulk/delete`**。`bulk/delete` 返回被软删的想法数组 —— 前端能立刻从本地列表移除它们，也能显示「已删除 N 条」，不必等重新拉列表。
  - 端点：`POST /api/ideas/bulk/complete`（body `{ids[], done}`）、`POST /api/ideas/bulk/tags`（body `{ids[], tags[], replace?}`）、`POST /api/ideas/bulk/delete`（body `{ids[]}`）、`POST /api/ideas/merge`（body `{ids[], content?, title?}`）

**前序 task 留下的、你必须知道的事**

- **`IdeaService` 的构造函数是 `(IdeaRepository, ProjectRepository, IOptions<AppOptions>)`** —— Task 6 为了在服务层做 `too_many_ideas` 检查加了 `IOptions<AppOptions>`。你要往 `IdeaService` 加方法时**不要改这个构造函数**；若你确实要手工 new 一个 `IdeaService`（比如测试里），必须补上 `IOptions<AppOptions>` 参数。
- **新端点一律用 `Endpoints/ApiResults.cs` 里的 `ApiResults.Run`** 包起来（Task 6 抽出来的共用异常包装，把 `ApiException` 转成 ProblemDetails + `Extensions["code"]`）。**不要**再写一份局部 `Run`。
- **`IdeaRepository.InTransaction(...)`** 是「仓储不自行开事务、事务边界在服务层」的落地方式。**合并必须是原子的**：插新想法 + 写标签 + 硬删源想法，这几步必须在**同一个事务**里。
- **`IdeaFilter.Matches(Idea idea, IdeaQuery query)`** 的第二个形参名是 `query`（不是 `q`）。调用时**不要用具名实参**，避免与计划里的签名对不上。
- **`IdeaQuery` / `IdeaFilter` 在 `IdeaApp.Domain` 命名空间**（Task 6 归位过）。

- [ ] **Step 1: 写失败测试**

`BulkAndMergeTests.cs`：

```csharp
[Fact] public async Task Bulk_complete_marks_all_and_bumps_updated_at

[Fact] public async Task Bulk_tags_appends_by_default_and_replaces_when_flagged

[Fact] public async Task Bulk_delete_soft_deletes_all

[Fact] public async Task Bulk_tags_bumps_updated_at
// 断言 bulk/tags 后受影响想法的 updated_at 变了（核心承诺：任何写操作都刷新）

[Fact] public async Task Bulk_delete_bumps_updated_at
// 断言 bulk/delete 后受影响想法的 updated_at 变了

[Fact] public async Task Bulk_with_empty_ids_is_a_no_op
// 空 ids → 200 + 空数组，不报错（"没选东西"与"选的全没了"是两回事）

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

### Task 8: 事件模型、事件总线（扇出）与服务侧发布接线

**Files:**
- Create: `server/IdeaApp/Domain/IdeaEvent.cs`
- Create: `server/IdeaApp/Infrastructure/EventBus.cs`
- Modify: `server/IdeaApp/Application/IdeaService.cs`（写操作后发布事件）
- Modify: `server/IdeaApp/Application/ProjectService.cs`（写操作后发布事件）
- Modify: `server/IdeaApp/Contracts/JsonContext.cs`（登记 `IdeaEvent` / `IdeaSnapshot` / `ProjectSnapshot` —— SSE 的 data 帧与 webhook 的 body 都要序列化它）
- Create: `server/IdeaApp.Tests/EventBusTests.cs`
- Create: `server/IdeaApp.Tests/EventPublishingTests.cs`

**Interfaces:**
- Consumes: Task 4/5/7 的 `ProjectService`、`IdeaService`、`Idea`、`Project`。
- Produces:
  - `public interface IEventPublisher { void Publish(IdeaEvent evt); }` —— 放在 `Domain/IdeaEvent.cs`。**服务层依赖这个接口，不依赖 `EventBus` 具体类**（这样测试能换成记录型的 fake）。
  - `public sealed class EventBus : IEventPublisher, IAsyncDisposable`
  - `public sealed record ProjectSnapshot(string Id, string Name, int SortOrder);` —— **必须带 `SortOrder`**：`project.updated` 的 `changed` 含 `sortOrder` 时，载荷里要能拿到新值，否则订阅端无法应用重排结果（评审 Important 1）
  - `public sealed record IdeaSnapshot(string Id, string ProjectId, string? Title, string Content, string[] Tags, bool Done, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);`
  - `public sealed record IdeaEvent(string Id, string Type, DateTimeOffset Timestamp, ProjectSnapshot? Project, IdeaSnapshot? Idea, string[]? Changed, string[]? MergedFrom);`
    **`Changed` 的三态语义（订阅端必须遵守，写在这里以便 Task 9/14 抄走）**：`null` = 该事件类型没有「改了什么」这个概念（如 `idea.created`）；空数组 = 写了但值没变（如 `Update` 传了同样的正文）；非空数组 = 真正改了的字段名。
  - `public static class EventTypes { public const string IdeaCreated = "idea.created"; IdeaUpdated = "idea.updated"; IdeaDeleted = "idea.deleted"; IdeaRestored = "idea.restored"; IdeaMerged = "idea.merged"; ProjectCreated = "project.created"; ProjectUpdated = "project.updated"; ProjectDeleted = "project.deleted"; public static readonly string[] All; }`
  - `public interface IEventHandler { ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct); }`
  - `EventBus` 构造 `EventBus(IEnumerable<IEventHandler> handlers, ILogger<EventBus> logger)`，方法 `void Publish(IdeaEvent evt)`（实现 `IEventPublisher`）、`ValueTask DisposeAsync()`
  - `public static class EventFactory`，方法 `IdeaEvent Create(string type, ProjectSnapshot? project, IdeaSnapshot? idea, string[]? changed = null, string[]? mergedFrom = null)` —— id 用 `MonotonicGuid.NewUuid7().ToString("N")`（**不是** `Guid.CreateVersion7()`，见 Global Constraints），timestamp 为当前 UTC。

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
- [ ] **Step 3: 实现（事件总线部分）**
  - `EventBus` 为每个 `IEventHandler` 建一条 `Channel<IdeaEvent>`，容量 1000，`BoundedChannelFullMode.Wait`；`Publish` 用 `TryWrite`，返回 false 时记 `LogWarning` 并丢弃该 handler 的这条事件。
  - 每个通道一个后台消费循环，`await handler.HandleAsync(evt, ct)` 包 try/catch，异常记日志。
  - `EventBus` 实现 `IAsyncDisposable`，释放时完成所有通道。

- [ ] **Step 3b: 实现（服务侧发布接线 —— 这是本 task 不可省略的一半）**

  **spec 的核心要求是「新增、修改、删除、合并都发事件广播」。事件总线本身没人 publish 就是死的**，所以本 task 必须把服务接上。

  `IdeaService` 与 `ProjectService` 各注入一个 `IEventPublisher`（**不要**依赖 `EventBus` 具体类）。

  **发布时机：事务提交之后。** 现有服务的方法体是 `repo.InTransaction(() => { ...; return x; })` —— 事件必须在 `InTransaction` **返回之后**才 publish，绝不能在事务回调里 publish（回滚会造成幽灵事件）。

  **事件映射表**：

  | 服务方法 | 事件类型 | `changed` | 备注 |
  |---|---|---|---|
  | `IdeaService.Create` | `idea.created` | — | 载荷带新想法 |
  | `IdeaService.Update` | `idea.updated` | `["title"]` / `["content"]` / 两者 | 只列真正改了的字段 |
  | `IdeaService.SetTags` | `idea.updated` | `["tags"]` | |
  | `IdeaService.SetDone` | `idea.updated` | `["done"]` | |
  | `IdeaService.BulkSetDone` | `idea.updated` × N | `["done"]` | 每条一条事件 |
  | `IdeaService.BulkSetTags` | `idea.updated` × N | `["tags"]` | 每条一条事件 |
  | `IdeaService.Delete` | `idea.deleted` | — | |
  | `IdeaService.BulkDelete` | `idea.deleted` × N | — | 每条一条事件 |
  | `IdeaService.Restore` | `idea.restored` | — | |
  | `IdeaService.Merge` | `idea.merged` | — | 载荷的 `mergedFrom` 带被硬删的源 id 列表 |
  | `ProjectService.Create` | `project.created` | — | |
  | `ProjectService.Rename` | `project.updated` | `["name"]` | |
  | `ProjectService.Reorder` | `project.updated` × N | `["sortOrder"]` | **N = `sort_order` 值真的变了的项目数**，不是请求里的 id 数 —— `repo.Reorder` 会重写全表 `sort_order`，只按请求 id 发会让订阅端漏掉被挤走的项目。这条语义要写进 `IdeaEvent` 的 XML 注释（订阅端要遵守） |
  | `ProjectService.Delete` | `project.deleted` | — | |

  > `ProjectService.Restore` 属于 Task 10（回收站），它的事件也在 Task 10 接。

  **事件载荷**：`IdeaEvent.Idea` 是 `IdeaSnapshot`（`Idea` 去掉 `DeletedAt`、标签转 `string[]`），`Project` 是 `ProjectSnapshot`（Id、Name 与 `SortOrder` —— 重排后订阅端要靠它更新本地顺序）。

  **DI**：组合根加 `builder.Services.AddSingleton<IEventPublisher>(sp => new EventBus(sp.GetServices<IEventHandler>(), sp.GetRequiredService<ILogger<EventBus>>()));`（`IEventHandler` 的实现 Task 9/14 才注册，此时通道数为 0，publish 是空操作 —— 这是对的）。

- [ ] **Step 3d: 补 `ApiResults.Run` 的兜底 catch（顺带修掉全项目缺口）**

  `Endpoints/ApiResults.cs` 现在只 catch `ApiException` —— DB 层异常（`SqliteException`）会直接逃成**裸 500**：没有 `application/problem+json`、没有 `code`。这对 agent 消费方很不友好（拿不到稳定的机器可读错误码）。

  改成 catch 住其余异常 → 500 + `ProblemDetails` + `Extensions["code"] = "internal_error"`。

  **注意两点**：
  - `OperationCanceledException`（客户端断开导致的取消）要**原样抛出**，不要转成 500 —— 否则客户端断开会刷一屏错误日志；
  - **不要把 `ApiException` 的 `Detail` 原文泄露给外部**：`internal_error` 的 `Detail` 用一句通用说明（「内部错误，请查看服务端日志」），真实异常信息只进日志。

  并同步改 `server/IdeaApp.Tests/BulkAndMergeTests.cs` 里那条原子性探针测试 —— 它现在断言 `Assert.ThrowsAnyAsync<SqliteException>`（即把「裸异常逃到客户端」固化成了期望行为）。Task 7 用 SQLite 触发器制造中途失败来验证合并的原子性，那条测试要改成断言 **500 + ProblemDetails + `code == "internal_error"`**，同时仍然断言「第一条源的删除被撤销、没有留下合并结果」（原子性的核心断言不能丢）。

- [ ] **Step 3c: 写发布接线的测试（`EventPublishingTests.cs`）**

  集成测试里把 `IEventPublisher` 换成**记录型的 fake**（在 `TestAppFactory` 里覆盖注册），然后：

  ```csharp
  [Fact] public async Task Create_idea_publishes_idea_created_after_commit
  [Fact] public async Task Update_idea_publishes_idea_updated_with_changed_fields
  [Fact] public async Task Set_tags_publishes_idea_updated_with_tags
  [Fact] public async Task Set_done_publishes_idea_updated_with_done
  [Fact] public async Task Delete_idea_publishes_idea_deleted
  [Fact] public async Task Restore_idea_publishes_idea_restored
  [Fact] public async Task Merge_publishes_idea_merged_with_merged_from
  [Fact] public async Task Create_project_publishes_project_created
  [Fact] public async Task Rename_project_publishes_project_updated_with_name
  [Fact] public async Task Events_are_published_only_after_the_transaction_commits
  // 制造一个事务中途失败（如触发唯一约束冲突），断言失败后**没有任何事件被发布**
  ```

  最后一条是防止幽灵事件的关键测试 —— **必须有**。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现事件模型与扇出式事件总线"`

---

### Task 9: SSE 端点（心跳、Last-Event-ID 补发、resync）

> **注册要求（Task 8 评审带出来的）**：`IEventHandler` 的实现**必须注册为 Singleton**。`EventBus` 在**根** provider 里用 `sp.GetServices<IEventHandler>()` 解析 handler —— 若注册成 Scoped，Development 下 `ValidateScopes` 会让根解析抛 `InvalidOperationException`，Production 下则会泄漏作用域。Task 14 的 webhook handler 同样遵守这一条。
>
> **收工信号（Task 8 实施者带出来的）**：`EventBus` 在 `DisposeAsync` 时会先 `_shutdown.Cancel()` 再排空。所以 **handler 收到的 token 在排空期间已经是取消状态** —— SSE handler（长连接）在收工时会放弃剩余事件，这是正确行为，不要把它当异常。
>
> **测试文件已经偏大**：`EventBusTests.cs` 约 450 行、`EventPublishingTests.cs` 约 600 行。你往里加 handler 相关测试前**先拆文件**（例如 `EventBusTests.cs` 拆成总线机制与释放语义两个文件），不要继续往同一个文件里堆。

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
  - `TrashService.List()`：先查已删项目（`project.deleted_at IS NOT NULL`），每个项目取其**全部** idea（含已删）作为容器；再查顶层 idea = `idea.deleted_at IS NOT NULL` 且（所属项目未删除，或 `idea.deleted_at != project.deleted_at`）。容器排在顶层之前。
  - `RestoreProject`：`UPDATE idea SET deleted_at=NULL, updated_at=now WHERE project_id=? AND deleted_at IS NOT NULL AND deleted_at = (SELECT deleted_at FROM project WHERE id=?)` —— 只恢复「因删项目而删」的。
  - **删项目时必须同步写其下想法的 `deleted_at`**（与项目 `deleted_at` 同一时刻）。**并且要为每条被软删的想法补发一条 `idea.deleted` 事件** —— 否则订阅端（浏览器、SSE）只知道项目没了，不知道它下面的想法也没了，前端列表会留着已删的条目。`ProjectService.Delete`（Task 4 建的）现在**只软删项目行**，其下想法仍是 active —— 结果是「删掉的项目下的想法继续出现在 `GET /api/ideas` 列表里」。本 task 要把这段语义补上：`ProjectService.Delete` 改为在同一个事务里把项目 `deleted_at` 与其下未删想法的 `deleted_at` 一起置为同一时刻。**这是 Task 10 的核心职责，不是可选的。**
  - **恢复项目时的原子性**：项目行与其下想法的 `deleted_at` 必须在**同一个事务**里一起恢复（用 `IdeaRepository.InTransaction()`）。
  - `IdeaService.Restore` 恢复一条想法时，若其所属项目仍在回收站，则**允许恢复**（恢复后该想法出现在所属项目的列表里，但项目本身仍在回收站，直到项目被恢复或到期清理）—— 与 spec §10.5 一致。**不要**因此拒绝恢复。
  - `TrashCleanupWorker`：启动时立刻跑一次，之后每小时一次。判定 `deleted_at < now - RetentionDays`。
  - 清理项目时先删该项目下 idea 行，再删项目行（webhook 靠 FK CASCADE 带走）。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(server): 实现回收站视图、恢复语义与到期清理"`

---

### Task 11: Webhook 配置 API 与 Telegram 模板预设

**Files:**
- Create: `server/IdeaApp/Domain/Webhook.cs`
- Modify: `server/IdeaApp/Contracts/Requests.cs`（Task 4 已建，本 task 往里加 webhook 的请求体 record）
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
    public sealed record ReorderProjectsRequest(string[]? Ids);
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
  > **注意**：`Contracts/Requests.cs` 已由 Task 4 建好，里面已有 `CreateProjectRequest` / `RenameProjectRequest` / `ReorderProjectsRequest`（其 `Ids` 是 `string[]?` 可空）。**只往里加 webhook 的记录，不要整段重写这个文件** —— 照抄本段示例会把 Task 4 的可空标注改回非空。
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
  - **活动时间口径只有一份**：`ProjectRepository.LastActivityAt` 必须返回「未删想法的最大 `updated_at`，无未删想法时取项目自身的 `updated_at`」，**不返回 null**。DTO 的 `ProjectDto.LastActivityAt` 与这里的排序用的是同一个值 —— 前端展示与排序口径不能分叉。

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

// ApiResults 若用静态日志器（Task 8 建的），必须在这里注入，否则未预期异常不记日志
ApiResults.UseLogger(app.Logger);

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
Run: `bash server/scripts/aot-smoke.sh` → Expected: 退出码 0（脚本内部会 publish、启动、断言 `/api/health` 返回 200 而不是 500）

> 这一步是整个后端**唯一**能发现「端点 JSON 序列化在 AOT 下失效」的验证。普通 `dotnet test` 全绿但 AOT 产物 500，是这个项目最可能踩的坑。

- [ ] **Step 7: 提交**

```bash
git add openapi/ tools/ docs/nginx/ README.md server/IdeaApp/Program.cs server/IdeaApp/appsettings.json
git commit -m "feat(server): 接线全部端点、导出 OpenAPI 文档与部署说明"
```
