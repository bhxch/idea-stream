# 后端全文搜索 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 idea 记事本加一套真正建在 SQLite FTS5 上的服务端全文检索：中文按 token 相邻、英文词级，页面与 agent 都能用，命中带高亮位置。

**Architecture:** 自写一个微信式分词器（ASCII 字母数字合并成词，其余逐字；标点与空白丢弃），索引侧与查询侧共用同一份实现。索引用三张 FTS5 虚表（`idea_fts` / `project_fts` / `tag_fts`），与业务数据**同库同事务**同步维护，靠 `search_meta` 的版本号在启动时回填老库。查询侧：中文用短语查询（要求 token 相邻）、多段之间用 AND；命中位置由服务端自己在原文上算（FTS5 没有 `offsets()`）。

**Tech Stack:** .NET 10（Native AOT、`InvariantGlobalization=true`）、ASP.NET Core Minimal API、`Microsoft.Data.Sqlite` 10.0.14（SQLite 3.53.3，`ENABLE_FTS5`）、Vue 3 + TypeScript + Vite、Vitest、Playwright。

**Spec:** `docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md` —— 计划从规格推导，**执行者两个都要读**。

---

## Global Constraints

这些是规格里的项目级要求，**每个 task 都隐含包含**：

- **不引新的 NuGet 依赖。** 分词器自己写，不引 jieba，不用原生 SQLite 扩展。
- **AOT 硬要求**：每个端点返回的**静态类型**必须在 `Contracts/JsonContext.cs` 里有 `[JsonSerializable]`，否则 AOT 产物 500 而 `dotnet test` 全绿。漏登记只有 `server/scripts/aot-smoke.sh` 能测出来。
- **`IdeaJsonContext` 没有设全局 `DefaultIgnoreCondition`** → 想要「字段为 null 就不写出」，必须在**属性上**加 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`。
- **不引 ORM**，手写 SQL；新端点的返回值类型必须登记进 `Contracts/JsonContext.cs`。
- **SQLite 版本 3.53.3，FTS5 可用**（实测）。`unicode61` 对中文无效、`trigram` 的 `MATCH` 查不了 2 字词 —— 这两条是既定结论，不要重新试别的分词器。
- **不要用 SQL 触发器维护索引**：`Infrastructure/schema.sql` 按 `;` 切分语句（`SqliteDatabase.cs:132`），`CREATE TRIGGER ... BEGIN...END;` 会被切坏。索引在仓储层显式维护。
- **索引与业务写入同事务**；任何硬删 `idea` 的路径都要同步删掉 `idea_fts` 行（rowid 会被复用）。
- **新错误码必须登记进 `Contracts/ApiErrorCodes.cs` 的 `ApiErrorCodes.All`**，`ApiErrorCodeTests` 会扫源码核对。
- **skill 说明书要同步**：`skills/idea-manager/references/api.md`（端点清单）、`errors.md`（错误码表，集合必须与 `ApiErrorCodes.All` **完全相等**）、`calling.md`（示例）。
- **契约快照**：`openapi/idea-v1.json` 是快照，由 `dotnet run --project tools/ExportOpenApi` 重新导出。`OpenApiDocumentTests.Committed_document_matches_the_running_app` 会比对。
- **现有排序承诺不变**：`GET /api/ideas` 恒为 `updated_at ASC, id ASC`（最老在顶）。分页是「往回翻历史」，返回的数组仍是这个顺序。
- **注释与文档用中文**，与仓库现有风格一致；每个公开类型/方法都要有 XML 文档注释说明「为什么」。

### 闸门命令

- 后端单测/集成测试：`dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
- 前端单测：`cd web && npm test`
- 类型检查：`cd web && npm run typecheck`
- 全部闸门：`bash verify.sh`（四步：后端测试 → 取件器单测 → AOT 冒烟 → 取件器测试脚本）
- E2E：`bash web/e2e/run-e2e.sh --fresh`（每轮一个端口 + 一个独立库）

---

## Review Focus

规格说了「必须做到什么」，没说「边界输入怎么办」。下面五条是最可能咬人的地方 —— **每条都要有对应的测试钉住**（在下面指定的 task 里加）：

1. **硬删一条想法后再新建一条** —— 新想法的索引不能被旧想法留下的孤儿行占住（`idea` 表没有显式整数主键，rowid 会被复用）。行为：新建后搜新想法的内容能搜到，搜旧想法的内容搜不到。
2. **回收站到期清理之后** —— 被清理掉的想法不该还能被搜到（`TrashCleanupWorker` 用裸 SQL 批量删，绕过仓储层）。行为：清理后搜它的内容搜不到。
3. **项目改名之后** —— 搜**旧**项目名不该再命中该项目下的想法，搜**新**项目名该命中。
4. **关键词全是标点或空白**（`...`、`   `）—— 不报错、不返回空，当作「没搜」（返回全部）。
5. **搜索 + 标签过滤 + 分页同时用** —— 一页不能因为内存里的标签过滤而变成空的，否则前端会误判成「到底了」，后面的命中永远看不到。

---

## File Structure

**后端新增**

| 文件 | 职责 |
|---|---|
| `server/IdeaApp/Domain/TextTokenizer.cs` | 分词、构造 MATCH 表达式、算命中偏移。纯函数，无 IO |
| `server/IdeaApp/Domain/TextToken.cs` | 词元及其在原文中的位置（并入 `TextTokenizer.cs` 亦可） |
| `server/IdeaApp/Infrastructure/SearchIndex.cs` | 索引表的写入/更新/删除；文本 → 索引文本 |
| `server/IdeaApp/Infrastructure/SearchIndexInitializer.cs` | 启动时的版本检查与整体回填 |
| `server/IdeaApp/Infrastructure/SearchRepository.cs` | `/api/search` 的三组查询（想法/项目/标签） |
| `server/IdeaApp/Application/SearchService.cs` | 编排搜索：分页参数校验 → 调仓储 → 组装响应 |
| `server/IdeaApp/Contracts/SearchDtos.cs` | `SearchPage<T>` / `SearchResponse` |
| `server/IdeaApp/Endpoints/SearchEndpoints.cs` | `GET /api/search` |
| `server/IdeaApp.Tests/TextTokenizerTests.cs` | 分词与偏移的单测 |
| `server/IdeaApp.Tests/SearchIndexTests.cs` | 索引维护与回填的测试 |
| `server/IdeaApp.Tests/SearchEndpointTests.cs` | 两个端点的 HTTP 层测试 |

**后端修改**

| 文件 | 改什么 |
|---|---|
| `server/IdeaApp/Infrastructure/schema.sql` | 追加三张 FTS 虚表 + `search_meta` 表 |
| `server/IdeaApp/Infrastructure/IdeaRepository.cs` | `BuildFilter` 加 FTS 条件；写路径同步索引 |
| `server/IdeaApp/Infrastructure/ProjectRepository.cs` | 建/改名项目时同步 `project_fts` |
| `server/IdeaApp/Infrastructure/TrashCleanupWorker.cs` | 清理时同步删索引行 |
| `server/IdeaApp/Application/IdeaService.cs` | `ParseQuery` 收 `q`/`limit`/`offset`；`List` 做切片 |
| `server/IdeaApp/Endpoints/IdeaEndpoints.cs` | `GET /api/ideas` 收三个新参数 |
| `server/IdeaApp/Contracts/Dtos.cs` | `SearchMatch`；`IdeaDto` 加 `Matches` |
| `server/IdeaApp/Contracts/ApiErrorCodes.cs` | `invalid_pagination`、`invalid_search_query` |
| `server/IdeaApp/Contracts/JsonContext.cs` | 登记新类型 |
| `server/IdeaApp/Program.cs` | 接线：回填 + `MapSearchEndpoints()` |

**前端新增**

| 文件 | 职责 |
|---|---|
| `web/src/lib/highlight.ts` | 按行 + 命中区间切片段；把命中分到「视觉标题」与「正文」两组 |
| `web/src/lib/highlight.spec.ts` | 上述的单测 |

**前端修改**

| 文件 | 改什么 |
|---|---|
| `web/src/api/types.ts` | `SearchMatch` 类型；`Idea.matches?` |
| `web/src/lib/filter.ts` | `toQueryParams()`：把 `IdeaFilter` 转成查询参数 |
| `web/src/stores/ideas.ts` | `loadIdeas(filter, limit?)`；`fetchOlderIdeas(filter, offset)`；`hasMore` / `loadingMore` |
| `web/src/pages/HomePage.vue` | 过滤条件下沉、防抖重拉、SSE 数据流、往回翻 |
| `web/src/components/FilterBar.vue` | 搜索框的注释更新（防抖在重拉入口，不在这里） |
| `web/src/components/MessageItem.vue` | 正文按片段渲染 `<mark>` |

**文档**

| 文件 | 改什么 |
|---|---|
| `openapi/idea-v1.json` | 重新导出 |
| `skills/idea-manager/references/api.md` | 端点清单加 `GET /api/search`；`GET /api/ideas` 的入参加 `q`/`limit`/`offset` |
| `skills/idea-manager/references/errors.md` | 错误码表加两行 |
| `skills/idea-manager/references/calling.md` | `curl` 示例 |
| `docs/superpowers/specs/2026-11-11-idea-manager-design.md` | 规格 §17 列的六处 |
| `web/e2e/COVERAGE.md` | 用例计数与概览表 |

---

### Task 1: 分词器、MATCH 表达式与命中偏移

**Files:**
- Create: `server/IdeaApp/Domain/TextTokenizer.cs`
- Test: `server/IdeaApp.Tests/TextTokenizerTests.cs`

**Interfaces:**
- Consumes: 无（这是最底层的一块）
- Produces:
  ```csharp
  namespace IdeaApp.Domain;

  /// 一个词元及其在原文中的位置（UTF-16 code unit）。
  public sealed record TextToken(string Token, int Start, int Length);

  /// 一处命中：在原文里的字符区间（UTF-16 code unit）。
  public sealed record TextMatch(int Start, int Length);

  public static class TextTokenizer
  {
      /// 切词：ASCII 字母数字合并成词并转小写；其余字符逐字一个；
      /// 标点与空白丢弃。索引侧与查询侧共用这一个实现。
      public static IReadOnlyList<string> Tokenize(string text);

      /// 同 Tokenize，但带上每个词元在原文里的 UTF-16 code unit 区间。
      public static IReadOnlyList<TextToken> Analyze(string text);

      /// 把查询串变成 FTS5 的 MATCH 表达式；全空返回 null（= 不过滤）。
      public static string? BuildMatchExpression(string query);

      /// 在原文里找出查询命中的位置。一个查询段 = 一处命中，
      /// 区间从首个命中词元的起点到末个命中词元的终点（跨过被丢弃的标点）。
      public static IReadOnlyList<TextMatch> FindMatches(string text, string query);
  }
  ```

