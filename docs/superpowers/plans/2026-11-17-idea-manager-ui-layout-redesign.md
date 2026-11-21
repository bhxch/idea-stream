# Idea 管理工具 · 前端布局重设计 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 先把「标题」这个概念从产品里整个去掉（数据模型、REST 契约、全文索引、前端类型），再做前端布局重设计：宽屏铺满、窄屏外壳压到两行、想法排成两列交错流、卡片只显示一整块正文。

**Architecture:** 分两段。**第一段（Task 1–8）**是前置改动：服务端把 `title` 从「领域记录 → 仓储 → 应用层 → 端点 → 契约 → 索引」整条链上摘掉，索引表从 4 列变 3 列并给旧库加一道「形状检查」（FTS5 虚表的列清单建表时定死，`IF NOT EXISTS` 改不了，不重建就是 500），前端同步改类型并**拆掉 SSE 帧校验里的 title 那一条**（留着它，每一帧都会被判成畸形而丢弃，界面再也不更新）。**第二段（Task 9–15）**是纯前端：主页骨架换成「侧栏 + 铺满的主区」，列表 DOM 从「一条一个卡片」换成「一排两个格子」，往回翻的 prepend 净增条数改成偶数（否则整墙左右互换），卡片改成「一整块正文 + 底部 meta 行」。

**Tech Stack:** .NET 10（Native AOT、`InvariantGlobalization=true`）、ASP.NET Core Minimal API、`Microsoft.Data.Sqlite`（SQLite 3.53.3，`ENABLE_FTS5`）、Vue 3 + TypeScript + Vite、Vitest、Playwright。

**Spec:** `docs/superpowers/specs/2026-11-17-idea-manager-ui-layout-redesign.md` —— 计划从规格推导，**执行者两个都要读**。

---

## Global Constraints

这些是规格与仓库的项目级要求，**每个 task 都隐含包含**：

- **顺序不能反**：§4「去掉标题」必须先于 §5–§10「布局重设计」做完（规格 §4.3 J）。否则布局要按「有标题」写一遍再拆一遍。
- **提交用 Conventional Commits**：`feat` / `fix` / `docs` / `refactor` / `test` 前缀 + 中文说明，正文先说清 **why**（为什么这么改），再一句话概括 what。
- **每个 task 提交前先对一遍 Files 清单**：`git status --short` 的结果里，本 task 的 `Files` 列出的每个路径都要出现（改了或删了都行）。漏掉的文件会一直躺在工作树里 —— 后面没有别的 task 会 `git add` 它，收尾的「工作树干净」就过不了。
- **`.superpowers/` 与 `.temp/` 不进提交**（`.gitignore` 已排除，别 `git add -A`）。
- **服务端**：.NET 10 Native AOT + SQLite，**无 ORM、无反射式序列化**。新端点返回的静态类型必须在 `Contracts/JsonContext.cs` 里有 `[JsonSerializable]`，否则 AOT 产物 500 而 `dotnet test` 全绿。
- **不引新的依赖**（NuGet 与 npm 都不引）。
- **前端**：Vue 3 + TS + Vite。`npm run typecheck`（vue-tsc）必须无错。
- **契约变更后必须重新生成 `openapi/idea-v1.json`**：`dotnet run --project tools/ExportOpenApi`。`OpenApiDocumentTests` 会拿提交进仓库的 JSON 与运行中的应用比对，忘了重新生成就红。
- **不引 SQL 触发器维护索引**：`Infrastructure/schema.sql` 按 `;` 切分语句（`SqliteDatabase.cs:159`），`CREATE TRIGGER ... BEGIN...END;` 会被切坏。
- **排序语义不变**：`GET /api/ideas` 恒为 `updated_at ASC, id ASC`（最老在顶）。分页是「往回翻历史」，返回的数组仍是这个顺序。
- **⚠️ `title` 这个词在本仓库被严重超载，别误伤**（规格 §4.5）：`ApiProblem.title` / `ProblemDetails.title`、HTML 的 `title=` 属性、CSS 类名里带 "title" 的（`.merge-dialog__title`、`.bar__title` 等）、`UnicodeCategory.TitlecaseLetter`、webhook 模板里的 `{{title}}`（**消息**标题，不是想法标题）—— **全都不要动**。只有 `{{idea.title}}` 与想法的 `title` 字段要删。
- **主题跟随系统**（没有手动切换开关）；只用现有主题变量（`--border` / `--accent` / `--chip-bg` / `--muted` / `--panel` / `--danger` / `--chip-bg` 等，见 `web/src/styles/theme.css`），**不引入新颜色**。
- **断点**：侧栏 `1024px`（沿用现状）、过滤栏**形态**切换 `1023px`（窄屏 = 没有侧栏的那一档，状态三态收成下拉）、两列 `768px`、触摸目标统一 `767px`（把现有的 `640px` 一并改掉，规格 §D9 / §6.10）。**三套断点并存是规格本身的样子**（§D7 与 §D9/§6.10）：形态跟着「有没有侧栏」走，两列与触摸目标跟着「屏幕多窄」走。
- **注释与文档用中文**，与仓库现有风格一致；每个公开类型/方法都要有 XML 文档注释 / TSDoc 说明「为什么」。
- **E2E 一轮 = 一个端口 + 一个库**，端口从 5300 起（见 `web/e2e/README.md`）。用例必须 `import { test, expect } from './fixtures'`（从 `@playwright/test` 导入则每例的自动复位不生效）。
- **因结构变化而改写的既有断言，要逐条说明理由**（规格 §9 回归第 1 条）：理由写在**该 task 的提交说明**里 —— 「结构变了，断言没写错」。
  这条对**前端单测与 e2e 都适用**（规格只提了前端单测，但 e2e 的断言同样要说明）。

### 闸门命令

- 后端单测/集成测试：`dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
- 前端单测：`cd web && npm test`
- 类型检查：`cd web && npm run typecheck`
- 全部闸门：`bash verify.sh`（四步：后端测试 → 取件器单测 → AOT 冒烟 → 取件器测试脚本）
- E2E：`bash web/e2e/run-e2e.sh --fresh`（每轮一个端口 + 一个独立库）

---

## Review Focus

规格说了「必须做到什么」，没说「边界输入怎么办」。下面八条是最可能咬人的地方 —— **每条都要有对应的测试钉住**（在下面指定的 task 里加）：

1. **旧库的 `idea_fts` 还是 4 列，而代码用 3 个 bm25 权重** —— FTS5 虚表的列清单建表时定死，`IF NOT EXISTS` 改不了，形状不对就永远是 4 列。行为：拿一个**旧形状**的库启动，索引表被重建成 3 列、`/api/search` 正常返回 200 且有命中。
   ⚠️ **后果不是 500**（规格 §4.3 A 写的是 500，**实测不成立**：用本仓库自己的 SQLite 3.53.3 试过 ——
   `bm25(4 列表, 3 个权重)` **不报错**，多余的权重忽略、缺少的按默认 1.0 处理）。
   真实后果是**相关度静默错位**：4 列上 3 个权重落到 title=5.0 / content=5.0 / tags=3.0，
   项目名那列拿默认的 1.0；旧库里那个永远为空的 `title` 列也一直在。→ **Task 1**。
2. **bm25 的权重个数与 `idea_fts` 的列数悄悄对不上** —— 加一列忘了改权重，搜索结果的相关度会静默错掉（不报错）。行为：权重个数 == `PRAGMA table_info(idea_fts)` 的列数。→ **Task 1**。
3. **SSE 帧校验里留着 `record.title === null || typeof record.title === 'string'`** —— 服务器不再发 `title` 时 `record.title` 是 `undefined`，`undefined === null` 为假 → **每一帧都被判成畸形而丢弃** → 界面再也不更新（静默但致命）。行为：一帧**不带 `title` 键**的想法快照必须被接受并交给 `onEvent`。→ **Task 6**。
4. **老客户端仍然发 `title`** —— .NET 的 System.Text.Json 默认忽略未知成员，所以 `{"content":"x","title":"y"}` 必须**静默忽略**那个字段（内容正常保存、返回 201、响应里没有 `title`），**不为此报错**（规格 §4.3 F / §7.8）。→ **Task 2**。
5. **老 webhook 模板里写着 `{{idea.title}}`** —— 占位符被删掉后它必须**渲染成空串**（不是报错、不是把占位符原样留着）。
   ⚠️ 渲染器本身**不提供**这个语义：键不存在 = 「未识别的占位符」→ **原样保留**；只有「键在、值为 null」才渲染成空串。
   所以要拿到空串，渲染器得认识「这个占位符已下线」—— 见 Task 4 的 `RetiredPlaceholders`。→ **Task 4**。
6. **旧库里 `idea` 表还留着 `title TEXT NULL` 列** —— `CREATE TABLE IF NOT EXISTS` 意味着旧库保留那一列（永远 NULL，代码不再读写），**代码不得依赖这一列在不在**。行为：旧库升级后 `/api/ideas` 正常，代码里不再 SELECT/INSERT 这一列。→ **Task 3**。
7. **往回翻加载更早的一页时 prepend 的条数是奇数** —— 现在每页实际要 101 条（`PAGE_SIZE + 1`，多要的那条是探针），奇数 → **每翻一页整墙左右互换**。行为：满页时丢掉**最老**的那一条（探针），净增 100 条（偶数）→ 已有卡片不换边。→ **Task 12**。
8. **悬停才显形的按钮成了「唯一入口」** —— 键盘 Tab 到它时必须也显形（`:focus-within`）；而**没有悬停能力**的设备（`(hover: none)`）与窄屏（≤767px）一律常驻。行为：窄屏与无 hover 能力的设备上勾选框与 🏷🗑 始终可见；键盘聚焦时也可见。→ **Task 15**（**不是 Task 10**，理由见下面）。
   ⚠️ **为什么改派到 Task 15**：显隐是**纯 CSS 媒体查询 + 伪类**，而单测环境（vitest 的 jsdom）
   **不求值媒体查询** —— `window.matchMedia` 与 `CSS.supports` 都是 undefined，
   在单测里读不出「窄屏常驻 / 桌面隐藏」的差别，写出来的只能是一批恒真的断言（比没有测试更坏）。
   能钉住的只有 **e2e**（Playwright + Chromium 能读真实的 `opacity`，也能用 `hasTouch: true` 的
   context 模拟「无悬停能力」的设备）。Task 15 的验收表里已经有「桌面端悬停才显形、键盘聚焦也显形」
   与「窄屏常驻」两条，还需补「**无悬停能力的设备**」那一支 —— 详见 Task 15 Step 3。

---

## File Structure

**新增**

| 文件 | 职责 |
|---|---|
| `web/src/lib/text.ts` | 文本小工具：`firstLine(text, maxLength?)` 取正文首行（供可访问名称与合并对话框用） |
| `web/src/lib/text.spec.ts` | 上述的单测 |
| `web/src/lib/rows.ts` | `toRows(ideas)`：把想法切成「一排两个格子」（`<script setup>` 里不能 `export`，所以单独一个文件） |
| `web/src/lib/rows.spec.ts` | 上述的单测 |
| `web/src/components/ProjectDropdown.vue` | 窄屏顶栏的项目下拉（数据源与侧栏同一份：`projects` + `currentId`，排序方式仍由后端给） |
| `web/src/components/MoreMenu.vue` | 顶栏的「⋯」下拉菜单（项目管理 / 回收站 / Webhook） |

**服务端修改（Task 1–4）**

| 文件 | 改什么 |
|---|---|
| `server/IdeaApp/Infrastructure/schema.sql` | `idea` 表去掉 `title` 列；`idea_fts` 改成 3 列 |
| `server/IdeaApp/Infrastructure/SqliteDatabase.cs` | `Initialize()` 里加「FTS 表形状检查」 |
| `server/IdeaApp/Infrastructure/SearchIndex.cs` | `CurrentVersion` 升 `"2"`；写入/回填去掉 title 列与参数；分块参数从每行 5 个变 4 个 |
| `server/IdeaApp/Infrastructure/SearchIndexInitializer.cs` | 版本升到 `"2"` 后自动整体重建（行为已具备，只需保证版本号对得上） |
| `server/IdeaApp/Infrastructure/SearchRepository.cs` | SELECT 去掉 `i.title`；bm25 权重改成 3 个（正文/标签/项目） |
| `server/IdeaApp/Infrastructure/IdeaRepository.cs` | `SelectColumns` / `Insert` / `UpdateContent` / `Read` 去掉 title（reader 下标前移） |
| `server/IdeaApp/Infrastructure/IdeaMatches.cs` | 命中字段去掉 `"title"` |
| `server/IdeaApp/Infrastructure/EventPlaceholders.cs` | 删掉 `idea.title` 占位符 |
| `server/IdeaApp/Infrastructure/TemplateRenderer.cs` | 加一张「已下线占位符」表：`{{idea.title}}` 渲染成**空串**（未识别的占位符仍然原样保留） |
| `server/IdeaApp/Application/WebhookService.cs` | Telegram 预设模板改用 `{{idea.content}}` |
| `server/IdeaApp/Domain/Idea.cs`、`Domain/IdeaEvent.cs` | `Idea` / `IdeaSnapshot` 去掉 `Title` |
| `server/IdeaApp/Application/IdeaService.cs` | `Create` / `Update` / `Merge` 去掉 title 参数；删 `NormalizeTitle` |
| `server/IdeaApp/Contracts/Dtos.cs`、`Contracts/Requests.cs` | `IdeaDto` / 三个请求体去掉 `Title` |
| `server/IdeaApp/Endpoints/IdeaEndpoints.cs`、`Endpoints/BulkIdeaEndpoints.cs` | 端点不再传 title |
| `server/IdeaApp/Infrastructure/OpenApiEventTransformer.cs` | 手写的 `IdeaSnapshot` schema 去掉 `title` |
| `server/IdeaApp.Tests/SkillDocGuardTests.cs` | **新增**一条字段级守卫（契约与手册里都没有 idea 的 `title`） |

**前端修改（Task 6–7、9–14）**

| 文件 | 改什么 |
|---|---|
| `web/src/api/types.ts` | `Idea` 去掉 `title`；`SearchMatch.field` 去掉 `'title'` |
| `web/src/api/events.ts` | SSE 帧校验去掉 title 那一条 |
| `web/src/lib/highlight.ts` | 删掉 `splitTitleAndBody` / `trimSegments` / `trimLeadingNewlines` |
| `web/src/components/MessageItem.vue` | 卡片改成「正文 + 底部 meta 行」；去掉 `<h2 class="title">` |
| `web/src/components/MessageList.vue` | 输出「一排两个格子」 |
| `web/src/components/FilterBar.vue` | 窄屏紧凑形态（状态分组容器保持可见）；`defineExpose` 暴露搜索框聚焦 |
| `web/src/components/ProjectTabs.vue`（**删除**） | 窄屏的项目切换改成顶栏下拉后没人用了，连同它那条 e2e 用例一起删 |
| `web/src/components/MergeDialog.vue` | 用正文首行代替标题 |
| `web/src/pages/HomePage.vue` | 骨架、顶栏、两列、窄屏外壳 |
| `web/src/stores/ideas.ts` | 往回翻裁掉探针；`OlderPage` 加 `hasMore` |
| `web/src/composables/useHistoryScroll.ts` | prepend 后的视口补偿（边界排用锚点校正） |
| `web/src/styles/base.css` | `.page` 去掉 `max-width`；新增窄表单容器 |

**文档**

| 文件 | 改什么 |
|---|---|
| `skills/idea-manager/references/api.md` / `calling.md` / `scenarios.md` / `webhooks.md` / `SKILL.md` | 去掉 title 相关字段与占位符 |
| `docs/superpowers/specs/2026-11-11-idea-manager-design.md` | §9 / §10.4 / §11.2 / §12.2 / §12.3 / §13.2 / §13.3 / §19 |
| `docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md` | §8.1 / §10.2 / §11.3 / §11.4 / §12.3 / §13 |
| `docs/superpowers/specs/2026-11-13-skill-distribution-design.md` | 「标题空串 = 清成无标题」那句 |
| `README.md` | curl 示例里的 `"title"` |
| `openapi/idea-v1.json` | 重新导出（不手改） |

> **行号说明**：下面每个 task 的 `Files` 里给的是**改动前**的行号（以本计划落盘时的工作树为准）。前面的 task 改过文件之后，后面的 task 的行号会漂 —— **以文件里的符号与内容为准**，行号只用来快速定位。

---

# 第一段：去掉「标题」这个概念（规格 §4）

### Task 1: 全文索引去掉标题列、FTS 表形状检查、bm25 权重重编

**Files:**
- Modify: `server/IdeaApp/Infrastructure/schema.sql:12-24`（`idea` 表**本 task 不动**）、`:77-83`（`idea_fts` 的 DDL）
- Modify: `server/IdeaApp/Infrastructure/SqliteDatabase.cs:125-149`（`Initialize()`：应用 schema **之前**加形状检查）
- Modify: `server/IdeaApp/Infrastructure/SearchIndex.cs:40`（`CurrentVersion`）、`:102-121`（`UpsertIdea`）、`:124-145`（`InsertIdeaRow`）、`:247-299`（`LoadIdeaRows`）、`:312-347`（`InsertIdeaRows`）、`:355-360`（`IdeaIndexRow`）
- Modify: `server/IdeaApp/Infrastructure/SearchRepository.cs:70-117`（`Ideas()` 里读项目名的下标）、`:199-242`（`IdeasPageSql`）、`:481-491`（`ReadIdea`）
- Modify: `server/IdeaApp/Infrastructure/IdeaMatches.cs:42-56`（`For`）
- Modify: `server/IdeaApp/Infrastructure/IdeaRepository.cs:394-438`（`Insert` 的索引调用）、`:441-484`（`UpdateContent` 的索引调用）、`:585-601`（`ReplaceTags` 的索引调用）
- Test: `server/IdeaApp.Tests/SearchIndexTests.cs`（**追加 + 清理**，见下）、`server/IdeaApp.Tests/SearchEndpointTests.cs`（改期望）

**Interfaces:**
- Consumes: 无（本 task 是第一段的最底层，不依赖别的 task）
- Produces:
  ```csharp
  namespace IdeaApp.Infrastructure;

  public static class SearchIndex
  {
      /// 当前索引版本。分词规则或列清单变了就 +1，启动时会自动重建索引。
      public const string CurrentVersion = "2";

      /// 写入/更新一条想法的索引行（先删后插，保证幂等）。
      /// **没有 title 参数了** —— 索引里不再有标题这一列。
      public static bool UpsertIdea(
          SqliteConnection connection,
          SqliteTransaction? transaction,
          string ideaId,
          string content,
          IReadOnlyList<string> tags,
          string projectName);

      /// 重建一个项目下全部想法的索引行。
      public static void ReindexProjectIdeas(
          SqliteConnection connection, SqliteTransaction? transaction, string projectId);

      /// 把全部想法的索引行整体写入（整体重建专用）。
      public static void InsertAllIdeaRows(SqliteConnection connection, SqliteTransaction? transaction);

      // internal 记录：去掉 Title 字段。
      internal sealed record IdeaIndexRow(
          long RowId,
          string Content,
          IReadOnlyList<string> Tags,
          string ProjectName);
  }
  ```
  ```csharp
  namespace IdeaApp.Infrastructure;

  public sealed class SearchRepository(SqliteDatabase db)
  {
      /// idea_fts 的 bm25 权重，**按列顺序**：正文、标签、项目。
      /// 权重个数必须等于 idea_fts 的列数 —— FTS5 的列清单建表时定死。
      internal static readonly double[] IdeaFtsWeights = [5.0, 5.0, 3.0];
  }
  ```
  ```csharp
  namespace IdeaApp.Infrastructure;

  public sealed class SqliteDatabase
  {
      /// 建库建表并写入内置收件箱。可重复调用。
      /// **在建表之前**会检查 idea_fts 的列清单：与 schema.sql 不一致就 DROP 掉，
      /// 让新的 DDL 按正确的列重建（旧的 FTS5 虚表改不了列清单）。
      public void Initialize();
  }
  ```

- [ ] **Step 1: 写失败的测试**

  **（a）形状检查（Review Focus 第 1、2 条）** —— 在 `server/IdeaApp.Tests/SearchIndexTests.cs` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `Initialize_rebuilds_a_legacy_idea_fts_with_the_title_column` | 造一个**旧形状**的 `idea_fts`（4 列，含 title），跑 `SqliteDatabase.Initialize()` → `PRAGMA table_info(idea_fts)` 的列名恰好是 `["content","tags","project"]`（**3 列**） |
  | `Initialize_keeps_the_idea_fts_shape_when_it_already_matches` | 新库（跑一次 `Initialize()` 再跑一次）→ 列名仍是那 3 列，且**没有**丢掉数据 |
  | `Search_weights_match_the_index_column_count` | `PRAGMA table_info(idea_fts)` 的列数 == `SearchRepository.IdeaFtsWeights.Length`（**权重个数与列数必须相等**） |

  > **造「旧形状」的库的做法**（`TestAppFactory` 的库文件是在**应用启动时**才建的，
  > 所以旧形状必须在启动**之前**造好）：
  >
  > ```csharp
  > var db = new SqliteDatabase(databasePath);
  > using (var connection = db.Open())
  > using (var command = connection.CreateCommand())
  > {
  >     // 旧版本的 idea_fts：四列，含 title。
  >     command.CommandText =
  >         "CREATE VIRTUAL TABLE idea_fts USING fts5(title, content, tags, project);";
  >     command.ExecuteNonQuery();
  > }
  > db.Initialize();   // 这一步之后列名就该是那 3 列了
  > ```
  >
  > `idea` 表**不用手建** —— `Initialize()` 里的 `CREATE TABLE IF NOT EXISTS idea` 会建。
  > 旧形状的 `idea_fts` 行会被 DROP 掉（内容本该由 `EnsureIndexed` 从 `idea` 重建），
  > 所以这里**不用**往旧 `idea_fts` 里塞数据。
  >
  > ⚠️ `PRAGMA table_info(idea_fts)` 返回的是**声明的列**（FTS5 的隐藏列 `rank` 不在里面 ——
  > 已实测：`fts5(title, content, tags, project)` 的 `table_info` 恰好返回那 4 列），
  > 所以直接比对列名即可。

  **（b）端到端：旧库升级后搜索可用** —— 在 `server/IdeaApp.Tests/SearchEndpointTests.cs` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `Search_works_on_a_database_upgraded_from_the_old_schema` | **在 `factory.CreateClient()` 之前**把旧形状的 `idea_fts`（4 列）造进 `factory.DatabasePath` → `CreateClient()` 启动应用（`Initialize()` → `EnsureIndexed`）→ `POST /api/ideas` 造一条正文含「咖啡」的想法 → `GET /api/search?q=咖啡` 返回 **200** 且命中那条想法 |

  > ⚠️ **这条测试单独用是钉不住形状检查的**：`bm25(4 列表, 3 个权重)` 在本仓库的 SQLite 3.53.3 上
  > **不报错**（实测：多余的权重忽略、缺少的按默认 1.0 处理），所以「搜索 200 且命中」在**没有**形状检查时
  > **也是绿的**。它是一条**回归保护**（确保升级过程不把搜索搞挂），
  > 真正钉住形状的是（a）里那条 `PRAGMA table_info(idea_fts)` 的列名断言。
  > 造旧形状的库的做法见上面（a）里的那段代码。

  **（c）相关度排序按新权重重调** —— 修改 `server/IdeaApp.Tests/SearchEndpointTests.cs` 里已有的两条用例：

  | 用例 | 改法 |
  |---|---|
  | `Search_ideas_group_is_sorted_by_relevance`（原：标题命中排在正文命中的前面） | 改成：**正文命中的那条排在项目命中的那条前面**。两条数据：① 正文「咖啡」、项目「工作」；② 正文「今天天气不错」、项目「咖啡项目」。期望顺序 `[第一条, 第二条]` |
  | `Search_offset_skips_the_most_relevant_ones`（原：标题/正文/正文三条） | 改成同样两条（正文命中 + 项目命中），期望顺序 `[第一条, 第二条]`；⚠️ **`:640` 的 `Assert.Equal(3, ordered.Length)` 要一并改成 `Assert.Equal(2, ordered.Length)`**（数据从三条变两条，这个断言必须跟着变，否则必红）；其余断言（先取全量再逐页对照）**不动** |

  > 这两条断言的意义：正文权重从 1.0 提到 5.0 之后，正文命中才压得过项目命中。
  > **实测过**（SQLite 的 bm25）：权重 `1.0, 5.0, 3.0` 时顺序是 `[项目命中, 正文命中]`，
  > 权重 `5.0, 5.0, 3.0` 时是 `[正文命中, 项目命中]` —— 顺序会**反过来**，
  > 所以「权重没调高」的实现会让这两条用例红。若在本仓库的 SQLite（3.53.3）上实测顺序与上面相反，
  > 把 `IdeaFtsWeights` 的正文权重再往上抬（8.0），并把这个实测值写进测试的注释里。

  **（d）命中字段只剩三种** —— 在 `server/IdeaApp.Tests/SearchEndpointTests.cs` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `Search_matches_only_carry_content_tags_and_project` | 造一条正文含「咖啡」、带标签「烘焙」、项目名「工作复盘」的想法 → 搜「咖啡」→ `matches[].field` 的取值集合 ⊆ `{"content","tags","project"}`，**不含 `"title"`** |

  同时删除已有的 `Get_ideas_matches_title`（整个用例消失 —— 想法不再有标题，搜不到标题里的词）。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SearchIndexTests|FullyQualifiedName~SearchEndpointTests"`
  Expected: 失败 ——（a）里的形状用例红（旧库的 `idea_fts` 还是 4 列）；
  `Search_weights_match_the_index_column_count` 会因为 `SearchRepository.IdeaFtsWeights` 还不存在而**编译失败**。
  ⚠️（b）里的端到端用例**不会红**（bm25 权重数不匹配不报错，见上面的说明）—— 它是回归保护，不是形状检查的证据。

- [ ] **Step 3: 改 schema 的 `idea_fts` DDL**

  在 `server/IdeaApp/Infrastructure/schema.sql:77-83` 把

  ```sql
  -- 想法的全文索引。四列对应四个可检索字段：标题、正文、标签名、项目名。
  CREATE VIRTUAL TABLE IF NOT EXISTS idea_fts USING fts5(
      title, content, tags, project,
      tokenize = 'unicode61 remove_diacritics 0'
  );
  ```

  改成三列（**注释也要改 —— 「四列对应四个可检索字段」已经不成立**）：

  ```sql
  -- 想法的全文索引。三列对应三个可检索字段：正文、标签名、项目名。
  -- ⚠️ FTS5 虚表的列清单**建表时定死**，IF NOT EXISTS 改不了 ——
  -- 旧库（4 列，含 title）必须先 DROP 再重建，见 SqliteDatabase.Initialize 里的形状检查。
  CREATE VIRTUAL TABLE IF NOT EXISTS idea_fts USING fts5(
      content, tags, project,
      tokenize = 'unicode61 remove_diacritics 0'
  );
  ```

  ⚠️ **本 task 不改 `idea` 表**（`title TEXT NULL` 那一列留着，Task 3 才去掉）。
  索引里没有标题了，但业务表里那一列还在 —— 那是 Task 3 的事。

- [ ] **Step 4: 在 `SqliteDatabase.Initialize()` 里加形状检查**

  在 `server/IdeaApp/Infrastructure/SqliteDatabase.cs:125-149`，**应用 schema 之前**（`foreach (var statement in ReadSchemaStatements())` 那一段**之前**）加一段：

  ```csharp
  // 旧库的 idea_fts 是 4 列（含 title），而 schema.sql 现在是 3 列。
  // FTS5 虚表的列清单建表时定死，CREATE ... IF NOT EXISTS 改不了 ——
  // 所以形状对不上时先 DROP，让后面的 schema 按新 DDL 重建。
  //
  // ⚠️ 必须放在 schema **之前**：schema 里的 CREATE VIRTUAL TABLE IF NOT EXISTS
  // 遇到已存在的表是 no-op，先 DROP 才轮得到新 DDL。
  DropLegacyIdeaFts(connection, transaction);
  ```

  `DropLegacyIdeaFts` 是要新增的私有静态方法，做三件事：
  1. `PRAGMA table_info(idea_fts)` 读出列名（**只返回声明的列**，FTS5 的隐藏列 `rank` 不在里面 —— 已实测：`fts5(title, content, tags, project)` 的 `table_info` 恰好返回那 4 列）；
  2. **表不存在**（新库，`table_info` 返回 0 行）→ 什么都不做（schema 会建它）；
  3. 列名**恰好等于** `["content", "tags", "project"]` → 什么都不做；
  4. 其它情况 → `DROP TABLE idea_fts;`（让它被 schema 重新建成 3 列）。

  ⚠️ **DROP 之后索引行就没了** —— 靠 `SearchIndex.CurrentVersion` 升到 `"2"` 触发整体重建（Step 5）。所以形状检查与版本号**必须一起改**，少一个就是「索引空着但版本对上了」。

- [ ] **Step 5: 改 `SearchIndex`（版本号、写入、回填）**

  在 `server/IdeaApp/Infrastructure/SearchIndex.cs`：

  - `CurrentVersion` 从 `"1"` 改成 `"2"`（`:40`）。注释补一句：**为什么必须是 2** —— 旧库的 `idea_fts` 被 DROP 重建后是空表，版本号不变的话 `SearchIndexInitializer.EnsureIndexed` 会认为索引已是最新而直接返回，索引就永远空着。
  - `UpsertIdea`（`:102-121`）：删掉 `string? title` 参数与它的 XML 文档行；调用 `InsertIdeaRow` 时不再传 title。
  - `InsertIdeaRow`（`:124-145`）：SQL 改成
    `INSERT INTO idea_fts(rowid, content, tags, project) VALUES ($rowId, $content, $tags, $project);`，
    参数去掉 `("$title", ToIndexText(title))`。
  - `LoadIdeaRows`（`:247-299`）：SELECT 改成 `SELECT i.rowid, i.content, p.name, (子查询标签名)`，
    **reader 下标全部前移一位**：content 从 2 → 1、project 从 3 → 2、标签子查询从 4 → 3。
    构造 `IdeaIndexRow` 时不再传 title（`reader.IsDBNull(1) ? null : reader.GetString(1)` 那行删掉，content 直接 `reader.GetString(1)`）。
  - `InsertIdeaRows`（`:312-347`）：
    - SQL 改成 `INSERT INTO idea_fts(rowid, content, tags, project) VALUES `；
    - 每行的占位符从 `($r{i}, $t{i}, $c{i}, $g{i}, $p{i})` 改成 `($r{i}, $c{i}, $g{i}, $p{i})`；
    - `parameters` 的初始容量从 `chunk.Count * 5` 改成 `chunk.Count * 4`；
    - 去掉 `parameters.Add(($"$t{i}", ToIndexText(row.Title)));`；
    - ⚠️ **`chunkSize` 要改**：每行从 5 个参数变 4 个 → `999 ÷ 4 = 249.75` → **249 行**是还能塞进 SQLite 最老的变量上限（999）的最大块大小（250 行要 1000 个参数，超了）。把 `const int chunkSize = 199;` 改成 `const int chunkSize = 249;`，并把上面那段注释里「199 不是随手取的：每行 5 个参数，999 ÷ 5 = 199.8」改成对应的 4 个参数的算式。
  - `IdeaIndexRow`（`:355-360`）：删掉 `string? Title` 字段。

  ⚠️ **`SearchIndexInitializer` 不用改代码** —— 它读的是 `SearchIndex.CurrentVersion`，版本号一升，下次启动就会整体重建（清空三张索引表 + 重灌）。它的职责与本 task 的变化无关。

- [ ] **Step 6: 清理 `SearchIndexTests.cs` 里的 title 残留与硬编码版本号**

  `server/IdeaApp.Tests/SearchIndexTests.cs` 直接往 `idea_fts` 与 `idea` 表里直插，
  那些 SQL 与 DDL 是一一对应的 —— 本 task 改了表结构，这些语句必须一起改：

  | 位置 | 改什么 |
  |---|---|
  | `:77`、`:94` | `Assert.Equal("1", ReadIndexVersion());` → `Assert.Equal(SearchIndex.CurrentVersion, ReadIndexVersion());`（**两处都要改**：版本号升到 `"2"` 之后写死的 `"1"` 必然对不上，`EnsureIndexed_rebuilds_when_the_version_does_not_match` 与 `EnsureIndexed_is_a_noop_when_version_matches` 都会红；写成常量，以后版本号再升也不用改这里） |
  | `:153` | `INSERT OR REPLACE INTO search_meta(key, value) VALUES ('index.version', '1');` → 值改成 `SearchIndex.CurrentVersion`（**别写死 '1'**：版本号升到 2 之后，写死的 '1' 会让 `EnsureIndexed` 真的重建 → 哨兵行被删 → `EnsureIndexed_skips_the_rebuild_when_another_instance_rebuilt_it_meanwhile` 断言 `IdeaFtsRowIds() == [999999]` 失败） |
  | `:154` | `INSERT INTO idea_fts(rowid, title, content, tags, project) VALUES (999999, '哨兵', '', '', '');` → 去掉 title 列与实参 |
  | `:233` | `SearchIndex.UpsertIdea(connection, null, id, title, content, tags, projectName);` → 少传 title 实参 |
  | `:431` | 哨兵行的 `INSERT INTO idea_fts(rowid, title, content, tags, project)` → 去掉 title 列 |
  | `:498` | `private sealed record IndexRow(string Title, string Content, string Tags, string Project);` → 去掉 `Title` |
  | `:505`、`:523` | `SELECT title, content, tags, project FROM idea_fts …` → `SELECT content, tags, project …`，**读列的下标同步前移** |
  | `:557-564` | `ReadIdeaTitle` 辅助方法（`SELECT title FROM idea`）**整个删掉**，连同 `:228` 的调用 |

  ⚠️ **不要把这份清理派给 `SearchIndexConsistencyTests.cs`** —— 那个文件里**没有任何 title 相关语句**
  （它只用 `MatchFts` / `ReadFtsText(..., "tags")`），它在本 task 里**不需要改**。

- [ ] **Step 7: 改 `IdeaRepository` 里三处索引调用**

  在 `server/IdeaApp/Infrastructure/IdeaRepository.cs` —— `SearchIndex.UpsertIdea` 一共**三处**调用
  （少改一处就编译不过）：
  - `Insert`（`:434-437` 附近）：`SearchIndex.UpsertIdea(connection, transaction, idea.Id, idea.Title, idea.Content, tags, ...)` → 去掉 `idea.Title` 那一段。
  - `UpdateContent`（`:470-479` 附近）：同理，去掉 `idea.Title`。
  - `ReplaceTags`（`:591-601` 附近，读回 `Idea` 之后那次）：同理，去掉 `idea.Title`。
  - ⚠️ **本 task 不动 `SelectColumns` / `Read` / `Insert` 里的 title 列** —— 业务表还有那一列（Task 3 才去掉）。这里只是索引不再写它。

- [ ] **Step 8: 改 `SearchRepository`（SELECT 与 bm25 权重）**

  在 `server/IdeaApp/Infrastructure/SearchRepository.cs`：
  - `IdeasPageSql`（`:216-242`）：
    - SELECT 去掉 `i.title`：`SELECT i.id, i.project_id, i.content, i.done, i.created_at, i.updated_at, i.deleted_at, p.name, `；
    - bm25 调用改成用 `IdeaFtsWeights` 拼出来（不要写死字面量 —— 写死了将来加列就会忘了改）：
      ```csharp
      sql.Append($"bm25(idea_fts, {string.Join(", ", IdeaFtsWeights)}) AS score ");
      ```
    - 注释里那段「`idea_fts` 的列序是 `title, content, tags, project` → 标题 10、正文 1、标签 5、项目 3」改成
      「列序是 `content, tags, project` → 正文 5.0、标签 5.0、项目 3.0。**权重个数必须等于列数**」。
  - `ReadIdea`（`:482-491`）：删掉 `Title:` 那一行，**reader 下标全部前移一位**：
    content 从 3 → 2、done 从 4 → 3、createdAt 从 5 → 4、updatedAt 从 6 → 5、deletedAt 从 7 → 6。
    ⚠️ `ReadIdea` 构造的是 `Idea`（领域记录）—— **本 task 里 `Idea` 仍有 `Title` 字段**（Task 3 才删），所以这里给 `Title: null` 占位。
  - ⚠️ **`Ideas()`（`:94`）里还有第三处按位置读列**：
    `rows.Add((ReadIdea(reader), reader.GetString(8)));` —— 那个 `8` 是**项目名**那一列。
    去掉 `i.title` 之后项目名落到 **7**（`8` 变成 `score` 列），
    **不改就去读一个 REAL/NULL → `InvalidCastException` → `/api/search` 对所有用户 500**。
    改成 `reader.GetString(7)`。
  - ⚠️ **本仓库一共有三处按位置读列**：`Ideas()` 的项目名、`ReadIdea`、`IdeaRepository.Read`。
    改 SELECT 时**三处一起数**（少改一处不会编译报错，只会在运行时炸）。
  - `IdeaFtsWeights`（`internal static readonly double[]`）加在 `SearchRepository` 类的顶部。

- [ ] **Step 9: 改 `IdeaMatches`（命中字段去掉 title）**

  在 `server/IdeaApp/Infrastructure/IdeaMatches.cs:42-56`：删掉 `Add(matches, "title", idea.Title, keyword);`。
  类注释里「标题、正文、标签名、项目名四个字段各算一遍」改成「正文、标签名、项目名三个字段各算一遍」；
  `Add` 的 `field` 参数文档改成（`content` / `tags` / `project`）。

- [ ] **Step 10: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS（**包括原有的全部测试** —— 索引重建在每个测试实例建库时都会跑一次，不能把现有测试搞红）。

- [ ] **Step 11: 跑 AOT 冒烟**

  Run: `bash server/scripts/aot-smoke.sh`
  Expected: PASS。**这一步不能省** —— 本 task 没加新类型，但 `SearchRepository` 的改动不能引入反射式序列化。

- [ ] **Step 12: 提交**

  ```bash
  git add server/IdeaApp/Infrastructure/schema.sql server/IdeaApp/Infrastructure/SqliteDatabase.cs \
          server/IdeaApp/Infrastructure/SearchIndex.cs server/IdeaApp/Infrastructure/SearchRepository.cs \
          server/IdeaApp/Infrastructure/IdeaMatches.cs server/IdeaApp/Infrastructure/IdeaRepository.cs \
          server/IdeaApp.Tests/SearchIndexTests.cs server/IdeaApp.Tests/SearchEndpointTests.cs \
          server/IdeaApp.Tests/SearchIndexConsistencyTests.cs
  git commit -m "fix(search): 全文索引去掉标题列，旧库按形状重建

  FTS5 虚表的列清单是建表时定死的，CREATE ... IF NOT EXISTS 改不了 ——
  旧库的 idea_fts 永远是 4 列（含 title）。

  所以在建表之前先比对 PRAGMA table_info(idea_fts) 的列名，对不上就 DROP，
  让新的 DDL 按 3 列重建；版本号同时升到 2 触发整表重建（光 DROP 不重建
  索引就永远是空的）。

  ⚠️ 权重个数与列数对不上**不会报错**（SQLite 3.53.3 实测：多余的权重忽略、
  缺少的按默认 1.0 处理）—— 后果是相关度静默错位：4 列上 3 个权重落到
  title / content / tags，项目名那列拿默认 1.0。旧库里那个永远为空的
  title 列也一直在。

  正文权重从 1.0 提到 5.0：标题列没了之后正文权重 1 会被标签 5 / 项目 3
  压过去，「正文里写了咖啡」的那条排在「项目名带咖啡」的那条后面。"
  ```

---

### Task 2: 对外契约不再有「标题」

**Files:**
- Modify: `server/IdeaApp/Domain/IdeaEvent.cs:39-53`（`IdeaSnapshot`）、`:69`（`changed` 说明里的 title）
- Modify: `server/IdeaApp/Contracts/Dtos.cs:8`（`SearchMatch.Field` 文档）、`:17-46`（`IdeaDto`）
- Modify: `server/IdeaApp/Contracts/Requests.cs:18-25`（`CreateIdeaRequest`）、`:27-34`（`UpdateIdeaRequest`）、`:59-66`（`MergeIdeasRequest`）
- Modify: `server/IdeaApp/Application/IdeaService.cs:234-260`（`Create`）、`:270-310`（`Update`）、`:568-620`（`Merge`）、`:837-845`（`NormalizeTitle`）、`:1085-1110`（`ToDto`）
- Modify: `server/IdeaApp/Endpoints/IdeaEndpoints.cs:60-78`、`server/IdeaApp/Endpoints/BulkIdeaEndpoints.cs:60-68`
- Modify: `server/IdeaApp/Infrastructure/OpenApiEventTransformer.cs:131`（`changed` 说明）、`:142-180`（手写的 `BuildIdeaSnapshotSchema`）
- Test: `server/IdeaApp.Tests/IdeaApiTests.cs`、`server/IdeaApp.Tests/EventPublishingTests.cs`、`server/IdeaApp.Tests/BulkAndMergeTests.cs`、`server/IdeaApp.Tests/JsonSerializationTests.cs`、`server/IdeaApp.Tests/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SearchIndex`（索引里已经没有标题了）
- Produces:
  ```csharp
  namespace IdeaApp.Contracts;

  /// 一处命中：某个字段里的字符区间（UTF-16 code unit）。
  /// 字段只有三种：content / tags / project。
  public sealed record SearchMatch(string Field, int Start, int Length);

  /// 一条想法的对外表示。**没有标题** —— 想法就是一段文字。
  public sealed record IdeaDto(string Id, string ProjectId, string Content,
                               string[] Tags, bool Done,
                               DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                               [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                               SearchMatch[]? Matches = null,
                               [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                               bool? Trashed = null);
  ```
  ```csharp
  namespace IdeaApp.Contracts;

  /// 创建想法的请求体。projectId 缺省或为空串时落到内置收件箱；
  /// content 缺省或全空白是非法正文（400 invalid_content）；tags 缺省表示不带标签。
  public sealed record CreateIdeaRequest(string? ProjectId, string? Content, string[]? Tags);

  /// 修改想法的请求体。**Content = null 表示不改正文**（语义不变，只是没有标题那一半了）。
  public sealed record UpdateIdeaRequest(string? Content);

  /// 合并想法的请求体。ids 是要合并的想法 id（至少两条）。
  /// content 缺省时用源想法正文按 updated_at 升序拼接的结果。
  public sealed record MergeIdeasRequest(string[]? Ids, string? Content);
  ```
  ```csharp
  namespace IdeaApp.Domain;

  /// 想法的对外快照：**没有标题**。
  public sealed record IdeaSnapshot(string Id, string ProjectId, string Content,
                                    string[] Tags, bool Done,
                                    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
  ```
  ```csharp
  namespace IdeaApp.Application;

  public sealed class IdeaService
  {
      /// 新建一条想法（正文 + 标签）。**没有标题参数**。
      public IdeaDto Create(string projectId, string content, IReadOnlyList<string> tags);

      /// 修改想法的正文。content 为 null 表示不改正文。
      public IdeaDto Update(string id, string? content);

      /// 合并多条想法成一条。content 为 null 时用源想法正文按 updated_at 升序拼接。
      public IdeaDto Merge(IReadOnlyList<string> ids, string? content);
  }
  ```

- [ ] **Step 1: 写失败的测试**

  在 `server/IdeaApp.Tests/IdeaApiTests.cs` 里：

  | 测试 | 断言 |
  |---|---|
  | `Create_idea_returns_no_title_field` | `POST /api/ideas` → 响应 JSON 里**没有 `title` 键**（`root.TryGetProperty("title", out _) == false`） |
  | `Update_idea_with_null_content_keeps_the_body` | `PATCH /api/ideas/{id}` 传 `{"content": null}` → 200，正文保持原样 |
  | `Update_idea_with_empty_content_is_rejected` | `PATCH /api/ideas/{id}` 传 `{"content": ""}` → 400 `invalid_content`（正文不能被清空） |
  | `Create_idea_ignores_a_legacy_title_field` | **Review Focus 第 4 条**：`POST /api/ideas` 传 `{"content":"正文","title":"老客户端的标题"}` → **201**，正文存的是「正文」，响应里**没有 `title` 键**（老客户端静默忽略，不报错） |
  | `Merge_request_body_overrides_content` | `POST /api/ideas/merge` 传 `{"ids":[a,b],"content":"自己写的正文"}` → 合并结果正文是「自己写的正文」，响应里没有 `title` 键 |

  删掉已有的：
  - `Update_idea_with_null_title_keeps_existing_title`（整个用例消失）
  - `Update_idea_with_empty_title_clears_title`（整个用例消失）
  - `IdeaApiTests` 里对 `title` 的 JSON 断言（`:30` 那条 `Assert.Equal(JsonValueKind.Null, root.GetProperty("title").ValueKind)` 改成断言 `title` 键不存在）

  ⚠️ **`JsonSerializationTests.cs` 会编译不过**：它用**命名参数**构造 `IdeaDto`，
  `:22` 的 `Title: "标题",` 与 `:53` 的 `Title: null,` 在 `IdeaDto` 去掉这个参数后是
  **CS1739（没有形参名为 Title）** —— 整个测试项目编译不过，闸门根本跑不起来。
  两处的 `Title:` 实参都删掉。

  ⚠️ **`Update_idea_with_null_content_keeps_the_body` 不要新增**：既有的
  `IdeaApiTests.cs:349` 已经有 `Update_idea_with_null_content_keeps_existing_content`
  （PATCH `{"content": null}` → 200，正文原样保留），语义完全一样 —— 复用它即可，别写第二条。

  在 `server/IdeaApp.Tests/EventPublishingTests.cs` 里：
  - 删掉 `Update_idea_publishes_both_changed_fields_when_title_and_content_change`；
  - 把 `Update_idea_publishes_changed_fields` 改成只测正文（`changed` 里是 `["content"]`，**不含 `"title"`**）；
  - `CreateIdeaAsync` helper 的 `title` 参数删掉。

  在 `server/IdeaApp.Tests/BulkAndMergeTests.cs` 里：
  - 删掉 `Merge_result_takes_title_of_first_source`（整个用例消失）；
  - `Merge_request_body_overrides_content_and_title` 改成 `Merge_request_body_overrides_content`（只测正文）。

  在 `server/IdeaApp.Tests/OpenApiDocumentTests.cs` 里：
  - 删掉「`title` 必须可空（string | null）」那两条断言（`:191-192`）；
  - 加一条：`Assert.False(Required(snapshot).Contains("title"))` 且快照的 properties 里**没有** `title` 键；
  - **再加一条把规格 §4.4 第 1 条变成可执行断言的**（用文件里已有的 `CommittedDocumentAsync()` 与 `RepoPaths.Contract`）：

    | 测试 | 断言 |
    |---|---|
    | `The_committed_contract_has_no_idea_title_property` | 读**提交进仓库的** `openapi/idea-v1.json`：遍历 `components.schemas`，`IdeaDto` / `IdeaSnapshot` / `CreateIdeaRequest` / `UpdateIdeaRequest` / `MergeIdeasRequest` 这五个 schema 的 `properties` 里都**没有** `title` 键；且整份文档的**正文里不出现「标题」二字**（`info.title` 是 "Idea Stream"、ProblemDetails 的 `title` 是 ProblemDetails 的字段名，都不算） |

    > 为什么要「遍历五个 schema」而不是只查一个：规格 §4.4 第 1 条写的是
    > 「**全文搜不到** idea 的 title（ProblemDetails 的 `title` 与文档标题不算）」——
    > 那是**整份契约**的断言，只查一个 schema 挂一漏万。
    > 「不出现「标题」二字」这条能盖住 `OpenApiEventTransformer.cs` 里那份**手写**的
    > `IdeaSnapshot` schema —— 它的 `title` 属性的 description 里就写着「标题。可以是 null（未命名想法）…」
    > （当前 `openapi/idea-v1.json:2087`），只查 `properties` 的键名会漏掉它。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~IdeaApiTests|FullyQualifiedName~EventPublishingTests|FullyQualifiedName~BulkAndMergeTests|FullyQualifiedName~OpenApiDocumentTests"`
  Expected: 编译失败（`IdeaDto` 的构造参数个数变了、`UpdateIdeaRequest` 的参数变了）。

- [ ] **Step 3: 改领域快照与契约**

  - `server/IdeaApp/Domain/IdeaEvent.cs`：
    - `IdeaSnapshot`（`:39-41`）去掉 `string? Title`；
    - `From(Idea idea)`（`:44-52`）去掉 `idea.Title,`；
    - `:69` 的 `changed` 说明里「如 <c>title</c>、<c>done</c>」改成「如 <c>done</c>、<c>tags</c>」；
    - `:76` 的「<c>Update</c>（改正文/标题）」改成「<c>Update</c>（改正文）」。
  - `server/IdeaApp/Contracts/Dtos.cs`：
    - `SearchMatch` 的 `Field` 文档（`:8`）改成「命中的字段：<c>"content"</c> | <c>"tags"</c> | <c>"project"</c>」；
    - `IdeaDto`（`:40-46`）去掉 `string? Title` 参数；
    - `IdeaDto` 的 `<param name="Title">` 文档行删掉；
    - `SearchMatch` 的类注释里若有 title 字样一并改。
  - `server/IdeaApp/Contracts/Requests.cs`：
    - `CreateIdeaRequest`（`:25`）→ `public sealed record CreateIdeaRequest(string? ProjectId, string? Content, string[]? Tags);`，XML 文档里删掉 `<c>Title</c>` 相关的说明；
    - `UpdateIdeaRequest`（`:34`）→ `public sealed record UpdateIdeaRequest(string? Content);`，文档改成「<c>Content</c> 为 null 表示不改正文」；
    - `MergeIdeasRequest`（`:66`）→ `public sealed record MergeIdeasRequest(string[]? Ids, string? Content);`，文档里删掉「<c>Title</c> 缺省时用第一条源想法的标题」。

- [ ] **Step 4: 改应用层**

  在 `server/IdeaApp/Application/IdeaService.cs`：
  - `Create`（`:234`）：签名改成 `public IdeaDto Create(string projectId, string content, IReadOnlyList<string> tags)`；
    里面造 `Idea` 时 `Title: NormalizeTitle(title)` 改成 `Title: null`（**本 task 领域记录 `Idea` 仍有 Title 字段**，Task 3 才删）；
    XML 文档里删掉 `<paramref name="title"/>`。
  - `Update`（`:281`）：签名改成 `public IdeaDto Update(string id, string? content)`；
    - 删掉 `var nextTitle = title is null ? existing.Title : NormalizeTitle(title);`；
    - 删掉 `if (!string.Equals(existing.Title, nextTitle, ...)) { changed.Add("title"); }` 那一段；
    - `repo.UpdateContent(id, nextTitle, nextContent)` → `repo.UpdateContent(id, nextContent)`（仓储签名在 Task 3 才改，**本 task 先按旧签名传 `null`**：`repo.UpdateContent(id, null, nextContent)`）；
    - XML 文档（`:270-280`）里「<c>title</c> 为 null 表示**不改标题**」那几行删掉，改成「<c>content</c> 为 null 表示不改正文」。
  - `Merge`（`:581`）：签名改成 `public IdeaDto Merge(IReadOnlyList<string> ids, string? content)`；
    合并结果造 `Idea` 时 `Title: title is not null ? NormalizeTitle(title) : sources[0].Title,` → `Title: null,`；
    XML 文档（`:568`）里 `<param name="title">` 那行删掉。
  - `NormalizeTitle`（`:837-845`）：**整个方法删掉**（`Title: null` 是唯一用途）。
  - `ToDto`（`:1085-1110`）：删掉 `Title: idea.Title,` 与 `Title: dto.Title,`。

- [ ] **Step 5: 改端点**

  - `server/IdeaApp/Endpoints/IdeaEndpoints.cs`：
    - `POST /api/ideas`（`:60-70`）：`service.Create(request.ProjectId ?? string.Empty, request.Content ?? string.Empty, request.Title, request.Tags ?? Array.Empty<string>())` → 去掉 `request.Title,`；
    - `PATCH /api/ideas/{id}`（`:74-78`）：`service.Update(id, request.Title, request.Content)` → `service.Update(id, request.Content)`。
  - `server/IdeaApp/Endpoints/BulkIdeaEndpoints.cs`：
    - `POST /api/ideas/merge`（`:62-66`）：`service.Merge(request.Ids ?? Array.Empty<string>(), request.Content, request.Title)` → 去掉 `request.Title`。

- [ ] **Step 6: 改 OpenAPI 的事件快照 schema（手写副本）**

  在 `server/IdeaApp/Infrastructure/OpenApiEventTransformer.cs`：
  - `BuildIdeaSnapshotSchema`（`:142-180`）：
    - `Required` 集合里删掉 `"title"`；
    - `Properties` 里删掉 `["title"] = new OpenApiSchema { ... }` 那一整块；
  - `:131` 的 `changed` 说明里「（如 title、done、tags、name、sortOrder、deletedAt）」改成「（如 done、tags、content、name、sortOrder、deletedAt）」。

  ⚠️ **这个 schema 是手写的 `IdeaSnapshot` 副本，与 `Domain/IdeaEvent.cs` 之间没有任何强制一致的机制** —— 忘了改就会继续对外宣称 payload 里有 title。`OpenApiDocumentTests` 只比对「提交进仓库的 JSON」与「运行中的应用」，不会发现「领域里没有、契约里却有」。

- [ ] **Step 7: 重新导出契约快照**

  Run: `dotnet run --project tools/ExportOpenApi`
  Expected: `openapi/idea-v1.json` 被更新（`IdeaDto` 与 `IdeaSnapshot` 的 schema 里没有 `title`，三个请求体的 schema 里没有 `title`）。

  若导出失败或文件没变，先跑 `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~OpenApiDocumentTests"` 看差异。

- [ ] **Step 8: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS。

- [ ] **Step 9: 提交**

  ```bash
  git add server/IdeaApp/Domain/IdeaEvent.cs server/IdeaApp/Contracts/Dtos.cs server/IdeaApp/Contracts/Requests.cs \
          server/IdeaApp/Application/IdeaService.cs server/IdeaApp/Endpoints/IdeaEndpoints.cs \
          server/IdeaApp/Endpoints/BulkIdeaEndpoints.cs server/IdeaApp/Infrastructure/OpenApiEventTransformer.cs \
          openapi/idea-v1.json \
          server/IdeaApp.Tests/IdeaApiTests.cs server/IdeaApp.Tests/EventPublishingTests.cs \
          server/IdeaApp.Tests/BulkAndMergeTests.cs server/IdeaApp.Tests/JsonSerializationTests.cs \
          server/IdeaApp.Tests/OpenApiDocumentTests.cs
  git commit -m "feat(api): 去掉「标题」这个概念，接口不再收也不返

  想法就是一段文字：请求体里没有 title，响应里也没有 title，事件快照里
  也没有。PATCH 的语义只剩「content 为 null = 不改正文」这一条。

  OpenAPI 里的 IdeaSnapshot schema 是手写的副本，跟领域类型之间没有任何
  强制一致的机制 —— 这里手工去掉，否则契约会继续对外宣称 payload 里有
  title，而运行时早就不发了。

  老客户端仍然发 title 的话会被 System.Text.Json 静默忽略（内容正常保存）：
  不为单个字段加特例，全局改成「未知成员即 400」超出这次的范围。"
  ```