- [ ] **Step 1: 写失败的测试**

  创建 `server/IdeaApp.Tests/TextTokenizerTests.cs`，用 xunit（`[Fact]` / `[Theory]`），断言：

  | 调用 | 期望 |
  |---|---|
  | `Tokenize("Hello520世界")` | `["hello520", "世", "界"]` |
  | `Tokenize("手冲咖啡的关键在于水温")` | 11 个单字：`手 冲 咖 啡 的 关 键 在 于 水 温` |
  | `Tokenize("项目, 温度!")` | `["项", "目", "温", "度"]` |
  | `Tokenize("项目，温度！")` | 同上（**全角与半角一样**） |
  | `Tokenize("👍 咖啡")` | `["咖", "啡"]`（emoji 是 Symbol，丢弃） |
  | `Tokenize("...")` | 空 |
  | `Tokenize("a-b")` | `["a", "b"]` |
  | `Tokenize("咖 啡")` | `["咖", "啡"]`（空白是分隔符） |
  | `Tokenize("a\u0301b")`（组合锐音符 U+0301） | `["a", "b"]`（`Mn` 不是 `L*`/`N*`，丢弃） |
  | `Tokenize("a\u200Db")`（零宽连接符 U+200D） | `["a", "b"]`（`Cf` 丢弃） |
  | `Analyze("咖啡，水温")` | `[(咖,0,1), (啡,1,1), (水,3,1), (温,4,1)]` |
  | `BuildMatchExpression("咖啡")` | `"咖 啡"`（含两侧双引号） |
  | `BuildMatchExpression("咖啡 水温")` | `"咖 啡" AND "水 温"` |
  | `BuildMatchExpression("Hello520")` | `"hello520"` |
  | `BuildMatchExpression("咖啡 ... 水温")` | `"咖 啡" AND "水 温"`（空段跳过） |
  | `BuildMatchExpression("...")` / `BuildMatchExpression("")` / `BuildMatchExpression("   ")` | `null` |
  | `FindMatches("手冲咖啡的关键", "咖啡")` | `[(2,2)]` |
  | `FindMatches("咖啡和咖啡", "咖啡")` | `[(0,2), (3,2)]`（多处命中各出一处） |
  | `FindMatches("咖啡，水温", "咖啡水温")` | `[(0,5)]`（**一个**命中，跨过逗号） |
  | `FindMatches("咖啡的水温", "咖啡水温")` | 空（「的」是汉字词元，挡路） |
  | `FindMatches("咖啡水温", "咖啡 水温")` | `[(0,2), (2,2)]`（两个查询段各出一处） |
  | `FindMatches("Hello world", "hello")` | `[(0,5)]`；`FindMatches("Hello world", "hel")` | 空（英文词级） |
  | `FindMatches("Hello520", "hello")` | 空（英文数字合并成一个 token） |
  | `FindMatches("第一行\n第二行咖啡", "咖啡")` | `[(7,2)]`（偏移跨行要算对） |

  测 `BuildMatchExpression` 时注意：**返回的字符串两侧带双引号**，断言要写全。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~TextTokenizerTests"`
  Expected: 编译失败（`TextTokenizer` 不存在）。

- [ ] **Step 3: 实现 `TextTokenizer`**

  在 `server/IdeaApp/Domain/TextTokenizer.cs` 里实现上面四个方法。实现要点（规格 §7.1 有完整版）：

  - **遍历单位是 Unicode 标量值**：`text.EnumerateRunes()`。不能用 `char` 逐个遍历 —— emoji 是代理对，会被切成两半。
  - **token 字符的判定：Unicode 一般类别以 `L` 或 `N` 开头的才是 token 字符**（`UppercaseLetter`/`LowercaseLetter`/`TitlecaseLetter`/`ModifierLetter`/`OtherLetter`/`DecimalDigitNumber`/`LetterNumber`/`OtherNumber`），**其余一律当分隔符丢弃** —— 标点、符号、`Cf`（零宽连接符、变体选择符）、`Cc`（控制字符）、`Mn`（组合记号）等等全都丢。
    - ⚠️ **这条边界是量出来的，不是猜的**：FTS5 的 `unicode61` 划 token 字符的依据与「`L*`/`N*` 是 token 字符、其余是分隔符」**基本一致**（实测：`中`/`é`/`ʰ`/`0`/`①`/`〇` 是 token 字符；`，`/`—`/`$`/`→`/`😀`/ZWJ/变体选择符/C1 控制字符 都是分隔符）。我们保留的字符集合**基本**是 unicode61 的子集 → 「FTS 命中了但算不出高亮区间」这一类偏差基本不会发生。
    - **两处分叉要写清楚，不要写成「彻底不存在」**：① unicode61 把 `Mn`（组合记号）当词内附加符保留，而**我们丢弃** —— 丢掉的字符 unicode61 看不到（索引文本里已经没有），方向是安全的；② unicode61 把 **21 个 `Lo` 码点**（New Tai Lue 元音符号 `U+19B0..U+19C0`/`U+19C8`/`U+19C9`、Vedic 符号 `U+1CF2`/`U+1CF3`）当分隔符而 .NET 判它们是 `Lo`（我们保留）—— **只有这 21 个码点上仍可能出现「FTS 命中却算不出高亮」**，冷门到不会有人输入，认了。
    - 判定用 `Rune.GetUnicodeCategory(rune)`（`InvariantGlobalization=true` 下可用，不依赖 ICU）。
  - ⚠️ **`Rune.IsAsciiLetterOrDigit` 不存在** —— ASCII 字母数字要自己按 `rune.Value` 的码位区间判断（`'a'..'z'` / `'A'..'Z'` / `'0'..'9'`）。
  - 转小写用 `rune.ToString().ToLowerInvariant()` —— 不能用 `(char)rune`（emoji 会溢出）。
  - **段切分用同一套规则**（按 `Rune.IsWhiteSpace`），不要另用一套 `char.IsWhiteSpace` 判定 —— 两套规则迟早漂移。
  - `Analyze` 在切词的同时累加 **UTF-16 code unit** 偏移：每个词元记下它在原文里的起始位置与长度（一个 rune 可能是 2 个 code unit）。
  - `BuildMatchExpression`：按空白切段 → 每段 `Tokenize` → 全空的段跳过 → 每段包成 `"tok1 tok2"` → 用 `" AND "` 连接。**返回 null 当且仅当所有段都空**。
  - `FindMatches`：把查询按空白切段，每段得到一个词元序列；在 `Analyze(text)` 的结果里找出该序列的**所有连续出现**，每次出现产出一个 `TextMatch`（起点 = 首个词元的 `Start`，长度 = 末个词元的 `Start + Length - 首个词元的 `Start`）。
  - 词元序列的比较**大小写不敏感**（切词时已经转小写，两边都是小写，直接比即可）。
  - 查询里某个段在原文里找不到 → 该段不产出命中（不抛异常）。
  - `FindMatches` 的 XML doc 里要写明：**同一查询可能产出重复或重叠的区间**（`咖啡 咖啡` 会产出两处一样的；`哈哈哈哈` 搜 `哈哈` 会产出自重叠的区间）—— **调用方需自行合并**。这是给下游（T4 的 `matches`、T9 的前端渲染）的契约，不能只留在实现者的口头说明里。

- [ ] **Step 4: 跑测试确认它通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~TextTokenizerTests"`
  Expected: 全部 PASS。

- [ ] **Step 5: 提交**

  ```bash
  git add server/IdeaApp/Domain/TextTokenizer.cs server/IdeaApp.Tests/TextTokenizerTests.cs
  git commit -m "feat(search): 加中文友好的分词器与命中偏移计算

  照微信的规则切词：ASCII 字母数字合并成词并转小写，其余字符逐字一个，
  标点与空白丢弃。索引侧与查询侧共用同一个实现 —— 这是两边语义一致的唯一保证。

  中文按词元相邻做短语查询，语义等价于子串；英文是词级。
  命中位置在原文上算（FTS5 没有 offsets()），偏移用 UTF-16 code unit，
  与前端 String.slice 对齐。"
  ```

---

### Task 2: 索引表、写入辅助与老库回填

**Files:**
- Modify: `server/IdeaApp/Infrastructure/schema.sql`（在文件末尾追加）
- Create: `server/IdeaApp/Infrastructure/SearchIndex.cs`
- Create: `server/IdeaApp/Infrastructure/SearchIndexInitializer.cs`
- Modify: `server/IdeaApp/Program.cs`（`Initialize()` 之后调用回填）
- Modify: `server/IdeaApp.Tests/DatabaseInitTests.cs`（加断言）
- Test: `server/IdeaApp.Tests/SearchIndexTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `TextTokenizer.Tokenize`
- Produces:
  ```csharp
  namespace IdeaApp.Infrastructure;

  public static class SearchIndex
  {
      /// 当前索引版本。分词规则变了就 +1，启动时会自动重建索引。
      public const string CurrentVersion = "1";

      /// 原文 → 索引里存的文本（切分后的词元串，词元间用单个空格）。
      public static string ToIndexText(string? text);

      /// 写入/更新一条想法的索引行（先删后插，保证幂等）。
      public static void UpsertIdea(
          SqliteConnection connection, SqliteTransaction? transaction,
          string ideaId, string? title, string content,
          IReadOnlyList<string> tags, string projectName);

      /// 删除一条想法的索引行。**必须在 idea 行还在时调用**（它靠 idea 行取 rowid）。
      public static void DeleteIdea(
          SqliteConnection connection, SqliteTransaction? transaction, string ideaId);

      /// 写入/更新一个项目的索引行。
      public static void UpsertProject(
          SqliteConnection connection, SqliteTransaction? transaction,
          string projectId, string projectName);

      /// 删除一个项目的索引行。
      public static void DeleteProject(
          SqliteConnection connection, SqliteTransaction? transaction, string projectId);

      /// 写入一个标签的索引行（标签名）。已存在则忽略。
      public static void UpsertTag(
          SqliteConnection connection, SqliteTransaction? transaction, string tagName);
  }

  public static class SearchIndexInitializer
  {
      /// 启动时调用：版本对得上就什么都不做；对不上就整体重建。
      /// 失败直接抛 —— 索引与业务不一致时，搜索会给出**错误的结果**，
      /// 那比「服务起不来」危险得多。
      public static void EnsureIndexed(SqliteDatabase db);
  }
  ```

- [ ] **Step 1: 写失败的测试**

  创建 `server/IdeaApp.Tests/SearchIndexTests.cs`：

  - `DatabaseInitTests.Initialize_creates_all_tables_and_indexes` 里加断言：对象集合里要包含 `idea_fts`、`project_fts`、`tag_fts`、`search_meta`。（`ReadSchemaObjects()` 那个 helper 已在该文件里，读的是 `sqlite_master`。）
  - 新建 `SearchIndexTests`：
    | 测试 | 断言 |
    |---|---|
    | `EnsureIndexed_on_empty_database_creates_index_rows` | 回填后 `idea_fts` 的行数 = 想法数 |
    | `EnsureIndexed_rebuilds_when_version_differs` | 把 `search_meta` 的 `index.version` 改成 `"0"`，再调一次 → 行数仍是想法数（说明重建过） |
    | `EnsureIndexed_is_a_noop_when_version_matches` | 版本已是 `"1"` 时，索引行数不变、且不重建（可断言 `search_meta` 的版本没变） |
    | `UpsertIdea_makes_the_idea_findable` | 造一条想法 → `UpsertIdea` → 用 `SELECT rowid FROM idea_fts WHERE idea_fts MATCH '"咖 啡"'` 能查到 |
    | `UpsertIdea_indexes_tags_and_project_name` | 标签「咖啡」与项目名「工作」都在索引里 → 搜 `"咖 啡"`、`"工 作"` 各自能查到同一条 |
    | `DeleteIdea_removes_the_row` | 删掉后 MATCH 查不到 |

  测试里造数据可以直接用 `SqliteDatabase` + 手写 SQL（与 `DatabaseInitTests` 同一套用法），不必走 HTTP。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SearchIndexTests|FullyQualifiedName~DatabaseInitTests"`
  Expected: 失败（`SearchIndex` / `SearchIndexInitializer` 不存在；表也不存在）。