---

### Task 3: 存储层去掉「标题」列

**Files:**
- Modify: `server/IdeaApp/Infrastructure/schema.sql:12-24`（`idea` 表去掉 `title TEXT NULL`）
- Modify: `server/IdeaApp/Infrastructure/IdeaRepository.cs:27-28`（`SelectColumns`）、`:394-438`（`Insert`）、`:441-484`（`UpdateContent`）、`:834-843`（`Read`）
- Modify: `server/IdeaApp/Domain/Idea.cs:6-9`（`Idea` 记录）
- Modify: `server/IdeaApp/Infrastructure/SearchRepository.cs:481-491`（`ReadIdea`）
- Test: `server/IdeaApp.Tests/IdeaApiTests.cs`（`:390`/`:409`/`:435`/`:471` 的 SQL helper 与 `IdeaSummary`）、`server/IdeaApp.Tests/BulkAndMergeTests.cs`（`:736`/`:755`/`:783`/`:796`/`:872` 的同款 helper）、`server/IdeaApp.Tests/FilteringTests.cs:279`、`server/IdeaApp.Tests/IndexSkippedWarningTests.cs:164`、`server/IdeaApp.Tests/SearchIndexTests.cs`（`:387-396` 的 `InsertIdea` helper）

**Interfaces:**
- Consumes: Task 2 的 `IdeaDto`（没有 Title）、Task 1 的 `SearchIndex.UpsertIdea`
- Produces:
  ```csharp
  namespace IdeaApp.Domain;

  /// 一条想法的领域记录。**没有标题** —— 想法就是一段文字。
  public sealed record Idea(string Id, string ProjectId, string Content,
                            IReadOnlyList<string> Tags, bool Done,
                            DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                            DateTimeOffset? DeletedAt);
  ```
  ```csharp
  namespace IdeaApp.Infrastructure;

  public sealed class IdeaRepository
  {
      /// 更新正文，并刷新 updated_at；索引行跟着更新（只有正文那一列）。
      public Idea UpdateContent(string id, string content);
  }
  ```

- [ ] **Step 1: 写失败的测试**

  在 `server/IdeaApp.Tests/IdeaApiTests.cs` 里追加一条（**Review Focus 第 6 条**）：

  | 测试 | 断言 |
  |---|---|
  | `The_idea_table_has_no_title_column_in_a_fresh_database` | 新库跑完 `Initialize()` 后 `PRAGMA table_info(idea)` 的列名里**没有** `title` |
  | `An_old_database_with_a_title_column_still_works` | **在 `factory.CreateClient()` 之前**把旧形状的 `idea` 表（带 `title TEXT NULL`）造进 `factory.DatabasePath` → `CreateClient()` 启动应用 → `POST /api/ideas` 造一条想法 → `GET /api/ideas` 返回 **200** 且那条想法在列表里、正文正确（旧库那一列永远 NULL，代码不读写它） |

  > **造旧形状的 `idea` 表的做法**：`TestAppFactory` 的库文件是在**应用启动时**才建的，
  > 所以旧形状必须在 `CreateClient()` **之前**造好：
  >
  > ```csharp
  > var db = new SqliteDatabase(factory.DatabasePath);
  > using (var connection = db.Open())
  > using (var command = connection.CreateCommand())
  > {
  >     // 旧版本的 idea 表：保留 title TEXT NULL 那一列。
  >     command.CommandText =
  >         "CREATE TABLE idea (" +
  >         "    id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES project(id), " +
  >         "    title TEXT NULL, content TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0, " +
  >         "    deleted_at TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);";
  >     command.ExecuteNonQuery();
  > }
  > ```
  >
  > ⚠️ `CREATE TABLE IF NOT EXISTS` 不会改已有表的列 —— 旧库升级后那一列还在，这是**预期**：
  > **代码不得依赖这一列在不在**（规格 §4.3 A）。
  > ⚠️ 这张旧表引用的 `project(id)` 在应用启动时才会建（外键约束是延迟检查的，
  > 建表时 `PRAGMA foreign_keys=ON` 也不会因为外键目标不存在而失败）——
  > 若担心，在上面那段之后先 `INSERT OR IGNORE INTO project(...)` 造一条收件箱。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~IdeaApiTests"`
  Expected: 失败（新库还有 `title` 列；旧库那条会红，因为代码还在 SELECT `title`）。

- [ ] **Step 3: 改 schema 的 `idea` 表**

  在 `server/IdeaApp/Infrastructure/schema.sql:12-21` 删掉 `title TEXT NULL,` 那一行。

  ⚠️ **旧库不需要迁移**：`CREATE TABLE IF NOT EXISTS` 意味着旧库保留那一列（永远是 NULL，代码不再读写），无害；新库没有这一列。**代码里任何地方都不得出现 `title` 这一列的 SELECT / INSERT / UPDATE。**

- [ ] **Step 4: 改领域记录与仓储**

  - `server/IdeaApp/Domain/Idea.cs:6-9`：`Idea` 记录去掉 `string? Title`：
    ```csharp
    public sealed record Idea(string Id, string ProjectId, string Content,
                              IReadOnlyList<string> Tags, bool Done,
                              DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
                              DateTimeOffset? DeletedAt);
    ```
  - `server/IdeaApp/Infrastructure/IdeaRepository.cs`：
    - `SelectColumns`（`:27-28`）改成 `"id, project_id, content, done, created_at, updated_at, deleted_at"`；
    - `Read`（`:834-843`）：删掉 `Title: reader.IsDBNull(2) ? null : reader.GetString(2),`，
      **reader 下标全部前移一位**：content 从 3 → 2、done 从 4 → 3、createdAt 从 5 → 4、updatedAt 从 6 → 5、deletedAt 从 7 → 6；
    - `Insert`（`:394-438`）：SQL 改成
      `INSERT INTO idea(id, project_id, content, done, deleted_at, created_at, updated_at) VALUES ($id, $projectId, $content, $done, $deletedAt, $createdAt, $updatedAt);`，
      删掉 `command.Parameters.AddWithValue("$title", Parameter(idea.Title));`；
    - `UpdateContent`（`:441-484`）：签名改成 `public Idea UpdateContent(string id, string content)`；
      SQL 改成 `UPDATE idea SET content = $content, updated_at = $now WHERE id = $id;`，
      删掉 `$title` 参数；XML 文档里「<paramref name="title"/> 为 null 表示把标题写成「无标题」」删掉，
      「改正文与标题」改成「改正文」。
  - `server/IdeaApp/Infrastructure/SearchRepository.cs:481-491`（`ReadIdea`）：删掉 `Title:` 那一行，下标前移（Task 1 已经前移过一次，这里是**再前移一位** —— 最终 content 在 1、done 在 2、createdAt 在 3、updatedAt 在 4、deletedAt 在 5）。

  ⚠️ **下标是最容易出错的地方**：改完 `Read` / `ReadIdea` 后，逐条核对每个 `reader.GetString(N)` / `GetInt64(N)` / `IsDBNull(N)` 的 N。

- [ ] **Step 5: 改调用点（应用层与仓储内部）**

  - `server/IdeaApp/Application/IdeaService.cs`：
    - `Create` 里造 `Idea` 的地方：`Title: null,` 那一行**整个删掉**（记录没有这个字段了）；
    - `Update` 里 `repo.UpdateContent(id, null, nextContent)` → `repo.UpdateContent(id, nextContent)`；
    - `Merge` 里造 `Idea` 的地方：`Title: null,` 那一行删掉；
    - `ToDto` 里 `Title: idea.Title,` 已经在 Task 2 删掉了，确认没有残留。
  - `server/IdeaApp/Infrastructure/IdeaRepository.cs` 里其余造 `Idea` 的地方（`SetDone`、`ReplaceTags`、`ReadIdea` 等）同样删掉 `Title:` 那一行。

- [ ] **Step 6: 改测试里的 SQL helper 与领域构造**

  - `server/IdeaApp.Tests/IdeaApiTests.cs`：
    - `IdeaSummary` 记录（`:471`）去掉 `string? Title`；
    - 读 JSON 的地方（`:390`/`:409`）去掉 `Title: element.GetProperty("title").GetString(),`；
    - 造想法的裸 SQL（`:435` 附近）去掉 `title` 列与 `$title` 参数。
  - `server/IdeaApp.Tests/BulkAndMergeTests.cs`：同款改动（`:736`/`:755`/`:783`/`:796`/`:872`）。
  - `server/IdeaApp.Tests/FilteringTests.cs:279`：`Title: null,` 删掉。
  - `server/IdeaApp.Tests/IndexSkippedWarningTests.cs:164`：`Title: null,` 删掉。
  - ⚠️ **`server/IdeaApp.Tests/SearchIndexTests.cs` 的 `InsertIdea` helper 也要改**（Task 1 只处理了它
    与 `idea_fts` 有关的那一半，这一半属于 **业务表 `idea`**）：
    - `:387` 的签名 `private void InsertIdea(string id, string projectId, string? title, string content)`
      → `private void InsertIdea(string id, string projectId, string content)`；
    - `:392-393` 的 `INSERT INTO idea(id, project_id, title, content, …) VALUES (…, $title, …)`
      → 去掉 `title` 列与 `$title`；
    - `:396` 的 `command.Parameters.AddWithValue("$title", …)` 删掉；
    - **全部调用点跟着改**（少传第三个实参）—— `:52`/`:53`/`:64`/`:72`/`:84`/`:104`/`:143`/`:176`/`:210`/`:258`/`:295`/`:330`/`:344`/`:361`；
      `:210` 那个实参是 `$"标题 {i}"`，一并删掉。
    ⚠️ **不改这一处的话，Task 3 之后每次 `InsertIdea` 都抛
    `table idea has no column named title`** —— 新库已经没有 `title` 列了（`schema.sql` 里删掉了），
    而这个 helper 在该文件里被调用了十几处。

- [ ] **Step 7: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS。

- [ ] **Step 8: 提交**

  ```bash
  git add server/IdeaApp/Infrastructure/schema.sql server/IdeaApp/Infrastructure/IdeaRepository.cs \
          server/IdeaApp/Domain/Idea.cs server/IdeaApp/Infrastructure/SearchRepository.cs \
          server/IdeaApp/Application/IdeaService.cs \
          server/IdeaApp.Tests/IdeaApiTests.cs server/IdeaApp.Tests/BulkAndMergeTests.cs \
          server/IdeaApp.Tests/FilteringTests.cs server/IdeaApp.Tests/IndexSkippedWarningTests.cs \
          server/IdeaApp.Tests/SearchIndexTests.cs
  git commit -m "refactor(store): 想法表不再有标题列

  idea 表去掉 title TEXT NULL。旧库不需要迁移：CREATE TABLE IF NOT EXISTS
  意味着旧库保留那一列（永远是 NULL，代码不再读写），无害 —— 但代码里
  任何地方都不得出现这一列的 SELECT / INSERT / UPDATE。

  连带的是三个按位置读列的 reader：下标全部前移一位，内容错位不会报错，
  只会安静地读出别的字段。"
  ```

---

### Task 4: webhook 占位符去掉 `{{idea.title}}`

**Files:**
- Modify: `server/IdeaApp/Infrastructure/EventPlaceholders.cs:9`（类注释）、`:28-41`（`Build` 的字典）
- Modify: `server/IdeaApp/Infrastructure/TemplateRenderer.cs:9-12`（类注释）、`:36-38` 之后（新增「已下线占位符」表）、`:78-86`（`RenderPlain` 的替换分支）、`:288-297`（`Resolve` 的替换分支）
- Modify: `server/IdeaApp/Application/WebhookService.cs:330`（Telegram 预设模板）
- Test: `server/IdeaApp.Tests/TemplateRendererTests.cs:31`（`SampleEvent` 的 `Title`）、`:37`（`SampleEvent` 的 `changed` 默认值）、`:223`（占位符白名单）、`:304`（`changed` 断言）

**Interfaces:**
- Consumes: Task 2 的 `IdeaSnapshot`（没有 Title 了）
- Produces:
  - `EventPlaceholders.Build` 的字典**不再有 `idea.title` 这个键**（其余键不变：`event` / `timestamp` / `project` / `project.id` / `idea.id` / `idea.content` / `idea.tags` / `idea.done` / `changed` / `idea.url`）；
  - `TemplateRenderer` 里多一张「已下线占位符」表（`private static readonly HashSet<string> RetiredPlaceholders`，内容是 `"idea.title"`）——
    命中它的占位符**渲染成空串**，而不是「原样保留」。

- [ ] **Step 1: 写失败的测试**

  在 `server/IdeaApp.Tests/TemplateRendererTests.cs` 里：

  | 测试 | 断言 |
  |---|---|
  | `The_placeholder_table_has_no_idea_title` | `EventPlaceholders.Build(evt, null)` 的键集合里**没有** `"idea.title"` |
  | `A_template_with_a_removed_placeholder_renders_as_empty` | **Review Focus 第 5 条**：模板正文是 `{"text":"[{{idea.title}}] {{idea.content}}"}`，事件里那条想法**有正文** → 渲染结果是 `{"text":"[] 正文"}` —— **渲染成空串，不是报错、也不是把占位符原样留着** |
  | `An_unknown_placeholder_is_still_kept_verbatim` | **别把语义改坏**：模板正文是 `{"text":"{{unknown}}"}` → 渲染结果仍是 `{"text":"{{unknown}}"}`（未识别的占位符**原样保留**，这是既有语义，`:83-88` 已经钉着） |
  | `The_telegram_preset_template_does_not_use_idea_title` | `WebhookService` 的 Telegram 预设模板里**不含** `{{idea.title}}` |

  同时：
  - `:223` 那份占位符白名单里的 `"idea.title"` **删掉**（白名单是「**包含**」的断言 ——
    `Assert.True(values.ContainsKey(key))`，不是「完全相等」；删掉这一项就不再断言它存在，
    上面那条 `The_placeholder_table_has_no_idea_title` 断言的是它**不在**）；
  - `SampleEvent`（`:31`）里的 `Title: "标题",` 删掉；
  - ⚠️ `SampleEvent` 的**默认** `changed`（`:37` 的 `Changed: changed ?? ["title", "done"]`）改成 `["done"]` ——
    「标题」这个字段名已经不存在了，示例事件不该还带一个；
  - `:304` 的 `SampleEvent(changed: ["title", "done"])` 改成 `["done"]`，
    `:307` 的断言 `Assert.Equal("title,done", values["changed"])` 改成 `Assert.Equal("done", values["changed"])`。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~TemplateRendererTests"`
  Expected: 失败 —— `SampleEvent` 编译不过（`IdeaSnapshot` 没有 Title 了）；
  `A_template_with_a_removed_placeholder_renders_as_empty` 会红：**渲染器现在把没键的占位符原样保留**，
  实际输出是 `{"text":"[{{idea.title}}] 正文"}` 而不是 `{"text":"[] 正文"}`。
  ⚠️ **这不是 bug，是要靠 Step 4 新增的「已下线占位符」机制补上的** —— 渲染器原本的语义是
  「键在、值为 null → 空串；键**不在** → 原样保留」（`TemplateRenderer.cs:44-46` 的 XML 文档、
  `:83-88` 的 `RenderPlain`、`:293-297` 的 `Resolve` 三处都这么写）。
  规格 §4.3 D 要的是空串，而「键消失」落到的是「原样保留」那一档 —— 两句话对不上，
  **本 task 用一张「已下线占位符」表把这两句话接上**。

- [ ] **Step 3: 改 `EventPlaceholders`**

  在 `server/IdeaApp/Infrastructure/EventPlaceholders.cs`：
  - 删掉 `["idea.title"] = evt.Idea?.Title,`；
  - 类注释（`:9`）里「（<c>event</c>、<c>idea.title</c>…）」改成「（<c>event</c>、<c>idea.content</c>…）」。

- [ ] **Step 4: 给渲染器加「已下线占位符」表**

  在 `server/IdeaApp/Infrastructure/TemplateRenderer.cs`：

  1. 在 `PlaceholderPattern`（`:36-38`）之后加一张表：

     ```csharp
     /// <summary>
     /// 已经下线、但老模板里可能还留着的占位符。
     ///
     /// 能力去掉之后，这些占位符不再出现在 <see cref="EventPlaceholders"/> 的字典里 ——
     /// 但渲染器**不能**把它们当「未识别的占位符」原样保留：老模板的正文里会冒出
     /// 「{{idea.title}}」这样的字面量。规格 §4.3 D 要的是**渲染成空串**。
     ///
     /// <para>
     /// 为什么单开一张表：渲染器原本的语义是「键在、值为 null → 空串；键不在 →
     /// 原样保留」。占位符下线时键会消失，于是落进「原样保留」那一档 ——
     /// 这张表把「已下线」与「拼错了」区分开。
     /// </para>
     /// </summary>
     private static readonly HashSet<string> RetiredPlaceholders =
         new(StringComparer.Ordinal) { "idea.title" };
     ```

  2. `RenderPlain` 的替换分支（`:81-88`）与 `Resolve`（`:289-297`）**两处都要改**：

     ```csharp
     if (!values.TryGetValue(name, out var value))
     {
         // 未识别的占位符：原样保留 —— **但已下线的占位符渲染成空串**。
         return RetiredPlaceholders.Contains(name) ? string.Empty : match.Value;
     }
     ```

     ⚠️ **两处都要改**：JSON 模板走 `Resolve`（`:289`），纯文本模板走 `RenderPlain`（`:72`）——
     只改一处的话，另一条路径上的老模板还是会露出字面量。

  3. 类注释（`:9-12`）里「未识别的占位符**原样保留** —— 模板是用户手写的，名字拼错不该让投递炸掉」
     补一句：「**已下线的占位符**（见 {@link RetiredPlaceholders}）渲染成空串 ——
     老模板不该在正文里露出占位符的字面量」；
     `RenderJson` / `RenderPlain` 的 `values` 参数文档（`:44-46`）里
     「字典里**没有**这个键才是「未识别的占位符」，原样保留」补上「（已下线的除外，见 RetiredPlaceholders）」。

- [ ] **Step 5: 改 Telegram 预设模板**

  在 `server/IdeaApp/Application/WebhookService.cs:330`，把

  ```
  """{"chat_id":-1003800671233,"message_thread_id":89,"text":"[{{project}}] {{event}}\n{{idea.title}}\n{{idea.content}}\n#{{idea.tags}}"}"""
  ```

  改成

  ```
  """{"chat_id":-1003800671233,"message_thread_id":89,"text":"[{{project}}] {{event}}\n{{idea.content}}\n#{{idea.tags}}"}"""
  ```

- [ ] **Step 6: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS（**包括** `An_unknown_placeholder_is_still_kept_verbatim` ——
  「未识别的占位符原样保留」这条既有语义不能被改坏）。

- [ ] **Step 7: 提交**

  ```bash
  git add server/IdeaApp/Infrastructure/EventPlaceholders.cs server/IdeaApp/Infrastructure/TemplateRenderer.cs \
          server/IdeaApp/Application/WebhookService.cs server/IdeaApp.Tests/TemplateRendererTests.cs
  git commit -m "feat(webhook): 占位符去掉 idea.title

  想法不再有标题，{{idea.title}} 这个占位符也就没有意义了。Telegram 预设
  模板改成用 {{idea.content}}。

  渲染器加了「已下线占位符」表：这些占位符不再出现在事件字典里，但老模板
  里还留着 —— 渲染成空串，而不是让正文里冒出 {{idea.title}} 这样的字面量。
  「未识别的占位符原样保留」那条既有语义不变（名字拼错不该让投递炸掉），
  现在只有已下线的占位符走空串这条路。

  这是有意为之的能力删除：老模板要自己把 {{idea.title}} 换成 {{idea.content}}。"
  ```

---

### Task 5: skill 手册与契约同步

**Files:**
- Modify: `skills/idea-manager/references/api.md:30-31`（端点速查表）、`:60`（相关度说明）、`:93`（检索范围说明）、`:103`（合并端点）、`:183`/`:185`/`:199`（逐端点字段表）、`:232`（`matches.field` 取值）
- Modify: `skills/idea-manager/references/calling.md:40`、`:54`（curl 示例）
- Modify: `skills/idea-manager/references/scenarios.md:11`、`:111`、`:135`、`:139`、`:144`、`:160`
- Modify: `skills/idea-manager/references/webhooks.md:71`（占位符表）、`:89`（事件说明）、`:126`（示例模板）
- Modify: `skills/idea-manager/SKILL.md:105`
- Test: `server/IdeaApp.Tests/SkillDocGuardTests.cs`（**新增一条字段级守卫**，见 Step 1）

**Interfaces:**
- Consumes: Task 2 的契约（`openapi/idea-v1.json`）、Task 4 的占位符集合
- Produces: 无代码（手册与契约一致，`SkillDocGuardTests` 与 `OpenApiDocumentTests` 全绿）

- [ ] **Step 1: 先补一条字段级守卫（现有的守卫查不到字段）**

  ⚠️ **`SkillDocGuardTests` 现有的 17 个用例只比对端点路径、错误码表与文档结构**，
  **没有任何一条比对请求体/响应体的字段** —— 删掉 `title` 既不改路径也不改错误码，
  那些用例**仍然是绿的**。规格 §4.3 F 承诺的「契约改了手册没改就红」在字段层面
  **没有任何自动守卫**（实测：`rg "requestBody|properties" server/IdeaApp.Tests/SkillDocGuardTests.cs` 无输出）。

  所以先在 `server/IdeaApp.Tests/SkillDocGuardTests.cs` 里**新增一条**：

  | 测试 | 断言 |
  |---|---|
  | `Manual_and_contract_have_no_idea_title_field` | ① **契约**：读 `openapi/idea-v1.json`（用 `OpenApiDocumentTests` 里那套 `RepoPaths.Contract` 读法），`IdeaDto` / `IdeaSnapshot` / `CreateIdeaRequest` / `UpdateIdeaRequest` / `MergeIdeasRequest` 五个 schema 的 `properties` 里都**没有** `title` 键；② **手册**：扫**语料**（索引页 + `references/` 下递归的全部 .md，**排除 `references/errors.md`**），断言**不出现**三种形态：`` `title` ``（表格里的字段名）、`"title"`（JSON 键）、`{{idea.title}}`（webhook 占位符，允许中间带空格） |

  ⚠️ **扫描要排除 `references/errors.md`** —— 那里面的 `"title"` 是 **ProblemDetails 的字段名**
  （`errors.md:12`），跟想法的标题无关（规格 §4.5 明说别误伤）。
  ⚠️ **扫描范围用语料枚举（`Corpus()`），不要点名几份文件** —— 将来 `references/` 下新加一份 .md，
  它自动进守卫；硬编码名单会让新文档不受这条守卫管。
  ⚠️ **占位符形态必须一起禁**：`{{idea.title}}` 是 Task 4 刚下线的那个（服务端
  `TemplateRenderer` 的 `RetiredPlaceholders`），也是 agent 最容易照抄回手册的形态 ——
  只禁前两种形态，它会从守卫的缝里溜过去。
  ⚠️ **不要禁中文「标题」**：手册得能用中文讲「标题字段这件事」（变更说明就要讲），
  禁掉中文等于禁止写变更说明（与规格 §4.5「别误伤」的基调相反）。
  ⚠️ 契约那半条**在 Task 2 做完之后就已经是绿的**（契约里确实没有 title 了）——
  真正会红的是**手册那半条**：手册里现在还有 **16 处** `title`（`api.md` 7、`calling.md` 2、
  `scenarios.md` 4、`webhooks.md` 2、`SKILL.md` 1），外加 12 行中文「标题」。
  ⚠️ **那 16 处里有 3 处是占位符形态**（`scenarios.md` 的 Telegram 模板、`webhooks.md` 的
  占位符表与示例模板，都是 `{{idea.title}}`）—— 前两种形态只盖得住剩下的 **13 处**，
  这就是守卫必须扩到第三种形态的理由。

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SkillDocGuardTests"`
  Expected: **失败**（手册那半条红 —— 手册里还有 **16 处** `title`
  （`api.md` 7 处、`calling.md` 2 处、`scenarios.md` 4 处、`webhooks.md` 2 处、`SKILL.md` 1 处，
  其中 **3 处是 `{{idea.title}}` 占位符形态**、只有 13 处落在字段名 / JSON 键这两种形态里），
  外加 12 行中文「标题」；下面 Step 2–4 的编辑清单覆盖了全部这些位置）。

  > 这一条守卫是**长期**的：以后契约里加/删字段而手册没跟上，它会红。

- [ ] **Step 2: 改 `api.md`**

  - 端点速查表（`:30-31`）：
    - `| POST /api/ideas | 新建一条想法 | 无 | \`projectId\`、\`content\`、\`title\`、\`tags\` | 201 |` → 去掉 `\`title\``；
    - `| PATCH /api/ideas/{id} | 改标题/正文 | 路径参数 \`id\` | \`title\`、\`content\` | 200 |` → `| PATCH /api/ideas/{id} | 改正文 | 路径参数 \`id\` | \`content\` | 200 |`。
  - 合并端点（`:103`）：`ids`（数组）、\`content\`、\`title\` → `ids`（数组）、\`content\`。
  - 逐端点字段表（`:183`/`:185`/`:199`）：**三行 `title` 行整个删掉**（`POST /api/ideas`、`PATCH /api/ideas/{id}`、`POST /api/ideas/merge` 各一行）。
  - `matches.field` 的取值列表（`:232`）：`"title" / "content" / "tags" / "project"` → `"content" / "tags" / "project"`。
  - ⚠️ **两处散文里也提到标题**（这两处是**准确性**问题，**不是守卫问题** ——
    守卫只扫 ASCII 的 `` `title` `` / `"title"` / `{{idea.title}}` 三种形态，
    中文「标题」它扫不到，也**不应该**扫：手册里必须留着「老客户端仍然发这个字段会被静默忽略」
    那两句中文说明，扫中文会把自己人判成违规）：
    - `:60`「**按相关度排**：标题命中比正文命中更相关。」→「**按相关度排**：正文与标签命中**同权**，项目名命中的权重低一档（得分还受词频与文档长度影响，不是同权就一定同分）。」
      ⚠️ **别写「正文比标签更相关」**：真实权重是 `content=5.0 / tags=5.0 / project=3.0`
      （`SearchRepository.cs` 的 `IdeaFtsWeights`，列序 content/tags/project）——
      正文与标签**同权**，只有项目名低一档（与本计划 Task 1 Step 1(c) 钉住的期望、
      规格 §4.3 A 一致）。
    - `:93`「**搜的不只是正文**：标题、正文、标签名、项目名都在索引里」→「**搜的不只是正文**：正文、标签名、项目名都在索引里」。
  - **在请求体字段表下面补一句**（规格 §4.3 F 要求「变更说明里要点明这条行为」）：
    点明「老客户端多带标题键会被**静默忽略**（内容正常保存、不报错）」——
    ⚠️ **措辞本身也要守 Step 1**（不得出现被守卫禁的字段形态）：
    写成「**老客户端仍然带上标题字段会被静默忽略**（内容正常保存、不报错）——
    想法已经没有标题这个字段了：请求体里多带的那个键，服务端直接丢掉。」，
    **不要**写出那个字段的字面拼写（写了就与 Step 1 的守卫互斥）。

- [ ] **Step 3: 改 `calling.md` 与 `scenarios.md`**

  - `calling.md:40`、`:54`：curl 示例里的 `{"content":"…","title":"带特殊字符","tags":["笔记"]}` → 去掉 `"title":"带特殊字符",`；
  - `scenarios.md:11`：同款；
  - `scenarios.md:111`：合并场景的示例 `{"ids":[…],"content":"合并后的正文","title":"合并"}` → 去掉 `"title":"合并"`；
  - `scenarios.md:135`：`# 只改标题：正文键整个别发` → `# 只改正文：其它键整个别发`；
  - `scenarios.md:139`：`{"title":"新标题"}` → `{"content":"新正文"}`（这个例子讲的是「PATCH 只发要改的字段」）；
  - `scenarios.md:144`：`想清掉标题给空串 —— 那是「清成无标题」。` → 这句讲的是 title 的三态语义，**整句删掉**；
  - `scenarios.md:160`：webhook 模板示例里的 `{{idea.title}}` → `{{idea.content}}`。

- [ ] **Step 4: 改 `webhooks.md` 与 `SKILL.md`**

  - `webhooks.md:71`：占位符表里 `{{idea.id}} / {{idea.title}} / {{idea.content}}` → `{{idea.id}} / {{idea.content}}`（并把「想法的 id / 标题 / 正文」改成「想法的 id / 正文」）；
  - `webhooks.md:89`：`改正文/标题、换标签、标记完成；` → `改正文、换标签、标记完成；`；
  - `webhooks.md:126`：示例模板 `{{idea.title}}: {{idea.content}}` → `{{idea.content}}`；
  - `SKILL.md:105`：那句「（想法的 \`title\` 空串 = 清成无标题；webhook 的 \`headers\` 给 \`{}\` = 清空请求头）」→ 删掉前半句，改成「（webhook 的 \`headers\` 给 \`{}\` = 清空请求头）」。

- [ ] **Step 5: 跑守卫测试确认它通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SkillDocGuardTests|FullyQualifiedName~OpenApiDocumentTests"`
  Expected: 全部 PASS。

  ⚠️ **别指望现有的守卫会替你发现字段没同步** —— 它只查路径与错误码；
  真正兜住「字段」的是 Step 1 新加的那条 `Manual_and_contract_have_no_idea_title_field`。

- [ ] **Step 6: 提交**

  ```bash
  git add skills/idea-manager/references/api.md skills/idea-manager/references/calling.md \
          skills/idea-manager/references/scenarios.md skills/idea-manager/references/webhooks.md \
          skills/idea-manager/SKILL.md server/IdeaApp.Tests/SkillDocGuardTests.cs
  git commit -m "docs(skill): 手册与契约同步：想法没有标题了

  手册里补一句：老客户端仍然发 title 会被静默忽略（内容正常保存、不报错）。

  顺带给守卫补一条字段级的检查：现有的守卫只比对端点路径与错误码，
  查不到请求体/响应体的字段 —— 契约改了字段而手册没跟上，不会有任何
  测试发现。新守卫断言契约的五个 idea 相关 schema 与手册里都没有
  title（errors.md 里的 ProblemDetails.title 不算）。"
  ```

---

### Task 6: 前端去掉「标题」（类型、SSE 帧校验、视觉标题层）

**Files:**
- Modify: `web/src/api/types.ts:14-18`（`SearchMatch`）、`:20-37`（`Idea`）、`:142`（`IdeaEvent` 的注释）
- Modify: `web/src/api/events.ts:141-144`（`validateIdeaSnapshot` 的 title 校验）
- Modify: `web/src/lib/highlight.ts:150-201`（`trimSegments`）、`:239-247`（`trimLeadingNewlines`）、`:248-271`（`splitTitleAndBody`）
- Modify: `web/src/components/MessageItem.vue:5-12`（imports）、`:183-228`（`titleAndBody` / `visualTitle`）、`:571-667`（模板）、`:669-890`（样式）
- Modify: `web/src/components/MergeDialog.vue:55-60`（预览里的标题行）
- Modify: `web/src/pages/HomePage.vue:571-591`（`onSubmit` 里的 `title: null`）
- Create: `web/src/lib/text.ts`、`web/src/lib/text.spec.ts`
- Test: `web/src/api/events.spec.ts:89`（fixture 的 `title: null`）、`:218`（`changed: 'title'`）、`:286`（坏帧用例）、`web/src/api/types.spec.ts`、`web/src/lib/highlight.spec.ts`、`web/src/components/MessageItem.spec.ts`、`web/src/components/MergeDialog.spec.ts`、`web/src/components/MessageList.spec.ts`、`web/src/pages/HomePage.spec.ts`、`web/src/pages/TrashPage.spec.ts`、`web/src/stores/ideas.spec.ts`、`web/src/lib/merge.spec.ts`、`web/src/lib/filter.spec.ts`、`web/src/composables/useHistoryScroll.spec.ts`

**Interfaces:**
- Consumes: Task 2 的服务端契约（不再发 `title`）
- Produces:
  ```ts
  // web/src/api/types.ts
  /** 一处命中：某个字段里的字符区间（UTF-16 code unit，与 JS 的 slice 对齐）。 */
  export interface SearchMatch {
    field: 'content' | 'tags' | 'project';
    start: number;
    length: number;
  }

  /** 一条想法。**没有标题** —— 想法就是一段文字。 */
  export interface Idea {
    id: string;
    projectId: string;
    content: string;
    tags: string[];
    done: boolean;
    createdAt: string;
    updatedAt: string;
    matches?: SearchMatch[];
  }
  ```
  ```ts
  // web/src/lib/text.ts
  /**
   * 取一段文本的**第一行**（去掉首尾空白），超长时截断并加省略号。
   *
   * 用途：想法没有标题了，但界面仍然需要一个能认出这条想法的短文本 ——
   * 勾选框的可访问名称、合并对话框的预览行都用它。
   *
   * @param text 原文。
   * @param maxLength 最多保留多少个字符；超出的部分用「…」收尾。
   */
  export function firstLine(text: string, maxLength = 40): string;
  ```
  `web/src/lib/highlight.ts` 的导出变成：`highlightLines` / `flattenLines` / `tagHitFlags`（`splitTitleAndBody` / `trimSegments` / `trimLeadingNewlines` **删掉**）。

- [ ] **Step 1: 写失败的测试**

  **（a）SSE 帧校验** —— 在 `web/src/api/events.spec.ts` 里：

  | 测试 | 断言 |
  |---|---|
  | `a_idea_snapshot_without_a_title_key_is_accepted` | **Review Focus 第 3 条**：一帧的 `data.idea` **没有 `title` 键** → 校验通过、事件被交给 `onEvent`（不报 bad frame） |
  | `a_legacy_idea_snapshot_with_a_title_key_is_also_accepted` | 一帧的 `data.idea` 带 `title: "旧标题"` → **也**通过（我们不再校验这个键，多余的键不判坏帧） |

  > 这条测试的意义：服务器不再发 `title` 时 `record.title` 是 `undefined`，
  > `undefined === null` 为假 → 校验失败 → **每一帧都被判成畸形而丢弃** →
  > 界面再也不更新。**静默但致命。**

  删掉已有的坏帧用例（`:286`）：`{ ...base, idea: { ...validIdea, title: 42 } }`（那条断言「idea.title 既不是 null 也不是字符串」）。
  fixture（`:89`）里的 `title: null,` 删掉；`:218` 的 `changed: 'title'` 改成 `changed: 'content'`。

  **（b）正文首行工具** —— 新建 `web/src/lib/text.spec.ts`：

  | 测试 | 断言 |
  |---|---|
  | `firstLine_takes_the_text_before_the_first_newline` | `firstLine('第一行\n第二行')` === `'第一行'` |
  | `firstLine_trims_the_whitespace` | `firstLine('  第一行  \n第二行')` === `'第一行'` |
  | `firstLine_truncates_long_lines` | `firstLine('x'.repeat(60))` 的长度 ≤ 40，且以 `…` 结尾 |
  | `firstLine_of_empty_text_is_empty` | `firstLine('')` === `''`；`firstLine('\n\n')` === `''` |

  **（c）高亮工具** —— 改 `web/src/lib/highlight.spec.ts`：
  - 删掉 `describe('trimSegments', …)` 整组（5 条）与 `describe('splitTitleAndBody', …)` 整组（5 条）；
  - import 改成 `import { flattenLines, highlightLines, tagHitFlags } from './highlight';`。

  **（d）组件** —— 改 `web/src/components/MessageItem.spec.ts`：
  - fixture（`:11`）里的 `title: null,` 删掉；
  - 原来断言 `.title` 的用例改成断言**正文里包含首行**：
    - 「标题为空时用正文首行当视觉标题」→ 改成「正文显示全文（含首行）」：正文「第一行\n第二行」→ `wrapper.get('.body').text()` === `'第一行\n第二行'`（**首行不再被摘走**）；
    - 「有标题时标题显示在 `.title`、正文显示整段」→ 整个用例删掉（没有标题了）；
    - 命中高亮的用例（`:167`/`:181`/`:195`）改成断言 `.body` 里的 `<mark>`；
  - 新增一条：勾选框的 `aria-label` 用**正文首行**（截断到约 40 字）：
    正文「给路由器固定 IP，省得每次 SSH 都记不住…」→ `wrapper.get('input.select').attributes('aria-label')` 包含正文首行。

  改 `web/src/components/MergeDialog.spec.ts`：预览行不再显示「（无标题）」，改成显示**正文首行**。

  改 `web/src/pages/HomePage.spec.ts`：
  - fixture（`:154`）里的 `title: null,` 删掉；
  - `titledIdea`（`:220`）改成 `const plainIdea: Idea = { ...idea, content: '原正文' };`（或直接用 `idea`）；
  - `:1305`/`:1381` 那两条断言 `.title` 的改成断言 `.body` 的文本；
  - ⚠️ **`:564-569` 那条「提交想法」的请求体断言**：它断言的是**整个请求体**，
    `title: null,` 那一行要从**期望值**里删掉 —— 实际调用里已经没有这个键了，
    留着就是「期望有、实际没有」→ 断言红。

  **（e）其余 spec 里带 `Idea` 字面量的 `title` 也要清掉** ——
  `Idea` 接口少了 `title` 之后，对象字面量里多余的 `title` 会触发 TS 的
  **TS2353「对象字面量只能指定已知的属性」**，而 `web/tsconfig.json` 的 `include`
  是 `["src/**/*", …]` → **spec 文件也在 `vue-tsc` 的检查范围内** → 本 task 的
  `npm run typecheck` 闸门会红。逐个清掉：

  | 文件 | 位置 |
  |---|---|
  | `web/src/api/events.spec.ts` | `:89` 的 `title: null,` |
  | `web/src/components/MessageItem.spec.ts` | `:11` 的 `title: null,`；`:61`/`:80`/`:91`/`:109`/`:127`/`:143`/`:203`/`:239` 那些 `title: '标题'` 的 override |
  | `web/src/components/MessageList.spec.ts` | `:11` 的 `title: null,` |
  | `web/src/components/MergeDialog.spec.ts` | `:11` 的 `title: null,` |
  | `web/src/pages/HomePage.spec.ts` | `:154` 的 `title: null,`、`:220` 的 `titledIdea` |
  | `web/src/pages/TrashPage.spec.ts` | `:23` 的 `title: null,` |
  | `web/src/stores/ideas.spec.ts` | `:30` 的 `title: null,` |
  | `web/src/lib/merge.spec.ts` | `:11` 的 `title: null,` |
  | `web/src/lib/filter.spec.ts` | `:17` 的 `title: null,` |
  | `web/src/composables/useHistoryScroll.spec.ts` | `:80` 的 `title: null,` |

  ⚠️ `merge.spec.ts` 与 `filter.spec.ts` **在别的 task 里一次都没出现过** ——
  只靠「别处顺手改」是改不掉的，这张表就是它们的归属。
  （Task 11 / Task 12 会再次碰其中三个文件，届时说明「已经在本 task 删过了」。）

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/api/events.spec.ts src/lib/text.spec.ts src/lib/highlight.spec.ts src/components/MessageItem.spec.ts src/components/MergeDialog.spec.ts src/pages/HomePage.spec.ts`
  Expected: 失败（`a_idea_snapshot_without_a_title_key_is_accepted` 红；`text.ts` 不存在；`.title` 断言与新结构对不上）。

- [ ] **Step 3: 改类型**

  在 `web/src/api/types.ts`：
  - `SearchMatch.field`（`:15`）：`'title' | 'content' | 'tags' | 'project'` → `'content' | 'tags' | 'project'`；
  - `Idea`（`:21-37`）：删掉 `title: string | null;`，注释「`title` 可以为 null —— 只记正文不记标题的想法是允许的」改成「想法就是一段文字，**没有标题**」；
  - `IdeaEvent` 的注释（`:142`）里「（如 \`title\`、\`done\`、\`sortOrder\`）」改成「（如 \`done\`、\`tags\`、\`content\`、\`sortOrder\`）」。

- [ ] **Step 4: 改 SSE 帧校验**

  在 `web/src/api/events.ts:141-144`，**把那三行整个删掉**：

  ```ts
  // title 必填可空：值要么是 null、要么是字符串。
  if (!(record.title === null || typeof record.title === 'string')) {
    return 'idea.title 既不是 null 也不是字符串';
  }
  ```

  ⚠️ **留着它就会把每一帧都丢掉**：服务器不再发 `title`，`record.title` 是 `undefined`，
  `undefined === null` 为假 → 校验失败 → 帧被当作坏帧丢弃 → 界面再也不更新。
  **这是本 task 存在的头号理由。**

- [ ] **Step 5: 新建 `web/src/lib/text.ts`**

  实现 `firstLine(text, maxLength = 40)`：
  1. 取 `text` 的第一个 `\n` 之前的部分（`text.split('\n')[0]`）；
  2. `trim()`；
  3. 长度 ≤ `maxLength` 就原样返回；否则取前 `maxLength - 1` 个字符 + `…`（`…` 占一个字符，所以总长 ≤ `maxLength`）。

  写 TSDoc 注释说明**为什么**：想法没有标题了，但界面仍需要一个能认出这条想法的短文本。

- [ ] **Step 6: 改 `highlight.ts`（删掉三个函数）**

  在 `web/src/lib/highlight.ts`：
  - 删掉 `trimSegments`（`:150-201`）、`trimLeadingNewlines`（`:239-247`）、`splitTitleAndBody`（`:248-271`）；
  - 文件头注释里提到「把片段序列按「第一个非空行」分成视觉标题与正文两组」的那段（如果有）一并删掉；
  - 保留 `highlightLines` / `flattenLines` / `tagHitFlags` / `mergeRanges` / `cutLine`（`lineText` 只被 `splitTitleAndBody` 用 → 一起删掉）。

- [ ] **Step 7: 改 `MessageItem.vue`（卡片只显示一整块正文）**

  在 `web/src/components/MessageItem.vue`：
  - import（`:5-12`）去掉 `splitTitleAndBody` 与 `trimSegments`，加上 `import { firstLine } from '../lib/text';`；
  - `matchesOf`（`:179-181`）保留 —— `field` 类型改成 `'content' | 'tags' | 'project'` 后仍然能用；
  - 删掉 `titleAndBody`（`:183-211`）、`titleSegments`（`:213-214`）、`bodySegments`（`:216-217`）、`visualTitle`（`:227-228`），
    换成一个：
    ```ts
    /** 正文的片段（命中的那几段渲染成 <mark>）。正文是**全文**（含首行）。 */
    const bodySegments = computed(() =>
      flattenLines(highlightLines(props.idea.content, matchesOf('content'))),
    );
    ```
  - 新增：
    ```ts
    /** 这条想法的短文本（正文首行，截断到约 40 字）—— 勾选框的可访问名称用它。 */
    const accessibleName = computed(() => firstLine(props.idea.content));
    ```
  - 模板（`:571-667`）：
    - `<h2 v-if="!editing" class="title">…</h2>` **整个删掉**；
    - 勾选框的 `:aria-label="\`选择 ${visualTitle}\`"` → `:aria-label="\`选择 ${accessibleName}\`"`；
    - `.body` 的内容保持「按片段渲染」不变；
  - 样式（`:669-890`）：
    - `.title` 那条规则（`:736-741`）删掉；
    - `.idea--done .title,` 从选择器里去掉（只留 `.idea--done .body`）；
    - `.title mark,` 从 `<mark>` 的选择器里去掉（只留 `.body mark,` 与 `.chip mark`）；
    - 窄屏那块 `@media (max-width: 640px)`（`:812-870`）里这几条**一起**删掉 —— 它们是**一套**，不是各管各的：
      - `.title { order: 4; flex: 1 1 100%; }`（标题独占一行 —— 标题没了）；
      - `.head { flex-wrap: wrap; align-items: center; }` 连同它上面的那段注释（窄屏改成两行，是为了给标题腾出整行 —— 标题没了，这一行只剩「勾选框 → 标记完成 → 时间」，一行放得下）；
      - `.select { order: 1; align-self: auto; }`、`.done { order: 2; }`、`.time { order: 3; }` —— `order` 在不再换行之后是死代码（DOM 顺序本来就是「勾选框 → 标记完成 → 时间」，`order` 改不了视觉顺序）。
      ⚠️ **`.select { align-self: auto; }` 必须跟窄屏的 `.head { align-items: center; }` 一起删**：
      单留它、删掉 `align-items: center`，`align-self: auto` 就落到父级的 `baseline` ——
      窄屏那个 32×32 的自绘复选框基线在**下边缘**，反过来支配整行基线：
      按钮与时间戳被顶到复选框下边缘那条线上（比复选框顶低 15px，行高 32px → 39px），
      正是基础规则 `.select { align-self: flex-start; }`（`:682-690` 的注释）要防的「高一截」。
      **代价**：窄屏复选框从「跟着按钮居中」变回「顶对齐」—— 这是**有意为之**，
      不是漏改；这段对齐本来就是过渡态，Task 10 的 Step 3 会把 `<article class="idea">` 改成「勾选框 → 正文 → meta 行」、`.head` 整个拆掉。

- [ ] **Step 8: 改 `MergeDialog.vue` 与 `HomePage.vue`**

  - `web/src/components/MergeDialog.vue:55-60`：
    - `<span class="merge-dialog__item-title">{{ idea.title?.trim() || '（无标题）' }}</span>`
      → `<span class="merge-dialog__item-title">{{ firstLine(idea.content) }}</span>`；
    - import 加上 `import { firstLine } from '../lib/text';`；
    - `.merge-dialog__item-title` 的 CSS 类名**留着**（那是「预览行」的样式，不是想法的标题）—— ⚠️ 规格 §4.5 明说 CSS 类名里带 "title" 的不要动。
  - `web/src/pages/HomePage.vue:571-591`：`onSubmit` 里的请求体
    ```ts
    await apiSend<Idea>('POST', '/ideas', {
      projectId: currentProjectId.value,
      content,
      title: null,
      tags: [],
    });
    ```
    → 删掉 `title: null,` 那一行。

- [ ] **Step 9: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。

  Run: `cd web && npm run typecheck`
  Expected: 无错误（`Idea` 少了 `title`、`SearchMatch.field` 少了 `'title'` ——
  下游用到它们的地方本 task 都已经改掉了，**包括上面那张表里的 10 个 spec 文件** ——
  少改一个就是 TS2353）。

- [ ] **Step 10: 提交**

  ```bash
  git add web/src/api/types.ts web/src/api/events.ts web/src/api/events.spec.ts web/src/api/types.spec.ts \
          web/src/lib/text.ts web/src/lib/text.spec.ts web/src/lib/highlight.ts web/src/lib/highlight.spec.ts \
          web/src/components/MessageItem.vue web/src/components/MessageItem.spec.ts \
          web/src/components/MergeDialog.vue web/src/components/MergeDialog.spec.ts \
          web/src/components/MessageList.spec.ts web/src/pages/TrashPage.spec.ts \
          web/src/stores/ideas.spec.ts web/src/lib/merge.spec.ts web/src/lib/filter.spec.ts \
          web/src/composables/useHistoryScroll.spec.ts \
          web/src/pages/HomePage.vue web/src/pages/HomePage.spec.ts
  git commit -m "fix(web): 去掉「标题」，连同 SSE 帧校验里那条会丢帧的检查

  帧校验里那条 record.title === null || typeof record.title === 'string'
  是按「服务器恒定发 title」写的。服务器不再发 title 之后 record.title 是
  undefined，undefined === null 为假 —— 每一帧都被判成畸形而丢弃，界面
  从此再也不更新。静默但致命。

  类型同步收窄：Idea 没有 title，SearchMatch.field 只剩三种。

  界面上也就不能再留一个「视觉标题」层 —— 同一句话在一条卡片里出现两次，
  或者首行被吃掉，两种都不行。正文一律显示全文（含首行），靠
  -webkit-line-clamp 截断。splitTitleAndBody 的存在意义就是「摘正文首行
  当视觉标题」，跟标题能力一起去掉。

  可访问名称改用正文首行（截断到 40 字）：<h2 class=\"title\"> 拆掉之后，
  勾选框的 aria-label 不能再空着。"
  ```

---

### Task 7: e2e 夹具与用例去掉「标题」

**Files:**
- Modify: `web/e2e/fixtures.ts:45`（`RESET_ARTIFACT`）、`:53`（`Idea` 接口）、`:103`（`CreateIdeaInput`）、`:214`（`createIdea`）、`:219`（`updateIdea`）、`:242-249`（`mergeIdeas`）、`:358-386`（`clearIdeas`）、`:443-448`（`Seed.idea` / `Seed.doneIdea`）、`:496-512`（`seed` 实现）
- Modify: `web/e2e/search.spec.ts:60-71`（「搜索：能搜到标题里的词」）、`:107`（带标题的那条想法）
- Modify: `web/e2e/layout.spec.ts:100-133`（home 路由的造数据与就绪探针）、`:127-131`（`textBlocks`）
- Modify: `web/e2e/fixtures.spec.ts:38-46`（复位自检：断言 `artifact.title` → `artifact.content`）
- Modify: `web/e2e/ideas.spec.ts:96-97`、`:108-109`、`:113`、`:421-422`、`:450-451`、`:477`（前五个位置依赖「视觉标题」的断言；`:421-422` 是合并对话框预览行与正文重复导致的 strict mode violation）
- Modify: `web/e2e/COVERAGE.md`（「想法带 title 时…」那两行）
- Modify: `web/e2e/README.md:231`（选择器策略里 `aria-label="选择 <标题>"` 那行）
- Test: `server/IdeaApp.Tests/SseTests.cs:35`、`:54`、`:425`（`CreateIdeaAsync` 的 title 参数）、`server/IdeaApp.Tests/TrashTests.cs:601`、`server/IdeaApp.Tests/SettingsTests.cs:222`

**Interfaces:**
- Consumes: Task 2 的契约（请求体没有 `title`）
- Produces:
  ```ts
  // web/e2e/fixtures.ts
  export interface Idea {
    id: string;
    projectId: string;
    content: string;
    tags: string[];
    done: boolean;
    createdAt: string;
    updatedAt: string;
  }

  export interface CreateIdeaInput {
    projectId?: string;
    content: string;
    tags?: string[];
  }
  ```

- [ ] **Step 1: 跑测试确认它失败**

  Run: `bash web/e2e/run-e2e.sh --fresh e2e/search.spec.ts`
  Expected: 失败（「搜索：能搜到标题里的词」那条用例红 —— 想法没有标题了，造不出带标题的想法）。

  Run: `bash web/e2e/run-e2e.sh --fresh e2e/ideas.spec.ts e2e/fixtures.spec.ts`
  Expected: 失败 —— `fixtures.spec.ts` 的 `expect(artifact.title).toBe(RESET_ARTIFACT)` 红
  （`Idea` 接口没有 `title` 了 → 恒为 `undefined`）；`ideas.spec.ts` 里 6 处依赖
  **视觉标题**的断言也红（Task 6 把 `<h2 class="title">` 拆了、正文改成全文），
  另有 `:421-422` 两处红：合并对话框的预览行改成了**正文首行**，
  与下面 `<pre class="merge-dialog__item-body">` 的正文文本重复 → `getByText(..., { exact: true })` 命中 2 个元素（strict mode violation）。

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SseTests|FullyQualifiedName~TrashTests|FullyQualifiedName~SettingsTests"`
  Expected: 编译失败或失败（`CreateIdeaAsync` 还带 `title` 参数）。

- [ ] **Step 2: 改 `web/e2e/fixtures.ts`**

  - `Idea` 接口（`:53`）：删掉 `title: string | null;`；
  - `CreateIdeaInput`（`:103`）：删掉 `title?: string | null;`；
  - `createIdea`（`:210-217`）：请求体里删掉 `title: input.title ?? null,`；
  - `updateIdea`（`:219-221`）：签名改成 `async updateIdea(id: string, patch: { content?: string }): Promise<Idea>`；
  - `mergeIdeas`（`:241-250`）：签名改成
    `async mergeIdeas(ids: string[], options: { content?: string } = {}): Promise<Idea>`，请求体里删掉 `title: options.title ?? null,`；
  - `clearIdeas`（`:358-386`）：
    - 造那条合并产物时（`:379`、`:386`）删掉 `title: RESET_ARTIFACT,`；
    - ⚠️ **复位用的 `RESET_ARTIFACT` 断言要改到 content 上** —— 残渣想法的**正文**是 `e2e-reset`（标题那一列已经不存在了）。`RESET_ARTIFACT` 常量（`:45`）的注释改成「复位留下的合并产物的**正文**」；
  - `Seed.idea` / `Seed.doneIdea`（`:443-448`）：选项类型里的 `title?: string | null` 删掉；
  - `seed` 实现（`:496-512`）：请求体里删掉 `title: options.title ?? null,` 与 `title: null,`。

  **复位自检在另一个文件里** —— `web/e2e/fixtures.spec.ts`：
  - `:38` 的注释「残渣形态：有想法的项目留一条标题为 e2e-reset 的想法」改成「留一条**正文**为 e2e-reset 的想法」；
  - `:45` 的 `expect(artifact.title).toBe(RESET_ARTIFACT);` 改成 `expect(artifact.content).toBe(RESET_ARTIFACT);`。
  ⚠️ **规格 §4.3 H 说的「复位用的 `RESET_ARTIFACT` 断言改到 content 上」指的就是这里** ——
  断言在 `fixtures.spec.ts` 里，不在 `fixtures.ts` 里。漏掉它，e2e 一轮就红在这儿。

- [ ] **Step 3: 改 `web/e2e/search.spec.ts`**

  - 删掉整条用例「搜索：能搜到标题里的词」（`:60-71`）；
  - 「搜索：结果带高亮」（`:107`）那条想法的 `title: '采购清单'` 删掉。