- [ ] **Step 3: 追加 schema**

  在 `server/IdeaApp/Infrastructure/schema.sql` 末尾追加（**全部 `IF NOT EXISTS`**，保持文件幂等；`schema.sql` 按 `;` 切分执行，所以**不要写触发器**）：

  ```sql
  -- 想法的全文索引。四列对应四个可检索字段：标题、正文、标签名、项目名。
  -- 存的是**切分后**的词元串，不是原文 —— 所以不能用 external content 表
  -- （external content 表会用本表的 tokenize 去处理原文，中文会整段成一个 token）。
  CREATE VIRTUAL TABLE IF NOT EXISTS idea_fts USING fts5(
      title, content, tags, project,
      tokenize = 'unicode61 remove_diacritics 0'
  );

  CREATE VIRTUAL TABLE IF NOT EXISTS project_fts USING fts5(
      name,
      tokenize = 'unicode61 remove_diacritics 0'
  );

  CREATE VIRTUAL TABLE IF NOT EXISTS tag_fts USING fts5(
      name,
      tokenize = 'unicode61 remove_diacritics 0'
  );

  -- 索引元信息。key = 'index.version'，value = 当前版本号。
  -- 版本号变了就整体重建索引 —— 分词规则改了、或老库还没建索引，都靠它。
  CREATE TABLE IF NOT EXISTS search_meta (
      key   TEXT PRIMARY KEY,
      value TEXT NOT NULL
  );
  ```

  ⚠️ **`remove_diacritics 0` 是必须的，不能省**：unicode61 **默认会折叠变音符**（实测：`MATCH 'arger'` 能命中索引里的 `ärger`）。而分词器**保留**变音符 —— 不关掉的话，搜「arger」会命中「Ärger」那条，但服务端按自己的规则算不出高亮区间，又变成规格 §12.2 明令要避免的「搜得到却高亮不出来」。关掉之后 unicode61 与分词器的口径完全一致。

- [ ] **Step 4: 实现 `SearchIndex`**

  在 `server/IdeaApp/Infrastructure/SearchIndex.cs` 里实现上面的方法。关键点：

  - **rowid 映射**：`idea_fts.rowid = idea.rowid`、`project_fts.rowid = project.rowid`、`tag_fts.rowid = tag.id`（`tag` 表是 `INTEGER PRIMARY KEY AUTOINCREMENT`，rowid 就是 id）。这三张表都没有 `WITHOUT ROWID`，有隐式 rowid。
  - `ToIndexText(text)`：`TextTokenizer.Tokenize(text)` 后用单个空格连接；`text` 为 null 或切出空 → 返回空串。
  - `UpsertIdea`：**先删后插**（`DELETE FROM idea_fts WHERE rowid = (SELECT rowid FROM idea WHERE id = $id)` 再 `INSERT INTO idea_fts(rowid, title, content, tags, project) VALUES (...)`）。先删后插比 `UPDATE` 更直白，也保证「改了内容后不会残留旧 token」。
    - `tags` 列的内容是**所有标签名用空格连接后再切词**（`ToIndexText(string.Join(" ", tags))` 之后再切 —— 注意 `ToIndexText` 已经切过词，所以这里直接 `ToIndexText(string.Join(" ", tags))`）。
    - `project` 列是项目名的切分结果。
    - `idea` 行不存在时**静默跳过**（不抛异常）—— 回填时可能出现这种顺序问题。
  - `DeleteIdea`：先 `SELECT rowid FROM idea WHERE id = $id` 拿到 rowid，再 `DELETE FROM idea_fts WHERE rowid = $rowid`。**idea 行不在了就什么都不做**。
  - `UpsertProject` / `DeleteProject`：同理，rowid 取自 `project` 行。
  - `UpsertTag`：`INSERT OR IGNORE INTO tag_fts(rowid, name) SELECT id, <切分后的名字> FROM tag WHERE name = $name COLLATE NOCASE;` —— `tag_fts` 的 rowid 必须是 `tag.id`，所以用 `INSERT ... SELECT` 从 `tag` 表取 id。
    - ⚠️ `INSERT OR IGNORE ... SELECT` 的写法要注意：`INSERT OR IGNORE INTO tag_fts(rowid, name) SELECT id, $indexText FROM tag WHERE name = $name COLLATE NOCASE;` —— 切分后的名字作为参数传进去。

- [ ] **Step 5: 实现 `SearchIndexInitializer`**

  在 `server/IdeaApp/Infrastructure/SearchIndexInitializer.cs` 里实现 `EnsureIndexed(SqliteDatabase db)`：

  1. 开一个连接，**在事务外**读 `SELECT value FROM search_meta WHERE key = 'index.version'`。
     放在事务外是有意的：版本对得上时**不该占写锁** —— 每次启动都 `BEGIN IMMEDIATE` 会跟正在跑的实例抢锁。
  2. 值等于 `SearchIndex.CurrentVersion` → 直接返回（什么都不做）。
  3. 否则开一个事务（Microsoft.Data.Sqlite 的 `BeginTransaction()` 发的是 **BEGIN IMMEDIATE**，会拿写锁），**在事务内再读一次版本号**：
     - 期间别的实例可能已经重建过了 → 版本已是当前值 → **直接 no-op**（回滚即可）。这样两个实例同时启动不会重复重建。
     - 版本仍是旧值 → 继续重建。
  4. 在**这个事务**里：
     - `DELETE FROM idea_fts; DELETE FROM project_fts; DELETE FROM tag_fts;`
     - 灌入全部数据：
       - 标签：`SELECT id, name FROM tag` → 逐条 `SearchIndex.UpsertTag`（用 `tag_fts` 的 rowid = `tag.id`）。
       - 项目：`SELECT rowid, name FROM project` → 逐条写 `project_fts`。
       - 想法：一条 SQL 把三样凑齐：
         ```sql
         SELECT i.rowid, i.id, i.title, i.content, p.name,
                (SELECT group_concat(name, ' ') FROM (
                    SELECT t.name FROM idea_tag it JOIN tag t ON t.id = it.tag_id
                    WHERE it.idea_id = i.id ORDER BY t.name COLLATE NOCASE ASC, t.name ASC))
         FROM idea i JOIN project p ON p.id = i.project_id;
         ```
         ⚠️ **`group_concat` 的顺序不保证**（实测会得到 `mango zebra apple` 这种顺序），所以标签名要显式排序后再拼。
       - 写 `INSERT OR REPLACE INTO search_meta(key, value) VALUES ('index.version', '1')`。
  4. **失败直接往上抛**（不要 catch 掉）。规格 §10.4：索引与业务不一致时搜索给的是错结果，比起不来危险得多。

- [ ] **Step 6: 在组合根接上回填**

  在 `server/IdeaApp/Program.cs` 里，紧跟着 `app.Services.GetRequiredService<SqliteDatabase>().Initialize();` 之后加一行：

  ```csharp
  SearchIndexInitializer.EnsureIndexed(app.Services.GetRequiredService<SqliteDatabase>());
  ```

  需要 `using IdeaApp.Infrastructure;`（该文件已经有了，确认一下）。

- [ ] **Step 7: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS（**包括原有的全部测试** —— 回填在每个测试实例建库时都会跑一次，不能把现有测试搞红）。

- [ ] **Step 8: 提交**

  ```bash
  git add server/IdeaApp/Infrastructure/schema.sql server/IdeaApp/Infrastructure/SearchIndex.cs \
          server/IdeaApp/Infrastructure/SearchIndexInitializer.cs server/IdeaApp/Program.cs \
          server/IdeaApp.Tests/DatabaseInitTests.cs server/IdeaApp.Tests/SearchIndexTests.cs
  git commit -m "feat(search): 建全文索引表并支持老库自动回填

  三张 FTS5 虚表（想法/项目/标签）加一张 search_meta 版本表。索引里存的是
  切分后的词元串而不是原文 —— external content 表会用本表的 tokenize 处理
  原文，中文会整段成一个 token。

  老库没有索引，靠 search_meta 的版本号在启动时整体重建一次：版本对得上就
  什么都不做，对不上就清空重灌。回填失败直接让启动失败 —— 索引与业务数据
  不一致时搜索会给的是错结果，那比服务起不来危险得多。"
  ```

---

### Task 3: 仓储层在写路径上维护索引

**Files:**
- Modify: `server/IdeaApp/Infrastructure/IdeaRepository.cs`
- Modify: `server/IdeaApp/Infrastructure/ProjectRepository.cs`
- Modify: `server/IdeaApp/Infrastructure/TrashCleanupWorker.cs`
- Test: `server/IdeaApp.Tests/SearchIndexConsistencyTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SearchIndex`
- Produces: 无新公开类型（只是让现有写路径顺带维护索引）

  **必须在这些路径上同步索引**（规格 §10.2）：

  | 路径 | 动作 |
  |---|---|
  | `IdeaRepository.Insert` | 插入索引行（title/content/tags/project） |
  | `IdeaRepository.UpdateContent` | 更新索引行的 title/content |
  | `IdeaRepository.ReplaceTags` | 更新索引行的 tags；**新建的标签**要写 `tag_fts` |
  | `IdeaRepository.HardDelete` | **删除索引行**（必须在 idea 行还在时） |
  | `IdeaRepository.SoftDelete` / `Restore` / `SoftDeleteByProject` / `RestoreWithProject` | **不动索引**（软删的行仍在索引里，搜索时用 `deleted_at` 判断） |
  | `IdeaRepository.SetDone` | 不动 |
  | `ProjectRepository.Insert` | 插入 `project_fts` 行 |
  | `ProjectRepository.Rename` | 更新 `project_fts` 行；**并重建该项目下所有想法的 `project` 列** |
  | `ProjectRepository.SoftDelete` / `Restore` / `Reorder` | 不动 |
  | `TrashCleanupWorker.Cleanup` | 三条 `DELETE` 之前各补一条删索引的 `DELETE` |

- [ ] **Step 1: 写失败的测试**

  创建 `server/IdeaApp.Tests/SearchIndexConsistencyTests.cs`，用 `TestAppFactory` + HTTP 打真实路径，然后直接查库里的 `idea_fts` 验证：

  | 测试 | 步骤与断言 |
  |---|---|
  | `Create_idea_makes_it_findable` | `POST /api/ideas`（正文「咖啡机滤芯」）→ `SELECT rowid FROM idea_fts WHERE idea_fts MATCH '"咖 啡"'` 有 1 行 |
  | `Update_content_updates_the_index` | `PATCH /api/ideas/{id}` 改成「喝茶」→ 搜 `"咖 啡"` **0 行**，搜 `"喝 茶"` 1 行 |
  | `Set_tags_updates_the_index` | `PUT /api/ideas/{id}/tags` 设成「烘焙」→ 搜 `"烘 焙"` 1 行；改回空 → 搜 `"烘 焙"` 0 行 |
  | `Merge_hard_deletes_the_source_from_the_index` | 合并两条 → 源 id 在 `idea_fts` 里查不到 |
  | `Delete_idea_keeps_the_row_in_the_index` | 软删 → 索引行**仍在**（搜索时靠 `deleted_at` 判断） |
  | `Rename_project_updates_the_index_of_its_ideas` | 项目改名 → 搜**旧**项目名 0 行、搜**新**项目名 1 行 |
  | `Hard_delete_then_create_does_not_reuse_a_stale_row` | **Review Focus 第 1 条**：造一条想法 → 硬删（走合并）→ 再建一条新想法 → 新想法的正文能搜到，旧想法的正文搜不到 |
  | `Trash_cleanup_removes_the_index_rows` | **Review Focus 第 2 条**：软删一条想法（`deleted_at` 设成很久以前）→ 调 `TrashCleanupWorker.Cleanup()` → 索引里查不到它 |

  > `TrashCleanupWorker` 在 `Testing` 环境下**不注册**（`Program.cs:106-109`），所以这条测试要**直接 new 一个 `TrashCleanupWorker`** 并调它的 `Cleanup()`（`public` 的）。构造方式是：
  > ```csharp
  > var options = Options.Create(new AppOptions { Trash = new TrashOptions { RetentionDays = 1 } });
  > var worker = new TrashCleanupWorker(
  >     new SqliteDatabase(factory.DatabasePath),
  >     options,
  >     NullLogger<TrashCleanupWorker>.Instance);
  > worker.Cleanup();
  > ```
  > `NullLogger<T>` 在 `Microsoft.Extensions.Logging.Abstractions` 里（测试项目通过 `ProjectReference` 传递引用）。
  > 造「已过期」的数据：把想法的 `deleted_at` 直接 UPDATE 成一个很早的时间戳（早于 `now - RetentionDays`）。

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SearchIndexConsistencyTests"`
  Expected: 失败（索引没有跟着写）。