- [ ] **Step 4: 改 `web/e2e/ideas.spec.ts`（依赖视觉标题的断言）**

  Task 6 把 `<h2 class="title">` 拆了、正文改成全文之后，`ideas.spec.ts` 里 6 处断言失效；
  另有 `:421-422` 两处是 Task 6 把合并对话框的预览行改成**正文首行**造成的
  （预览行与 `<pre>` 正文文本重复 → strict mode violation）。
  **这些都不能拖到 Task 15 才改** —— 本 task 的 e2e 闸门要全绿：

  | 位置 | 现在断言的 | 改成 |
  |---|---|---|
  | `:96`、`:108` | `item.getByRole('heading', { name: '第一行：今天想到一个点子', exact: true })` | `expect(item).toContainText('第一行：今天想到一个点子')`（`article` 里现在有全文） |
  | `:97`、`:109` | `expect(item.getByTitle('点击编辑')).toHaveText('第二行：再补一句细节')` | `toHaveText('第一行：今天想到一个点子\n第二行：再补一句细节')` —— **正文现在是全文**（首行不再被摘走当标题） |
  | `:421`、`:422` | `dialog.getByText('想法甲', { exact: true })` —— 预览行现在也是「想法甲」，与 `<pre>` 正文文本重复 → 命中 2 个元素（strict mode violation） | 定位预览行本身：`expect(dialog.locator('.merge-dialog__item-title').first()).toHaveText('想法甲')`（`.merge-dialog__item-title` 是预览行的类名 —— 规格 §4.5 明说 CSS 类名里带 "title" 的不要动） |
  | `:450`、`:451` | 合并结果的 heading + 只剩第二行 | 同上：断言 `article` 里**包含完整的合并正文** |
  | `:477` | `getByRole('heading', { name: '我自己整理过的正文' })` | `expect(ideaItem(page, ideas[0].id)).toContainText('我自己整理过的正文')` |
  | `:113` | `seed.idea('原来的正文', { title: '一条想法' })` | 去掉 `{ title: '一条想法' }`（`Seed` 的选项类型里已经没有 `title` 了） |

  ⚠️ **这些是「结构变了，断言没写错」** —— 按 Global Constraints 的要求，
  每一条调整的理由要在本 task 的提交说明里写清。

- [ ] **Step 5: 改 `web/e2e/layout.spec.ts`**

  - `prepare`（`:100-111`）：造那条想法时删掉 `title: '排版探测标题',`；
  - `ready`（`:112`）：`page.getByText('排版探测标题', { exact: true }).first()` → 改成正文里的一个文本：`page.getByText('排版探测想法', { exact: false }).first()`（正文是「排版探测想法：第一行\n第二行正文…」）；
  - `textBlocks`（`:127-131`）：
    - `'想法条目的标题'` 那一条**删掉**（`<h2 class="title">` 没了）；
    - `'超长不换行标题'` 那一条改成 `'超长不换行正文'`，定位器从 `.idea .title` 改成 `.idea .body`。

- [ ] **Step 6: 改服务端测试里的 helper**

  - `server/IdeaApp.Tests/SseTests.cs`：`CreateIdeaAsync`（`:425`）的 `string? title = null` 参数删掉，请求体里的 `title` 删掉；调用点 `:35`、`:54` 的第三个实参「标题」删掉；
  - `server/IdeaApp.Tests/TrashTests.cs:601` 的 helper：同款（看有没有 title 参数，有就删）；
  - `server/IdeaApp.Tests/SettingsTests.cs:222` 的 helper：同款（`:225` 的请求体里 `title = "标题"` 删掉）。

- [ ] **Step 7: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS。

  Run: `bash web/e2e/run-e2e.sh --fresh`
  Expected: 全部 PASS。

- [ ] **Step 8: 改 `web/e2e/COVERAGE.md` 与 `web/e2e/README.md`**

  - `COVERAGE.md`：删掉那两行「想法带 title 时…」的用例记录，并把用例总数与覆盖矩阵按**实际**用例数对齐（⚠️ 不要照抄任何一个数字 —— 现有文件里的数字与 git log 里的数字本来就不一致）。
  - `README.md`（`:231`）：选择器策略里的
    `- \`aria-label="选择 <标题>"\`（想法条目的复选框）`
    → `- \`aria-label="选择 <正文首行>"\`（想法条目的复选框）`
    （勾选框的可访问名现在取正文首行，见 Task 6）。

- [ ] **Step 9: 提交**

  ```bash
  git add web/e2e/fixtures.ts web/e2e/fixtures.spec.ts web/e2e/search.spec.ts web/e2e/ideas.spec.ts \
          web/e2e/layout.spec.ts web/e2e/COVERAGE.md web/e2e/README.md \
          server/IdeaApp.Tests/SseTests.cs server/IdeaApp.Tests/TrashTests.cs server/IdeaApp.Tests/SettingsTests.cs
  git commit -m "test(e2e): 夹具与用例去掉标题

  想法不再有标题，夹具里的 title 字段与那条「搜索：能搜到标题里的词」的
  用例一起消失。复位留下的残渣想法，断言要改到正文上（标题那一列已经
  不存在了）—— 那条断言在 fixtures.spec.ts 里，不在 fixtures.ts 里。

  ideas.spec.ts 里 6 处断言原来指向 <h2 class=\"title\"> 与「正文只剩第二行」：
  结构变了（标题层拆掉、正文改成全文），断言没写错，所以改成断言 article
  里包含完整正文。

  排版用例的就绪探针从「标题」改到正文，<h2 class=\"title\"> 没了之后
  那个「关键文本块」条目也要重写。"
  ```

---

### Task 8: 规格与 README 同步

**Files:**
- Modify: `docs/superpowers/specs/2026-11-11-idea-manager-design.md:66`、`:137`、`:220`、`:231`、`:250`、`:282`、`:299`、`:378`、`:398`、`:448-449`、`:465`、`:522-523`、`:708`
- Modify: `docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md:17`、`:29`、`:254`、`:294`、`:391-392`、`:577`、`:651`、`:739`、`:874-886`、`:918`、`:922`、`:946`、`:968`、`:1030`、`:1032`
- Modify: `docs/superpowers/specs/2026-11-13-skill-distribution-design.md:234`
- Modify: `README.md:401`
- Test: 无（纯文档）

**Interfaces:**
- Consumes: Task 1–7 的最终形态
- Produces: 无代码

- [ ] **Step 1: 改 `2026-11-11-idea-manager-design.md`**

  按规格 §4.3 I 点名的八处逐条改：
  - `:66`「一段正文 + 可选标题 + 若干标签」→「一段正文 + 若干标签」；
  - `:137` 数据模型里的 `title TEXT NULL,` 删掉；
  - `:220`「写操作包括：改正文/标题、…」→「改正文、…」；
  - `:231`「匹配范围：标题、正文、标签名、项目名**都在索引里**」→「正文、标签名、项目名**都在索引里**」；
  - `:250` 合并规则里的「`title` 取第一条源想法的 title」删掉；
  - `:282` 事件表里「改正文/标题、设标签、标记完成」→「改正文、设标签、标记完成」；
  - `:299` 事件示例里的 `"title": null,` 删掉；
  - `:378` 占位符表里的 `{{idea.title}}` 删掉；
  - `:398` Telegram 模板里的 `{{idea.title}}\n` 删掉；
  - `:448-449` 接口表里的 `title?` 删掉（`POST /api/ideas` 与 `PATCH /api/ideas/{id}`）；
  - `:465` 合并接口的 `title?` 删掉；
  - `:522-523` 界面描述里的「标题留空」与「标题（可空）」删掉；
  - `:708`「留给实现者的空白」里那条「想法条目的 `title` 是否在列表里单独占一行」**删掉**（已经没有 title 了，这不再是待定项）。

- [ ] **Step 2: 改 `2026-11-15-backend-full-text-search-design.md`**

  按规格 §4.3 I 点名的六处逐条改：
  - `:17`、`:29` 目标里的「标题、标签名、项目名都搜不到」→「标签名、项目名都搜不到」；G2 的「检索范围覆盖**正文、标题、标签名、项目名**」→「**正文、标签名、项目名**」；
  - `:254` FTS 表结构里的 `title, content, tags, project,` → `content, tags, project,`；
  - `:294` 列说明表里的 `idea_fts | title | …` 那一行删掉；
  - `:391-392` 写路径表里的「插入 `idea_fts` 行（title/content/tags/project 四列）」→「（content/tags/project 三列）」；「更新 `idea_fts` 行的 `title`/`content` 两列」→「只更新 `content` 那一列」；
  - `:577` `Field` 的取值列表去掉 `"title"`；
  - `:651` 权重那句改成「列序是 `content, tags, project` → 正文 5.0、标签 5.0、项目 3.0」；
  - `:739`「只对**想法**的 `title` / `content` / `tags` / `project` 四列算」→ 三列；
  - `:874-886`「视觉标题带来的偏移错位」那一节改成「**没有标题了**：命中区间直接相对完整原文，渲染时按区间切片段，不需要按行分组」；
  - `:918`、`:922`、`:946`、`:968`、`:1030`、`:1032` 里提到「标题命中」的地方按新语义改（`:1032` 的权重写成 `bm25(idea_fts, 5.0, 5.0, 3.0)` = 正文/标签/项目）。

- [ ] **Step 3: 改 `2026-11-13-skill-distribution-design.md` 与 `README.md`**

  - `2026-11-13-skill-distribution-design.md:234`：那句「（想法的 `title` 空串 = 清成无标题；webhook 的 `headers` 给 `{}` = 清空请求头）」→「（webhook 的 `headers` 给 `{}` = 清空请求头）」；
  - `README.md:401`：curl 示例 `{"content":"正文","title":"标题"}` → `{"content":"正文"}`。

- [ ] **Step 4: 跑闸门**

  Run: `bash verify.sh`
  Expected: 四步全过。

  Run: `cd web && npm test && npm run typecheck`
  Expected: 全部 PASS。

- [ ] **Step 5: 提交**

  ```bash
  git add docs/superpowers/specs/2026-11-11-idea-manager-design.md \
          docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md \
          docs/superpowers/specs/2026-11-13-skill-distribution-design.md README.md
  git commit -m "docs(spec): 三份规格与 README 同步「想法没有标题」

  规格里凡是提到标题字段、标题命中、标题占位符、合并时取首条标题的地方
  全部改掉 —— 当前事实以 2026-11-17 的布局重设计规格为准。

  旧的 plan 文档（docs/superpowers/plans/*.md）不改：那是历史记录。"
  ```

---

# 第二段：前端布局重设计（规格 §5–§10）

> **前置**：Task 1–8 全部完成后才开始这一段。`web/src/api/types.ts` 的 `Idea` 没有 `title`、`SearchMatch.field` 没有 `'title'` —— 下面的组件都按这个形态写。

### Task 9: 主页骨架 —— 侧栏贴左、主区铺满、顶栏

**Files:**
- Modify: `web/src/pages/HomePage.vue:1170-1280`（模板）、`:1282-1431`（样式）
- Create: `web/src/components/ProjectDropdown.vue`、`web/src/components/MoreMenu.vue`
- Delete: `web/src/components/ProjectTabs.vue`、`web/src/components/ProjectTabs.spec.ts`（窄屏的项目切换改成下拉后它没人用了）
- Modify: `web/e2e/layout.spec.ts`（删掉「窄屏项目 Tab 条的渐隐带…」那条用例与它的 `measureFade` 辅助函数；删掉 `containers` 里的 `.project-tabs__list` 与 `INTENTIONAL_H_SCROLL` 里的同名条目）
- Modify: `web/e2e/COVERAGE.md`（`:56` 的文件清单、`:123`/`:124` 的矩阵行、`:415`/`:420` 的漏项说明里对 `ProjectTabs.vue` 的引用）
- Test: `web/src/components/ProjectDropdown.spec.ts`（新建）、`web/src/components/MoreMenu.spec.ts`（新建）、`web/src/pages/HomePage.spec.ts`

**Interfaces:**
- Consumes: Task 6 之后的 `Idea`；`web/src/stores/projects.ts` 的 `projects` / `currentProjectId`
- Produces:
  ```ts
  // web/src/components/ProjectDropdown.vue
  /**
   * 窄屏顶栏的项目下拉。
   * 数据源与侧栏同一份（projects + currentId），宽屏侧栏不在这里重复。
   */
  defineProps<{
    /** 项目列表（顺序由后端给，前端不重排）。 */
    projects: Project[];
    /** 当前选中的项目；null 表示「全部项目」。 */
    currentId: string | null;
  }>();
  defineEmits<{
    /** 选了某个项目（null = 全部项目）。 */
    select: [id: string | null];
  }>();
  ```
  ```ts
  // web/src/components/MoreMenu.vue
  /**
   * 顶栏的「⋯」下拉菜单：项目管理 / 回收站 / Webhook。
   * （主页原来没有 PageNav，手机上进回收站要两步。）
   */
  defineEmits<{
    manageProjects: [];
    trash: [];
    webhooks: [];
  }>();
  ```
  HomePage 的模板结构变成：
  ```html
  <main class="home">
    <aside class="home__sidebar"><ProjectSidebar … /></aside>
    <section class="home__content">
      <header class="home__topbar">
        <h1 class="home__topbar-title">想法</h1>
        <div class="home__topbar-project"><ProjectDropdown … /></div>
        <div class="home__topbar-actions">
          <button class="home__topbar-search" type="button" @click="focusSearch">🔍</button>
          <MoreMenu @manage-projects="…" @trash="…" @webhooks="…" />
        </div>
      </header>
      <div class="home__filters"><FilterBar … /></div>
      <div class="home__body"> … 滚动容器 + MessageList + BatchBar … </div>
      <Composer … />
    </section>
  </main>
  ```

- [ ] **Step 1: 写失败的测试**

  **新建 `web/src/components/ProjectDropdown.spec.ts`**：

  | 测试 | 断言 |
  |---|---|
  | `the_trigger_shows_the_current_project_name` | `currentId` 指向一个叫「工作复盘」的项目 → 触发器里有「工作复盘」 |
  | `the_trigger_shows_all_projects_when_none_is_selected` | `currentId` 为 null → 触发器里有「全部项目」 |
  | `picking_a_project_emits_select` | 点面板里的「工作复盘」→ 发出 `select` 事件，参数是那个项目的 id |
  | `picking_all_projects_emits_null` | 点「全部项目」→ 发出 `select`，参数是 `null` |

  **新建 `web/src/components/MoreMenu.spec.ts`**：

  | 测试 | 断言 |
  |---|---|
  | `the_three_management_entries_are_reachable` | 打开菜单 → 「项目管理」「回收站」「Webhook」三项都在 |
  | `each_entry_emits_its_own_event` | 点「回收站」→ 发出 `trash`；点「Webhook」→ 发出 `webhooks`；点「项目管理」→ 发出 `manageProjects` |
  | `clicking_outside_closes_the_menu` | 点组件外 → 菜单收起（照 `TagFilter` 的做法：`document` 的 click 监听 + 组件根节点包含判断） |

  **改 `web/src/pages/HomePage.spec.ts`**：
  - 加一条：主页顶栏的「⋯」菜单里有「回收站」与「Webhook」两项（点「回收站」→ 路由变成 `/trash`）。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/components/ProjectDropdown.spec.ts src/components/MoreMenu.spec.ts`
  Expected: 失败（组件不存在）。

- [ ] **Step 3: 新建 `ProjectDropdown.vue`**

  照 `web/src/components/TagFilter.vue` 的结构写（同样的「根节点 + `open` ref + document click 收起 + Esc 收起」）。

  ⚠️ **这段「点外面收起 / Esc 收起」的逻辑与 `TagFilter.vue` 逐字相同**（重复是真的，不是错觉）。
  Task 9 只能照抄（计划要求如此），**但别再写第四份**：Task 13 会把它连同 `TagFilter.vue`
  一起收敛成 `web/src/composables/useDropdown.ts`（Task 13 引入并复用）。

  - 触发器：一个 `<button>`，`aria-expanded` 绑 `open`，文本是当前项目名（或「全部项目」）+ `▾`；
    ⚠️ **名字可能很长**：触发器要 `inline-flex` + `max-width: 100%` + `overflow: hidden`，
    名字那一段（span）要是**块级盒**（行内盒上 `text-overflow` 不生效），
    否则长项目名会压到顶栏的动作区（🔍 / ⋯）上、甚至顶出视口 —— 窄屏 375px 上实测过。
  - 面板：`v-if="open"` 的一块绝对定位的层，**第一项是「全部项目」**，其余是 `projects`（顺序照 `projects` 数组 —— 后端已经按排序方式排好了，前端不重排）；
  - 每一项是一个 `<button>`，点了就 `emit('select', id)` 并收起面板；
  - 面板的 CSS 照 `TagFilter.vue` 的 `.tag-filter__panel` 抄（`position: absolute`、`min-width: 180px`、`border: 1px solid var(--border)` 等），**只用现有主题变量**；
    ⚠️ **实现时把面板的公共外观收到了 `web/src/styles/dropdown.css`**（Task 13 引入）：面板 `max-width: calc(100vw - 24px)`、面板项 `display: block` + `overflow-wrap: anywhere`（长项目名要能换行，不能顶出面板），
    四个下拉组件（TagFilter / ProjectDropdown / MoreMenu / 新的状态下拉）都挂这个共享 class —— 抄一份改一处、漏一处就是「面板的样子对不上」；
  - ⚠️ **触发器要满足触摸目标**（规格 §6.10 末句：「新加的下拉触发器、发送按钮同样适用」）：
    ```css
    @media (max-width: 767px) {
      .project-dropdown__trigger {
        min-width: 32px;
        min-height: 32px;
      }
    }
    ```
    ⚠️ 触点目标这条**不能只在 Task 15 的 e2e 里才发现** —— 布局 e2e 有一轮全页扫描
    （`layout.spec.ts` 的「移动端点按目标」用例，扫 `button, a[href], input, …`），
    它扫到这些新按钮就会红。

- [ ] **Step 4: 新建 `MoreMenu.vue`**

  同样照 `TagFilter.vue` 的下拉面板做法（规格 §7.5：**是下拉面板，不是原生 `<details>`**）；
  「点外面收起 / Esc 收起」那段逻辑与 `TagFilter.vue` 逐字相同 —— Task 13 会收敛成
  `web/src/composables/useDropdown.ts`（Task 13 引入并复用），别再写第四份：

  - 触发器：`<button aria-expanded="open">⋯</button>`（要有可访问名：`aria-label="更多"`）；
  - 面板里三项：「项目管理」「回收站」「Webhook」，每项一个 `<button>`，点了发对应事件并收起；
  - ⚠️ 菜单项的**文字**要与 `PageNav.vue` 里的链接文字一致（「想法 / 项目 / 回收站 / Webhook」），但这里**不含「想法」**（主页本身就是想法页）；
  - ⚠️ **触发器与菜单项都要满足触摸目标**（规格 §6.10 末句）：
    ```css
    @media (max-width: 767px) {
      .more-menu__trigger,
      .more-menu__item {
        min-width: 32px;
        min-height: 32px;
      }
    }
    ```

- [ ] **Step 5: 改 `HomePage.vue` 的模板**

  在 `web/src/pages/HomePage.vue:1170-1280`：
  - `<header class="bar">` 换成 `<header class="home__topbar">`；
  - `<h1 class="bar__title">想法</h1>` 换成 `<h1 class="home__topbar-title">想法</h1>`（**`h1` 与文案「想法」不能变** —— e2e 依赖这个 heading）；
  - 在标题右边加窄屏的项目下拉：`<div class="home__topbar-project"><ProjectDropdown :projects="projects" :current-id="currentProjectId" @select="onSelectProject" /></div>`；
  - 在右边加动作区：`<button class="home__topbar-search" type="button" aria-label="搜索" @click="focusSearch">🔍</button>` 与 `<MoreMenu @manage-projects="onManageProjects" @trash="goTrash" @webhooks="goWebhooks" />`；
  - 删掉 `<div class="home__tabs">` 那一段（窄屏的项目 Tab 条换成下拉了 —— 规格 §D7）；**`ProjectTabs` 的 import 也删掉**；
  - 动作区的两个导航方法：
    ```ts
    /** 去回收站。 */
    function goTrash(): void { void router.push('/trash'); }
    /** 去 Webhook 页。 */
    function goWebhooks(): void { void router.push('/webhooks'); }
    ```
  - `focusSearch` 的实现留给 Task 13（窄屏 🔍 要聚焦过滤行里的搜索框）—— **本 task 先占位**：
    ```ts
    /** 滚回列表顶部并聚焦搜索框（Task 13 补齐聚焦逻辑）。 */
    function focusSearch(): void {
      void nextTick();
    }
    ```
    ⚠️ Task 13 会把它改完整；**不要在这里引第二套搜索状态**。

- [ ] **Step 6: 改 `HomePage.vue` 的样式（骨架铺满）**

  在 `web/src/pages/HomePage.vue:1282-1431`：
  - `.home`（`:1287-1296`）：
    - **删掉 `justify-content: center;`**（这是「两侧各空 400px」的根因）；
    - `padding: 16px` → `padding: 0`（侧栏要贴左边缘）；
    - `gap: 20px` → `gap: 10px`（规格 §8.1 的倾向：主区四周留 10–12px）；
  - `.home__content`（`:1304-1312`）：
    - **删掉 `max-width: 880px;`**（主页不再有页面级宽度上限 —— 这是「宽屏铺满」的关键）；
    - `gap: 10px` 保留；
    - ⚠️ **另外加 `padding: 0 12px 12px;`**（规格 §8.1「主区四周留 10–12px」）：
      只删 `max-width` 的话主区会一路贴到窗口右边缘（卡片顶到边）。
      计划原先没写这一条 —— 实施时补上的，别再删回去；
  - `.home__sidebar`（`:1298-1302`）：`flex: 0 0 220px` 保留；
    - ⚠️ **另外加 `padding: 10px 0 0 8px;`**：侧栏**这一列**贴左边缘（`.home` 的 padding 归零了），
      但里面的内容别贴边 —— 上 10px（标题别顶到窗口上边缘）、左 8px
      （「项目」标题与选中项的高亮条别画在屏幕物理边缘上，看着像被裁掉）；
  - 新增 `.home__topbar` 的规则：`display: flex; align-items: center; gap: 10px; padding: 10px 0;`
    - ⚠️ **是 `10px 0`，不是 `10px 12px`**：主区自己已经有 12px 的内边距了，
      顶栏再带 12px 左内边距的话，顶栏标题会比下面那行（过滤行、卡片）**多缩进 12px**；
  - `.home__topbar-title`：`margin: 0; font-size: 20px;`（照 `.bar__title` 改）+ `flex: none`（标题不参与压缩）；
  - `.home__topbar-project`：窄屏才显示（`display: none` 默认，`@media (max-width: 1023px)` 里 `display: block`）
    + `min-width: 0`（这一格要允许被压窄，否则长项目名把顶栏撑破）；
  - `.home__topbar-actions`：`margin-left: auto; display: flex; align-items: center; gap: 8px;` + `flex: none`
    （🔍 与 ⋯ 必须一直点得到，不参与压缩）；
  - `.home__topbar-search`：窄屏才显示（宽屏的搜索框在过滤行里，不重复）
    + `@media (max-width: 767px)` 下 `min-width/min-height: 32px`（规格 §6.10：新加的按钮同样适用）；
  - 删掉 `.bar` / `.bar__title` / `.home__tabs` 的规则；
  - 窄屏那一段 `@media (max-width: 1023px)`（`:1389-1419`）：
    - `.home` 的 `flex-direction: column` 保留（窄屏竖排），但 **`justify-content: center` 已经删了**，改成 `flex-start`；
    - ⚠️ **窄屏的 `.home` 的 `padding` 也是 `0`**（计划原先写的 `12px` 不用）：
      留白统一交给主区那 12px，否则窄屏左右各 24px（375px 的屏上白扔 48px）；
    - `.home__sidebar { display: none; }` 保留；
    - **删掉 `.home__tabs { display: block; order: 2; }`**（组件删了）；
    - `.home__filters` 的 `order: 4` → `order: 2`（**过滤行要在列表顶部**，这是 Task 13 的事，但骨架顺序现在就该定下来 —— 窄屏上过滤行随内容滚走、不钉死）；
    - `.composer { order: 5 }` 保留（输入条永远贴底）。

- [ ] **Step 7: 删掉没人用的 `ProjectTabs` 与它那条 e2e 用例**

  窄屏的项目切换改成下拉之后，`web/src/components/ProjectTabs.vue` **没人用了**（宽屏是侧栏）：

  - 删掉 `web/src/components/ProjectTabs.vue` 与 `web/src/components/ProjectTabs.spec.ts`；
  - `web/e2e/layout.spec.ts` 里那条「窄屏项目 Tab 条的渐隐带不淡掉还看得清的字」用例（连同它的
    `measureFade` 辅助函数与 `measureFade` 的类型）**一起删掉** —— Tab 条没了，这条用例没有对象；
  - ⚠️ **`web/e2e/layout.spec.ts` 里还有两处指着 `.project-tabs__list`**：
    `INTENTIONAL_H_SCROLL`（`:94`）与 home 路由的 `containers`（`:124`）——
    组件删了之后这个定位器打不中任何元素，而 `expectNoUnexpectedHorizontalScroll`
    对不存在的元素做 `evaluate` 会**抛错** → 布局 e2e 红。两处都删掉；
  - ⚠️ **`web/e2e/COVERAGE.md` 里还有 5 处引用 `ProjectTabs.vue`**（`:56` 的文件清单、
    `:123`/`:124` 的覆盖矩阵行、`:415`/`:420` 的漏项说明）—— 一起改掉；
  - ⚠️ **连带后果**：Tab 条上的「＋ 新建项目 / ⋯ 菜单」两个按钮随组件一起消失，
    窄屏下新建项目只剩「⋯ → 项目管理」两步（规格 §D11 接受这个入口结构，但这里要写明）。
  - ⚠️ 删之前**先看一遍**那条用例测的是什么（Tab 条的渐隐带不许淡掉还看得清的字）：
    它的**意图**（「渐隐带不许吃掉可读的字」）在新的下拉上**不适用**（下拉面板是浮层，不滚动），
    所以是**连意图一起作废**，不是换个选择器继续测。

- [ ] **Step 8: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。

  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 9: 提交**

  ```bash
  git add web/src/components/ProjectDropdown.vue web/src/components/ProjectDropdown.spec.ts \
          web/src/components/MoreMenu.vue web/src/components/MoreMenu.spec.ts \
          web/src/components/ProjectTabs.vue web/src/components/ProjectTabs.spec.ts \
          web/src/pages/HomePage.vue web/src/pages/HomePage.spec.ts \
          web/e2e/layout.spec.ts web/e2e/COVERAGE.md
  git commit -m "feat(web): 主页骨架改成侧栏 + 铺满的主区

  之前 .home 整体 justify-content: center，内容栏还卡着 max-width: 880px ——
  1920px 的屏上左右各空约 400px，一屏能看到的条数一点没变。

  这次去掉页面级的居中与宽度上限：侧栏贴左边缘，主区铺满剩余宽度。
  窄屏的项目切换从横向 Tab 收成顶栏里的下拉（顶栏挤不下第二条 Tab 行），
  主页顶栏补上「⋯」菜单直接进回收站 / Webhook —— 手机上进回收站原来
  要点两步。

  Tab 条因此没人用了，连同它那条「渐隐带不许淡掉可读的字」的 e2e 用例
  一起删掉（那条规则的意图在浮层式的下拉上不适用）。"
  ```

---

### Task 10: 卡片改成「一整块正文 + 底部 meta 行」

**Files:**
- Modify: `web/src/components/MessageItem.vue`（模板 `:571-667`、样式 `:669-890`）
- Modify: `web/src/components/MessageList.vue:47-69`（把新的 `position` prop 传进去）
- Test: `web/src/components/MessageItem.spec.ts`

**Interfaces:**
- Consumes: Task 9 的骨架；`web/src/lib/format.ts` 的 `formatRelativeTime`
- Produces:
  ```ts
  // web/src/components/MessageItem.vue
  defineProps<{
    idea: Idea;
    selected: boolean;
    busy?: boolean;
    highlight?: boolean;
    /**
     * 这一条在当前流里的位置序号（1 = 最老的那条，屏幕最上面那个）。
     *
     * 编号是「位置」不是身份：往上翻加载更早的一批，已有条目的编号会 +P。
     */
    position: number;
    suggest: (prefix: string) => string[];
    onSaveContent: (id: string, content: string) => Promise<SaveOutcome>;
    onSaveTags: (id: string, tags: string[], options?: SaveOptions) => Promise<SaveOutcome>;
  }>();
  ```
  卡片 DOM 结构：
  ```html
  <article class="idea" :data-idea-id="idea.id" …>
    <input class="select" type="checkbox" :aria-label="`选择 ${accessibleName}`" … />
    <div class="body" title="点击编辑" @click="startEdit"> …片段… </div>
    <div class="meta">
      <button class="meta__done" type="button" :aria-pressed="idea.done" …>○</button>
      <ul class="tags meta__tags"> …chips… </ul>
      <span class="meta__num num">{{ position }}</span>
      <time class="meta__time">{{ formatRelativeTime(idea.updatedAt) }}</time>
      <button class="meta__action meta__action--tags" …>🏷</button>
      <button class="meta__action meta__action--delete" …>🗑</button>
    </div>
  </article>
  ```

- [ ] **Step 1: 写失败的测试**

  在 `web/src/components/MessageItem.spec.ts` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `the_body_is_one_block_of_text` | 正文「第一行\n第二行」→ `.body` 的文本**包含**两行（首行没有被摘走） |
  | `there_is_no_title_element` | `wrapper.find('h2.title').exists()` 为 false；`wrapper.find('.title').exists()` 也为 false |
  | `the_position_is_rendered_next_to_the_time` | `position = 12` → `.meta__num` 的文本是 `'12'`，且它在 DOM 里排在 `.meta__time` **之前** |
  | `the_done_button_is_always_visible` | `.meta__done` 存在，且**没有**被 `hover`/`focus` 之类的类控制显隐（断言它不在 `.idea--hover-only` 这类容器里） |
  | `the_tag_and_delete_buttons_are_marked_as_hover_only` | `.meta__action--tags` 与 `.meta__action--delete` 存在，且带 `data-hover-only` 属性（供 CSS 与 e2e 判定「悬停才显形」） |
  | `the_checkbox_is_marked_as_hover_only` | `input.select` 带 `data-hover-only` 属性 |
  | `the_delete_button_still_asks_for_confirmation` | 点删除 → 先弹确认，点「确认删除」才发出 `delete` 事件（**别把二次确认丢了**） |

  ⚠️ **`mountItem` 辅助函数要补 `position`**（`web/src/components/MessageItem.spec.ts:20-31`）：
  `position` 是**必填** prop，`@vue/test-utils` 的 `mount()` 在类型层面要求必填 prop 必须出现在
  `props` 里 —— 不补的话 `npm run typecheck`（vue-tsc）会报「缺少属性 position」。
  做法：在 `mountItem` 的 `props` 里加一行 `position: 1,`（放在 `selected: false,` 后面）。

  > `data-hover-only` 是一个**纯标记**属性：真正的显隐由 CSS 媒体查询 + `:hover` / `:focus-within` 决定
  > （规格 §6.6：「悬停显形：纯 CSS，不引入 JS」）。这个属性只给 e2e 与单测一个可断言的锚点。
  >
  > ⚠️ **这三条（连同 Review Focus 第 8 条）钉不住真实的显隐**：显隐是纯 CSS 媒体查询 + 伪类，
  > 而单测环境（vitest 的 jsdom）**不求值媒体查询** —— 写单测只能写出一批恒真的断言。
  > 真实显隐（桌面默认隐藏 / 悬停显形 / 键盘聚焦显形 / 窄屏与无 hover 能力设备常驻）
  > 由 **Task 15 的 e2e** 钉住（Review Focus 第 8 条已改派到那里）。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/components/MessageItem.spec.ts`
  Expected: 失败（`.meta` 那套结构还不存在）。

- [ ] **Step 3: 改模板**

  在 `web/src/components/MessageItem.vue:571-667`：
  - `<article class="idea">` 里改成：**勾选框 → 正文 → meta 行**（meta 行在最后）；
  - 正文 `.body` 保持「按片段渲染」不变（Task 6 已经改好）；
  - meta 行（`.meta`）的内容与顺序（照规格 §D3 的排法）：
    1. **完成态按钮** `.meta__done`：一个小圆圈，**常驻**。文案用字形：`idea.done ? '●' : '○'`（规格 §D3 的图是「一个小圆圈按钮」）；`aria-pressed` 绑 `idea.done`，`aria-label` 给「标记完成」/「已完成」；
    2. **标签 chips** `.meta__tags`：常驻（有才显示）—— 复用 Task 6 留下的 `<ul class="tags">` + `<li class="chip">`（**class 名不要改**：`.tags` / `.chip` 在样式与 e2e 里都被引用），只把它挪进 meta 行；
       ⚠️ **两个 class 都给**（`<ul class="tags meta__tags">`）：上面的 DOM 结构块写的是 `meta__tags`，
       这一条要求保留 `.tags` / `.chip` —— 实现两个都挂上，两句话同时成立。
       下游 task（Task 11/15）**按 `.tags` 或 `.meta__tags` 选都行**（两个类名都在那个 `<ul>` 上）。
    3. **编号** `.meta__num`：`<span class="num">{{ position }}</span>` —— **「淡色圆底 + 普通数字」的徽标**，不用 `①②③`（规格 §7.2）；
    4. **时间** `.meta__time`：`<time>{{ formatRelativeTime(idea.updatedAt) }}</time>` —— **沿用现有的相对时间，不改成钟点**（规格 §7.1）；
    5. **🏷 标签** `.meta__action--tags` 与 **🗑 删除** `.meta__action--delete`：桌面上悬停/聚焦才显形；
  - 编号与时间在 meta 行里**紧挨**（DOM 上相邻：`.meta__num` 在 `.meta__time` 前面）；
    ⚠️ **视觉间距由 `.meta` 的 `gap: 6px` 决定**（Step 4 的精确值），**不是字面上的「一个空格」** ——
    规格 §D3 只说「紧挨时间」，没给量化；「一个空格」是本计划自己加的说法，与同一步的 Step 4 直接冲突。
    下游 task（Task 15 的「编号紧挨时间」那条 e2e 断言）按**元素盒距离 ≤ 20px** 判，不要按「一个空格」判。
  - 标签编辑态（`<TagEditor>`）保持在卡片里（meta 行上方或原位，别丢功能）。

- [ ] **Step 4: 改样式（尺寸、间距、悬停显形、触摸目标）**

  在 `web/src/components/MessageItem.vue:669-890`：
  - `.idea`（`:670-676`）：`padding: 10px 12px` → `padding: 9px 11px 7px`（规格 §6.9）；`border-radius: 8px` → `10px`；
  - `.body`（`:749-755`）：字号与行高按规格 §6.9 —— 正文 12.5px / 行高 1.55；
    - **加截断**：`-webkit-line-clamp: 3` + `display: -webkit-box` + `-webkit-box-orient: vertical` + `overflow: hidden`（规格 §7.3：用 `-webkit-line-clamp`，不用 `max-height + overflow: hidden`）；
    - ⚠️ **宽屏与窄屏都是 3 行**（规格 §7.4）；
    - ⚠️ 截断后**唯一的全文入口是「点正文进编辑态」**（规格 §6.11）—— 编辑框里的 textarea **不做行数截断**（`.editor` 那条规则别动）；
  - `.meta`：`display: flex; align-items: center; gap: 6px; margin-top: 6px; font-size: 10.5px;`（规格 §6.9：meta 行字号 10.5–11px）；
  - `.meta__num`：`background: var(--chip-bg); border-radius: 999px; padding: 0 6px; font-size: 10.5px;`（**淡色圆底徽标**，底色用 `--chip-bg`）；
  - `.meta__time`：`color: var(--muted); white-space: nowrap;`；
  - `.meta__action`：`padding: 2px 6px; font-size: 11px;`；
  - **悬停显形（纯 CSS）**：
    ```css
    /* 桌面端（宽屏 + 有悬停能力）：勾选框与 🏷🗑 悬停/聚焦才显形 */
    @media (min-width: 768px) and (hover: hover) {
      .idea .select,
      .idea .meta__action {
        opacity: 0;
        transition: opacity 120ms ease-in-out;
      }
      .idea:hover .select,
      .idea:hover .meta__action,
      .idea:focus-within .select,
      .idea:focus-within .meta__action {
        opacity: 1;
      }
    }
    ```
    ⚠️ **必须带 `:focus-within`** —— 悬停才显形的按钮不能是「唯一入口」，键盘 Tab 到它时也要显形（规格 §6.6 / Review Focus 第 8 条）。
    ⚠️ **窄屏或无悬停能力的设备一律常驻**（规格 §D8 的并集判据）：
    ```css
    @media (max-width: 767px), (hover: none) {
      .idea .select,
      .idea .meta__action {
        opacity: 1;
      }
    }
    ```
    ⚠️ **不要用 JS 判断触屏**（`ontouchstart` 会把「触屏笔记本」误判成纯触屏）。
  - **触摸目标（窄屏）**：
    ```css
    @media (max-width: 767px) {
      .idea .meta__done,
      .idea .meta__action {
        /* 字形保持小，用 padding / 伪元素撑开 hit area，不额外增加 meta 行的高度 */
        min-width: 32px;
        min-height: 32px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
      }
    }
    ```
    ⚠️ 这条**会改变卡片高度**（窄屏上 meta 行 ≥32px）—— 是预期的（规格 §6.10：单卡约 110px，375×667 一屏约 5 张卡）。
  - 把原来那段 `@media (max-width: 640px)` 的断点改成 **767px**（规格 §D9 / §6.10：触摸目标的 32×32 规则统一到 767px）。
    ⚠️ **里面的 `.head` flex-wrap 那两条规则 Task 6 已经删掉了**（见 Task 6 Step 7 的样式清单）——
    **本 task 不用再删**，也**别把 `.select { align-self: auto; }` 加回来**：
    窄屏复选框的对齐已经改成「顶对齐」（有意为之，见 Task 6 Step 7 的说明），
    把 `align-self: auto` 加回来会让 32×32 的自绘复选框支配整行基线
    （行高 32px → 39px，按钮与时间戳被顶到复选框下边缘那条线上）。

- [ ] **Step 5: 跑测试确认它们通过**

  Run: `cd web && npm test -- src/components/MessageItem.spec.ts`
  Expected: 全部 PASS。

  Run: `cd web && npm run typecheck`
  Expected: 无错误。

  ⚠️ **`position` 是新的必填 prop**，而 `MessageList.vue` 现在还没传它 —— **本 task 里就把它传上**：
  把 `MessageList.vue` 的 `v-for="idea in ideas"` 改成 `v-for="(idea, index) in ideas"`，
  并给 `<MessageItem>` 加 `:position="index + 1"`（当前是单列，下标 +1 就是位置序号）。
  Task 11 再把 DOM 换成「一排两格」的结构。

- [ ] **Step 6: 提交**

  ```bash
  git add web/src/components/MessageItem.vue web/src/components/MessageItem.spec.ts \
          web/src/components/MessageList.vue
  git commit -m "feat(web): 卡片改成一块正文 + 底部 meta 行

  之前每条想法固定占 4 行：勾选 + 标记完成 + 标题 / 正文 / 标签 /
  常驻的「🏷 标签 🗑 删除」。20 条就差出小半屏。

  现在是「一块正文 + 一行 meta」：完成态常驻（翻看整理时最常用），标签
  chips 常驻（有才显示），🏷🗑 与勾选框在桌面上悬停/聚焦才显形，窄屏与
  无悬停能力的设备上一律常驻。

  编号用「淡色圆底 + 普通数字」的徽标，不用带圈字符 —— 一页就有 101 条，
  带圈字符到 ⑳ 之后就没有一致的写法了。"
  ```

---

### Task 11: 想法排成两列交错流

**Files:**
- Modify: `web/src/components/MessageList.vue:47-69`（模板）、`:71-84`（样式）
- Create: `web/src/lib/rows.ts`、`web/src/lib/rows.spec.ts`
- Test: `web/src/components/MessageList.spec.ts`

**Interfaces:**
- Consumes: Task 10 的 `MessageItem`（新增 `position` prop）
- Produces:
  ```ts
  // web/src/lib/rows.ts（新建）
  /**
   * 一排两个格子。
   *
   * 奇数位（第 1、3、5…条）进左格，偶数位进右格 —— 编号与左右由**同一个
   * 位置序号**决定，所以「奇数在左、偶数在右」永远成立。
   * 最后一条若只有一条，它在左格，右格留空。
   */
  export interface IdeaRow {
    /** 这一排的第一条（位置序号 = 2k+1）。 */
    left: Idea;
    /** 这一排的第二条（位置序号 = 2k+2）；没有时是 null（右格留空）。 */
    right: Idea | null;
  }

  /** 把想法切成「一排两个格子」。 */
  export function toRows(ideas: Idea[]): IdeaRow[];
  ```

- [ ] **Step 1: 写失败的测试**

  **新建 `web/src/lib/rows.spec.ts`**：

  | 测试 | 断言 |
  |---|---|
  | `toRows_pairs_the_ideas_up` | 5 条 → 3 排：`[{1,2},{3,4},{5,null}]`（用 `idea.id` 比） |
  | `toRows_of_an_empty_list_is_empty` | `toRows([])` → `[]` |
  | `toRows_keeps_the_input_order` | `toRows` 的输出按原顺序铺平后与输入一致（**不重排**） |

  在 `web/src/components/MessageList.spec.ts` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `the_odd_ones_are_on_the_left` | 渲染 3 条 → 第 1、3 条在 `.row:nth-child(1) .cell--left` / `.row:nth-child(2) .cell--left`，第 2 条在 `.row:nth-child(1) .cell--right` |
  | `the_last_row_leaves_the_right_cell_empty` | 渲染 3 条 → 第 2 排的右格是空的（没有 `.idea` 元素） |
  | `the_position_is_the_index_plus_one` | 渲染 3 条 → 三条的 `.meta__num` 文本依次是 `'1'`、`'2'`、`'3'` |

  > **「窄屏是单列」这条没有单测** —— 它是纯 CSS（`grid-template-columns`），jsdom 不做布局，
  > 单测测不出来。它由 **Task 15 的 e2e** 验（`窄屏 <768px：单列` + `窄屏：没有想法被藏起来`）。
  > 本 task 的单测负责「DOM 结构是「一排两个格子」、编号是下标 +1」这一层。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/lib/rows.spec.ts src/components/MessageList.spec.ts`
  Expected: 失败（`../lib/rows` 不存在；DOM 还是一条一个卡片）。

- [ ] **Step 3: 实现 `toRows` 并改模板**

  在 **新建的 `web/src/lib/rows.ts`** 里实现 `toRows` 与 `IdeaRow`（`<script setup>` 里不能 `export`，
  所以这个纯函数单独放一个文件，`MessageList.vue` 从 `../lib/rows` 引入）：
  - `toRows(ideas)`：按下标切排 —— 第 k 排装下标 `2k` 与 `2k+1`；
    下标 `2k` → `left`、下标 `2k+1` → `right`；最后一条只有一条时 `right` 是 `null`；
  - 模板改成：
    ```html
    <div class="list" :data-at-bottom="atBottom">
      <p v-if="ideas.length === 0 && !loading" class="empty">这里还没有想法。</p>
      <div v-for="(row, rowIndex) in rows" :key="row.left.id" class="row">
        <div class="cell cell--left">
          <MessageItem :idea="row.left" :position="rowIndex * 2 + 1" … />
        </div>
        <div class="cell cell--right">
          <MessageItem v-if="row.right" :idea="row.right" :position="rowIndex * 2 + 2" … />
        </div>
      </div>
    </div>
    ```
  - ⚠️ **不要用 CSS `columns` 实现两列**（规格 §6.1）：CSS 多列在内容增删时会重新平衡各列，导致整墙重排；用「排」为显式 DOM 容器，每排 2 格，追加只影响最后一排。

- [ ] **Step 4: 改样式**

  在 `web/src/components/MessageList.vue:71-84`：
  - `.list`：`display: flex; flex-direction: column;` → `display: flex; flex-direction: column; gap: 9px;`（排与排之间的间距 9–10px）；
  - 新增 `.row`：`display: grid; grid-template-columns: 1fr 1fr; gap: 9px;`（两列之间的间距 9–10px）；
  - 新增 `.cell`：`min-width: 0;`（不让长内容把格子撑破）；
  - `.empty` 保留。
  - ⚠️ **窄屏（<768px）是单列**（规格 §D9 的断点表：`< 768px → 1 列`、`768–1023px → 2 列`、
    `≥1024px → 2 列 + 侧栏`）—— 375px 屏上两列的话每格只有约 175px 宽，
    正文 3 行截断后卡片又高又碎，规格 §6.10 的「单卡约 110px、一屏约 5 张卡」也前提不对：
    ```css
    @media (max-width: 767px) {
      .row {
        grid-template-columns: 1fr;   /* 窄屏单列 */
      }
    }
    ```
    ⚠️ **单列不需要藏任何东西**：`.row` 是 grid 容器，网格只剩一列之后，
    两个格子**自动落到两行**（同一列、上下堆叠），顺序仍是 1、2、3、4…，
    **每一条想法都还在**。
    - ⚠️ **绝对不要**把 `.cell--right` 整个 `display: none` —— 右格里装着
      `toRows` 切出来的**第 2、4、6… 条**想法（`toRows` 不许改），藏掉它就是
      **手机上每两条想法里有一条永远看不见**（列表变成「想法 1、3、5、7…」）。
      这个错误**没有任何闸门能发现**：藏掉一半之后「一屏 ≥5 条」照样成立。
    - 只需要处理**空格子**：`toRows` 的最后一排可能只有一条（右格是 `null`），
      那时让空格子不占位：
      ```css
      .cell--right:empty { display: none; }
      ```
      （`:empty` 只匹配**真的没有子元素**的格子 —— 右格里有 `MessageItem` 时不匹配。）

- [ ] **Step 5: 跑测试确认它们通过**

  Run: `cd web && npm test -- src/lib/rows.spec.ts src/components/MessageList.spec.ts src/components/MessageItem.spec.ts`
  Expected: 全部 PASS.

  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 6: 提交**

  ```bash
  git add web/src/components/MessageList.vue web/src/components/MessageList.spec.ts \
          web/src/lib/rows.ts web/src/lib/rows.spec.ts
  git commit -m "feat(web): 想法排成两列交错流

  一排两个格子，编号与左右由同一个位置序号决定 —— 奇数在左、偶数在右，
  顺序一眼可辨。编号是「位置」不是身份：往上翻一页已有条目的编号会 +P。

  不用 CSS columns：多列在内容增删时会重新平衡各列，整墙重排；用「排」
  做显式 DOM 容器，追加只影响最后一排。"
  ```

---

### Task 12: 往回翻的 prepend 净增偶数条（不换边）

**Files:**
- Modify: `web/src/stores/ideas.ts:27`（`PAGE_SIZE`）、`:178-205`（`fetchOlderIdeas`）、`:217-229`（`prependOlder`）
- Modify: `web/src/composables/useHistoryScroll.ts:74-125`（`loadOlder`）
- Test: `web/src/stores/ideas.spec.ts`、`web/src/composables/useHistoryScroll.spec.ts`、`web/src/lib/rows.spec.ts`（位置序号的奇偶用例放在这里 —— 位置序号是渲染层算的）、`web/src/pages/HomePage.spec.ts`（`:2153` 那个 `prependOlder` 调用点与 `:2155` 的深度断言）

**Interfaces:**
- Consumes: Task 11 的 `lib/rows.toRows` 与两列布局
- Produces:
  ```ts
  // web/src/stores/ideas.ts
  /** 一页多少条（不含探针）。 */
  export const PAGE_SIZE = 100;

  /**
   * 往回翻时**实际请求**的条数：多要的那一条是探针（用来判断还有没有更旧的）。
   *
   * ⚠️ 探针落在这一页的**最老**那端（服务端按 updated_at 升序返回），
   * 所以满页时要丢掉的是**最老**的那一条 —— 丢掉的那条落在已加载区域的
   * **外侧**，下一轮请求窗口会把它重新要回来（集合中间不会留洞）。
   */
  export const PROBE_LIMIT = PAGE_SIZE + 1;

  export interface OlderPage {
    /** 这一页的条目（满页时已丢掉最老的那一条）。 */
    items: Idea[];
    /** 这一页是不是已经过期。 */
    stale: boolean;
    /** 服务端这一页是不是满的（满 = 还有更旧的）。 */
    hasMore: boolean;
  }

  /** 往回翻：取**更旧**的一页。 */
  export async function fetchOlderIdeas(filter: IdeaFilter, offset: number): Promise<OlderPage>;

  /** 把往回翻取回的一页用到列表上。 */
  export function prependOlder(page: OlderPage): void;
  ```

- [ ] **Step 1: 写失败的测试**

  **Review Focus 第 7 条** —— 在 `web/src/stores/ideas.spec.ts` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `fetchOlderIdeas_drops_the_oldest_item_when_the_page_is_full` | 服务端返回 101 条（`PROBE_LIMIT`）→ `page.items` 的长度是 **100**，且**丢的是最老的那一条**（`items[0]` 是服务端返回的第 2 条，`items[items.length-1]` 是服务端返回的最后一条） |
  | `fetchOlderIdeas_keeps_the_whole_page_when_it_is_not_full` | 服务端返回 37 条（不到 101）→ `page.items` 的长度是 **37**，一条不丢 |
  | `fetchOlderIdeas_marks_a_full_page_as_has_more` | 101 条 → `page.hasMore` 为 true；37 条 → `page.hasMore` 为 false |
  | `prependOlder_adds_an_even_number_of_items` | 预置 100 条 → prepend 一个满页（100 条）→ 列表长度 200，**净增 100（偶数）** |
  | `prependOlder_keeps_the_list_contiguous` | 预置 100 条（id 1..100，最新的是 100）→ prepend 满页 → 列表的 id 是 1..200 的连续序列（**中间没有洞**） |

  **位置序号的奇偶** —— 放在 **`web/src/lib/rows.spec.ts`**（不是 `useHistoryScroll.spec.ts`）：
  位置序号是**渲染层**算的（`MessageList.vue` 的 `rowIndex * 2 + 1`，Task 11 引入 `lib/rows.ts`），
  `useHistoryScroll` 只负责取页 + prepend + 视口补偿，**压根不知道「位置序号」** ——
  把用例放在那儿只能自己重算一遍序号，测不到任何生产逻辑。

  | 测试 | 断言 |
  |---|---|
  | `toRows_keeps_the_parity_when_an_even_number_of_items_is_prepended` | 用 `toRows` 直接验：已有 4 条（位置序号 1..4）→ 在**头部** prepend 2 条 → 原来那 4 条的位置序号变成 3..6（+2），**奇偶不变**（1→3、2→4、3→5、4→6） |
  | `toRows_flips_the_parity_when_an_odd_number_of_items_is_prepended` | 已有 2 条 → prepend 3 条（奇数）→ 位置序号 +3 → **奇偶翻转** |

  **锚点校正（规格 §6.5 的第二套补偿）** —— 放在 `web/src/composables/useHistoryScroll.spec.ts`：

  | 测试 | 断言 |
  |---|---|
  | `prepend_uses_the_anchor_to_correct_the_viewport` | 给 `useHistoryScroll` 传假的 `captureAnchor` / `restoreAnchor`：prepend **之前**记下的锚点（`{ id, top }`）被原样交给 `restoreAnchor`（断言 `restoreAnchor` 被调用，且参数是 prepend 前记下的那个 id） |
  | `prepend_does_nothing_when_the_anchor_is_gone` | `captureAnchor` 返回 `null` → `restoreAnchor(null)` 不抛异常，且 `scrollTop` **不被二次补偿**（没有锚点就别乱动） |

  > **位置序号与奇偶的关系（实现前先读这一段）**：规格 §D4 定义「位置序号 = 1 是最老的那条（屏幕最上）」，
  > §6.4 说「prepend 之后编号会 +P」并且「prepend 的条数必须是偶数，否则整墙左右互换」。
  > 两句话在 **P 为偶数**时同时成立：prepend 的条目更早、插到列表**头部** → 已有条目的位置序号整体 +P →
  > P 是偶数时奇偶不变 → 「奇数在左、偶数在右」不变 → **不换边** ✓。
  > P 是奇数时奇偶翻转 → 整墙换边（规格接受的边界：只有翻到最开头那一页会发生一次）。
  > **所以实现上只需要保证「满页时净增 100 条」，奇偶逻辑一行都不用改。**

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/stores/ideas.spec.ts src/lib/rows.spec.ts src/composables/useHistoryScroll.spec.ts`
  Expected: 失败（`fetchOlderIdeas` 现在返回整页 101 条；`prependOlder` 的签名还是 `prependOlder(items: Idea[])`；
  锚点那两条因为回调还不存在而红）。