- [ ] **Step 3: 让 `IdeaRepository` 的写路径同步索引**

  在 `server/IdeaApp/Infrastructure/IdeaRepository.cs` 里：

  - `Insert`：在 `WriteTagLinks(...)` 之后（此时标签链接已写好、能读到标签名），调用
    ```csharp
    SearchIndex.UpsertIdea(connection, transaction, idea.Id, idea.Title, idea.Content,
        ReadTagNames(connection, transaction, idea.Id),
        ReadProjectName(connection, transaction, idea.ProjectId));
    ```
    `ReadProjectName` 是要**新增的私有静态方法**：`SELECT name FROM project WHERE id = $projectId`，查不到返回空串。
    ⚠️ **标签名必须用 `ReadTagNames` / `GetTagNames` 读回来的那份（它按 `t.name COLLATE NOCASE ASC, t.name ASC` 排序），不要直接用调用方给的标签列表。** 回填路径是把标签名**按名称排序后**再拼的（`SearchIndexInitializer` 里 `group_concat` 子查询带 `ORDER BY`），增量路径必须逐字节一致 —— 否则同一条想法在两条路径下写出的 `tags` 列文本不同：普通匹配不受影响，但**跨两个标签的短语查询**会受影响（`"咖 啡"` 这种短语要相邻）。
  - `UpdateContent`：拿到 `nextTitle` / `nextContent` 之后调 `SearchIndex.UpsertIdea(...)`（标签与项目名沿用库里的现值：`GetTagNames(id)` 与 `ReadProjectName(...)`）。
  - `ReplaceTags`：`WriteTagLinks` 之后调 `SearchIndex.UpsertIdea(...)`；**新建的标签**要写 `tag_fts` —— 标签行是在 `EnsureTag` 里创建的，所以要在 `EnsureTag` 新建行之后调一次 `SearchIndex.UpsertTag(connection, transaction, name)`（复用了已有行就不必再写）。
    ⚠️ 传给 `UpsertIdea` 的标签名同样要用 `ReadTagNames` / `GetTagNames` 读回来的**排序版**，不要用 `ReplaceTags` 的入参（那是用户给的顺序）。
  - `HardDelete`：**先** `SearchIndex.DeleteIdea(connection, transaction, id)`，**再** `DELETE FROM idea WHERE id = $id`（`DeleteIdea` 靠 idea 行取 rowid，顺序反了就取不到）。
    ⚠️ **并且要把 `HardDelete` 从 `Execute` 改成 `Write`**：现在它走 `Execute`（单语句助手；没有外层事务时是**两次独立自动提交**）→ 索引行删了、业务行没删的话，那条想法就再也搜不到了（没有异常、没有日志）。`Write` 助手自带事务，两条语句要么都成、要么都不成。
  - `SoftDelete` / `Restore` / `SetDone` / `SoftDeleteByProject` / `RestoreWithProject`：**不动**。

- [ ] **Step 4: 让 `ProjectRepository` 同步索引**

  在 `server/IdeaApp/Infrastructure/ProjectRepository.cs` 里：

  - `Insert`：插入 `project` 行之后调 `SearchIndex.UpsertProject(connection, transaction, id, name)`。
  - `Rename`：更新 `project` 行之后调 `SearchIndex.UpsertProject(connection, transaction, id, name)`；**并重建该项目下所有想法的 `project` 列** —— 做法是查出该项目下所有想法的 id，然后逐条调 `SearchIndex.UpsertIdea`（标题/正文从库里读，**标签用 `ReadTagNames` 读回来的排序版**，项目名用新名字）。
    - 想法数量是几百到几千级，重建成本可忽略。
  - `SoftDelete` / `Restore` / `Reorder`：不动。

- [ ] **Step 5: 让 `TrashCleanupWorker` 同步删索引**

  在 `server/IdeaApp/Infrastructure/TrashCleanupWorker.cs` 的 `Cleanup()` 里，**三条业务 `DELETE` 之前**各插一条删索引的 `DELETE`（顺序很重要：删索引要靠 `idea` / `project` 行取 rowid）：

  ```sql
  -- 1) 与下面第一条 DELETE 同样的条件
  DELETE FROM idea_fts WHERE rowid IN (SELECT rowid FROM idea
      WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff
        AND project_id NOT IN (SELECT id FROM project WHERE deleted_at IS NOT NULL));

  -- 2) 与下面第二条 DELETE 同样的条件
  DELETE FROM idea_fts WHERE rowid IN (SELECT rowid FROM idea
      WHERE project_id IN (SELECT id FROM project WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff));

  -- 3) 与下面第三条 DELETE 同样的条件
  DELETE FROM project_fts WHERE rowid IN (SELECT rowid FROM project
      WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff);
  ```

  这三条与原有的三条 `DELETE` 在**同一个事务**里。

- [ ] **Step 6: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS。

- [ ] **Step 7: 提交**

  ```bash
  git add server/IdeaApp/Infrastructure/IdeaRepository.cs server/IdeaApp/Infrastructure/ProjectRepository.cs \
          server/IdeaApp/Infrastructure/TrashCleanupWorker.cs server/IdeaApp.Tests/TestAppFactory.cs \
          server/IdeaApp.Tests/SearchIndexConsistencyTests.cs
  git commit -m "feat(search): 写路径上同步维护全文索引

  索引与业务写在同一个事务里 —— 同库同事务才能保证强一致，索引与业务异步
  同步会带来「搜到不完整的记录」和「跨库无法保证事务原子性」两个问题。

  回收站到期清理是裸 SQL 批量删，绕过仓储层，所以要单独补上删索引的语句：
  孤儿行不只是脏数据，它占着 rowid，而 idea 表没有显式整数主键、rowid 会被
  复用，下一条新想法可能顶到它的位置上，索引就串了。"
  ```

---

### Task 4: `GET /api/ideas` 支持 `q` / `limit` / `offset`

**Files:**
- Modify: `server/IdeaApp/Contracts/Dtos.cs`
- Modify: `server/IdeaApp/Contracts/JsonContext.cs`
- Modify: `server/IdeaApp/Contracts/ApiErrorCodes.cs`
- Modify: `server/IdeaApp/Application/IdeaService.cs`
- Modify: `server/IdeaApp/Infrastructure/IdeaRepository.cs`
- Modify: `server/IdeaApp/Endpoints/IdeaEndpoints.cs`
- Test: `server/IdeaApp.Tests/SearchEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `TextTokenizer`、Task 2 的 `SearchIndex`
- Produces:
  ```csharp
  namespace IdeaApp.Contracts;

  /// 一处命中：某个字段里的字符区间（UTF-16 code unit）。
  public sealed record SearchMatch(string Field, int Start, int Length);

  /// IdeaDto 追加两个可选字段（加在末尾，带默认值）：
  ///   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SearchMatch[]? Matches = null,
  ///   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Trashed = null
  ```
  `IdeaService` 的签名变化：
  ```csharp
  /// 把 HTTP 查询参数翻译成查询条件。
  /// keyword / limit / offset 是新增的：关键词走全文索引，limit+offset 是往回翻历史的分页。
  public static IdeaQuery ParseQuery(string? projectId, string? status, string? tags,
                                     string? tagMode, string? q, string? limit, string? offset);

  /// 按查询条件列出想法。给了 limit 就只返回那一页（往回翻历史）。
  public IReadOnlyList<IdeaDto> List(IdeaQuery query, string? keyword, string? limit, string? offset);
  ```
  `IdeaQuery` **不变**（关键词不进 `IdeaQuery` —— `IdeaFilter.Matches` 不判关键词，两套语义打架会误杀）。

- [ ] **Step 1: 写失败的测试**

  创建 `server/IdeaApp.Tests/SearchEndpointTests.cs`（HTTP 层，用 `TestAppFactory`）：

  | 测试 | 断言 |
  |---|---|
  | `Get_ideas_matches_chinese_substring` | 正文「咖啡机滤芯」，`?q=咖啡` → 命中 |
  | `Get_ideas_does_not_match_across_a_chinese_character` | 正文「咖啡的水温」，`?q=咖啡水温` → **不**命中 |
  | `Get_ideas_ignores_case_for_english` | 正文「Weather today」，`?q=weather` → 命中 |
  | `Get_ideas_does_not_match_english_prefix` | 正文「hello」，`?q=hel` → **不**命中（词级） |
  | `Get_ideas_matches_title` | 只有标题里有那个词 → 命中 |
  | `Get_ideas_matches_tag_name` | 想法带标签「咖啡」→ `?q=咖啡` 命中 |
  | `Get_ideas_matches_project_name` | 项目名「工作复盘」，项目下的想法 → `?q=工作复盘` 命中 |
  | `Get_ideas_with_multiple_terms_matches_both` | 正文「咖啡…水温」（隔很远），`?q=咖啡 水温` → 命中（段间 AND，不要求相邻） |
  | `Get_ideas_with_punctuation_only_query_returns_all` | **Review Focus 第 4 条**：`?q=...` → 返回全部，不报错 |
  | `Get_ideas_with_blank_query_returns_all` | `?q=%20%20` → 返回全部 |
  | `Get_ideas_limit_returns_the_newest_page` | 造 5 条（**按创建顺序**，id 单调递增所以顺序稳定），`?limit=2` → 返回**最新**的 2 条（`updated_at` 升序） |
  | `Get_ideas_offset_walks_backwards` | 造 5 条，`?limit=2&offset=2` → 返回第 3、4 新（与 `?limit=2` 的结果不重叠） |
  | `Get_ideas_limit_bypasses_too_many_ideas` | **Review Focus 第 5 条**：`maxIdeasPerResponse: 2`，造 3 条 → `?limit=2` 返回 200 且 2 条（不给 limit 时是 400 `too_many_ideas`） |
  | `Get_ideas_with_tags_and_limit_returns_a_full_page` | 造 4 条，其中 3 条带标签「咖啡」→ `?tags=咖啡&limit=2` → 返回 **2** 条（不是 0 条、也不是 3 条）。这一条钉的是「切片在内存标签过滤之后」：如果先切页再过滤，这一页会被滤掉一部分，`hasMore` 的判定就错了 |
  | `Get_ideas_returns_matches_offsets` | `?q=咖啡` → 响应里 `matches` 数组有 `field: "content"`、`start`、`length`，且 `content.slice(start, start+length)` 等于命中词 |
  | `Get_ideas_without_query_omits_matches` | 不带 `q` → 响应里**没有** `matches` 键（属性级 `JsonIgnore` 生效） |
  | `Get_ideas_with_invalid_limit_returns_400` | `?limit=0` / `?limit=100000` / `?offset=-1` → 400，code = `invalid_pagination` |
  | `Get_ideas_with_offset_but_no_limit_returns_400` | `?offset=10`（不带 limit）→ 400 `invalid_pagination` |

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SearchEndpointTests"`
  Expected: 失败（端点还不认 `q`）。

- [ ] **Step 3: 加 DTO 与错误码**

  - `server/IdeaApp/Contracts/Dtos.cs`：
    - 加 `using System.Text.Json.Serialization;`
    - 加 `public sealed record SearchMatch(string Field, int Start, int Length);`
    - `IdeaDto` 末尾追加两个可选字段（带 `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`）。
      ⚠️ **属性级** `JsonIgnore`：`IdeaJsonContext` 没有设全局 `DefaultIgnoreCondition`，不加这个特性的话 null 字段会恒定写出。
  - `server/IdeaApp/Contracts/ApiErrorCodes.cs`：在 400 那一组里加
    `new("invalid_pagination", 400, "limit 或 offset 超出允许范围")`
    （按码名排序插在 `invalid_merge_selection` 之后、`invalid_project_name` 之前）。
  - `server/IdeaApp/Contracts/JsonContext.cs`：加
    `[JsonSerializable(typeof(SearchMatch))]` 与 `[JsonSerializable(typeof(SearchMatch[]))]`。