- [ ] **Step 3: 改 `stores/ideas.ts`**

  - 加 `export const PROBE_LIMIT = PAGE_SIZE + 1;`（`:27` 附近）；
  - `fetchOlderIdeas`（`:178-205`）：
    - 仍然 `params.limit = String(PROBE_LIMIT);`；
    - 拿到 `result` 之后：
      ```ts
      // 满页 → 丢掉**最老**的那一条（探针），净增 100 条（偶数）。
      //
      // ⚠️ **要丢的是 result 的第一个元素**（服务端按 updated_at 升序返回，最老的在顶）：
      // 多要的那一条落在这一页的**最老**那端 —— 丢它不会在集合中间留洞
      // （丢掉的那条落在已加载区域的**外侧**，下一轮的请求窗口会把它要回来）。
      //
      // ⚠️ **别写成 slice(0, result.length - 1)**：那丢的是这一页**最新**的那一条 ——
      // 它紧挨着已加载区域，丢掉就在集合中间留洞（洞永远补不上，还会多出一条重复）。
      // 仓库里已经踩过一次这个坑（`slice(0, PAGE_SIZE)`），`stores/ideas.ts` 的注释里写着。
      const full = result.length >= PROBE_LIMIT;
      const items = full ? result.slice(1) : result;
      return { items, stale: generation < appliedGeneration, hasMore: full };
      ```
      **验算**（与规格 §6.4 一致）：总数 C、已加载 k 条（距离区间 `[C-k, C)`）→
      本轮请求返回距离 `[C-k-101, C-k)` 的 101 条 → **丢掉最老的那个（距离 C-k-101，
      也就是数组下标 0）** → 收下 `[C-k-100, C-k)` 的 100 条 → 已加载变成 `[C-k-100, C)`
      的 k+100 条 ✓ 连续 ✓；下一轮 offset = k+100 → 同样丢最老的那条 ✓。
    - 注释里原来那段「这一页**整页收下**，一条都不丢」的说明要改掉（现在满页会丢一条）；
  - `prependOlder`（`:217-229`）：签名改成 `prependOlder(page: OlderPage): void`：
    ```ts
    export function prependOlder(page: OlderPage): void {
      hasMore.value = page.hasMore;
      if (page.items.length === 0) {
        return;
      }
      ideas.value = [...page.items, ...ideas.value];
      scrolledItems.value += page.items.length;
    }
    ```
  - ⚠️ `scrolledItems` 加的是**实际 prepend 的条数**（100 或到底那页的条数）—— SSE 重拉的基准要跟它对上。

  **同时改既有调用点与既有断言** —— 签名与语义都变了（传数组 → 传 page；满页整页收下 → 丢掉探针），
  **既有测试不改就必红**（`page.items` 是 `undefined` → `TypeError`；长度与深度的期望值 101 → 100）：

  | 文件:行 | 现在 | 改成 |
  |---|---|---|
  | `web/src/stores/ideas.spec.ts:202` | `prependOlder(makeIdeas(PAGE_SIZE + 1));` | `prependOlder({ items: makeIdeas(PAGE_SIZE), stale: false, hasMore: true });` —— **改成 100 条** |
  | `web/src/stores/ideas.spec.ts:288` / `:302` / `:327` / `:362` | `prependOlder(page.items);` | `prependOlder(page);` |
  | `web/src/stores/ideas.spec.ts:311` | `prependOlder([]);` | `prependOlder({ items: [], stale: false, hasMore: false });` |
  | `web/src/pages/HomePage.spec.ts:2153` | `prependOlder(page.items);` | `prependOlder(page);`（⚠️ **这个文件不在本 task 原来的 Test 清单里** —— 已补进 Files） |

  ⚠️ **`:202` 那处必须改成 100 条，否则同一张表自相矛盾**：新的 `prependOlder` 是
  「**加多少就是多少**」（`scrolledItems.value += page.items.length`），
  而「丢掉探针」发生在 **`fetchOlderIdeas`** 里（它把满页 101 条裁成 100 条）。
  这个用例**直接**调 `prependOlder`、绕过了 `fetchOlderIdeas` ——
  塞 101 条的话深度会记 101、列表会变成 `3 + 101 = 104`，
  与下面那三条断言的 100 基准对不上。
  改成 `makeIdeas(PAGE_SIZE)`（100 条，与 `fetchOlderIdeas` 交过来的一致）之后，下面的期望值才成立。
  （另一种自洽的写法是保留 101 条、那三条断言也不改；**二选一，别混着写**。）

  | 文件:行 | 断言 | 改成 |
  |---|---|---|
  | `web/src/stores/ideas.spec.ts:234` | `expect(page.items).toHaveLength(PAGE_SIZE + 1);`（用例名「fetchOlderIdeas 整页返回更旧的那一页」） | `toHaveLength(PAGE_SIZE);`，用例名改成「fetchOlderIdeas 满页时丢掉最老的那一条（探针）」，上面那句注释「这一页**整页**收下：服务端给的条数一条都不丢」也要改 |
  | `web/src/stores/ideas.spec.ts:203` / `:211` | `expect(scrolledItems.value).toBe(PAGE_SIZE + 1);` | `toBe(PAGE_SIZE);` |
  | `web/src/stores/ideas.spec.ts:210` | `expect(ideas.value).toHaveLength(3 + PAGE_SIZE + 1);` | `toHaveLength(3 + PAGE_SIZE);` |
  | `web/src/stores/ideas.spec.ts:290` | `toHaveLength(2 + PAGE_SIZE + 1)` | `toHaveLength(2 + PAGE_SIZE)` |
  | `web/src/stores/ideas.spec.ts:294` | `toBe(PAGE_SIZE + 1)` | `toBe(PAGE_SIZE)` |
  | `web/src/composables/useHistoryScroll.spec.ts:162` / `:300` / `:360` | `toHaveLength(2 + PAGE_SIZE + 1)` | `toHaveLength(2 + PAGE_SIZE)` |
  | `web/src/composables/useHistoryScroll.spec.ts:165` / `:301` | `toBe(PAGE_SIZE + 1)` | `toBe(PAGE_SIZE)` |
  | `web/src/composables/useHistoryScroll.spec.ts:407` | `const grown = (PAGE_SIZE + 1) * 10;` | `const grown = PAGE_SIZE * 10;`（那一页现在只拼进 100 条） |
  | `web/src/pages/HomePage.spec.ts:2155` | `expect(scrolledItems.value).toBe(PAGE_SIZE + 1);`（上面那句注释「往回翻回来的那一页是**整页**（PAGE_SIZE + 1 条）」） | `toBe(PAGE_SIZE);`，注释改成「满页丢掉探针，净增 100 条」 |
  | `web/src/pages/HomePage.spec.ts:2171` | `expect(limit).toBe(PAGE_SIZE + 1 + (PAGE_SIZE + 1));`（SSE 重拉的 limit = `loadedLimit(scrolledItems)`） | `toBe(PAGE_SIZE + 1 + PAGE_SIZE);` —— 深度从 101 变 100，`loadedLimit(100)` = 201 |
  | `web/src/pages/HomePage.spec.ts:2492-2493` | 注释「服务端给的这一页是**整页**（PAGE_SIZE + 1 条）—— 一条都不丢：丢的那一条正好是紧挨着已加载区域的那条（列表中间会留一个洞）」 | **因果整个反了**，要改：「服务端给的这一页是**满页**，我们丢掉**最老**的那一条（探针）—— 它落在已加载区域的**外侧**，下一轮会要回来，集合中间不会留洞」 |
  | `web/src/pages/HomePage.spec.ts:2494` | `expect(wrapper.findAll('.idea')).toHaveLength((PAGE_SIZE + 1) * 2);` | `toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);`（101 + 100 = 201） |
  | `web/src/pages/HomePage.spec.ts:2495` | `expect(wrapper.findAll('.idea')[0].attributes('data-idea-id')).toBe('idea-older-0');` | `toBe('idea-older-1');` —— **探针落在这一页的最老那端**（mock 的 `makeOlderIdeas` 按下标升序返回 `idea-older-0 …`，数组第 0 个就是最老的那条），丢的是它 |
  | `web/src/pages/HomePage.spec.ts:2512` | 注释「更旧的一页插到了视口**上方**（101 条 × 300px）」 | 改成「100 条 × 300px」 |
  | `web/src/pages/HomePage.spec.ts:2514` | `expect(scroller.scrollTop).toBe((PAGE_SIZE + 1) * 300);` | `toBe(PAGE_SIZE * 300);`（高度差 = 拼进去的 100 条 × 300px） |
  | `web/src/pages/HomePage.spec.ts:2544` | `expect(scroller.scrollTop).toBe((PAGE_SIZE + 1) * 300);` | `toBe(PAGE_SIZE * 300);`（同 `:2514`） |
  | `web/src/pages/HomePage.spec.ts:2591` | `expect(ideas.value).toHaveLength((PAGE_SIZE + 1) * 2);` | `toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);` |
  | `web/src/pages/HomePage.spec.ts:2592` | `expect(ideas.value[0].id).toBe('idea-older-0');` | `toBe('idea-older-1');`（同上：探针被丢） |
  | `web/src/pages/HomePage.spec.ts:2606` | 注释「深度是「用户往上翻过了多少条」：这一页 101 条（整页）。」 | 改成「这一页 100 条（满页丢掉探针）」 |
  | `web/src/pages/HomePage.spec.ts:2608` | `expect(scrolledItems.value).toBe(PAGE_SIZE + 1);` | `toBe(PAGE_SIZE);` |
  | `web/src/pages/HomePage.spec.ts:2656` | `expect(wrapper.findAll('.idea')).toHaveLength((PAGE_SIZE + 1) * 2);` | `toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);` |

  ⚠️ **上面这几处 mock 的「往回翻那一页」都是满页**（`mockPagedIdeas(PAGE_SIZE + 1)` 与
  `mockPendingOlderIdeas()` 的默认值都是 `makeOlderIdeas(PAGE_SIZE + 1)` = 101 条），
  所以它们都会被裁成 100 条 —— 上面的新值成立。
  ⚠️ `:2495`/`:2592` 这类「第一条是谁」的断言**漏改的后果最坑**：它会让人以为
  prepend 的顺序逻辑错了，去改 `toRows` 或排序 —— 改错方向。

  ⚠️ **有一处**不用改**：`web/src/stores/ideas.spec.ts:355-368` 那条「一直往回翻……一条不漏：翻出来的正好是库里那 250 条」。
  满页丢掉的那一条落在**已加载区域的外侧**，下一轮的请求窗口会把它要回来（规格 §6.4 的验算），
  所以总数仍然是 250 —— **别去动它**（动了反而会把「不丢东西」这条保证改坏）。
  ⚠️ 同理，`ideas.spec.ts:328` 那条「列表与深度始终对得上」的公式
  `scrolledItems === ideas.length - (PAGE_SIZE + 1)` **也不用改**：首屏仍是 101 条，
  深度 = 翻出来的条数，公式照样成立。

  按 Global Constraints 的要求，这些断言的调整理由（「结构变了：满页丢掉探针，净增 100 条」）
  要写进本 task 的提交说明。

- [ ] **Step 4: 改 `useHistoryScroll.ts`**

  在 `web/src/composables/useHistoryScroll.ts:74-125`：
  - `prependOlder(page.items)` → `prependOlder(page)`；
  - ⚠️ **视口补偿两处都要**（规格 §6.5）：
    - 现有的高度差补偿（`container.scrollTop += container.scrollHeight - before`）**保留** ——
      两列布局里，只要已有排的高度没变，这个补偿是精确的（偶数 prepend 不换边，排的高度也不变）；
    - **新增锚点校正**：边界排换边时（到底那一页是奇数）那一排的高度可能变，高度差补偿就不精确了 →
      用 `[data-idea-id]` 找锚点校正（规格 §6.5 指的是 `HomePage.captureViewportAnchor` / `restoreViewportAnchor`，
      **那套逻辑在 HomePage 里**）。
      **做法**：在 `useHistoryScroll` 的 `loadOlder` 里，prepend 之前记下「视口顶部那条的 id 与它相对视口上沿的位置」，
      prepend 之后如果高度差补偿后的位置仍偏了，就按那个 id 校正。
      ⚠️ **`useHistoryScroll` 现在拿不到 HomePage 的锚点函数** —— 给 `HistoryScrollOptions` 加两个可选的回调：
      ```ts
      /** 记下视口顶部那条（第一个露出来的条目）；没有就返回 null。 */
      captureAnchor?: () => { id: string; top: number } | null;
      /** 把视口钉回锚点那条（锚点不在了就什么都不做）。 */
      restoreAnchor?: (anchor: { id: string; top: number } | null) => void;
      ```
      HomePage 里把现有的 `captureViewportAnchor` / `restoreViewportAnchor` 传进来。

- [ ] **Step 5: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。

  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 6: 提交**

  ```bash
  git add web/src/stores/ideas.ts web/src/stores/ideas.spec.ts \
          web/src/composables/useHistoryScroll.ts web/src/composables/useHistoryScroll.spec.ts \
          web/src/lib/rows.ts web/src/lib/rows.spec.ts \
          web/src/pages/HomePage.vue web/src/pages/HomePage.spec.ts
  git commit -m "fix(web): 往回翻丢掉探针那一条，prepend 净增偶数条

  实际每页要的是 101 条（多要的那条是探针），101 是奇数 —— 每翻一页
  整墙左右互换，用户看着像列表在乱跳。

  现在满页时丢掉**最老**的那一条（探针落在这一页的最老那端），净增 100 条
  是偶数，已有卡片的奇偶不变 → 不换边。丢最老的那条不会在集合中间留洞：
  丢掉的那条落在已加载区域的外侧，下一轮请求窗口会把它要回来。反过来丢
  最新的那条才会留洞（洞永远补不上，还会多出一条重复）。

  视口补偿两处一起用：高度差补偿在偶数 prepend 时是精确的，边界排换边
  （到底那一页是奇数）时用锚点校正。"
  ```

---

### Task 13: 窄屏把过滤行收到列表顶部 + 顶栏 🔍 聚焦搜索框

**Files:**
- Create: `web/src/composables/useDropdown.ts`（**收敛 Task 9 留下的三份逐字相同的
  「点外面收起 / Esc 收起」逻辑**：`TagFilter.vue` / `ProjectDropdown.vue` / `MoreMenu.vue`）
- Modify: `web/src/components/TagFilter.vue`、`web/src/components/ProjectDropdown.vue`、
  `web/src/components/MoreMenu.vue`（改用 `useDropdown`）
- Modify: `web/src/components/FilterBar.vue:1-139`
- Modify: `web/src/pages/HomePage.vue`（`focusSearch` 的实现、`FilterBar` 的 ref 绑定）
- Test: `web/src/composables/useDropdown.spec.ts`、`web/src/components/FilterBar.spec.ts`、
  `web/src/pages/HomePage.spec.ts`

**Interfaces:**
- Consumes: Task 9 的顶栏
- Produces:
  ```ts
  // web/src/components/FilterBar.vue
  /**
   * 窄屏紧凑形态：状态三态收成**一个下拉**（复用 web/src/composables/useDropdown.ts
   * 与 TagFilter 的面板样式），搜索框撑开剩余宽度，标签下拉与清除按钮保留。
   */
  defineExpose<{
    /** 把焦点放进搜索框（窄屏顶栏的 🔍 用它）。 */
    focusSearch: () => void;
  }>();
  ```