- [ ] **Step 4: 仓储层支持关键词过滤**

  在 `server/IdeaApp/Infrastructure/IdeaRepository.cs` 里：

  - `List(IdeaQuery query)` 改成 `List(IdeaQuery query, string? keyword)`：
    - `BuildFilter(query, keyword)` 多接一个参数；有关键词时在 where 末尾追加
      `AND i.rowid IN (SELECT rowid FROM idea_fts WHERE idea_fts MATCH $match)`
      并把参数 `$match` = `TextTokenizer.BuildMatchExpression(keyword)` 加进 `parameters`。
    - ⚠️ **两条查询都要起别名 `i`**：SQLite 在有别名时**会把表名藏起来**（实测：`FROM idea i` 之后写 `idea.rowid` 报 `no such column: idea.rowid`）。所以主查询也要改成 `FROM idea i`，共享的 WHERE 片段里写 **`i.rowid`** —— 两处 FROM 的别名一致，片段才能共用。裸 `rowid` 也会报 `ambiguous column name`（标签查询的 FROM 里有三张带 rowid 的表）。
    - 关键词为 null 或 `BuildMatchExpression` 返回 null（全是标点）时**不追加这个条件**（当作不过滤）。
    - SQL **不带** `LIMIT` / `OFFSET`：切片在内存做（见 Step 5）。
  - `BuildFilter` 产出的 where 片段仍然被主查询与 `ReadTagNamesOfCandidates` **共用** —— 两边口径一致，标签批量查询会跟着变窄。

- [ ] **Step 5: 服务层做参数解析与切片**

  在 `server/IdeaApp/Application/IdeaService.cs` 里：

  - `ParseQuery` 多接三个参数：
    - `q` → trim 后为空则 `null`；否则原样保留（**不做大小写折叠**，切词时自己转小写）。
    - `limit` → null 表示不限；不在 1..1000 → 抛 `new ApiException(400, "invalid_pagination", ...)`。
    - `offset` → null 视为 0；小于 0 → 400 `invalid_pagination`；大于 0 而 `limit` 为 null → 400 `invalid_pagination`。
    - 关键词、limit、offset **都不进 `IdeaQuery`**（`IdeaQuery` 只管项目/状态/标签/模式）。
  - `List(IdeaQuery query)` 保留原签名（内部按不过滤调用新的重载）。
  - 新增 `List(IdeaQuery query, string? keyword, string? limit, string? offset)`：
    1. `var all = repo.List(query, keyword);` —— 已是 `updated_at ASC` 顺序。
    2. **不给 limit** → 检查 `all.Count > options.Value.Api.MaxIdeasPerResponse` → 抛 `too_many_ideas`（与今天一样）；返回全部。
    3. **给了 limit** → 不检查上限；切片：
       ```csharp
       var end   = all.Count - offset;
       var start = Math.Max(0, end - limit);
       var page  = all.Skip(start).Take(end - start).ToList();
       ```
       （`all` 是升序，最老的在 `[0]`，最新的在 `[Count-1]`；`offset` 是「跳过最新的 N 条」）
    4. 每条转 DTO 时，若有关键词且命中，填 `Matches`（用 `TextTokenizer.FindMatches` 对 `title` / `content` / 标签名 / 项目名各算一遍，`Field` 分别是 `"title"` / `"content"` / `"tags"` / `"project"`）。
       ⚠️ **项目名要从库里取，而且要缓存**：领域类型 `Idea` 里只有 `ProjectId`，没有项目名。
       用一个**按项目 id 缓存的字典**取（同一页里通常只有 1–5 个不同项目），**不要逐条查库**。
       ⚠️ **`IdeaService.ToDto` 不动** —— 它被创建/更新/合并等所有路径共用，`matches` 只在**列表这一条路径**上填。

- [ ] **Step 6: 端点收参数**

  在 `server/IdeaApp/Endpoints/IdeaEndpoints.cs` 的 `GET /api/ideas` 上：

  - 端点签名加 `string? q, string? limit, string? offset`。
    ⚠️ **`limit` / `offset` 绑 `string?`（不是 `int?`）**：实测（跑真实应用）绑 `int?` 时，`?limit=abc` 在参数绑定阶段失败后**框架只设一个 400 状态码就返回** —— **空 body、没有 code**，进不了 `ApiExceptionHandler`。而这个应用里既有的查询参数（`status` / `tagMode`）都是 `string?` + 自己解析 + 自己的错误码。跟它们一致：所有非法情形（非数字、越界）一律回 `invalid_pagination` 并带说得清楚的 detail。
    代价：OpenAPI 里这两个参数的 schema 是 `string` 而不是 `integer`。
  - 调 `IdeaService.ParseQuery(projectId, status, tags, tagMode, q, limit, offset)` 与 `service.List(query, keyword, limit, offset)`。
  - `.ProducesApiProblem(StatusCodes.Status400BadRequest, ...)` 里加上 `"invalid_pagination"`（`ApiErrorCodeTests.Endpoint_declared_codes_are_documented_with_the_right_status` 会核对）。
    ⚠️ **不要**加 `"invalid_request_body"`：那是**请求体**绑定失败的码，而 `GET /api/ideas` 没有请求体、查询串绑定失败又拿不到它（见上）。声明一个真不会返回的码，契约就不准了。

- [ ] **Step 7: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS（原有测试不能红 —— 尤其 `Get_ideas_returns_bare_array_sorted_by_updated_at_asc` 与 `Get_ideas_over_limit_returns_400_code_too_many_ideas`）。

- [ ] **Step 8: 提交**

  ```bash
  git add server/IdeaApp/Contracts/Dtos.cs server/IdeaApp/Contracts/JsonContext.cs \
          server/IdeaApp/Contracts/ApiErrorCodes.cs server/IdeaApp/Application/IdeaService.cs \
          server/IdeaApp/Infrastructure/IdeaRepository.cs server/IdeaApp/Endpoints/IdeaEndpoints.cs \
          server/IdeaApp.Tests/SearchEndpointTests.cs
  git commit -m "feat(search): GET /api/ideas 支持关键词与往回翻的分页

  q 走全文索引，语义是「词元相邻」：中文等价于子串（标点与空白不挡路），
  英文是词级。limit/offset 是往回翻历史的分页 —— offset 从最新一端数，
  返回的数组仍是 updated_at 升序。

  切片放在内存标签过滤之后：SQL 层切出来的一页在内存过滤后可能只剩零星
  几条，前端按「返回条数 == limit+1」判定没更多，后面的命中就永远看不到。
  给了 limit 就不再报 too_many_ideas —— 明确要了一页，不该被顶回来。"
  ```

---

### Task 5: `GET /api/search` 全库搜索端点

**Files:**
- Create: `server/IdeaApp/Contracts/SearchDtos.cs`
- Create: `server/IdeaApp/Infrastructure/SearchRepository.cs`
- Create: `server/IdeaApp/Application/SearchService.cs`
- Create: `server/IdeaApp/Endpoints/SearchEndpoints.cs`
- Modify: `server/IdeaApp/Contracts/Dtos.cs`（`ProjectDto` 加 `Trashed`）
- Modify: `server/IdeaApp/Contracts/JsonContext.cs`
- Modify: `server/IdeaApp/Contracts/ApiErrorCodes.cs`
- Modify: `server/IdeaApp/Program.cs`（`MapSearchEndpoints()`）
- Test: `server/IdeaApp.Tests/SearchEndpointTests.cs`（追加用例）

**Interfaces:**
- Consumes: Task 1 的 `TextTokenizer`、Task 4 的 `SearchMatch`
- Produces:
  ```csharp
  namespace IdeaApp.Contracts;

  /// 一类命中结果的一页。
  public sealed record SearchPage<T>(T[] Items, int Total, int Offset, int Limit, bool HasMore);

  /// 全库搜索的响应：想法、项目、标签三组。
  public sealed record SearchResponse(
      string Query,
      SearchPage<IdeaDto> Ideas,
      SearchPage<ProjectDto> Projects,
      SearchPage<TagDto> Tags);
  ```
  ```csharp
  namespace IdeaApp.Infrastructure;

  /// 命中的想法：想法本身 + 是否在回收站 + 命中位置。
  public sealed record IdeaHit(Idea Idea, bool Trashed, IReadOnlyList<SearchMatch> Hits);

  /// 命中的项目：项目本身 + 是否在回收站。
  public sealed record ProjectHit(Project Project, bool Trashed);

  /// 一页命中结果 + 命中总数（总数不受分页影响）。
  public sealed record SearchSlice<T>(IReadOnlyList<T> Items, int Total);

  public sealed class SearchRepository(SqliteDatabase db)
  {
      public SearchSlice<IdeaHit> Ideas(string matchExpression, bool includeTrash, int limit, int offset);
      public SearchSlice<ProjectHit> Projects(string matchExpression, bool includeTrash, int limit, int offset);
      public SearchSlice<TagDto> Tags(string matchExpression, int limit, int offset);
  }
  ```
  `SearchMatch` 复用 Task 4 定的 `IdeaApp.Contracts.SearchMatch`（Infrastructure 层引用 Contracts 已有先例：`TagRepository` 就返回 `TagDto`）。**不要另造一个同义的类型。**
  ```csharp
  namespace IdeaApp.Application;

  public sealed class SearchService(SearchRepository repository)
  {
      /// 全库搜索。query 为空串 → 400 invalid_search_query。
      public SearchResponse Search(string query, int limit, int offset, bool includeTrash);
  }
  ```
  `GET /api/search` 的查询参数：`q`（必填）、`limit`（默认 20，1..100）、`offset`（默认 0，≥0）、`includeTrash`（默认 true）。
  ⚠️ **`limit` / `offset` 绑 `string?`（不是 `int?`）**，由服务层自己解析 —— 理由与 Task 4 相同：绑 `int?` 时非数字输入回的是**空 body、没有 code** 的 400（实测），agent 读到不知道错在哪。

- [ ] **Step 1: 写失败的测试**

  在 `server/IdeaApp.Tests/SearchEndpointTests.cs` 追加：

  | 测试 | 断言 |
  |---|---|
  | `Search_returns_three_groups` | 造想法（正文含「咖啡」、标签「烘焙」、项目「工作」）→ 响应里 `ideas` / `projects` / `tags` 三个数组，`query` 回显 |
  | `Search_ideas_group_is_sorted_by_relevance` | 一条想法标题含「咖啡」，另一条正文含「咖啡」→ 标题那条在前面 |
  | `Search_finds_projects_and_tags` | 搜「工作」→ `projects` 里有那个项目；搜「烘焙」→ `tags` 里有那个标签 |
  | `Search_includes_trash_by_default` | 软删一条想法 → 搜它的内容 → `ideas` 里有它且 `trashed` 为 `true` |
  | `Search_excludes_trash_when_asked` | `?includeTrash=false` → 搜不到 |
  | `Search_paginates_each_group` | `?limit=1&offset=1` → 每组只返回 1 条、`total` 不变、`hasMore` 正确 |
  | `Search_with_empty_query_returns_400` | 不带 `q`、或 `q=` → 400 `invalid_search_query` |
  | `Search_with_out_of_range_limit_returns_400` | `?limit=0` / `?limit=101` → 400 `invalid_pagination` |

- [ ] **Step 2: 跑测试确认它失败**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SearchEndpointTests"`
  Expected: 失败（端点还不存在）。

- [ ] **Step 3: 加 DTO 与错误码**

  - `server/IdeaApp/Contracts/SearchDtos.cs`：写上面定义的 `SearchPage<T>` 与 `SearchResponse`。
  - `server/IdeaApp/Contracts/Dtos.cs`：`ProjectDto` 末尾追加
    `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Trashed = null`。
  - `server/IdeaApp/Contracts/ApiErrorCodes.cs`：400 那组加
    `new("invalid_search_query", 400, "搜索关键词为空")`。
  - `server/IdeaApp/Contracts/JsonContext.cs`：加
    `[JsonSerializable(typeof(SearchPage<IdeaDto>))]`、`[JsonSerializable(typeof(SearchPage<ProjectDto>))]`、
    `[JsonSerializable(typeof(SearchPage<TagDto>))]`、`[JsonSerializable(typeof(SearchResponse))]`。
    ⚠️ **漏登记就是 AOT 产物 500** —— `dotnet test` 测不出来，只有 `server/scripts/aot-smoke.sh` 能测。

- [ ] **Step 4: 实现 `SearchRepository`**

  在 `server/IdeaApp/Infrastructure/SearchRepository.cs` 里实现三个方法。SQL 形状：

  - **想法**（`Ideas`）：
    ```sql
    -- 一页
    SELECT i.id, i.project_id, i.title, i.content, i.done, i.created_at, i.updated_at, i.deleted_at,
           p.name,
           bm25(idea_fts, 10.0, 1.0, 5.0, 3.0) AS score
    FROM idea_fts
    JOIN idea i ON i.rowid = idea_fts.rowid
    JOIN project p ON p.id = i.project_id
    WHERE idea_fts MATCH $match
      [AND i.deleted_at IS NULL]          -- includeTrash = false 时才加
    ORDER BY score ASC, i.id ASC          -- bm25 返回负值，越负越相关，ASC 即最相关在前
    LIMIT $limit OFFSET $offset;

    -- 总数（另跑一次）
    SELECT COUNT(*) FROM idea_fts JOIN idea i ON i.rowid = idea_fts.rowid
    WHERE idea_fts MATCH $match [AND i.deleted_at IS NULL];
    ```
    - `bm25()` 的权重按**列顺序**：`idea_fts` 的列序是 `title, content, tags, project` → 标题 10、正文 1、标签 5、项目 3。实测标题命中确实排最前。
    - 这一页想法的**标签名**要另跑一条批量查（`idea_tag JOIN tag`），按 id 集合查一次。
    - `Hits` 在 C# 里算：对每条命中的想法，用 `TextTokenizer.FindMatches` 分别对 `title`、`content`、标签名（空格连接）、项目名算一遍，`Field` 分别是 `"title"` / `"content"` / `"tags"` / `"project"`，命中位置直接用 `Contracts.SearchMatch`。
  - **项目**（`Projects`）：`project_fts JOIN project p ON p.rowid = project_fts.rowid WHERE project_fts MATCH $match [AND p.deleted_at IS NULL] ORDER BY bm25(project_fts) ASC, p.id ASC LIMIT ... OFFSET ...`。
  - **标签**（`Tags`）：`tag_fts JOIN tag t ON t.id = tag_fts.rowid WHERE tag_fts MATCH $match ORDER BY bm25(tag_fts) ASC, <使用次数> DESC, t.name ASC LIMIT ... OFFSET ...`。
    - 使用次数的口径与 `GET /api/tags` 一致：**只数未删除的想法**（`TagRepository.ListWithCounts` 里那条子查询）。

- [ ] **Step 5: 实现 `SearchService` 与端点**

  - `SearchService.Search(query, limit, offset, includeTrash)`：
    - `query` trim 后为空 → 抛 `new ApiException(400, "invalid_search_query", ...)`。
    - `TextTokenizer.BuildMatchExpression(query)` 得到 MATCH 表达式。**全是标点的查询词一律当作「没有关键词」→ 不过滤 → 三组都返回全部**（与 `/api/ideas` 的口径一致，也是规格 §9.2 与计划 Review Focus 第 4 条的要求）。
      ⚠️ **这里跟 `/api/ideas` 不同**：`/api/ideas` 的纯标点查询按「不过滤」处理（Review Focus 第 4 条），`/api/search` 同样处理 —— 全是标点的搜索词没有意义，返回空页更诚实。**两处口径必须一致**，按 `/api/ideas` 的口径来（不过滤 → 返回全部），规格 §11.3 的 `q` 只要求「trim 后为空」才 400。
    - 分别调三个仓储方法，组装成 `SearchPage<T>`（`HasMore = offset + items.Length < total`）。
  - `SearchEndpoints.cs`：
    - `app.MapGet("/api/search", (string? q, string? limit, string? offset, bool? includeTrash, SearchService service) => ...)`
    - 参数默认值：`limit` → 20、`offset` → 0、`includeTrash` → true（**`includeTrash` 默认是 `true`**）。
    - 返回 `Results.Json(response, IdeaJsonContext.Default.SearchResponse)` —— **必须显式带 `JsonTypeInfo`**，这是 AOT 的硬要求（漏了 AOT 产物 500，普通测试测不出来）。
    - `.Produces<SearchResponse>(StatusCodes.Status200OK)` 与 `.ProducesApiProblem(StatusCodes.Status400BadRequest, "invalid_pagination", "invalid_search_query")`。
    - ⚠️ **不要声明 415** —— `OpenApiDocumentTests.Only_endpoints_with_a_request_body_declare_415` 会查。
    - ⚠️ 用 `ApiResults.Run(...)` 包起来（与其它端点一致，它负责把 `ApiException` 转成 ProblemDetails）。
  - `Program.cs`：在 `app.MapIdeaEndpoints();` 附近加 `app.MapSearchEndpoints();`，并注册 `builder.Services.AddSingleton<SearchRepository>();` 与 `builder.Services.AddSingleton<SearchService>();`。

- [ ] **Step 6: 跑测试确认它们通过**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q`
  Expected: 全部 PASS。

- [ ] **Step 7: 跑 AOT 冒烟**

  Run: `bash server/scripts/aot-smoke.sh`
  Expected: PASS。**这一步不能省** —— `SearchResponse` 与三个 `SearchPage<T>` 漏登记 `[JsonSerializable]` 的话，普通测试全绿而 AOT 产物 500。

- [ ] **Step 8: 提交**

  ```bash
  git add server/IdeaApp/Contracts/SearchDtos.cs server/IdeaApp/Contracts/Dtos.cs \
          server/IdeaApp/Contracts/JsonContext.cs server/IdeaApp/Contracts/ApiErrorCodes.cs \
          server/IdeaApp/Infrastructure/SearchRepository.cs server/IdeaApp/Application/SearchService.cs \
          server/IdeaApp/Endpoints/SearchEndpoints.cs server/IdeaApp/Program.cs \
          server/IdeaApp.Tests/SearchEndpointTests.cs
  git commit -m "feat(search): 加 GET /api/search 全库搜索端点

  给 agent 用的统一入口：一次请求拿到想法、项目、标签三类命中，三组各自带
  分页信息。想法组按 bm25 相关度排（标题/标签命中权重高于正文），项目与
  标签组按各自的相关度排。

  默认含回收站 —— 它是「全库找东西」，东西被删进回收站不该凭空消失；
  每条结果标 trashed，agent 一眼能看出这条是不是已经删了。"
  ```

---

### Task 6: 契约同步（OpenAPI 快照、skill 说明书、主规格）

**Files:**
- Modify: `openapi/idea-v1.json`（**重新导出，不要手改**）
- Modify: `skills/idea-manager/references/api.md`
- Modify: `skills/idea-manager/references/errors.md`
- Modify: `skills/idea-manager/references/calling.md`
- Modify: `docs/superpowers/specs/2026-11-11-idea-manager-design.md`

**Interfaces:**
- Consumes: Task 4、Task 5 的端点与错误码
- Produces: 无代码

- [ ] **Step 1: 重新导出 OpenAPI 快照**

  Run: `dotnet run --project tools/ExportOpenApi`
  Expected: `openapi/idea-v1.json` 被更新（`/api/search` 出现、`GET /api/ideas` 多了 `q`/`limit`/`offset` 三个参数、错误码表里多了两条）。

  若导出失败或文件没变，先跑 `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~OpenApiDocumentTests"` 看差异。

- [ ] **Step 2: 更新 skill 的 `api.md`**

  在 `skills/idea-manager/references/api.md` 的端点速查表里：

  - 新增一行：`| GET /api/search | 全库搜索（想法/项目/标签三组，带分页） | \`q\`（必填）、\`limit\`、\`offset\`、\`includeTrash\` | 无 | 200 |`
  - `GET /api/ideas` 的「入参」列加上 `q`、`limit`、`offset`。
  - ⚠️ `SkillDocGuardTests.Manual_mentions_every_api_path_in_the_contract` 要求**契约里的每个 API 路径都在手册里出现** —— 漏了这条测试会红。

- [ ] **Step 3: 更新 skill 的 `errors.md`**

  在 `skills/idea-manager/references/errors.md` 的「## 服务端错误码」表里加两行：

  | code | 什么时候出现 | 怎么办 |
  |---|---|---|
  | `invalid_pagination` | `limit` / `offset` 超出允许范围 | 换小一点的 `limit`；`offset` 必须配 `limit` 一起给 |
  | `invalid_search_query` | `/api/search` 的 `q` 缺失或全是空白 | 给出非空的搜索词 |

  ⚠️ `SkillDocGuardTests.Manual_error_code_table_matches_the_registry` 要求这张表的 code 集合与 `ApiErrorCodes.All` **完全相等** —— 多一行少一行都红。

- [ ] **Step 4: 更新 skill 的 `calling.md`**

  加两个 `curl` 示例：一个 `GET /api/ideas?q=咖啡&limit=20`，一个 `GET /api/search?q=咖啡&limit=20`。

  另外在手册里写清三件 agent 容易踩的事：
  1. **英文是词级**：搜 `hel` 搜不到 `hello`，要输全词。
  2. **多段之间是 AND**：`咖啡 水温` 要求两个都出现，不要求相邻。
  3. **`/api/ideas` 与 `/api/search` 的 `offset` 含义不同**：前者是「往回翻历史」（从最新一端数），后者是「翻相关度页」（从最相关一端数）。

- [ ] **Step 5: 改写主规格里被推翻的条目**

  按 `docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md` 的 §17 表格，逐条改 `docs/superpowers/specs/2026-11-11-idea-manager-design.md` 的六处：§1.4 / §2.4（目标里的关键词描述）、§3（非目标里「不做服务端全文检索」那句，删掉）、§10.2（过滤）、§11.4（客户端行为）、§20（已定决定一览）。

  改法：在被改的地方加一句指向新规格的说明，例如
  `关键词过滤：服务端全文检索（见 specs/2026-11-15-backend-full-text-search-design.md）`。

- [ ] **Step 6: 跑守卫测试**

  Run: `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SkillDocGuardTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~ApiErrorCodeTests"`
  Expected: 全部 PASS。

- [ ] **Step 7: 提交**

  ```bash
  git add openapi/idea-v1.json skills/idea-manager/references/api.md \
          skills/idea-manager/references/errors.md skills/idea-manager/references/calling.md \
          docs/superpowers/specs/2026-11-11-idea-manager-design.md
  git commit -m "docs(search): 同步契约快照、skill 说明书与主规格

  OpenAPI 快照是导出来的不是手抄的 —— 抄出来的迟早和应用对不上。
  skill 手册要补三件 agent 容易踩的事：英文是词级、多段之间是 AND、
  两个端点的 offset 含义不同。

  主规格里「不做服务端全文检索」那条非目标作废，改成指向新规格。"
  ```

---

### Task 7: 前端过滤条件下沉与 SSE 数据流

**Files:**
- Modify: `web/src/api/types.ts`
- Modify: `web/src/lib/filter.ts`
- Modify: `web/src/stores/ideas.ts`
- Modify: `web/src/pages/HomePage.vue`
- Modify: `web/src/components/FilterBar.vue`（只改注释）
- Test: `web/src/lib/filter.spec.ts`、`web/src/stores/ideas.spec.ts`（新建）、`web/src/pages/HomePage.spec.ts`