- [ ] **Step 1: 写失败的测试**

  在 `web/src/composables/useDropdown.spec.ts` 里追加（这个文件在 Files 清单里，
  **但收敛动作本身没有测试就是白收敛** —— 四个组件共用同一份「点外面收起 / Esc 收起」，
  逻辑回归了要有一个地方先红）：

  | 测试 | 断言 |
  |---|---|
  | `it_exposes_the_root_element_and_the_open_flag` | `useDropdown(root)` 返回 `{ open, root }`；`open` 初始为 false |
  | `clicking_outside_closes_the_panel` | 点根节点外面 → `open` 变 false |
  | `pressing_escape_closes_the_panel` | 按 Esc → `open` 变 false |
  | `clicking_inside_keeps_the_panel_open` | 点根节点里面 → `open` 仍是 true |

  在 `web/src/components/FilterBar.spec.ts` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `the_status_buttons_are_replaced_by_a_dropdown_on_narrow_screens` | 组件里有一个状态下拉触发器（`.filter-bar__status-trigger`），点它展开一个面板，面板里有「全部 / 未完成 / 已完成」三项 |
  | `picking_a_status_emits_the_filter_update` | 点面板里的「已完成」→ 发出 `update:filter`，`status` 是 `'done'` |
  | `focusSearch_puts_the_focus_in_the_search_box` | 调 `wrapper.vm.focusSearch()` → `document.activeElement` 是那个 `input[type=search]` |

  在 `web/src/pages/HomePage.spec.ts` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `the_narrow_search_button_scrolls_to_the_top_and_focuses_the_search_box` | 点顶栏的 🔍 → 搜索框拿到焦点（断言 `document.activeElement` 是那个 searchbox） |
  | `the_narrow_search_button_does_not_create_a_second_search_state` | 点 🔍 之后，页面上**只有一个** `input[type=search]`（不引入第二套搜索状态） |

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/composables/useDropdown.spec.ts src/components/FilterBar.spec.ts src/pages/HomePage.spec.ts`
  Expected: 失败（状态下拉与 `focusSearch` 还不存在）。

- [ ] **Step 3: 改 `FilterBar.vue`（窄屏紧凑形态）**

  在 `web/src/components/FilterBar.vue`：
  - ⚠️ **先做收敛**：把 `TagFilter.vue` / `ProjectDropdown.vue` / `MoreMenu.vue` 里
    **逐字相同**的「根节点 + `open` ref + `document` click 收起 + Esc 收起」抽成
    `web/src/composables/useDropdown.ts`（返回 `{ open, root }`），
    三个组件与这个新的状态下拉**都用它** —— 到这一步为止仓库里已经有三份一样的代码，
    再抄第四份就是四处各改各的（漏一处就是一个组件关不掉面板）。
    面板样式（CSS）仍照 `TagFilter.vue` 的 `.tag-filter__panel`；
    ⚠️ **但不要把它再抄一份**：面板的公共外观（绝对定位、边框、底色、阴影…）
    提到 `web/src/styles/dropdown.css` 的共享 class，四个下拉组件（TagFilter /
    ProjectDropdown / MoreMenu / 新的状态下拉）都挂它，各组件的 scoped 样式只留
    自己的差异（宽度、右对齐）—— 抄一份改一处、漏一处就是「面板的样子对不上」，
    与那段 JS 逻辑被抄三份是同一个毛病。
  - 加一个 `open` ref 与状态下拉面板（**复用 `web/src/composables/useDropdown.ts`**，
    别再照 `TagFilter.vue` 抄一遍 `document` click 收起 + Esc 收起）；
  - 触发器 `.filter-bar__status-trigger` 的文本是当前状态（「全部」/「未完成」/「已完成」）+ `▾`；
  - 面板里三项，选中后 `emit('update:filter', { ...filter, status })`；
  - **宽屏形态不变**（三个分段按钮照旧显示）—— 用 CSS 控制两套形态的显隐：
    ```css
    /* 宽屏：三个分段按钮 */
    .filter-bar__status-segments { display: flex; gap: 4px; }
    .filter-bar__status-trigger { display: none; }
    /* 窄屏：收成一个下拉（断点 1023px，见下面 Step 4 的断点裁决） */
    @media (max-width: 1023px) {
      .filter-bar__status-segments { display: none; }
      .filter-bar__status-trigger { display: inline-flex; }
      .filter-bar__search { flex: 1 1 auto; width: auto; }
    }
    ```
    ⚠️ **形态的断点是 1023px，不是 767px**（Step 4 的裁决：「过滤行的窄屏形态用 1023px」——
    跟侧栏断点一致：「窄屏」在这份规格里指「没有侧栏的那一档」）。
    触摸目标那条另算（仍是 767px，见下）。
    ⚠️ **`.filter-bar__status` 这个 `role="group" aria-label="状态过滤"` 的容器本身不能被 `display: none`** ——
    隐藏的是**它里面的**分段按钮（`.filter-bar__status-segments`），容器要一直显示，
    窄屏时它包着那个下拉触发器。
    原因：布局 e2e 的关键元素检查里有
    `{ name: '状态过滤组', locator: page.getByRole('group', { name: '状态过滤' }) }`，
    而检查要求元素**可见**且在视口里（`toBeVisible()`）—— 容器整个 `display: none` 的话，
    窄屏那一档（375/768/1280/1920 每个视口都跑）**必红**；
    容器可见、只把分段按钮藏起来，两种形态都能命中同一个 group。
    → 所以模板要改成：
    ```html
    <div class="filter-bar__status" role="group" aria-label="状态过滤">
      <div class="filter-bar__status-segments">
        <button v-for="option in STATUS_OPTIONS" …>…</button>
      </div>
      <button class="filter-bar__status-trigger" type="button" :aria-expanded="open">…</button>
    </div>
    ```
  - ⚠️ **下拉触发器也要满足触摸目标**（规格 §6.10 末句「新加的下拉触发器…同样适用」）：
    ```css
    @media (max-width: 767px) {
      .filter-bar__status-trigger {
        min-width: 32px;
        min-height: 32px;
      }
    }
    ```
    ⚠️ 触摸目标这条**留在 767px**（形态那条是 1023px）：触摸目标管的是**手机**那一档，
    平板（768–1023px）有悬停能力，规则不适用（规格 §D8 的并集判据）。
  - 搜索框：宽屏 `width: 160px` 不变；**窄屏撑开剩余宽度**（`flex: 1 1 auto; width: auto;`）；
  - 加 `defineExpose`：
    ```ts
    /** 搜索框本身（窄屏顶栏的 🔍 要聚焦它）。 */
    const searchInput = ref<HTMLInputElement | null>(null);

    defineExpose({
      /** 把焦点放进搜索框。 */
      focusSearch: () => searchInput.value?.focus(),
    });
    ```
    模板里的 `input[type=search]` 加 `ref="searchInput"`。

- [ ] **Step 4: 改 `HomePage.vue`（过滤行的位置 + 🔍 的聚焦）**

  在 `web/src/pages/HomePage.vue`：
  - 给 `<FilterBar>` 加 `ref="filterBarRef"`；
  - 脚本里：
    ```ts
    /** 过滤栏的组件引用（窄屏顶栏的 🔍 要聚焦它的搜索框）。 */
    const filterBarRef = ref<InstanceType<typeof FilterBar> | null>(null);

    /**
     * 窄屏顶栏的 🔍：滚回列表顶部并聚焦过滤行里的搜索框。
     *
     * ⚠️ **不引入第二套搜索状态** —— 聚焦的是过滤行里那个输入框，
     * 它绑的就是同一份 `filter.keyword`。
     */
    function focusSearch(): void {
      void nextTick();
      scrollContainer.value?.scrollTo({ top: 0 });
      filterBarRef.value?.focusSearch();
    }
    ```
    ⚠️ `filterBarRef` 的类型：`InstanceType<typeof FilterBar>` 在 `<script setup>` 里拿不到组件类型 ——
    用 `ref<{ focusSearch: () => void } | null>(null)` 更省事（`defineExpose` 暴露出来的就是那个形状）。
  - ⚠️ **窄屏上过滤行不钉死**（规格 §D7）：`.home__filters` 在窄屏的样式里**不能**是 `position: sticky` 也不要放在 `.home__content` 之外 ——
    它已经在 `.home__content` 里、`.home__body` 之前，而 `.home__body` 才是滚动容器（`.home__scroll`）→
    **过滤行本来就在滚动容器之外**，所以它**不会**随内容滚走。
    → **要把它移进滚动容器里**：把 `<div class="home__filters">` 从 `.home__body` **外面**移到 `.home__scroll` **里面**（第一个子元素，在 `historySentinel` 之前）。
    ⚠️ **宽屏要保持钉死**：宽屏时过滤行仍在 `.home__scroll` 之外（钉在顶栏下面）。
    → **做法**：过滤行渲染**两份**？不行（两份的输入框会打架）。
    → **做法（CSS 移动）**：把 `.home__filters` 放进 `.home__scroll` 里，**宽屏时用 `position: sticky; top: 0` 把它钉住**，
    **窄屏时 `position: static`**（随内容滚走）。
    ```css
    .home__filters {
      position: sticky;
      top: 0;
      z-index: 5;
      padding: 6px 0;
      background: var(--bg);
    }
    @media (max-width: 1023px) {
      .home__filters { position: static; background: none; padding: 0; }
    }
    ```
    ⚠️ 宽屏钉死时背景要是不透明，滚上来的卡片会从它下面透出来 → `background: var(--bg)`。
    ⚠️ **窄屏的断点是 1023px 还是 767px？** 规格 §D7 说「窄屏」= 侧栏消失的那一档（<1024px），
    而 §D9 的两列断点是 768px。**过滤行的窄屏形态用 1023px**（跟侧栏断点一致：「窄屏」在这份规格里
    指「没有侧栏的那一档」），**两列与触摸目标用 767/768px**。
    ⚠️ 这两套断点**并存**是规格本身的样子，实施时保持一致即可（写进注释里）。

- [ ] **Step 5: 跑测试确认它们通过**

  Run: `cd web && npm test -- src/components/FilterBar.spec.ts src/pages/HomePage.spec.ts`
  Expected: 全部 PASS。

  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 6: 提交**

  ```bash
  git add web/src/components/FilterBar.vue web/src/components/FilterBar.spec.ts web/src/pages/HomePage.vue \
          web/src/pages/HomePage.spec.ts
  git commit -m "feat(web): 窄屏把过滤行收到列表顶部

  钉死的每一行都是从内容里扣的钱：窄屏上标题 / 项目 Tab / 过滤 ×2 行 /
  输入条全都钉死，375×667 上占掉约 180px（27%），列表只剩 4 张卡。

  过滤控制属于它过滤的那片内容，放在列表上方才说得通；底部留给「随手记」
  这件事本身。状态三态在窄屏收成一个下拉（复用 TagFilter 的面板做法），
  搜索框撑开剩余宽度。

  顶栏的 🔍 不引入第二套搜索状态：它滚回列表顶部并聚焦过滤行里那个
  同一个输入框。"
  ```

---

### Task 14: 管理页铺满 + 触摸目标断点统一到 767px

**Files:**
- Modify: `web/src/styles/base.css:76-86`（`.page`）
- Modify: `web/src/pages/TrashPage.vue`、`web/src/pages/ProjectsPage.vue`、`web/src/pages/WebhooksPage.vue`、`web/src/pages/ProjectWebhooksPage.vue`（容器 class）
- Modify: `web/src/components/PageNav.vue:105`、`web/src/components/BatchBar.vue:197`（640px → 767px）
- Test: `web/src/pages/TrashPage.spec.ts`、`web/src/pages/ProjectsPage.spec.ts`、`web/src/components/PageNav.spec.ts`

**Interfaces:**
- Consumes: 无（纯样式层）
- Produces:
  ```css
  /* web/src/styles/base.css */
  /* 页面容器：列表页铺满（没有宽度上限），表单页用 .page--narrow 保持窄。 */
  .page {
    margin: 0;
    padding: 24px 16px;
  }

  /* 窄容器：表单页用（表单本来就不该宽，一行拉到 1800px 没法看）。 */
  .page--narrow {
    max-width: 720px;
    margin: 0 auto;
  }

  @media (min-width: 1024px) {
    .page { padding: 32px 24px; }
    .page--narrow { padding: 32px 24px; }
  }
  ```

- [ ] **Step 1: 写失败的测试**

  在 `web/src/pages/TrashPage.spec.ts` 与 `web/src/pages/ProjectsPage.spec.ts` 里追加：

  | 测试 | 断言 |
  |---|---|
  | `the_list_page_has_no_width_cap` | 页面根元素**没有** `page--narrow` class（列表页不套窄容器） |
  | `the_webhook_form_page_uses_the_narrow_container` | Webhook 页的根元素**有** `page--narrow` class |

  > 这两条是**结构断言**（class 在不在），不是样式断言 —— 「铺满」是 CSS 的事，
  > 真正的视觉验证在 **Task 15 的 e2e `管理页铺满，Webhook 表单保持窄`** 那条里
  > （量 `.page` / `.page--narrow` 的实际宽度）。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `cd web && npm test -- src/pages/TrashPage.spec.ts src/pages/ProjectsPage.spec.ts`
  Expected: 失败（`.page--narrow` 还不存在）。

- [ ] **Step 3: 改 `base.css` 的 `.page`**

  在 `web/src/styles/base.css:75-86`：
  - `.page` 删掉 `max-width: 960px;` 与 `margin: 0 auto;`（**列表页铺满到右边缘**）；
  - 新增 `.page--narrow`（`max-width: 720px; margin: 0 auto;`）—— **Webhook 表单保持窄**；
  - 注释里写清为什么：列表铺满、表单不铺满（规格 §D10）。

- [ ] **Step 4: 给管理页套上正确的容器**

  - `web/src/pages/TrashPage.vue` 与 `web/src/pages/ProjectsPage.vue`：根容器保持 `class="page"`（**不**加 `page--narrow`）；
  - `web/src/pages/WebhooksPage.vue` 与 `web/src/pages/ProjectWebhooksPage.vue`：根容器改成 `class="page page--narrow"`；
  - ⚠️ 检查这两个页面的**表单区**是不是也套了 `page`（`WebhooksPage.vue` 只有 24 行，看一眼实际结构再改）。

- [ ] **Step 5: 统一触摸目标的断点**

  把 `@media (max-width: 640px)` 改成 `@media (max-width: 767px)`：
  - `web/src/components/PageNav.vue:105`；
  - `web/src/components/BatchBar.vue:197`；
  - `web/src/components/MessageItem.vue`（Task 10 已经改成 767px，确认一遍）。
  - ⚠️ **不要动 `web/src/components/ProjectTabs.vue`** —— 它在 Task 9 已经**被删掉**了
    （窄屏的项目切换改成下拉之后没人用它）。在 `git add` 里带上一个不存在的路径会直接报错：
    `fatal: pathspec 'web/src/components/ProjectTabs.vue' did not match any files`。

  ⚠️ **为什么统一**（规格 §D9）：触摸目标的 32×32 规则原来挂在 640px，而新的两列断点是 768px ——
  641–767px 这一段会「既按手机渲染又不满足触摸目标」。统一到 767px 就没这个缝了。
  ⚠️ `PageNav.vue:102-104` 的注释里「断点取 640px（手机竖屏的上限）」改成 767px 并说明原因。

- [ ] **Step 6: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。

  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 7: 提交**

  ```bash
  git add web/src/styles/base.css web/src/pages/TrashPage.vue web/src/pages/ProjectsPage.vue \
          web/src/pages/WebhooksPage.vue web/src/pages/ProjectWebhooksPage.vue \
          web/src/components/PageNav.vue web/src/components/BatchBar.vue \
          web/src/pages/TrashPage.spec.ts web/src/pages/ProjectsPage.spec.ts \
          web/src/components/PageNav.spec.ts
  git commit -m "style(web): 管理页铺满，触摸目标断点统一到 767px

  回收站、项目管理/Webhook 三个页面的 .page 都卡着 max-width: 960px 居中
  窄带 —— 大屏上同样是一条窄带。列表铺满到右边缘，Webhook 表单保持窄
  （表单本来就不该宽，一行拉到 1800px 没法看）。

  触摸目标的 32×32 规则原来挂在 640px，而两列断点是 768px：641–767px 这
  一段会既按手机渲染又不满足触摸目标。统一到 767px。"
  ```

---

### Task 15: e2e 布局用例按新外壳重写

**Files:**
- Modify: `web/e2e/layout.spec.ts:97-133`（home 路由的造数据/就绪探针/关键元素/文本块）、`:308-343`（`expectTextNotClipped`）、`:616-674`（点按目标用例）
- Modify: `web/e2e/ideas.spec.ts`（受结构变化影响的用例：卡片定位、勾选、批量操作、编辑）
- Modify: `web/e2e/COVERAGE.md`
- Test: `web/e2e/layout.spec.ts`（追加）

> ⚠️ **`web/e2e/COVERAGE.md` 里的行号引用有**两类**，Task 15 收口时都要复核**：
>
> 1. **`MessageItem.vue:行号`（10 处）—— Task 10 已经更新过一次**（提交 `75030ea`：
>    卡片模板从「标题 + 正文 + 标签 + 动作行」改成「一块正文 + 底部 meta 行」，脚本段整体下移，
>    那 10 处引用全部指错了）。
>    Task 15 收口时**只需复核这些行号是否仍指对**（Task 11–14 若不改 `MessageItem.vue` 就应保持原样）——
>    不要照着旧行号去改，把已经对的又改错。
>    （已核：Task 10 之后 `MessageItem.vue` 只有 `.confirm__delete:disabled` 那条在被引行号**之后**动过，
>    这 10 处仍指对。）
> 2. **`HomePage.vue:行号`（13 处引用点）—— Task 12/13 改了 `HomePage.vue` 之后**全部指错了**。
>    原先那段 ⚠️ 只提了 `MessageItem.vue`，漏了这一类。
>    下面是**新行号**（按 Task 13 修复轮之后的 `web/src/pages/HomePage.vue` 逐处核过；
>    Task 14/15 都不再动这个文件）：
>
>    | COVERAGE.md 里的旧引用 | 指的是什么 | 新行号 |
>    |---|---|---|
>    | `HomePage.vue:198-214` | `switchProject` 的 catch 分支（切项目失败回滚） | `:397-401` |
>    | `HomePage.vue:318-336` | `recoverFromTooMany` | `:531-549` |
>    | `HomePage.vue:339-347` | 提示文案 `tooManyMessage` | `:558-560` |
>    | `HomePage.vue:365-371` | 切模式时落盘（`setTagMode`） | `:578-583` |
>    | `HomePage.vue:381-401` | 提交失败时输入框内容保留（`onSubmit` 返回 false） | `:594-613` |
>    | `HomePage.vue:632-654` / `:641-645` | `replace: false`（批量加标签是并集式追加） | `:844-869` / `:856` |
>    | `HomePage.vue:689` | 合并守卫（至少要两条） | `:901` |
>    | `HomePage.vue:699` | 设 `focusIdeaId`（合并结果定位） | `:911` |
>    | `HomePage.vue:736-750` | `project.*` 分流 | `:987-991` |
>    | `HomePage.vue:831-877` / `:840-877` | `applyProjectEvent`（就地改名 / 删项目时切回「全部项目」） | `:1113-1150` |
>    | `HomePage.vue:979-986` | 上翻时不抢滚动（`void onNewContent()`） | `:998`（`useScrollFollow` 的调用在 `:113`） |
>    | `HomePage.vue:1016` | MergeDialog 的取消（`@cancel="mergeOpen = false"`） | `:1332` |
>
>    ⚠️ 这张表核过一次就没法保证永远准：**行号会随文件改动漂移**。
>    Task 15 动手前先 `grep` 确认再改。

**Interfaces:**
- Consumes: Task 9–14 的最终形态
- Produces: 无代码

- [ ] **Step 1: 改 `expectTextNotClipped`（只对编辑态生效）**

  在 `web/e2e/layout.spec.ts:308-343`：
  - 规格 §6.11：新卡片的正文**是**会被截断的（3 行），那条「关键文本块没被裁掉」的断言
    **只对编辑态生效**（编辑框里必须能看到全文）；
  - 做法：给 `RouteSpec` 加一个可选字段
    ```ts
    /** 只在编辑态里检查「文本没被裁掉」的块（编辑框里必须能看到全文）。 */
    textBlocksWhenEditing?: Named[];
    ```
    并新增一个检查函数 `expectTextNotClippedWhileEditing(page, blocks, label)`：
    - 先点开正文进入编辑态（`page.locator('.idea .body').first().click()`）；
    - 再对 `textBlocksWhenEditing` 做「scrollHeight <= clientHeight + 1」的断言；
  - `expectTextNotClipped` 本身**保留但只用于编辑态**（把普通 `textBlocks` 里那条「想法条目的正文」删掉 ——
    正文现在会被截断，断言会红）。

- [ ] **Step 2: 改 home 路由的用例数据与断言**

  在 `web/e2e/layout.spec.ts:97-133`：
  - `prepare`：多造几条想法（**宽屏一屏要能看到 ≥10 条** —— 造 14 条）；
  - ⚠️ **`keyElements` 的签名要加一个视口参数**：
    `RouteSpec` 里那行类型声明从 `keyElements: (page: Page) => Named[]` 改成
    `keyElements: (page: Page, viewport: { width: number; height: number }) => Named[]`，
    调用处（`route.keyElements(page)`）改成 `route.keyElements(page, viewport)`。
    其余四个路由的实现忽略这个参数即可。
  - `keyElements` 的内容：
    - 「标题「想法」」保留（`getByRole('heading', { name: '想法', exact: true })`）；
    - ⚠️ **「状态过滤组」按视口给** —— 窄屏（<1024px）过滤行**随内容滚走**
      （规格 §D7 / §9「375×667：钉死的外壳只有 2 行，过滤行随内容滚走」）：
      主页是聊天流，打开就滚到底（`HomePage.vue` 初始化时的 `scrollToBottom()`），
      过滤行在滚动内容的**最上面**、被推到视口**上方** ——
      而 `expectKeyElementsInViewport` 要求每个关键元素**可见且与视口相交**，
      所以窄屏那一档（375×667、768×1024 两档都 ≤1023px）**不能**把它当「关键元素」：
      ```ts
      keyElements: (page, viewport) => [
        { name: '标题「想法」', locator: page.getByRole('heading', { name: '想法', exact: true }) },
        // 窄屏过滤行随内容滚走 → 不在视口里，「关键元素必须在视口里」这条对它不成立。
        ...(viewport.width >= 1024
          ? [{ name: '状态过滤组', locator: page.getByRole('group', { name: '状态过滤' }) }]
          : []),
        { name: '底部输入框', locator: page.getByRole('textbox', { name: /记一条想法/ }) },
        { name: '「记下」按钮', locator: page.getByRole('button', { name: '记下' }) },
        { name: '顶栏的「⋯」菜单', locator: page.getByRole('button', { name: '更多' }) },
      ],
      ```
      ⚠️ 窄屏时那个 group **仍然存在且可见**（Task 13 只把里面的分段按钮藏起来），
      只是它**不在视口里** —— 别把「可见」与「在视口里」混为一谈；
    - 加「顶栏的「⋯」菜单」：`page.getByRole('button', { name: '更多' })`；
  - `containers`：加 `.home__topbar`（新）；`.home__content` 保留；
    ⚠️ `.project-tabs__list` **在 Task 9 已经删掉**（组件没了，定位器打不中任何元素，
    `expectNoUnexpectedHorizontalScroll` 对不存在的元素做 `evaluate` 会抛错 → e2e 红）；
  - `textBlocks`：删掉「想法条目的标题」，改成「想法条目的正文」（`page.locator('.idea .body').first()`）；
    ⚠️ **正文现在会被截断** → 它**不能**再进 `textBlocks`（那会红）。改放进 `textBlocksWhenEditing`。
    ⚠️ **顺带把种子文案换掉**：现在（Task 10 之后）那条「正文没被裁掉」的断言**只靠种子文案恰好不超过 3 行**才绿
    —— home 路由第二条种子正文是 102 个不换行字符，375px 视口下正文列宽约 313px，
    按 system-ui 字宽估算**正好贴 3 行的边**（评审 Task 10 时算过，没实测）。种子文案或字体度量一变就变 4 行，
    e2e 会对一个**正确**的实现报红。把正文从 `textBlocks` 里拿掉（上一条）就同时解决了这个问题。

- [ ] **Step 3: 追加新外壳的行为用例**

  在 `web/e2e/layout.spec.ts` 的 `test.describe('排版与响应式', …)` 里追加。
  ⚠️ **每条新用例自己造数据**，写法照现有那条循环里的四行（`web/e2e/layout.spec.ts:516-518`）：
  ```ts
  const path = await route.prepare(seed, api);
  await page.setViewportSize({ width, height });
  await page.goto(path);
  await expect(route.ready(page)).toBeVisible();   // ← 这一步不能省
  ```
  - 它们不在「路由 × 视口」那个循环里，不会自动拿到 `prepare` 造的数据；
  - ⚠️ **`await expect(route.ready(page)).toBeVisible();` 不能省**：想法列表是挂载后**异步**从服务端
    拉来的，`page.goto()` 只等 load 事件 —— 不等 `ready` 就去量，列表可能还是空的，
    下面那些**计数类**断言（可见条数 ≥10 / ≥5、`count` == 14）会**假红**（代码其实是对的）；
  - 造多少条也要写清：home 路由的 `prepare` 造 **14** 条，下面几条断言里的位置序号都是按这 14 条推的。

  | 用例 | 断言 |
  |---|---|
  | `宽屏：侧栏贴左，主区铺到右边缘` | 1920×1080 → `.home__sidebar` 的左边距 ≈ 0（`boundingBox().x <= 2`）；`.home__content` 的右边缘离视口右边缘 ≤ 24px（主区四周留 10–12px + 内容区留白） |
  | `宽屏：一屏能看到 ≥10 条想法` | 1920×1080 → **在视口里**的 `.idea` 数量 `>= 10`（用 `page.locator('.idea:visible')` 数 DOM 节点不等于「在视口里」—— 屏幕外但没被藏的卡片也是 `:visible`；要数「在视口里」的，就用 `boundingBox()` 与视口矩形相交来数） |
  | `宽屏：想法排成两列` | 1920×1080 → 第 1 排的左格与右格**都在视口里**，且左格的 x 小于右格的 x |
  | `平板 768×1024：两列、无侧栏` | 768×1024 → `.home__sidebar` **不可见**（断点 1024px 以下没有侧栏）；第 1 排的左格与右格**都在视口里**且 x 不同（规格 §D9：768–1023px → 2 列、无侧栏） |
  | `窄屏 <768px：单列` | 375×667 → 第 1 排的**两条**想法都在（`.cell--left .idea` 与 `.cell--right .idea` 各 1 个）；**先把两个盒子的 `boundingBox()` 断言成非 null**（被 `display:none` 藏起来的元素量不到位置，`boundingBox()` 是 `null`），再比：**x 相同**（同一列）、**y 不同**（上下堆叠）—— 规格 §D9 的「单列」是「排成一条竖列」，**不是**「只显示一半」 |
  | `窄屏：没有想法被藏起来` | 375×667 → **可见**的想法数 == 这一轮造出来的条数（14）：`await expect(page.locator('.idea:visible')).toHaveCount(14);`<br>⚠️ **不能用 `page.locator('.idea').count()`** —— Playwright 的 `count()` 数的是**匹配到的 DOM 节点**、**不看可见性**，用 `display:none` 藏起来的 `.idea` 照样数得到 ⇒ 那个错法下它仍然是 14、**照样绿**。<br>⚠️ 同理，「一屏能看到 ≥10 / ≥5」那两条数的是**在视口里**的卡片（`:visible` 只说明「没被藏」，屏幕外但没藏的也算 —— 见上面那两行） |
  | `窄屏：钉死的外壳只有 2 行` | 375×667 → 顶栏（`.home__topbar`）与输入框（`.composer`）都可见；**过滤行不在视口里**（它随内容滚走了）→ 复用文件里现成的 `intersectsViewport(box, viewport)`（`:353-361`）断言过滤行的矩形**与视口不相交** |
  | `窄屏：一屏能看到 ≥5 条想法` | 375×667 → **在视口里**的 `.idea` 数量 `>= 5`（判据同上：`:visible` 只说明「没被藏」，还要与视口矩形相交才算「一屏能看到」） |
  | `卡片只有一整块正文，没有标题层` | `page.locator('.idea h2.title').count()` === 0 |
  | `编号紧挨时间` | `.idea .meta__num` 与 `.idea .meta__time` 的水平距离 ≤ 20px，且 num 的 x < time 的 x |
  | `主页顶栏的「⋯」能直接进回收站` | 点「⋯」→ 点「回收站」→ URL 是 `/trash` |
  | `窄屏点 🔍 滚回列表顶部并聚焦搜索框` | 375×667 → 先滚到底，点顶栏的 🔍 → `document.activeElement` 是那个 searchbox（用 `page.evaluate` 读 `document.activeElement?.getAttribute('aria-label')`） |
  | `桌面端：勾选框与 🏷🗑 悬停才显形，键盘聚焦也显形` | 1280×800 → 不悬停时 `.idea .select` 的 `opacity` 为 0；悬停那条卡片后为 1；**Tab 聚焦到它时也为 1**（用 `page.keyboard.press('Tab')` 循环直到焦点落在它身上，或直接 `locator.focus()` 后读 opacity） |
  | `窄屏：勾选框与 🏷🗑 常驻` | 375×667 → `.idea .select` 的 `opacity` 为 1（不悬停） |
  | `无悬停能力的设备：勾选框与 🏷🗑 常驻` | 用 `browser.newContext({ hasTouch: true })` 另开一个 context（Chromium 里 `matchMedia('(hover: none)').matches` 为 true）→ **1280 宽也是** `.idea .select` 的 `opacity` 为 1（不悬停）—— 规格 §D8 / Review Focus 第 8 条：判据是「这台设备有没有悬停能力」，不只是屏宽 |
  | `窄屏：顶栏的新触发器够大` | 375×667 → 用文件里现成的 `expectTapTarget` 助手量「⋯」「🔍」与项目下拉触发器：宽高都 ≥ 32px（规格 §6.10 末句：新加的下拉触发器、发送按钮同样适用） |
  | `宽屏：新记的一条落在最下面，且左右按奇偶交替` | 1920×1080 → 这一轮已造 **14** 条（Step 2 的 `prepare`）→ 提交第 1 条新想法 → 它是**最后**一张卡、`.meta__num` 是 **'15'**（奇数位）→ 落在**左格**；再提交第 2 条 → `.meta__num` 是 **'16'**（偶数位）→ 落在**右格** —— 规格 §D5「第 2k+1 条进左格、第 2k+2 条进右格」+ §9 行为第 1 条。⚠️ **奇偶必须按实际条数推**：14+1=15 是奇数位（左格），别写死成右格 |
  | `宽屏：编辑一条靠前的想法，它跳到最下面，其下方卡片左右互换` | 1920×1080 → 编辑**第一条**想法并保存 → 它跳到最后一张卡；原来在它下面那条的 x **左右互换**了（规格 §9 行为第 2 条，**这是被明确批准的取舍**，不是 bug） |
  | `管理页铺满，Webhook 表单保持窄` | 1280×800 → 打开 `/trash` 与 `/projects`：`.page` 的右边缘离视口右边缘 ≤ 24px（**铺满**，`.page` 已经没有 `max-width` 了）；打开 `/webhooks`：`.page--narrow` 的宽度 ≤ 720px（**表单保持窄**，规格 §D10 / §9 视觉验收） |

  ⚠️ **上面那三条「显形」用例就是 Review Focus 第 8 条的落点**（计划开头那条 → **Task 15**）：
  桌面默认隐藏 / 悬停显形 / 键盘聚焦显形、窄屏常驻、**无悬停能力的设备常驻** —— 三条都要有。
  ⚠️ **移动端「点按目标」扫描替代不了它们**：那条扫描显式跳过 `opacity === 0` 的元素
  （`web/e2e/layout.spec.ts:562-564`），显形规则坏掉（控件全透明）时它会**静默跳过、照样绿**。
  所以显形必须**直接读 `opacity`** 来断言。

- [ ] **Step 4: 跑 e2e**

  Run: `bash web/e2e/run-e2e.sh --fresh`
  Expected: 全部 PASS。

  如果「宽屏一屏 ≥10 条」达不到（卡片太高 / 视口不够），**先量一下**：把 `.idea` 的高度与 meta 行的高度打出来，
  按规格 §6.10 的估算（宽屏正文 3 行 ≈ 58px + meta ≈ 24px + 间距 ≈ 9px → 单卡约 95px，1080px 高的视口扣掉顶栏与输入条约 110px → 约 10 条）。
  差几条就调 §6.9 的尺寸（圆角 10px / 内边距 9px 11px 7px / 正文 12.5px / 行高 1.55 / meta 10.5px），
  **不要**改验收标准。

  ⚠️ **两档的估算前提不同**（规格 §D9 + §6.10）：
  - **宽屏（两列）**：1080px 高的视口扣掉顶栏与输入条约 110px → 约 10 条（两列的话一屏是 10 张卡）；
  - **窄屏（单列，<768px）**：375×667 扣掉顶栏与输入条约 90px → 约 5 张卡
    （窄屏 meta 行 ≥32px、正文 3 行约 58px → 单卡约 110px，规格 §6.10 的原话）。
  ⚠️ 「窄屏 ≥5 条」是按**单列**估的 —— 如果窄屏还是两列，这条根本达不到（375px 屏每格才 175px 宽）。

- [ ] **Step 5: 更新 `web/e2e/COVERAGE.md`**

  - 更新「一、概览」里的用例总数与文件数（**按实际的数**，不要照抄）；
  - 在「二、覆盖矩阵」里补上新增的布局用例行（页面 | 功能点 | 覆盖它的用例 | 备注，用例列的格式是 `文件名.spec.ts:行号`）；
  - 在「三、漏掉的功能点」里把已经补上的项删掉。
  - ⚠️ **别重做 Task 10 已经做过的事**：指向 `MessageItem.vue` 的 10 处行号引用在 Task 10（提交 `75030ea`）
    已经更新到新结构上，**只需复核**（Task 11–14 若没动 `MessageItem.vue` 就保持原样），不要照着旧行号去改。

- [ ] **Step 6: 复核受影响的既有 e2e 用例**

  Run: `bash web/e2e/run-e2e.sh --fresh e2e/ideas.spec.ts e2e/navigation.spec.ts e2e/projects.spec.ts e2e/trash.spec.ts e2e/webhooks.spec.ts`
  Expected: 全部 PASS。

  受影响的用例（卡片定位、勾选、批量操作、编辑、点按目标扫描）逐条复核；
  失败的用例**逐条说明为什么需要调整**（规格 §9「因结构变化需要调整的断言要逐条说明理由」）。

- [ ] **Step 7: 跑全闸门**

  Run: `bash verify.sh`
  Expected: 四步全过。

  Run: `cd web && npm test && npm run typecheck`
  Expected: 全部 PASS。

- [ ] **Step 8: 提交**

  ```bash
  git add web/e2e/layout.spec.ts web/e2e/COVERAGE.md web/e2e/ideas.spec.ts
  git commit -m "test(e2e): 布局用例按新外壳重写

  卡片正文现在会被 3 行截断，「关键文本块没被裁掉」那条断言改成只对编辑态
  生效 —— 编辑框里必须能看到全文，非编辑态的正文不再受这条约束。

  新增：宽屏侧栏贴左且主区铺到右边缘、一屏 ≥10 条、两列交错、平板 768×1024
  两列无侧栏、窄屏 <768px 单列、窄屏钉死的外壳只有 2 行（过滤行随内容滚走）、
  一屏 ≥5 条、卡片没有标题层、编号紧挨时间、顶栏「⋯」一步进回收站、窄屏 🔍
  聚焦搜索框、悬停显形与键盘聚焦、窄屏常驻、顶栏新触发器够大、新记的一条
  落在最下面且左右按奇偶交替、编辑老想法后它跳到最下面且下方卡片左右互换
  （规格 §9 明确批准的取舍）。

  「窄屏钉死的外壳只有 2 行」那条：过滤行随内容滚走之后它在视口**上方**，
  y 是负数 —— 断言用的是「矩形与视口不相交」，不是「y > 667」。"
  ```

---

## 收尾（不属于任何 task）

全部 15 个 task 完成后，按顺序做这三件事：

1. **过一遍 Vercel 的 `web-design-guidelines` 清单**（`~/.agents/skills/web-design-guidelines`）：对比度、焦点可见、触摸目标、横向滚动、语义色 —— 逐条过，有问题就补一个 task 修掉。
2. **跑一次完整闸门**：`bash verify.sh` + `cd web && npm test && npm run typecheck` + `bash web/e2e/run-e2e.sh --fresh`。
3. **确认工作树干净**：`.superpowers/` 与 `.temp/` 不在提交里（`git status --ignored=no` 看一眼）。

## 自查（写这份计划时对照规格逐条过了一遍；审查后按两份审查意见又过了一遍）

- **规格覆盖**：§4.3 A–J 逐项落到了 Task 1–8；§5 D1–D11、§6.1–6.11、§7 的 8 条、§8 的 4 条、§9 的三组验收落到了 Task 9–15。
- **签名一致**：`IdeaDto` / `IdeaSnapshot` / `CreateIdeaRequest` / `UpdateIdeaRequest` / `MergeIdeasRequest` / `IdeaService.Create|Update|Merge` / `IdeaRepository.UpdateContent` / `SearchIndex.UpsertIdea` / `SearchIndex.IdeaIndexRow` / `SearchRepository.IdeaFtsWeights` / `stores.ideas.OlderPage` / `stores.ideas.prependOlder` / `lib.rows.toRows` 在各 task 之间一致。
- **Review Focus**：八条都有对应的测试，且放在拥有那段代码的 task 里（第 1、2 条在 Task 1，第 3 条在 Task 6，第 4 条在 Task 2，第 5 条在 Task 4，第 6 条在 Task 3，第 7 条在 Task 12，第 8 条在 Task 10）。
- **审查后修掉的四处「计划自己打架」**（第 1 轮审查发现的）：Task 9 删了 `ProjectTabs.vue` 而 Task 14 还在改它（`git add` 会报 pathspec 错误）；
  Task 13 把状态分组整个 `display: none` 而 Task 15 的关键元素检查要它可见；
  Task 12 的 `slice` 方向与它自己的测试描述矛盾（丢错了端 → 集合中间留洞）；
  Task 15 的「状态过滤组」关键元素要求可见且在视口内，而 Task 13 又把过滤行移进滚动容器（窄屏随内容滚走 → 不在视口里）→
  改成 `keyElements` 按视口给（窄屏不给这一条）。
- **第 4 轮（终审）又修掉的两处**（都在 Task 12 新加的那张「既有调用点与既有断言」表上）：
  ① 表自身矛盾 —— `:202` 的替换调用仍塞 101 条，而 `:203`/`:210`/`:211` 的期望值改成了 100 基准；
  新的 `prependOlder` 是「加多少就是多少」，探针的裁剪在 `fetchOlderIdeas` 里 ⇒ 照原表改那条用例仍红
  → `:202` 改成传 100 条（与 `fetchOlderIdeas` 交过来的一致）；
  ② 同一张表漏了 `web/src/pages/HomePage.spec.ts` 里另外 **9 条会变值的断言**与 **3 段会变味的注释**
  （`:2171`/`:2492-2493`/`:2494`/`:2495`/`:2512`/`:2514`/`:2544`/`:2591`/`:2592`/`:2606`/`:2608`/`:2656`）→
  补齐并给出新值（201 / `'idea-older-1'` / 30000 / 100）。
- **第 3 轮审查又修掉的四处**：
  ① Task 12 改了 `prependOlder` 的签名与语义，却没安排改 **7 个既有调用点**与约 **12 条既有断言**
  （含不在 Files 清单里的 `web/src/pages/HomePage.spec.ts:2153`）—— 照原计划做，Task 12 自己的 `npm test` 闸门必红；
  ② Task 15 的「窄屏：没有想法被藏起来」用 `count()` 判定 —— Playwright 的 `count()` **不看可见性**，
  藏起来的 `.idea` 照样数得到 ⇒ 它对要钉的那个错法**无效**；改成 `:visible` + `boundingBox()` 非 null；
  ③ 新增 e2e 的写法片段漏了 `await expect(route.ready(page)).toBeVisible();` —— 列表是异步拉来的，
  不等就量会**假红**；
  ④ 管理页「铺满 / 表单保持窄」的视觉验证没有落地（Task 14 把它指向 Task 15，Task 15 里没有）→ 补了一条 e2e。
- **第 2 轮审查又修掉的四处**：
  ① 窄屏单列**不能把右格藏掉** —— 网格只剩一列之后两个格子本来就自动上下堆叠，藏掉右格等于手机上每两条想法只剩一条（且没有任何闸门能发现）；
  ② Task 3 补上 `SearchIndexTests.cs` 的 `InsertIdea`（删掉 `idea.title` 列之后它的裸 SQL 会报 `no such column`）；
  ③ Task 1 补上 `SearchIndexTests.cs:77`/`:94` 两处硬编码的版本号断言；
  ④ 五个 task 的 `git add` 补齐自己 Files 里点名的文件（Task 5 的字段级守卫、Task 6 的 6 个 spec、Task 9 的 `COVERAGE.md`、Task 12 的 `rows.spec.ts`、Task 14 的 3 个 spec）——
  并在 Global Constraints 加了「提交前用 `git status --short` 对一遍 Files 清单」。
- **两处规格里的事实错误**（已在计划里按实测纠正，并写明依据）：
  ① 「bm25 权重个数与列数不匹配 → `/api/search` 500」—— 用本仓库自己的 SQLite 3.53.3 实测**不报错**（多余的权重忽略、缺少的按默认 1.0），真实后果是相关度静默错位；
  ② 「占位符被删掉 → 老模板渲染成空串」—— 渲染器原本的语义是「键不在 → 原样保留」，空串要靠 Task 4 新增的「已下线占位符」表才成立。
  这两处的**动作**（形状检查 + 重建、占位符下线 + 渲染成空串）仍然照规格做，改的只是「失败模式」的描述与对应的测试写法。