**Interfaces:**
- Consumes: Task 4 的 `GET /api/ideas?q=&limit=&offset=`、`IdeaDto.Matches`
- Produces:
  ```ts
  // web/src/api/types.ts
  /** 一处命中：某个字段里的字符区间（UTF-16 code unit，与 JS 的 slice 对齐）。 */
  export interface SearchMatch {
    field: 'title' | 'content' | 'tags' | 'project';
    start: number;
    length: number;
  }

  // Idea 上追加：matches?: SearchMatch[] | undefined;
  //   （后端在不带关键词时把这个键整个省略，所以是可选字段。）
  ```
  ```ts
  // web/src/lib/filter.ts
  /** 把过滤条件转成 GET /api/ideas 的查询参数。 */
  export function toQueryParams(filter: IdeaFilter): Record<string, string | undefined>;
  ```
  ```ts
  // web/src/stores/ideas.ts
  /** 一页多少条。 */
  export const PAGE_SIZE = 100;

  /** 还有没有更旧的（往上翻历史）。 */
  export const hasMore = ref(false);

  /** 正在加载更旧的。 */
  export const loadingMore = ref(false);

  /**
   * 拉取想法列表并**整表替换**。
   * 过滤条件（项目/状态/标签/AND-OR/关键词）全部交给后端。
   * @param limit 请求多少条；不给就是 PAGE_SIZE + 1（多一条用来判断还有没有更旧的）。
   */
  export async function loadIdeas(filter: IdeaFilter, limit?: number): Promise<void>;

  /**
   * 往回翻：取**更旧**的一页，返回新取到的那些（**不改动列表**）。
   * 调用方负责把它们拼到列表前面，并在 DOM 更新后修正滚动位置。
   */
  export async function fetchOlderIdeas(filter: IdeaFilter, offset: number): Promise<Idea[]>;
  ```

- [ ] **Step 1: 写失败的测试**

  - `web/src/api/types.ts` 加类型后，`web/src/lib/filter.spec.ts` 加：
    | 测试 | 断言 |
    |---|---|
    | `toQueryParams_maps_all_four_filters` | `{projectId:'p',status:'open',tags:['a','b'],tagMode:'and',keyword:'咖啡'}` → `{projectId:'p',status:'open',tags:'a,b',tagMode:'and',q:'咖啡'}` |
    | `toQueryParams_omits_empty_values` | 没有关键词 → 结果里**没有** `q` 键（`undefined` 而不是空串） |
  - 新建 `web/src/stores/ideas.spec.ts`：
    | 测试 | 断言 |
    |---|---|
    | `loadIdeas_sends_all_filters_to_the_server` | mock `apiGet` → 断言调用参数里带上了 `status`/`tags`/`tagMode`/`q` |
    | `loadIdeas_sets_hasMore_when_the_page_is_full` | mock 返回 PAGE_SIZE+1 条 → `hasMore` 为 true；返回 PAGE_SIZE 条 → false |
    | `fetchOlderIdeas_uses_the_given_offset` | 断言请求里 `offset` 等于传入的值 |
  - `web/src/pages/HomePage.spec.ts`：
    | 测试 | 断言 |
    |---|---|
    | `filter_change_triggers_a_reload_after_the_debounce` | 改关键词 → 250ms 内**不**发请求，过了才发（用 fake timers） |
    | `sse_event_triggers_a_reload_that_keeps_the_loaded_depth` | 触发一条 SSE 事件 → 断言重拉请求的 `limit` ≥ 已加载条数 |
    | `visible_ideas_are_not_filtered_again_on_the_client` | 服务端返回 2 条（其中 1 条不匹配关键词）→ 列表显示 **2** 条（显示层不再过滤） |

- [ ] **Step 2: 跑测试确认它们失败**

  Run: `cd web && npm test -- src/lib/filter.spec.ts src/stores/ideas.spec.ts src/pages/HomePage.spec.ts`
  Expected: 失败。

- [ ] **Step 3: 加类型与 `toQueryParams`**

  - `web/src/api/types.ts`：加 `SearchMatch` 接口；`Idea` 接口加 `matches?: SearchMatch[];`（**可选**，因为后端在不带关键词时把这个键整个省略）。
  - `web/src/lib/filter.ts`：实现 `toQueryParams`：
    - `projectId` → 有值才给；
    - `status` → 有值才给（`'all'` 也给，后端认）；
    - `tags` → 逗号连接的串，空数组不给；
    - `tagMode` → 有值才给；
    - `keyword` → trim 后非空才给，键名是 **`q`**。
  - `matchesKeyword` / `matchesFilter` / `hasActiveFilter` **语义都不变**（`matchesFilter` 仍然判 keyword —— SSE 增量条目靠它，规格 §13.6）。

- [ ] **Step 4: 改造 store**

  在 `web/src/stores/ideas.ts` 里：

  - `loadIdeas(filter: IdeaFilter, limit = PAGE_SIZE + 1)`：
    ```ts
    const params = toQueryParams(filter);
    params.limit = String(limit);
    const result = await apiGet<Idea[]>('/ideas', params);
    ideas.value = result;
    hasMore.value = result.length >= limit;
    ```
    - 原来 `loadIdeas(projectId)` 的调用方要改（见 Step 5）。
    - `loading` 的置位/复位保持现状。
  - `fetchOlderIdeas(filter, offset)`：
    ```ts
    loadingMore.value = true;
    try {
      const params = toQueryParams(filter);
      params.limit = String(PAGE_SIZE + 1);
      params.offset = String(offset);
      const result = await apiGet<Idea[]>('/ideas', params);
      hasMore.value = result.length > PAGE_SIZE;
      return result.slice(0, PAGE_SIZE);
    } finally {
      loadingMore.value = false;
    }
    ```
  - `applyLocalUpsert` / `removeLocal` 保持不变。

- [ ] **Step 5: 改造 `HomePage.vue`**

  - **过滤条件下沉**：`visibleIdeas` 不再过滤，只排序：
    ```ts
    const visibleIdeas = computed(() => sortIdeas(ideas.value));
    ```
    （服务端返回的数组已经是过滤过的；显示层再按近似的关键词判断过滤一遍，会把服务端判定命中的条目**误杀** —— 前端的子串判断比服务端的词元相邻更严格。）
  - **`datasetFilter` 移除**，`applyEvent` 改用 `activeFilter`：
    - `applyEvent(ideas.value, event, datasetFilter.value)` → `applyEvent(ideas.value, event, activeFilter.value)`。
    - `datasetFilter` 的 computed 整个删掉。
  - **防抖重拉**：加一个防抖 250ms 的重拉入口：
    ```ts
    /** 过滤条件变了 → 防抖后重拉（一次请求拿最终结果）。 */
    let reloadTimer: ReturnType<typeof setTimeout> | undefined;
    function scheduleReload(): void {
      if (reloadTimer !== undefined) clearTimeout(reloadTimer);
      reloadTimer = setTimeout(() => { void reloadList(() => loadIdeas(activeFilter.value)); }, 250);
    }
    ```
    - `watch(activeFilter, scheduleReload)`（`activeFilter` 是 computed，`filter.value` 整体替换 → 身份变了 → watch 会触发）。
    - `switchProject` 里原本调 `loadIdeas(next)` 的地方改成 `loadIdeas({ ...activeFilter.value, projectId: next })`。
    - `onResync` 里原本绕过 `loadIdeas` 直接 `apiGet` 的地方改成走 `loadIdeas`（它要带上全部过滤条件）。
  - **SSE 触发的重拉保留已加载深度**：在 `applyEventToList` 里，SSE 事件处理完之后调 `scheduleReload()`；重拉时用
    `loadIdeas(activeFilter.value, ideas.value.length + PAGE_SIZE + 1)`。
    - `loadIdeas` 的 `limit` 参数要多传一个值 —— 所以 `scheduleReload` 要能区分「条件变了」（重置到一页）与「SSE」（保留深度）。**用两个入口**：
      - `scheduleReload()`（条件变化）→ `loadIdeas(activeFilter.value)`；
      - `scheduleResync()`（SSE）→ `loadIdeas(activeFilter.value, ideas.value.length + PAGE_SIZE + 1)`。
  - **过滤条件变化后滚到底部**：条件变了命中集合也变了，之前的浏览深度没有意义 → 重拉后 `await nextTick(); scrollToBottom();`。

- [ ] **Step 6: 更新 `FilterBar.vue` 的注释**

  过滤栏仍然「输入即 emit」（`onKeywordInput` 不变）—— **防抖在重拉入口**，不在这里。改掉那段说「过滤在客户端做」的注释，改成「过滤在服务端做（设计文档 §11.1），这里只负责把输入报上去」。

- [ ] **Step 7: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。
  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 8: 提交**

  ```bash
  git add web/src/api/types.ts web/src/lib/filter.ts web/src/stores/ideas.ts \
          web/src/pages/HomePage.vue web/src/components/FilterBar.vue \
          web/src/lib/filter.spec.ts web/src/stores/ideas.spec.ts web/src/pages/HomePage.spec.ts
  git commit -m "feat(web): 过滤条件下沉到服务端，SSE 增量走近似判断

  之前是前端把范围内全部想法拉回来在内存里筛。现在项目/状态/标签/关键词
  一次请求拿最终结果，标签过滤仍在后端内存做（IdeaFilter 那套纯函数不动）。

  显示层不再按关键词过滤：前端的子串判断比服务端的词元相邻更严格，
  再过滤一遍会把服务端判定命中的条目误杀（正文「咖 啡」搜「咖啡」）。
  SSE 增量条目仍由前端用近似判断决定显不显示，随后防抖 250ms 重拉后端
  权威结果 —— 多端同步的即时性保住，代价是英文上个别条目先出现再被收回。"
  ```

---

### Task 8: 往回翻历史的无限滚动

**Files:**
- Modify: `web/src/pages/HomePage.vue`
- Modify: `web/src/components/MessageList.vue`（哨兵元素的位置）
- Test: `web/src/pages/HomePage.spec.ts`

**Interfaces:**
- Consumes: Task 7 的 `fetchOlderIdeas`、`hasMore`、`loadingMore`
- Produces: 无新公开类型

- [ ] **Step 1: 写失败的测试**

  在 `web/src/pages/HomePage.spec.ts` 追加：

  | 测试 | 断言 |
  |---|---|
  | `scroll_to_the_top_loads_older_ideas` | 滚到接近顶部 → 发出 `offset = 已加载条数` 的请求，返回的更旧的条目被**拼到列表前面** |
  | `prepending_older_ideas_keeps_the_viewport_in_place` | prepend 之后 `scrollTop` 增加了新增内容的高度（视口没跳） |
  | `no_more_older_ideas_stops_the_requests` | `hasMore` 为 false 时不再发往回翻的请求 |

  > jsdom 里没有真实的布局（`scrollHeight` / `clientHeight` 都是 0），所以「视口不跳」这条要**stub** 掉 `scrollHeight` / `clientHeight` / `scrollTop` 再断言。

- [ ] **Step 2: 跑测试确认它们失败**

  Run: `cd web && npm test -- src/pages/HomePage.spec.ts`
  Expected: 失败。

- [ ] **Step 3: 加哨兵与预加载**

  在 `web/src/pages/HomePage.vue` 里：

  - **哨兵元素**放在消息流的**顶部**（往回翻的方向是上）。`MessageList.vue` 的列表根部加一个 `<div ref="historySentinel" class="home__history-sentinel" aria-hidden="true"></div>`，或者在 `HomePage.vue` 的 `<MessageList>` 之前放一个。
  - **`IntersectionObserver`**：
    ```ts
    // 往回翻的哨兵：接近顶部就预加载更旧的一页。
    let observer: IntersectionObserver | undefined;
    onMounted(() => {
      observer = new IntersectionObserver(
        (entries) => { if (entries.some(e => e.isIntersecting)) void loadOlder(); },
        { root: scrollContainer.value, rootMargin: '400px 0px 0px 0px' },
      );
      if (sentinel.value) observer.observe(sentinel.value);
    });
    onBeforeUnmount(() => observer?.disconnect());
    ```
    - `rootMargin` 的 `'400px 0px 0px 0px'` = **顶部提前 400px** 就触发（列表遍历方向的尽头是顶部，用户说的「快到底时预加载」在这里就是「快到顶时预加载」）。
  - **`loadOlder()`**：
    ```ts
    async function loadOlder(): Promise<void> {
      if (loadingMore.value || !hasMore.value) return;   // 防重复触发
      const before = scrollContainer.value?.scrollHeight ?? 0;
      const older = await fetchOlderIdeas(activeFilter.value, ideas.value.length);
      if (older.length === 0) { hasMore.value = false; return; }
      ideas.value = [...older, ...ideas.value];
      await nextTick();
      // prepend 把内容插到顶部，视口会跟着往下推 —— 把它钉回原来那条。
      const el = scrollContainer.value;
      if (el) el.scrollTop += el.scrollHeight - before;
    }
    ```
  - 初始加载：走 `loadIdeas` 拿最新一页之后**滚到底部**（沿用现有的 `scrollToBottom()`；`init()` 里已经有）。

- [ ] **Step 4: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。
  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 5: 提交**

  ```bash
  git add web/src/pages/HomePage.vue web/src/components/MessageList.vue web/src/pages/HomePage.spec.ts
  git commit -m "feat(web): 往回翻历史的无限滚动，接近顶部就预加载

  看板本来就是聊天软件式的（打开页面停在最下边，底部是当下），所以分页
  方向是往上：第一次拿最新一页，接近顶部时预加载更旧的一页，prepend 到列表
  顶部。

  prepend 会把内容插到视口上方，得把 scrollTop 补回去，否则用户正在看的那条
  会往下跳 —— 预加载的意义就反了。"
  ```

---

### Task 9: 高亮渲染

**Files:**
- Create: `web/src/lib/highlight.ts`
- Create: `web/src/lib/highlight.spec.ts`
- Modify: `web/src/components/MessageItem.vue`

**Interfaces:**
- Consumes: Task 4 的 `SearchMatch`、Task 7 的 `Idea.matches`
- Produces:
  ```ts
  // web/src/lib/highlight.ts
  /** 一段文本及其是否命中。 */
  export interface HighlightSegment {
    text: string;
    marked: boolean;
  };

  /** 一行（按 \n 切开）及其片段。 */
  export interface HighlightLine {
    /** 这一行在原文里的起始偏移（UTF-16 code unit）。 */
    start: number;
    /** 这一行的片段序列。 */
    segments: HighlightSegment[];
  };

  /**
   * 把原文按行切开，并在每行内按命中区间标出片段。
   * 命中区间相对**完整原文**（后端给的就是这个基准）。
   * 没有命中时返回每行一个未命中的片段。
   */
  export function highlightLines(text: string, matches: SearchMatch[]): HighlightLine[];

  /**
   * 把片段序列按「第一个非空行」分成视觉标题与正文两组。
   * 标题为空时，组件会把第一个非空行摘出来当视觉标题 ——
   * 落在这行上的命中要跟着标题走，不能错切到正文里。
   */
  export function splitTitleAndBody(lines: HighlightLine[]): { title: HighlightSegment[]; body: HighlightSegment[] };
  ```

- [ ] **Step 1: 写失败的测试**

  创建 `web/src/lib/highlight.spec.ts`：

  | 测试 | 断言 |
  |---|---|
  | `highlightLines_marks_the_matching_range` | 原文「咖啡机滤芯」，命中 `{field:'content',start:0,length:2}` → 第一行只有一个片段，文本是「咖啡机滤芯」，`marked` 为 true |
  | `highlightLines_splits_unmatched_text` | 原文「买咖啡机」，命中 start=1,length=2 → 片段是 `[{text:'买',marked:false},{text:'咖啡',marked:true},{text:'机',marked:false}]` |
  | `highlightLines_offsets_are_relative_to_the_whole_text` | 原文「第一行\n第二行咖啡」，命中 start=7,length=2 → 第二行的片段是「咖啡」且 `marked` 为 true（第一行的片段全未命中） |
  | `highlightLines_with_no_matches_returns_plain_lines` | 无命中 → 每行一个未命中片段 |
  | `splitTitleAndBody_puts_the_first_non_empty_line_in_the_title` | 两行「咖啡」「水温」→ title 是「咖啡」，body 是「水温」 |
  | `splitTitleAndBody_skips_leading_blank_lines` | 第一行是空行 → title 是第二个非空行，body 是它之后的 |

- [ ] **Step 2: 跑测试确认它们失败**

  Run: `cd web && npm test -- src/lib/highlight.spec.ts`
  Expected: 失败（模块不存在）。

- [ ] **Step 3: 实现 `highlight.ts`**

  - `highlightLines(text, matches)`：
    - 先按 `\n` 把原文切成行，每行记下它在原文里的起始偏移（累加行长 + 1）。
    - 每行内按落在该行区间内的命中区间切片段（区间先合并再切）。
    - 没有命中时每行产出一个 `marked: false` 的片段。
  - `splitTitleAndBody(lines)`：
    - 找第一个「trim 后非空」的行 → 它的片段进 `title`；其余进 `body`。
    - `title` 的首尾片段要去掉首尾空白（组件的 `visualTitle` 是 `trim` 过的）—— 空白里不会有命中，所以 trim 不会切掉标记。
    - `body` 要跳过开头的空行（组件的 `bodyContent` 去掉首行后的开头换行）。

- [ ] **Step 4: 在 `MessageItem.vue` 里渲染**

  - 现有 `visualTitle` / `bodyContent` 两个 computed 的**取文本**逻辑不变，只是改成同时产出片段：
    - `idea.title` 有值 → 标题用 `idea.title` 配 `field === 'title'` 的命中；正文用整段 `content` 配 `field === 'content'` 的命中。
    - `idea.title` 为空 → 标题与正文都从 `content` 切出来，用 `splitTitleAndBody` 分组（`field === 'content'` 的命中）。
  - 模板里把正文从「一段文本」改成「片段序列」：
    ```html
    <template v-for="(segment, index) in bodySegments" :key="index">
      <mark v-if="segment.marked">{{ segment.text }}</mark>
      <template v-else>{{ segment.text }}</template>
    </template>
    ```
  - **不要用 `v-html`** —— Vue 的文本插值自带转义，走 `v-html` 等于把正文变成 HTML 注入面。
  - 没有 `matches`（SSE 增量条目、没有关键词的列表）时，片段就是整段未命中文本，渲染结果与今天一致。

- [ ] **Step 5: 跑测试确认它们通过**

  Run: `cd web && npm test`
  Expected: 全部 PASS。
  Run: `cd web && npm run typecheck`
  Expected: 无错误。

- [ ] **Step 6: 提交**

  ```bash
  git add web/src/lib/highlight.ts web/src/lib/highlight.spec.ts web/src/components/MessageItem.vue
  git commit -m "feat(web): 搜索命中的高亮渲染

  命中位置由服务端给（字符区间），前端按区间把正文切成片段，命中的那几段
  包 <mark>。不用 v-html —— 文本插值自带转义，走 v-html 等于把正文变成
  HTML 注入面。

  标题为空时组件会把正文第一个非空行摘出来当视觉标题，所以命中要跟着行
  分组：落在那行上的命中归标题，不能错切到正文里。"
  ```

---

### Task 10: e2e 用例与覆盖矩阵

**Files:**
- Modify: `web/e2e/fixtures.ts`（`ListIdeasParams` 加 `q`/`limit`/`offset`）
- Modify: `web/e2e/ideas.spec.ts`（追加搜索用例）
- Create: `web/e2e/search.spec.ts`（新的搜索用例文件）
- Modify: `web/e2e/COVERAGE.md`

**Interfaces:**
- Consumes: Task 4、Task 5 的端点、Task 7–9 的前端行为
- Produces: 无代码

- [ ] **Step 1: 给 e2e 的 API 客户端加参数**

  在 `web/e2e/fixtures.ts` 的 `ListIdeasParams` 里加 `q?: string; limit?: number; offset?: number;`，`listIdeas` 里对应地拼进 `URLSearchParams`。

- [ ] **Step 2: 追加 e2e 用例**

  **现有 4 条搜索用例（`web/e2e/ideas.spec.ts:588/601/614/625`）的行为口径必须保持**：大小写不敏感、搜不到显示空态、清空输入恢复全部、输入即过滤。改完前端后**这 4 条要仍然通过** —— 它们是这次改造的回归网。

  新增（放在新的 `web/e2e/search.spec.ts` 里）：

  | 用例 | 断言 |
  |---|---|
  | 搜索：能搜到标题里的词 | 造一条带标题的想法 → 搜标题里的词 → 只剩那一条 |
  | 搜索：能搜到标签名 | 造一条带标签「咖啡」的想法 → 搜「咖啡」→ 命中 |
  | 搜索：能搜到项目名 | 造一个项目「工作复盘」+ 里面一条想法 → 搜「工作复盘」→ 命中 |
  | 搜索：结果带高亮 | 搜中的那条正文里有 `<mark>` |
  | 搜索：英文是词级 | 搜 `hel` 搜不到 `hello`；搜 `hello` 能搜到 |
  | 往回翻：接近顶部自动加载更旧的一页 | 造够多条 → 滚到接近顶部 → 列表变长 |
  | 往回翻：打开页面停在最下边 | 最新一条在视口内（断言它可见） |
  | 搜索：多段之间是 AND | 正文「咖啡…水温」→ 搜「咖啡 水温」→ 命中 |

  > e2e 的用例**必须** `import { test, expect } from './fixtures'`（不能从 `@playwright/test` 导入），否则每例自动复位不生效。

- [ ] **Step 3: 跑 e2e**

  Run: `bash web/e2e/run-e2e.sh --fresh`
  Expected: 全部 PASS。

  如果「往回翻」那条在 jsdom/Playwright 里不稳定（滚动容器的布局），先确认哨兵的 `IntersectionObserver` 在真实浏览器里被触发了，再考虑把断言放宽到「发出过带 offset 的请求」。

- [ ] **Step 4: 更新覆盖矩阵**

  在 `web/e2e/COVERAGE.md` 里：
  - 更新「一、概览」里的用例总数与文件数；
  - 在「二、覆盖矩阵」里补上新增的搜索相关行（页面 | 功能点 | 覆盖它的用例 | 备注），用例列的格式是 `文件名.spec.ts:行号`；
  - 在「三、漏掉的功能点」里把已经补上的项删掉。

  ⚠️ 现有的覆盖矩阵头部写「196 条用例」而最近的 git log 是「更新到 194 条」—— **先按实际的用例数对齐**，不要照抄任何一个数字。

- [ ] **Step 5: 跑全闸门**

  Run: `bash verify.sh`
  Expected: 四步全过（后端测试 / 取件器单测 / AOT 冒烟 / 取件器测试脚本）。

  Run: `cd web && npm test && npm run typecheck`
  Expected: 全部 PASS。

- [ ] **Step 6: 提交**

  ```bash
  git add web/e2e/fixtures.ts web/e2e/ideas.spec.ts web/e2e/search.spec.ts web/e2e/COVERAGE.md
  git commit -m "test(e2e): 补上后端搜索的端到端用例

  标题/标签/项目名能搜到、命中带高亮、英文是词级、多段之间是 AND、
  往回翻历史时接近顶部自动加载更旧的一页。

  现有那 4 条搜索用例（大小写不敏感 / 空态 / 清空恢复 / 输入即过滤）继续
  作为回归网 —— 改造后行为口径不变。"
  ```
