# 后端全文搜索 · 设计文档

- 日期：2026-11-15
- 状态：待评审
- 技术栈：.NET 10 Native AOT + Vue 3 + SQLite（FTS5）
- 关联文档：`2026-11-11-idea-manager-design.md`（主规格，本文档是它的增量）

---

## 1. 意图与背景

主规格 §10.2 与 §20 把「关键词过滤」定为**客户端子串匹配**，并把「不做服务端全文检索」写进非目标。本文档**推翻**那一条决定。

推翻的理由是三条，都是实际卡点，不是洁癖：

1. **浏览器扛不住**。现在前端把当前项目范围内的**全部**想法拉回来，在内存里逐条做 `content.includes(needle)`。正文一多，传输、内存、渲染三头都涨。
2. **搜不到的东西太多了**。现在只匹配 `idea.content`。标签名、项目名都搜不到 —— 而这两样恰恰是用户脑子里的检索入口（「我给那条打的是咖啡吧」「上次那个叫工作复盘的项目」）。
3. **agent 是瞎的**。`skills/idea-manager/references/api.md` 里 `GET /api/ideas` 只认 `projectId`/`status`/`tags`/`tagMode`，agent 想找一条想法只能把列表整个拉回来自己扫。

顺带把「架构上对」也办了：数据在库里，就该让库来检索，而不是先全量捞出来在内存里翻。

参考对象是**微信的全文搜索**（§6 有出处）：它的本地搜索就是 SQLite FTS + 自研分词器，不是 Elasticsearch。这条路线在几亿人身上验证过，值得照抄它的骨架。

## 2. 目标

| # | 目标 |
|---|---|
| G1 | 服务端全文检索：SQLite FTS5 索引，**中文按 token 相邻**（等价于子串，但标点与空白不算 token、不挡路）、**英文词级** |
| G2 | 检索范围覆盖**正文、标签名、项目名** |
| G3 | 两个端点：`GET /api/ideas?q=`（页面筛）、`GET /api/search?q=`（agent 全库找东西） |
| G4 | 过滤条件下沉：项目 / 状态 / 标签 / AND-OR 模式 / 关键词**一次请求**拿最终结果 |
| G5 | 命中位置回传：字符偏移，前端渲染 `<mark>` |
| G6 | 分页：agent 端点带完整分页信息；页面端点支持可选 `limit`/`offset`（**往回翻历史**），前端**无限滚动 + 提前预加载** |
| G7 | 契约同步：OpenAPI 快照、skill 说明书、覆盖矩阵跟上 |

## 3. 非目标

- **不做**拼音 / 多音字搜索（微信有，见 §6.3，我们不做）。
- **不做**语义 / 向量搜索（不引入 `sqlite-vec`，不引入嵌入模型）。
- **不做**词典分词（不引入 jieba 及任何中文分词库）。
- **不做**搜索历史、最近搜索、搜索建议下拉。
- **不做**跨项目库 / 跨实例搜索。
- **不做**给搜索结果单独的页面（前端仍用现有的过滤栏）。

## 4. 反目标（做到这些就算失败）

- **不能牺牲中文的子串语义去换「像搜索引擎」。** 用户搜「咖啡」必须能搜到正文里写着「咖啡机滤芯」的那条。
- **不能因为索引坏了就把主功能拖下水。** 索引写入与业务写入同事务，但索引表在启动时就建好，不存在「忘了建表」这种运行时故障。
- **不能引入依赖反射 / 动态代码生成 / 运行时表达式编译的库。** 主规格 §4 的 AOT 红线继续有效。
- **不能引入新的 NuGet 依赖。** 分词器自己写。
- **不能让前端出现两套互相打架的匹配语义。** 客户端保留的近似判断只服务于 SSE 增量条目的即时显示，最终以服务端结果为准（§13.4）。

## 5. 约束

| 约束 | 来源 |
|---|---|
| .NET 10 Native AOT，`TrimMode=full`，`InvariantGlobalization=true` | 主规格 §4；意味着**不能用 ICU 分词器**，也不能用依赖 ICU 的库 |
| 手写 SQL，不引 ORM | 主规格 §8；意味着不能靠 EF 之类的自动维护索引 |
| 所有端点返回的静态类型必须在 `IdeaJsonContext` 里注册 | `IdeaEndpoints.cs` 顶部注释；否则 AOT 产物 500，`dotnet test` 测不出来 |
| `schema.sql` 按 `;` 切分语句 | `SqliteDatabase.cs:132`；**`CREATE TRIGGER` 的 `BEGIN…END;` 会被切坏**，所以本设计不用触发器 |
| 没有迁移框架，只有幂等 `schema.sql` | 新表用 `IF NOT EXISTS`；旧库靠一次性回填 |
| 错误码必须登记进 `ApiErrorCodes.All` | `ApiErrorCodeTests.cs:107` 会扫源码里的 `new ApiException(...)` |
| OpenAPI 快照与实际必须一致 | `OpenApiDocumentTests.cs` |
| skill 说明书与代码必须一致 | `SkillDocGuardTests.cs` |
| 索引表 / 索引的存在性有断言 | `DatabaseInitTests.cs:40-59` 断言了 7 张表 + 3 个索引，加表要同步加断言 |
| AOT 退化只有冒烟脚本能测出来 | `server/scripts/aot-smoke.sh` |
| e2e 覆盖矩阵的用例计数要同步 | `web/e2e/COVERAGE.md` |
| 前端过滤栏**输入即过滤**（无回车） | `FilterBar.vue:41-46` 现状 + e2e `ideas.spec.ts:588` 依赖这个手感 |

## 6. 调研结论

### 6.1 微信是怎么做全文搜索的

有两条一手来源，结论一致：**微信本地搜索 = SQLite FTS + 自研分词器**，不是 Elasticsearch。

- **PC 端**（逆向拿到的建表 SQL 快照）：
  ```sql
  CREATE VIRTUAL TABLE FTSChatMsg2 USING fts4(
      content, entityId, notindexed=entityId,
      tokenize=mmTokenizer "porter");
  CREATE TABLE FTSChatMsg2_MetaData(docid INTEGER PRIMARY KEY, msgId INTEGER, entityId INTEGER, ...);
  ```
  索引放在**独立库文件**（`FTSMsg.db` / `FTSContact.db` / `MultiSearchChatMsg.db`），每张 FTS 虚表配一张 `*_MetaData` 做 docid → 业务主键映射。
  出处：GitHub 仓库 `mlengin/-` 的 `数据库/` 目录（默认分支 `2.8.0.112`）下的 `FTSMsg.txt` / `FTSContact.txt` / `MultiSearchChatMsg.txt` —— 这三份是各库 `sqlite_master` 的**原文快照**。本设计引用的建表语句已从快照逐字核对过。
  链接：<https://github.com/mlengin/-/blob/2.8.0.112/数据库/FTSMsg.txt>
- **移动端**（微信官方文章《微信手机端的本地数据全文检索优化之路》）：
  - 全文搜索 2014-01-26 v5.4 上线，最初用 **SQLite FTS4**；后升级到 **FTS5 + 自定义辅助函数**（`MMHighLight` 算高亮偏移、`MMRank` 把多个排序因子打包成 64 位整数下推到 SQL、`MessageCount` 做会话内命中聚合）。
  - 分词规则原文：**「英文和数字合并分词，非英文和数字单独分词」**，例 `Hello520中国` → `Hello` / `520` / `中` / `国`。
  - 排序原文：**不按文档相关性排序**，按业务因子。
  出处：<https://cloud.tencent.com/developer/article/1006159>
- **PC 端分词器的第三方复刻**（Rust，C ABI 对齐 FTS5 tokenizer 接口）确认了规则：
  > ASCII letters → one token；ASCII digits → one token；**CJK / 多字节 / 辅助平面 → 每个码位一个 token**；Symbols → 可选。
  出处：<https://github.com/pandorafuture/wx-cli/blob/main/crates/wx-context/src/tokenizer.rs>
- **服务端「搜一搜」**是另一套：自研**内存**检索引擎 **ZeroSearch**（不是 ES），L0/L1/L2 三段打分，文本队列与向量队列在 L1/L2 之间融合。分词词典与 BM25 权重**没有公开**。
  出处：<https://cloud.tencent.com/developer/article/1877447>、<https://cloud.tencent.com/developer/article/1739482>

**我们照抄的骨架**：SQLite FTS + 自研分词器 + 英文合并 / 中文逐字 + 索引与业务数据同库同事务（我们不用微信的读写分离，见 §6.4）。

**我们不照抄的**：独立索引库（我们同库，强一致更简单）、`MMRank` 整数打包排序（我们数据量小，`bm25()` 够用）、拼音字表。

### 6.2 SQLite FTS5 处理中文的实测约束

在**本项目真正依赖的** SQLite（`Microsoft.Data.Sqlite` 10.0.14 → SQLitePCLRaw 2.1.13 → SQLite **3.53.3**，`ENABLE_FTS5` 已编译）上实测（探针是本机临时脚本，不进仓库；结论已逐条写进本文档）：

| 方案 | 实测结果 |
|---|---|
| FTS5 默认 `unicode61` | 整句中文 `手冲咖啡的关键在于水温与注水速度` 被切成 **1 个 token**；`MATCH '咖啡'` → **0 行**；`MATCH '手冲咖啡'` → **0 行**。中文直接不可用 |
| `unicode61` + 英文 | `MATCH 'hello'` 能命中 `Hello`（大小写折叠生效） |
| FTS5 `trigram` | `MATCH '咖啡'`（2 字）→ **0 行**；`MATCH '咖啡的'`（3 字）→ 命中。`content LIKE '%咖啡%'` → 命中且走索引（`EXPLAIN` 显示 `INDEX 0:L0`）。`bm25()` 可用但统计的是 3-gram 而非词，排序失真 |
| 逐字切分 + `unicode61` | `MATCH '"咖 啡"'` → 命中；`MATCH '"水 温"'` → 命中；多词 `("咖 啡") AND ("水 温")` → 命中。**代价**：`highlight()` 输出带空格（`手 冲 <b>咖 啡</b> 的 …`）；英文被合并成词后前缀搜不到（`"hello"` 匹配不到 `hello520`） |
| `bm25()` 符号 | 官方明确：FTS5 内部乘了 -1，返回**负值，越负越相关**；`ORDER BY bm25(t)` **不加 `DESC`** |
| 裸 `LIKE` 扫描 | 2000 条正文，`LIKE '%咖啡%'` 耗时 **< 1ms** |

**结论**：选「逐字切分 + `unicode61`」。理由：

1. `unicode61` 直接用对中文完全无效（已实测）。
2. `trigram` 的 `MATCH` 查不了 2 字以下的词 —— 而「项目」「想法」「看板」都是 2 字，是这个应用最高频的搜索词。改用 `LIKE` 绕过会让 `bm25()` 排序失真，且与「按相关度排」的目标冲突。
3. 逐字切分后，中文语义 = 短语查询 = **token 相邻**。中文正文里标点与空白是书写痕迹，不该挡路 —— 详见 §7.4 的「与今天的 `includes()` 的差异」表。英文走词级（§4 的取舍）。

**FTS5 没有 `offsets()`**（官方附录明说，那函数属于 FTS3/4）→ 命中位置必须自己算（§12）。这也顺带避开了微信踩过的坑：他们的文章说 `offsets()` 曾占单次循环 70%+ 耗时。

### 6.3 微信的定制带来的限制（我们据此划定非目标）

来自微信官方文章与社区实测的已知限制：

- **按字分词 → 词序颠倒搜不到**：搜「欢迎你北京」匹配不到「北京欢迎你」。
  → 我们的对策：查询按**空白**切成若干段，**段内要求 token 相邻、段间只要求都出现**（§9.3）。所以搜「北京 天气」能命中「北京今天的天气」；但搜一整段「北京今天的天气」时要求这七个 token 连续相邻（「的」会打断）。
- **多音字任意读音都能搜到**、**中拼音不能混搜**、**拼音索引只覆盖联系人昵称**。
  → 我们不做拼音（§3）。
- **索引与业务数据异步同步 → 可能搜到不完整的数据**；**跨库读写分离无法保证事务原子性**。
  → 我们同库同事务，**不存在**这个问题（§6.4）。
- **「搜索逻辑中不能使用分段查询，因为分段需要对结果排序，排序就要遍历所有结果，分段查询并不能降低搜索耗时」**（微信官方文章原文）。
  → 直接决定了 §11 的分页设计：按相关度排序的分页**只省传输，不省检索**。

### 6.4 我们和微信的架构差异

| | 微信 | 我们 | 理由 |
|---|---|---|---|
| 索引位置 | 独立 DB 文件，读写分离，EventBus 异步同步 | **同一个库文件**，与业务写入**同一个事务** | 同库才能保证原子性。微信那套在亿级数据下是为了分库分片，我们没有那个量级；同库的代价是索引会拖慢一点写入，可以接受 |
| 索引与业务的一致性 | 弱（异步，可能搜到不完整数据） | **强**（同事务） | 我们的规模不需要牺牲一致性 |
| 索引维护方式 | 服务端异步 | **仓储层同步维护**（§10） | 避免改 `schema.sql` 的分号切分逻辑（§5） |
| 排序 | 业务因子打包成整数下推 SQL | `bm25()` + id 兜底 | 数据量小，`bm25()` 够用且可解释 |

## 7. 匹配语义

### 7.1 分词规则（微信规则）

自写一个分词器，**不依赖任何原生扩展**（AOT + 不能注册自定义 tokenizer）。

规则，对**索引侧文本**和**查询侧文本**完全一致：

1. **token 字符 = Unicode 一般类别以 `L` 或 `N` 开头的字符**（字母与数字）。其余一律是**分隔符，丢弃** —— 标点、符号、`Cf`（零宽连接符、变体选择符）、`Cc`（控制字符）、`Mn`（组合记号）全都丢。
2. **连续的 ASCII 字母 / 数字**合并成**一个** token，并转小写。`Hello520` → `hello520`。
3. **其余的 token 字符逐字成一个 token**。`世界` → `世`、`界`。
4. **空白**是分隔符，不产生 token。

> **第 1 条的边界是量出来的，不是猜的。** FTS5 的 `unicode61` 划 token 字符的依据与「一般类别以 `L` 或 `N` 开头」**基本一致** —— 实测：`中`(Lo)、`é`(Ll)、`ʰ`(Lm)、`0`(Nd)、`①`(No)、`〇`(Nl) 是 token 字符；`，`(Po)、`—`(Pd)、`$`(Sc)、`→`(Sm)、`😀`(So)、ZWJ(Cf)、变体选择符(Cf)、C1 控制字符(Cc) 都是分隔符。
>
> **我们保留的字符集合基本是 unicode61 的子集** → 「FTS 命中了、但算不出高亮区间」这一类偏差基本不会发生。规格 §12.2 的那句承诺靠这条成立。
>
> **两处分叉，方向分别是安全的与可忽略的**：
> - unicode61 把 `Mn`（组合记号）当**词内附加符**保留，而**我们丢弃** —— 我们丢掉的字符 unicode61 根本看不到（索引文本里已经没有它），不影响一致性。
> - unicode61 把 **21 个 `Lo` 码点**（New Tai Lue 元音符号 `U+19B0..U+19C0`、`U+19C8`、`U+19C9`；Vedic 符号 `U+1CF2`、`U+1CF3`）当**分隔符**，而 .NET 判它们是 `Lo`（我们保留）—— **只有这 21 个码点上仍可能出现「FTS 命中却算不出高亮」**。它们冷门到不会有人输入，认了。

**遍历单位是 Unicode 标量值（rune），不是 UTF-16 code unit** —— 否则 emoji（代理对）会被切成两个半截 token。

**实现要点**（`InvariantGlobalization=true` 下全部可用，不依赖 ICU）：

- 遍历用 `string.EnumerateRunes()`；判定用 `Rune.GetUnicodeCategory(rune)`（看一般类别是不是 `L*`/`N*`）。
- ⚠️ **`Rune.IsAsciiLetterOrDigit` 不存在**（.NET 没有这个 API）。ASCII 字母数字要自己按 `rune.Value` 的码位区间判断（`'a'..'z'` / `'A'..'Z'` / `'0'..'9'`）。
- 转小写用 `rune.ToString().ToLowerInvariant()`（不能用 `(char)rune`，emoji 会溢出）。

**实测的分词结果**（本机 .NET 10 实跑）：

| 输入 | token 序列 |
|---|---|
| `项目, 温度!` | `项` `目` `温` `度` |
| `项目，温度！`（全角） | `项` `目` `温` `度`（与半角完全相同） |
| `咖啡，水温` | `咖` `啡` `水` `温`（**逗号被丢弃**，token 仍相邻） |
| `Hello520世界` | `hello520` `世` `界` |
| `a-b` | `a` `b` |
| `👍 咖啡` | `咖` `啡`（emoji 是 Symbol，被丢弃） |
| `咖 啡` | `咖` `啡`（空白是分隔符） |
| `...` | 空（全是标点） |
| `a` + U+0301（组合锐音符）+ `b` | `a` `b`（`Mn` 不是 `L*`/`N*`，丢弃） |
| `a` + U+200D（零宽连接符）+ `b` | `a` `b`（`Cf` 丢弃） |

> **第 1 条是我的定**（量出来的）。标点与符号进索引会让索引明显变大（中文正文里逗号句号极多），而收益只在「`ab` 不该命中 `a-b`」这种窄边界上 —— 而这个边界已经被规则 2 顺带解决了（`ab` 是一个英文词 token，`a-b` 索引出来是 `a`、`b` 两个 token，两者匹配不上）。
>
> **半角与全角必须一起丢**：若全角逗号保留成一个 token，正文 `咖啡，水温` 的 token 序列就是 `[咖,啡,，,水,温]`，搜「咖啡水温」的短语查询 `"咖 啡 水 温"` **不匹配** —— 中文正文里逗号句号遍地都是，这个坑会天天踩。
>
> 丢弃之后，token 只可能是 `[a-z0-9]+` 或**单个非 ASCII 的 L*/N* 字符**。ASCII 的 FTS5 语法字符（`"`、`*`、`(`、`)`、`:`、`^`、`-` 等）都是标点或控制字符，**被丢弃后不可能出现在 token 里** → MATCH 串不需要转义。

**分词器放哪**：`Domain` 层，纯函数、无 IO、有单测。索引侧与查询侧共用同一个实现 —— 这是语义一致的唯一保证。

### 7.2 中文按 token 相邻，英文按词级

| 检索词 | 正文 | 命中？ | 说明 |
|---|---|---|---|
| `咖啡` | `手冲咖啡的关键` | ✅ | 中文逐字 + 短语查询 = 子串，与今天的 `includes()` 一致 |
| `咖啡` | `咖啡机滤芯` | ✅ | 同上 |
| `咖啡` | `咖 啡`（中间有空格） | ✅ | 空格是分隔符，token 仍相邻。**比今天的 `includes()` 宽松**（今天搜不到） |
| `咖啡` | `咖，啡`（中间有逗号） | ✅ | 同上，**比今天宽松** |
| `咖啡水温` | `咖啡的水温` | ❌ | 「的」是汉字 token，会打断相邻 |
| `hel` | `hello` | ❌ | **英文词级**：必须输全词。**比今天的 `includes()` 严**（今天搜得到） |
| `hello` | `Hello` | ✅ | 大小写不敏感 |
| `hello` | `hello520` | ❌ | 英文数字合并成一个 token |

**英文从「任意子串」变成「词级」是本次唯一的行为收紧**，是 §4 的取舍：中文是主战场，英文在随手记的笔记里占比小，而「两套语义不一致、条目先冒出来又被收回」的 bug 极难查。

### 7.3 多词查询

查询按**空白**切成若干段：

- 每段内部：分词后组成一个**短语查询**（要求 token 相邻）。
- 段与段之间：**AND**（要求都出现，不要求相邻、不要求顺序）。

| 检索词 | MATCH 串 | 语义 |
|---|---|---|
| `咖啡` | `"咖 啡"` | 两个 token 相邻 |
| `咖啡 水温` | `"咖 啡" AND "水 温"` | 两个都出现，彼此可隔任意内容 |
| `咖啡水温` | `"咖 啡 水 温"` | 四个 token 相邻 |

> ⚠️ **「相邻」指 token 相邻，不是原文字符相邻。** 被丢弃的标点与空白在 token 序列里不存在，所以正文 `咖啡，水温` 的 token 序列是 `[咖,啡,水,温]` —— 搜「咖啡水温」**命中**，尽管原文里那四个字被逗号隔开。实现时不要把「相邻」理解成字符相邻。

### 7.4 与今天的客户端 `includes()` 的差异

**必须写清楚**：新语义**不是**与今天逐字一致，它有得有失。

| 情形 | 今天 `includes()` | 新语义 | 方向 |
|---|---|---|---|
| 搜 `咖啡`，正文是 `手冲咖啡的关键` | 命中 | 命中 | 一致 |
| 搜 `咖啡`，正文是 `咖啡的水温` | 命中 | 命中 | 一致 |
| 搜 `咖啡`，正文是 `咖 啡`（中间空格） | **不命中** | 命中 | **更宽松** |
| 搜 `咖啡`，正文是 `咖，啡`（中间标点） | **不命中** | 命中 | **更宽松** |
| 搜 `hel`，正文是 `hello` | 命中 | **不命中** | **更严格**（英文词级，§4 的取舍） |
| 搜 `咖啡 水温`，正文里「咖啡」和「水温」隔着一大段 | **不命中** | 命中 | **更宽松**（多段是 AND，§7.3） |

两处「更宽松」都源于**标点与空白被当作分隔符**（§7.1 规则 3、4）；一处「更严格」源于**英文词级**。

现存的 4 条 e2e 搜索用例（`web/e2e/ideas.spec.ts:588/601/614/625`）在这套新语义下**仍然通过**：`'苹果'`（2 字中文，逐字命中）、`'weather'`（大小写折叠命中）、`'这个词库里没有'`（多 token 短语不命中 `'有的想法'`）、清空恢复全部。

## 8. 数据模型

### 8.1 新增表与虚表

`schema.sql` 追加（全部 `IF NOT EXISTS`，保持幂等）：

```sql
-- 想法的全文索引。三列对应三个可检索字段。
-- 内容是**切分后**的 token 串（§7.1），不是原文 —— 因此不能用 external content 表
-- （external content 表会用本表的 tokenize 去处理原文，中文会整段成一个 token）。
CREATE VIRTUAL TABLE IF NOT EXISTS idea_fts USING fts5(
    content, tags, project,
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

-- 索引元信息。key='index.version'，value='1'。
-- 版本号变了就整体重建索引 —— 分词规则改了、或老库还没建索引，都靠它。
CREATE TABLE IF NOT EXISTS search_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

**`remove_diacritics 0` 是必须的**：unicode61 **默认会折叠变音符**（实测：`MATCH 'arger'` 能命中索引里的 `ärger`）。而我们的分词器**保留**变音符 —— 不关掉的话，搜「arger」会命中「Ärger」那条，但服务端按自己的规则算不出高亮区间，又变成「搜得到却高亮不出来」。关掉之后 unicode61 与分词器的口径完全一致。

**为什么不用 `content='idea'`（external content）**：external content 表的 tokenize 作用于**原表内容**，而我们要的是「先自己切分、再交给 unicode61」。unicode61 会把整段中文当成一个 token —— 正是 §6.2 实测出的致命问题。所以索引里必须存**自己切好的 token 串**。

**rowid 映射**：

| 虚表 | rowid | 说明 |
|---|---|---|
| `idea_fts` | `idea.rowid` | `idea` 表没有 `WITHOUT ROWID`，有隐式 rowid |
| `project_fts` | `project.rowid` | 同上 |
| `tag_fts` | `tag.id` | `tag` 表是 `INTEGER PRIMARY KEY AUTOINCREMENT`，rowid 就是 id |

> ⚠️ **rowid 会被复用**：`idea` 表没有显式整数主键，删掉最后一条后，新插入的行可能拿到刚被释放的 rowid。所以**硬删路径必须同步删掉索引行**（§10.3），否则新行会顶掉旧行的索引位置。

### 8.2 索引里存什么

| 虚表 | 列 | 内容 |
|---|---|---|
| `idea_fts` | `content` | 正文的切分结果 |
| | `tags` | 该想法**所有标签名**的切分结果（空格连接后切分） |
| | `project` | 所属**项目名**的切分结果 |
| `project_fts` | `name` | 项目名的切分结果 |
| `tag_fts` | `name` | 标签名的切分结果 |

**项目名为什么进想法的索引**：用户要能搜到项目名（§1 的理由 2）。放进想法索引后，搜「工作」能命中「工作」项目下的想法 —— 在页面上（`/api/ideas?q=`）这是唯一的搜项目名的途径。代价是**项目改名时要重建该项目下所有想法的索引行**（§10.2）；想法数量是几百到几千级，重建成本可忽略。

`project_fts` 仍然单独存在，因为 `/api/search` 要返回「项目本身」这个命中实体。

**标签名为什么进想法的索引**：同上一条，搜「咖啡」要能命中打了「咖啡」标签的想法。

## 9. 查询构造

### 9.1 索引侧文本

```
字段原文 → 分词（§7.1）→ token 用单个空格连接 → 写入 FTS 列
```

`unicode61` 会把空格分隔的每段当一个 token，正好等于我们切出来的 token。

### 9.2 查询侧 token 序列

```
查询原文 → 按空白切段 → 每段分词（§7.1）→ 每段得到 token 序列
```

**空段**：切分后没有 token 的段（全是标点 / 空白）**跳过**。

**全空**：所有段都空 → 等价于「没有关键词」→ 不过滤，返回全部（与今天的 `matchesKeyword('')` 一致）。

### 9.3 MATCH 串

```
每段 → "\"" + tokens 用空格连接 + "\""     （短语查询，要求相邻）
各段 → 用 " AND " 连接
```

| 查询 | MATCH 串 |
|---|---|
| `咖啡` | `"咖 啡"` |
| `咖啡 水温` | `"咖 啡" AND "水 温"` |
| `手冲 咖啡豆` | `"手 冲" AND "咖 啡 豆"` |
| `Hello520` | `"hello520"` |

短语查询里的 token 不含 ASCII 语法字符（§7.1 第 4 条），**不需要转义**。

### 9.4 查想法的 SQL

关键词条件以**子查询**的形式进入 `BuildFilter` 产出的 WHERE 片段：

```sql
-- BuildFilter 产出的 where（有 q 时才带上这一段）
deleted_at IS NULL
AND project_id = $projectId
AND (done = 0)                      -- 按 status
AND idea.rowid IN (SELECT rowid FROM idea_fts WHERE idea_fts MATCH $q)
```

主查询与标签批量查询**共用这一份 WHERE**，所以两边口径天然一致：

```sql
-- 主查询（IdeaRepository.List）—— 注意：也要起别名 i
SELECT <cols> FROM idea i WHERE {where} ORDER BY i.updated_at ASC, i.id ASC;

-- 标签批量查询（ReadTagNamesOfCandidates）
SELECT i.id, t.name FROM idea i
  JOIN idea_tag it ON it.idea_id = i.id
  JOIN tag t ON t.id = it.tag_id
WHERE {where}
ORDER BY t.name COLLATE NOCASE ASC, t.name ASC;
```

> ⚠️ **两条查询要用同一个别名** —— SQLite 在有别名时**会把表名藏起来**（实测：`FROM idea i` 之后写 `idea.rowid` 报 `no such column: idea.rowid`）。所以主查询也要起别名 `FROM idea i`，共享的 WHERE 片段里写 **`i.rowid`** —— 两处 FROM 的别名一致，片段才能共用。
>
> ⚠️ **`rowid` 裸写会歧义**：标签查询的 FROM 里有 `idea` / `idea_tag` / `tag` 三张表，三张都有 rowid（`tag` 的 rowid 就是它的 `id`），裸 `rowid` 会报 `ambiguous column name: rowid`。必须限定成 `i.rowid`。

> **为什么不用 JOIN 形式**（`FROM idea_fts JOIN idea i ON i.rowid = idea_fts.rowid WHERE idea_fts MATCH ?`）：JOIN 形式效率更高（FTS5 直接产出 rowid 序列，再按 rowid 查 `idea`），但它**没法塞进 WHERE 片段** —— JOIN 形式要求 WHERE 里所有列都带表前缀（`i.deleted_at IS NULL`），而现有 `BuildFilter` 产出的片段是无前缀的（`deleted_at IS NULL`），标签查询的 FROM 又是另一套别名。两套 FROM 要共用一份 WHERE，就只能保持无前缀，于是关键词条件只能写成不依赖 FROM 别名的子查询形式。
>
> **代价**：主查询会扫描 `idea` 再做 rowid 集合判定，而不是直接从 FTS5 产出的 rowid 定位。数据量是几百到几千条，这个差别可以忽略。
>
> **如果实测发现慢**：可以改成 JOIN 形式，代价是标签批量查询要单独写一份（不带 FTS 条件、或者改成从候选 id 集合驱动）。这条留给实现者按实测结果决定（§18）。

## 10. 索引维护

### 10.1 原则

**索引写入与业务写入在同一个事务里**（同库同事务 → 强一致）。

这样取舍的理由：微信那种「索引与业务异步同步」会带来「搜到不完整的记录」和「跨库无法保证事务原子性」两个问题（§6.3），我们没有那个量级，不需要换那个代价。同事务的代价是索引写失败会连累业务写 —— 但索引表在启动时就建好（`schema.sql` 幂等），唯一能失败的原因是磁盘满，那业务写本身也会失败。所以这个取舍是安全的。

### 10.2 需要同步的写路径

| 路径 | 动作 |
|---|---|
| `IdeaRepository.Insert` | 插入 `idea_fts` 行（content/tags/project 三列） |
| `IdeaRepository.UpdateContent`（改正文） | 只更新 `idea_fts` 行的 `content` 那一列 |
| `IdeaRepository.ReplaceTags` | 更新 `idea_fts` 行的 `tags` 列；**新建的标签**要插入 `tag_fts` 行 |
| `IdeaRepository.SetDone` | 不动（`done` 不在索引里） |
| `IdeaRepository.SoftDelete` | **不动索引**。软删的行仍在索引里，`/api/search` 用 `deleted_at` 判断 `trashed` |
| `IdeaRepository.Restore` | **不动索引** |
| `IdeaRepository.SoftDeleteByProject`（项目删除时连带软删想法） | **不动索引** |
| `IdeaRepository.RestoreWithProject`（项目恢复时连带恢复想法） | **不动索引** |
| `IdeaRepository.HardDelete`（合并时硬删源想法） | **删除 `idea_fts` 行**（必须，否则 rowid 复用会撞车，§10.3） |
| `ProjectRepository.Insert` | 插入 `project_fts` 行 |
| `ProjectRepository.Rename` | 更新 `project_fts` 行的 `name`；**并重建该项目下所有想法的 `idea_fts` 行的 `project` 列** |
| `ProjectRepository.SoftDelete` / `Restore` / `Reorder` | 不动索引（名字没变） |
| 标签首次出现 | 插入 `tag_fts` 行 |

> **项目没有硬删路径**：`ProjectRepository` 只有 `Insert` / `Rename` / `SoftDelete` / `Restore` / `Reorder`，删项目一律是软删进回收站。所以 `project_fts` 里不会有「硬删后残留」的问题。

> ⚠️ **回收站到期清理是一条绕过仓储层的裸 SQL 路径**，必须一并处理：
> `TrashCleanupWorker.Cleanup()`（`server/IdeaApp/Infrastructure/TrashCleanupWorker.cs:119/126/132`）直接执行
> ```sql
> DELETE FROM idea    WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff AND project_id NOT IN (...);
> DELETE FROM idea    WHERE project_id IN (SELECT id FROM project WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff);
> DELETE FROM project WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff;
> ```
> 它**不经过 `IdeaRepository`**，所以不会触发上面表格里的任何同步逻辑 → 清理后 `idea_fts` 里会留下**孤儿行**。
>
> **孤儿行不只是脏数据，还会撞车**：孤儿行占着 rowid，而 `idea` 表没有显式整数主键、rowid 会被复用 —— 新插入的想法可能拿到一个已被孤儿行占用的 rowid，索引就串了。
>
> 处理方式（实现者选其一）：
> - (a) 把这三条 `DELETE` 改成走仓储（`HardDelete` 逐条删），代价是批量删除变慢；
> - (b) 保留裸 SQL，但在这三条语句后**补一条按同样条件删索引的 SQL**：
>   `DELETE FROM idea_fts WHERE rowid IN (SELECT rowid FROM idea WHERE <同样的条件>)`；
> - (c) 在清理事务提交后，**整体重建 `idea_fts`**（清空 + 全量灌入）—— 保留期清理本来就是低频操作，重建成本可接受。
>
> 无论选哪个，**必须保证「删了业务行就一定没有对应的索引行」这条不变量成立**。推荐 (b)：改动最小，且保留了批量删除的性能。

> 标签**没有重命名 / 删除端点**（标签是自由文本字典，只增不改），所以 `tag_fts` 行一旦写入就不用动。

### 10.3 硬删与 rowid 复用

`idea` 表没有显式整数主键，SQLite 用「max(rowid)+1」分配新 rowid ⇒ **删掉的 rowid 会被下一个插入复用**。

不变量：**任何硬删 `idea` 的路径，都必须同步删掉它对应的 `idea_fts` 行**。做到了，rowid 复用就是安全的（新行会写自己的新索引行）。

**所有硬删 `idea` 的路径只有两条**：

1. `IdeaRepository.HardDelete` —— 合并时硬删源想法（`IdeaService.cs:473`）。
2. `TrashCleanupWorker.Cleanup()` —— 回收站到期清理的裸 SQL（见 §10.2 的警告框）。

第二条最容易漏 —— 它不经过仓储层。

### 10.4 老库回填

已存在的库没有索引。启动时（`SqliteDatabase.Initialize()` 之后）检查版本：

```
读 search_meta['index.version']
  != '1'  →  在一个事务里：
              DELETE FROM idea_fts;  DELETE FROM project_fts;  DELETE FROM tag_fts;
              重新灌入全部 idea / project / tag（含标签名、项目名）
              写 search_meta['index.version'] = '1'
```

- **回填在启动时同步做**，保证第一次搜索就有结果。数据量是几百到几千条，耗时在百毫秒级。
- **回填失败 = 启动失败（fail fast）**。索引与业务数据不一致时，搜索会给出**错误的结果**（漏掉或多出条目）—— 那比「服务起不来」危险得多，而且错误结果极难被发现。启动失败会立刻暴露问题。
  - 唯一可能的失败原因是磁盘满 / 库损坏 / 库被锁 —— 这几种情况下业务写本身也会失败。
  - 失败时必须打一条**说清楚原因**的日志（库路径、失败阶段、异常），别让人对着「应用起不来」猜。
  - 回填逻辑放在组合根（`Program.cs`）的 `SqliteDatabase.Initialize()` 之后，作为独立的一步（可以是一个 `SearchIndexInitializer` 静态类）。
- **不建迁移框架**：`search_meta` 只是一个 `key/value` 表，不是版本化迁移表。
- 以后改了分词规则，把版本号从 `'1'` 改成 `'2'` 就会自动重建 —— 这是选 `search_meta` 而不是硬编码判断的原因。

> `search_meta` 是内部表，不通过 `/api/settings` 暴露。

> ⚠️ **§4 的反目标「不能因为索引坏了就把主功能拖下水」说的是运行时**：运行时的索引写失败会随业务写一起回滚（§10.1），不会让已存的数据变得搜不到。**启动时的回填失败是另一回事** —— 那时索引与业务数据本来就还没对齐，宁可起不来。

### 10.5 段（Segment）合并

FTS5 每次写入产生一个 Segment，段多了查询会变慢。SQLite 提供 `INSERT INTO idea_fts(idea_fts) VALUES('merge')` 手动合并。

**不做自动合并**：写入量是几百到几千条，段数量天然很小。真需要时，在回填（§10.4）之后顺手 `merge` 一次即可。

## 11. HTTP API

### 11.1 `GET /api/ideas`（扩展）

在现有参数之外增加三个：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `q` | string | 空 | 关键词。trim 后为空 = 不过滤 |
| `limit` | string? | 不给 | 最多返回几条。范围 1..1000，越界 → 400 `invalid_pagination` |
| `offset` | string? | 0 | **往回翻**时已经翻过多少条（从**最新**一端数）。`offset > 0` 而 `limit` 缺省 → 400 `invalid_pagination`（`offset` 单独给没有意义） |

> **`limit` / `offset` 绑成 `string?`（不是 `int?`）**，由服务层自己解析。原因见 §11.5 的实测。

**响应形状不变：仍是裸数组 `IdeaDto[]`。** 不破坏现有契约、e2e 与 skill 文档。

#### 分页的方向：往回翻历史（聊天软件模型）

这个应用的列表是**聊天软件式**的：`HomePage.vue:193` 的注释写着「打开页面就看到最新的一条（列表是从上往下长的，底部才是当下）」，加载完就滚到底。

所以分页必须**往回翻**：

| 请求 | 返回 |
|---|---|
| `?limit=100` | **最新**的 100 条，按 `updated_at ASC` 排列（页内最老在顶、最新在底） |
| `?limit=100&offset=100` | 跳过最新的 100 条，取接下来的 100 条（**更旧的**） |
| `?limit=100&offset=200` | 再往旧 100 条 |

前端因此是：首次取最新一页 → 渲染 → **滚到底**；用户往上翻 → 取更旧的一页 → **prepend** 到列表顶部。

> **`offset` 的统一规则**：「跳过该端点自己的排序里的前 N 条」。
> - `/api/ideas`：分页取的是**最新的** N 条（从 ASC 数组的尾部往回数），`offset` 就是「跳过最新的 N 条」；返回给客户端的数组仍是 `updated_at ASC`（页内最老在顶）。
> - `/api/search`：排序是「最相关在前」，`offset` 就是「跳过最相关的前 N 条」。
>
> ⚠️ **两个端点的 `offset` 含义不同**（一个是往回翻历史，一个是翻相关度页）。skill 说明书里要把这两个端点分开写清楚，别让 agent 混用。

#### `too_many_ideas` 什么时候还报

| 情况 | 行为 |
|---|---|
| 不给 `limit` | 与今天一样：命中数超过 `Api:MaxIdeasPerResponse`（默认 2000）→ 400 `too_many_ideas` |
| 给了 `limit` | **不再报**。明确要了一页，不该被顶回来。返回该页（可能少于 `limit` 条，说明到底了） |

> 这两条不冲突：不传 `limit` 是「给我全部」，超上限就该明说；传了 `limit` 是「给我一页」，给了就一定给到。

#### 切片发生在哪一步

**在内存标签过滤之后。** 完整的数据流：

```
SQL：WHERE deleted_at IS NULL AND project_id = ? AND (done = ?)
     AND idea.rowid IN (SELECT rowid FROM idea_fts WHERE idea_fts MATCH $q)
     ORDER BY updated_at ASC, id ASC          ← 不带 limit/offset
     → 候选集（全部）
批量：ReadTagNamesOfCandidates(同一份 WHERE) → 候选集的标签
内存：IdeaFilter.Matches(idea, query) → 过滤标签 → 最终命中集（ASC）
切片：见下
```

**为什么不在 SQL 层切片**：标签过滤在内存（`IdeaFilter` 那套纯函数不动，§13.1），SQL 层切出来的一页在内存过滤后可能只剩零星几条 —— 前端按「返回条数 == limit+1」判定「没更多」，后面的命中就永远看不到了。

**切片算法**（候选集已经是 ASC，最老的在数组头部）：

```csharp
var items = allMatched;                      // ASC：最老在 [0]，最新在 [Count-1]
var end   = items.Count - offset;            // 排除「最新的 offset 条」
var start = Math.Max(0, end - limit);
var page  = items.Skip(start).Take(Math.Max(0, end - start)).ToList();   // 仍是 ASC
```

⚠️ **`Take` 的长度必须再夹一次 0**：`offset` 大于命中总数时 `end` 是负数，`end - start` 也是负数，而 `Enumerable.Take(负数)` 抛 `ArgumentOutOfRangeException` → 端点 500。

**代价**：服务端要把**全部候选**加载进内存再切页。

> 这**不是新增的代价** —— 今天的 `IdeaService.List` 就是先把 `repo.List(query)` 的全部结果读进内存，再检查 `too_many_ideas`（`IdeaService.cs:41-48`）。分页模式只是把「读全部」的结果切一刀。
>
> `/api/search` 更是必须如此：按 `bm25()` 排序要算**全部**命中项的得分才能定序（§6.3 引的微信原文：「分段需要对结果排序，排序就要遍历所有结果，分段查询并不能降低搜索耗时」）。分页在那里省的是**传输与 agent 的上下文**，不是检索。

#### 「还有没有更多」

客户端请求 `limit + 1` 条，多出来那条说明还有下一页。服务端不做额外提示。

> 之所以不把响应改成包装对象：主规格 §13.5 与 skill 说明书都按裸数组写的，改形状会波及一大片消费者，而分页信息对 `/api/ideas` 来说只有「还有没有更多」这一个用途，`limit+1` 足够。`/api/search` 是新端点，才带完整的分页信息（§11.3）。
>
> 往上翻的终止条件：返回的条数少于 `limit` → 没有更旧的了。

### 11.2 DTO 的变化

`IdeaDto` 增加**两个可选字段**：

```csharp
/// <summary>
/// 命中位置。仅在带关键词过滤的响应里出现；普通列表不带。
/// </summary>
public sealed record SearchMatch(string Field, int Start, int Length);

/// <summary>
/// 想法 DTO：加两个可选字段。
/// </summary>
public sealed record IdeaDto(
    /* …现有字段… */,
    SearchMatch[]? Matches = null,
    bool? Trashed = null);
```

- `Matches`
  - `Field`：`"content"` | `"tags"` | `"project"`
  - `Start`：**UTF-16 code unit** 偏移，相对**该字段的完整字符串**
  - `Length`：**UTF-16 code unit** 长度
  - 偏移用 UTF-16 code unit 而不是「字符序号」，是为了和 JS 的 `String.prototype.slice` 对齐 —— 前端拿到的偏移可以直接切字符串，不用自己换算代理对。
- `Trashed`
  - `true` = 这条想法在回收站里；`false` = 活的。
  - **只在 `/api/search` 的响应里出现**。`/api/ideas` 只返回未删除的想法，不填这个字段 → 省略。

`ProjectDto` 也加**一个可选字段** `bool? Trashed = null`（同样只在 `/api/search` 里填）。

**序列化时 `null` 就省略** —— 用**属性级**的 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`。

> ⚠️ `IdeaJsonContext` **没有设全局 `DefaultIgnoreCondition`**（只设了 `PropertyNamingPolicy = CamelCase`）→ 不加这个属性级特性的话，`null` 字段会**恒定写出**，每一条 `IdeaDto` 的响应都会多出 `"matches": null, "trashed": null` 两个键。
>
> 加了特性之后，**不带关键词、不带回收站标记的响应跟今天逐字节一样** —— 现有的 e2e 与 skill 消费者完全不受影响。

> **`TagDto` 不加 `matches`**：只有页面需要渲染高亮，而页面只显示想法。名字都很短，agent 自己一眼就能看出命中在哪。
>
> **不新增 DTO 类型**：加可选字段而不是另立 `SearchIdeaDto` / `SearchProjectDto`，是因为 `IdeaJsonContext` 里已经登记了 `IdeaDto` / `ProjectDto`（AOT 硬要求，见 §5）—— 加字段不用动 `JsonContext`，而新增类型必须新增 `[JsonSerializable]` 条目，漏一条就是 AOT 产物 500（`dotnet test` 还测不出来）。

### 11.3 `GET /api/search`（新增）

给 agent 用的全库检索端点。

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `q` | string | **必填** | 关键词。缺失或 trim 后为空 → 400 `invalid_search_query`。**全是标点**的查询词按「没有关键词」处理 → 不过滤 → 三组返回全部（与 `/api/ideas` 同一口径） |
| `limit` | string? | 20 | 每类结果最多几条。范围 1..100，越界 400 |
| `offset` | string? | 0 | **跳过最相关的前 N 条**。三类统一应用同一个 offset |
| `includeTrash` | bool | **true** | 是否包含回收站里的条目 |

> ⚠️ **`offset` 的含义与 `/api/ideas` 不同**：这里是「翻相关度页」（从最相关一端数），`/api/ideas` 是「往回翻历史」（从最新一端数）。见 §11.1 的说明。

**响应**：

```json
{
  "query": "咖啡",
  "ideas": {
    "items": [ { "id": "...", "content": "...", "trashed": false,
                 "matches": [ { "field": "content", "start": 4, "length": 2 } ] } ],
    "total": 42, "offset": 0, "limit": 20, "hasMore": true
  },
  "projects": { "items": [ { "id": "...", "name": "工作", "trashed": false } ],
                "total": 1, "offset": 0, "limit": 20, "hasMore": false },
  "tags":     { "items": [ { "name": "咖啡", "count": 5 } ],
                "total": 2, "offset": 0, "limit": 20, "hasMore": false }
}
```

- `ideas.items` 的元素是 `IdeaDto`（带 `matches` 与 `trashed`）。
- `projects.items` 的元素是 `ProjectDto`（带 `trashed`）。
- `tags.items` 的元素是 `TagDto`（`name` + `count`）。
- `total` 是**该类命中总数**（不受 `limit`/`offset` 影响）。
- `hasMore` = `offset + items.length < total`。
- `tags.items[].count` 的口径与 `GET /api/tags` **保持一致**（只数**未删除**的想法），**不随 `includeTrash` 变化** —— 它回答的是「这个标签现在被用着多少次」，不是「搜索结果里出现了多少次」。

**分页的语义边界**（来自微信官方文章那条原文，§6.3）：

> 按相关度排序的分页**只省传输，不省检索**。算 `bm25()` 必须把所有命中项都算一遍才能排序，切页是切完之后的事。

所以 `/api/search` 分页的真实价值是**省 agent 的上下文**（别一次塞 100 条正文进去），不是让服务端少干活。`total` 也要完整算一遍。

**三类各自分页、共用一个 `offset`/`limit`**：因为三类是不同实体，「第 41 条想法」和「第 41 个项目」没有共同的序，统一 offset 已经是妥协；agent 想要更多想法就把 `offset` 调大，多出来的项目/标签被跳过也无所谓。

### 11.4 排序

| 端点 | 排序 |
|---|---|
| `GET /api/ideas` | `updated_at ASC, id ASC`（**不变**）。搜索时也保持 —— 页面里条目位置不跳 |
| `GET /api/search` 的 `ideas` | `bm25(idea_fts, 5.0, 5.0, 3.0), id ASC` |
| `GET /api/search` 的 `projects` | `bm25(project_fts), id ASC` |
| `GET /api/search` 的 `tags` | `bm25(tag_fts), count DESC, name ASC` |

- `bm25()` 的权重按**列顺序**：第一个实参是最左列的权重。`idea_fts` 的列序是 `content, tags, project` → 正文 5.0、标签 5.0、项目 3.0。
- `bm25()` 返回**负值，越负越相关**，`ORDER BY bm25(...)` **不加 `DESC`**（官方语义）。
- `id ASC` 兜底 → 全序 → **分页不漂**。
- `tags` 组在相关度相同的情况下按使用次数倒序（用得多的更可能是用户要的），再按名称。

### 11.5 错误码

新增并登记进 `ApiErrorCodes.All`：

| 码 | HTTP | 何时 |
|---|---|---|
| `invalid_pagination` | 400 | `limit` / `offset` **不是数字或越界**（`limit` 不在 1..1000（想法列表）/ 1..100（全库搜索）、`offset` 为负）；**只有 `/api/ideas`** 才是「`offset > 0` 而 `limit` 缺省」—— `/api/search` 的 `limit` 有默认值 20，单独给 `offset` 是合法请求 |
| `invalid_search_query` | 400 | `/api/search` 的 `q` 缺失或 trim 后为空 |

> **`limit` / `offset` 绑定成 `string?`，由服务层自己解析** —— 所有非法情形（不是数字、越界）一律回 `invalid_pagination`。
> **`offset` 单独给只有 `/api/ideas` 算非法**：那里 `limit` 没有默认值，`offset > 0` 而 `limit` 缺省 → 400 `invalid_pagination`；
> 而 `/api/search` 的 `limit` 缺省回落成 `DefaultLimit = 20`，所以单独给 `offset` 是**合法请求**。
>
> **实测（跑真实应用，.NET 10）**：
>
> | 请求 | 响应 |
> |---|---|
> | `?limit=abc`（绑成 `int?` 时） | **400，空 body，没有 code** —— 框架在参数绑定阶段失败后**只设一个状态码就返回**，根本进不了 `ApiExceptionHandler` |
> | `?limit=0`（自己解析） | 400，`code: invalid_pagination`，detail 说明「limit 必须在 1..1000 之间，收到：0」 |
> | `?status=archived`（`string?` + 自己解析） | 400，`code: invalid_status` |
> | 请求体是坏 JSON 的 POST | 400，`code: invalid_request_body` |
>
> 也就是说：绑 `int?` 时，**非数字输入得到的是一个没有 body、没有 code 的 400** —— agent 读到只知道自己错了，不知道错在哪。
> 而这个应用里既有的查询参数（`status` / `tagMode`）**全都是 `string?` + 自己解析 + 自己的错误码**。
> 跟它们保持一致，`limit` / `offset`（两个端点都是）也绑 `string?`：
> - 非法输入一律 `invalid_pagination`，带说得清楚的 detail ✓
> - 与既有查询参数的处理方式一致 ✓
> - 代价：OpenAPI 里这两个参数的 schema 是 `string` 而不是 `integer`。
>
> ⚠️ 这是个**应用层面的既有缺口**，不只影响这几个参数（`?unmask=xyz` 这类 `bool?` 查询参数同样回空 body 的 400）。本设计只保证 `limit` / `offset` 走 `string?`；**`includeTrash` 仍是 `bool?`**（契约里 `boolean` 比 `string` 准确，agent 也只会传 true / false），所以 `?includeTrash=xyz` 这类非法布尔值仍然回**空 body、没有 code** 的 400 —— 那是本设计**没有**覆盖到的情形，不在「非法输入一律 `invalid_pagination`」这句承诺的范围里。把「查询串绑定失败也带 code」这个缺口补上，是另一件事。

> `ApiErrorCodeTests.cs:107` 会扫源码里的 `new ApiException(...)`，码没登记就会红。
> **`skills/idea-manager/references/errors.md` 的「## 服务端错误码」表也必须同步加这两行** —— `SkillDocGuardTests.Manual_error_code_table_matches_the_registry`（`server/IdeaApp.Tests/SkillDocGuardTests.cs:288-321`）要求该表的 code 集合与 `ApiErrorCodes.All` **完全相等**，漏了必红。

### 11.6 回收站口径

| 端点 | 默认 | `includeTrash` | 口径 |
|---|---|---|---|
| `GET /api/ideas?q=` | — | 无此参数 | **只搜未删除的**（`deleted_at IS NULL`），跟现在的列表口径一致 |
| `GET /api/search?q=` | **含**回收站 | `true` / `false` | `true` = 软删的**全部**都收（想法、项目都算）；`false` = 只收未删除的 |

- `/api/search` 默认含回收站，理由：它是「全库找东西」，东西被删进回收站不该凭空消失 —— 用户八成是想找回来。
- 每条结果标 `trashed: true/false`（§11.2）。
- **`includeTrash=true` 时**：
  - `ideas` 组包含**已软删项目下的想法**（项目被删时其下想法一并软删，`deleted_at` 非空 → 收）；
  - `projects` 组包含**已软删的项目**（`project_fts` 里软删项目还在，因为软删不动索引，§10.2）；
  - `tags` 组的 `count` 口径不变（只数未删除的想法，§11.3）。

### 11.7 契约同步

| 文件 | 改什么 |
|---|---|
| `openapi/idea-v1.json` | 重新导出（`dotnet run --project tools/ExportOpenApi`） |
| `skills/idea-manager/references/api.md` | 端点速查表加 `GET /api/search`；`GET /api/ideas` 的入参列加 `q`/`limit`/`offset` |
| `skills/idea-manager/references/errors.md` | 「## 服务端错误码」表加 `invalid_pagination` / `invalid_search_query` 两行（`SkillDocGuardTests` 要求该表与 `ApiErrorCodes.All` **完全相等**） |
| `skills/idea-manager/references/calling.md` | 加 `curl` 示例 |
| `docs/superpowers/specs/2026-11-11-idea-manager-design.md` | 五处要改写 —— §2「目标」的第 4 条、§3 非目标、§10.2、§11.4、§20 的「不做服务端全文检索」—— 见 §17 |
| `web/e2e/COVERAGE.md` | 用例计数与概览表 |

## 12. 命中位置

### 12.1 为什么自己算

FTS5 **没有** `offsets()`（官方附录明说，那函数属于 FTS3/4）。而 FTS5 的 `highlight()` 输出的是**切分后**的 token 串（带空格），跟原文对不上。

所以命中位置由服务端自己在**原文**上算。

### 12.2 算法

1. 把原文按 §7.1 切成 `(token, 起始偏移, 长度)` 的序列（偏移是 UTF-16 code unit）。
2. 把查询按 §9.2 切成若干段，每段是一个 token 序列。
3. 对每段，在原文的 token 序列上滑动比较，**找连续的、token 字符串完全相等的片段**（一处出现 = 一个命中）。
4. 每个命中的区间 = 首个命中 token 的起点 → 末个命中 token 的终点（**跨过两者之间被丢弃的标点与空白**）。

**这样算出来的偏移与 FTS 的匹配语义天然一致**：两边用的是同一套分词规则、同样的「token 相邻」判定。不会出现「搜得到但高亮不出来」或「高亮出来了但没搜到」。

> 拿正文 `咖啡，水温` 举例：它的 token 序列是 `[咖, 啡, 水, 温]`（逗号被丢弃）。搜「咖啡水温」时，FTS 的短语查询要求 `[咖,啡,水,温]` **连续** —— 索引里确实连续（逗号不产生 token），所以**命中**。而原文里那四个字被逗号隔开。
> 偏移算法在 token 序列上找，得到**一个**命中，区间是字符 0..5（含中间那个逗号）—— 前端渲染出「<mark>咖啡，水温</mark>」。
>
> **一个查询段 = 一个命中**：多段查询（`咖啡 水温`）产出两处命中，各自在原文里的位置。

### 12.3 偏移的适用范围

- 只对**想法**的 `content` / `tags` / `project` 三列算。
- `tags` 列的原文是「标签名用空格连接」，偏移相对**那个连接串**，不是相对某一个标签名。前端不渲染标签高亮（标签在卡片上是 chip，不在正文里），所以这条实际上用不上；保留是为了接口自洽。

## 13. 前端

### 13.1 过滤条件下沉

现在 `loadIdeas` 只传 `projectId`，状态/标签/关键词全在浏览器里过滤。改成一次请求拿最终结果：

```
GET /api/ideas?projectId=...&status=...&tags=...&tagMode=...&q=...
```

后端这四个参数**本来就有**（`IdeaService.ParseQuery`），只是前端一直没传。标签过滤仍在**后端内存**做（`IdeaFilter.Matches` 那套纯函数不动），项目 / 状态 / 关键词在 SQL 里过滤。

### 13.2 搜索框防抖

`FilterBar.vue` 现在输入即 `emit`，**没有防抖**。改后端搜索后每个击键都会打一次后端 → 必须加防抖。

- **防抖 250ms**（我的定）。
- **防抖放在「重拉」的入口**（`HomePage.vue`），**不在 `FilterBar` 里** —— 一个地方就能覆盖关键词、状态、标签、项目、SSE 全部触发源。`FilterBar` 仍然输入即 `emit`（输入框的手感不能因为防抖而变迟钝）。
- **输入即过滤的手感不变**（e2e `ideas.spec.ts:588` 依赖这个：填完关键词不用回车就该看到结果）。

### 13.3 无限滚动 + 往回翻历史（聊天软件模型）

用户明确要求两件事：**默认视图也走无限滚动**，以及**「类似聊天软件的那种，默认在最下边，往上翻才是历史」**。

现状已经是聊天软件式的（`HomePage.vue:193-200`：加载完 `scrollToBottom()`，底部是当下）。所以分页方向是**往回翻**：

- **初次加载** → `GET /api/ideas?...&limit=100`（最新一页）→ 渲染 → **滚到底部**（沿用现有的 `scrollToBottom()`）。
- **用户往上翻** → 接近**顶部**时预加载更旧的一页 → `GET /api/ideas?...&limit=100&offset=<已加载条数>` → 返回更旧的 → **prepend** 到列表顶部。
- **往下滚没有更多可加载**（底部就是当下）；新内容靠 SSE 进来。

**预加载的实现**：

- 列表**顶部**放一个哨兵元素，用 `IntersectionObserver` 观察，`rootMargin: '400px 0px 0px 0px'`（我的定）—— 距离顶部还有 400px 就发请求，不等用户真的翻到顶。
- 这就是用户说的「在快到底时就要预加载」的聊天版：**列表遍历方向的尽头是顶部**，预加载要提前在到达之前发生。
- 加载中要有 loading 态，且要防止重复触发（哨兵在加载完成前先摘掉）。

**prepend 之后必须修正滚动位置**：内容插到顶部会把用户正在看的内容往下推，视觉上会跳。标准做法：

```js
const before = container.scrollHeight;
list.value = [...older, ...list.value];
await nextTick();
container.scrollTop += container.scrollHeight - before;   // 把视口钉在原来那条上
```

**搜索视图用同一套模型**：搜索结果按时间序（§11.4），最新的命中在最下边，往上翻加载更旧的命中。

**「还有没有更旧的」怎么判**：请求 `limit + 1` 条，多出来那条说明还有（§11.1）。收到后把多出来那条丢掉，`已加载条数 += limit`。

> **F11 定位合并结果不受影响**：合并结果是最新的 → 一定在第一页（最新一页）里 → 定位正常。

### 13.4 前端的数据流

过滤条件下沉之后，**前端只有一层过滤判断**，位置在**数据层**（SSE 合并），不在显示层：

```
ideas.value        ← 服务端返回的【已过滤】数组（服务端已按 project/status/tags/q 过滤）
                    + SSE 增量（由 applyEvent 用近似判断决定进不进）
visibleIdeas       ← sortIdeas(ideas.value)   ← 只排序，不再过滤
```

**为什么显示层不再过滤**：`ideas.value` 里服务端返回的那部分**已经是过滤过的**。如果显示层再用近似的关键词判断过滤一遍，就会出现「服务端说命中、前端说没命中」的**误杀** —— 比如正文是 `咖 啡`（中间有空格），服务端命中（空格不算 token），前端 `includes("咖啡")` 不命中 → 明明搜到了却看不见。

**`applyEvent` 的过滤条件从 `datasetFilter` 改成 `activeFilter`**：

- `activeFilter` 含 project / status / tags / tagMode / **keyword**。
- keyword 这一项用**近似判断**（子串、忽略大小写），这就是用户选的「前端留一份近似判断」。
- `datasetFilter`（只按项目维度的那份）**移除**。它存在的理由是「数据集不能按显示条件过滤，否则切回别的过滤条件时条目找不回来」—— 过滤下沉之后，切换过滤条件会触发重拉，条目会从服务端回来，这条理由不再成立。

> ⚠️ 现有的单测 `web/src/pages/HomePage.spec.ts:1655`（「SSE 推来的新条目不匹配关键词时不显示（清掉关键词后它还在）」）**要改**：新架构下「清掉关键词后它还在」的成因从「本地数据集保留了它」变成「重拉后服务端把它带回来了」。断言的**结果**不变，但 mock 必须改成「清掉关键词后重拉返回两条」。

### 13.5 过滤条件变化与 SSE 的重拉

**过滤条件变化**（关键词、状态、标签、模式、项目）→ **防抖 250ms** → 重新 `GET /api/ideas`（带全部条件，`limit=一页`、`offset=0`）→ **整表替换** `ideas.value` → **滚到底部**（条件变了，命中集合也变了，之前的浏览深度没有意义）。

**SSE 事件到达**：

1. **立即**用近似判断决定这条要不要进列表（`applyEvent` 用 `activeFilter`）—— 多端同步的即时性保住。
2. 同时**防抖 250ms** 重新拉一次服务端结果，用权威结果覆盖本地列表。

**SSE 触发的重拉必须保留已加载的深度**：请求 `offset=0` + `limit = min(101 + 往上翻过的条数, 1000)`。

- 「往上翻过的条数」是一个**独立的计数**（往回翻一页时递增），**不是「列表当前长度」** —— 重拉是**整表替换**：列表长度会变成这次要到的条数，下一个事件又按更大的长度再要一页 → 每来一个事件列表就长一页（4 个事件就能把 500 条全塞进 DOM，哪怕用户从没往上翻过），`hasMore` 也跟着失真。
- 因为 `/api/ideas` 的分页是「从最新一端数」（§11.1），「最新的 N 条」正好就是用户当前看到的那一批（可能多出刚到的新条目，它们在最底下）。
- 如果只拉一页，用户往上翻了三页历史，重拉后历史就没了 —— 视口会突然跳回最新那批。
- 1000 是服务端 `limit` 的上限：超了回 400 `invalid_pagination`，一个 400 会让 SSE 的刷新彻底断掉。
- 返回的数组仍是 ASC（最老在顶），所以视口里那几条的**次序**不变 ✓；但重拉回来的列表顶部可能多出更旧的条目（内容从上面长出来），**视口要靠锚点修正钉回原处**（记住重拉前视口顶部那条，重拉后按它把视口钉回去；贴着底部就滚到底）。

**代价（用户已知并接受）**：英文上前后端语义不同（前端按子串、后端按词级），个别条目会**先出现再被收回**。中文上只有标点/空白分隔那种窄边界会有这个现象。

> **补充（实现期复审后细化记账规则）**
>
> 上面「往上翻过的条数是一个**独立的计数**，不是列表当前长度」这条**依然成立**
> —— 但这个计数**怎么算**改了。
>
> 起因：往回翻的那一页原来在**取回来的时候**记深度，调用方拿到这一页再决定要不要
> —— 记账分在两处，中间那个「深度记了、列表还没拼」的状态不自洽。复审实测复现了
> 一个由此而来的回归：过期的页被拼进列表 → 列表比深度多一页 → 下一次 SSE 重拉按
> 深度要条目 → 用户刚翻出来的历史凭空少一半。
>
> 现在的规则：
>
> - **取**（`fetchOlderIdeas`）**不写任何共享状态**，只返回这一页与「它是否过期」；
> - **用**（`prependOlder`）才把列表、深度、`hasMore` 在**一个动作**里一起改；
> - 过期页（回来时已经有更新的整表替换落地了）**整页丢掉** —— 它什么都没写，
>   丢掉也不用还什么；半用半丢会让列表既不是旧的也不是新的；
> - 整表替换（`loadIdeas`）落地后，深度按「这一页实收条数 − 头一页（101 条）」重算，
>   而不是「请求发出时清零 / 成功时不动」—— 两发重拉落地顺序颠倒时，后者会让
>   列表与深度脱钩。
>
> 「不是列表当前长度」没有被破坏：SSE 重拉的 `limit = 101 + 深度` 恒等于当前列表
> 长度，而响应回来的条数也等于 `limit` → 列表不会每来一个事件就长一页。

### 13.6 近似判断的语义

`lib/filter.ts` 的 `matchesKeyword` **保留**，语义不变（子串、忽略大小写），但**用途变了**：

- 以前：显示层的过滤依据。
- 现在：**只**用于 SSE 增量条目的即时判定（§13.4）。

`matchesFilter` 里的 keyword 判断**保留**（`applyEvent` 靠它），`hasActiveFilter` 里的 keyword 判定**不变**（关键词非空 = 有过滤生效 = 出现「清除」按钮）。

> 主规格 §11.4「客户端行为」里关于「客户端自行判定显不显示」的表述需要同步改写（§17）。

### 13.7 高亮渲染

`IdeaDto.matches` 到了前端，卡片正文要按区间切分渲染 `<mark>`。

- **不用 `v-html`**：把正文按命中区间切成若干段，用 `v-for` 渲染文本片段，命中的那几段包 `<mark>`。Vue 的文本插值自带转义，没有注入面。
- 只在「本次响应带了 `matches`」时启用高亮；SSE 增量条目没有 `matches`，不高亮。
- 命中区间可能重叠（同一段正文被多个查询段命中）→ **合并区间**后再切分。

#### 没有标题了：偏移直接相对完整原文

**没有标题了** —— 想法就是一段文字，`content` 全文都在正文里，没有「视觉标题」那一层。

所以 `matches` 的偏移**直接相对完整原文**（`content`），前端按命中区间切片段渲染即可，**不需要按行分组**、不需要拆出「标题组 / 正文组」。

> 换句话说：**服务端的偏移永远相对完整 `content`**（服务端不知道前端的渲染规则，契约不该跟前端耦合）；**前端负责把偏移映射到它实际渲染的那段文字上**。

## 14. Agent skill

- `skills/idea-manager/references/api.md` 的端点速查表加 `GET /api/search`，`GET /api/ideas` 的入参列加 `q` / `limit` / `offset`。
- 说明书里要说清楚三件 agent 容易踩的事：
  1. **英文是词级**：搜 `hel` 搜不到 `hello`，要输全词。
  2. **多段之间是 AND**：`咖啡 水温` 要求两个都出现，不要求相邻。
  3. **分页只省传输不省检索**，但省的是 agent 的上下文 —— 一次别要 100 条。
- `skills/idea-manager-client/`（取件器）**不用改**：它只取 skill 文档，不碰业务 API。
- `SkillDocGuardTests.cs` 必须仍通过。

## 15. 测试策略

### 15.1 后端单测

| 对象 | 断言 |
|---|---|
| `TextTokenizer` | 中文逐字、英文数字合并、小写化、标点丢弃、空白分隔、纯标点 → 空、混合中英文 |
| MATCH 串构造 | 单段 / 多段 / 空段跳过 / 全空 → 不过滤 / 无需转义（token 不含 FTS5 语法字符） |
| 偏移计算 | 命中区间正确、标点造成 token 间隔时仍能定位、多段各自命中、与 FTS 的命中集合一致 |
| `SearchMeta` / 回填 | 版本号缺失 → 重建；版本号已是当前 → 不重建 |

### 15.2 后端集成测试（HTTP 层）

| 场景 | 断言 |
|---|---|
| 中文子串 | 搜「咖啡」命中「咖啡机滤芯」 |
| 英文词级 | 搜 `hel` 不命中 `hello`；搜 `hello` 命中 `Hello` |
| 大小写不敏感 | 搜 `weather` 命中 `Weather today` |
| 标签命中 | 搜标签名能命中带该标签的想法 |
| 项目名命中 | 搜项目名能命中该项目下的想法 |
| 多词 AND | 两段都出现才算命中；不相邻也命中 |
| 相关度 | `/api/search` 里正文命中的排在项目命中的前面 |
| 分页 | `limit`/`offset` 正确；`hasMore` 正确；`total` 不受分页影响；**往回翻**：`offset=0` 是最新的一页，`offset=100` 是更旧的一页，且两页不重叠 |
| 回收站 | `/api/search` 默认含回收站并标 `trashed`；`includeTrash=false` 不含；`/api/ideas?q=` 不含 |
| 索引同步 | 写入 / 改正文 / 改标签 / 改项目名 / 软删 / 恢复 / 硬删后，索引与业务数据一致（**每条写路径都要有断言**） |
| 硬删 + rowid 复用 | 硬删后新插入的想法，索引不串行 |
| 老库回填 | 造一个「有想法但没索引」的库，启动后搜索能命中 |
| `too_many_ideas` | 不传 `limit` 时超上限报 400；传了 `limit` 时不报 |
| 错误码 | `invalid_pagination`、`invalid_search_query` 触发 |

`DatabaseInitTests.cs` 加断言：4 张新表 / 虚表存在。

### 15.3 前端单测

- `filter.spec.ts`：`matchesKeyword` 的近似语义**不变**；`matchesFilter` **仍然判 keyword**（`applyEvent` 靠它，§13.6）。
- `FilterBar.spec.ts`：防抖（连续输入只在 250ms 后发一次）。
- `HomePage.spec.ts`：过滤条件下沉（请求带上四个参数）、无限滚动预加载、高亮渲染、`matches` 缺失时不崩。
- ⚠️ `HomePage.spec.ts:1655`（「SSE 推来的新条目不匹配关键词时不显示（清掉关键词后它还在）」）**要改**：新架构下成因变了 —— 「清掉关键词后它还在」不再是「本地数据集保留了它」，而是「重拉后服务端把它带回来了」。断言的**结果**不变，mock 要改成「清掉关键词后重拉返回两条」。

### 15.4 e2e

**现有 4 条搜索用例的行为口径必须保持**（`web/e2e/ideas.spec.ts:565-700`）：大小写不敏感、搜不到显示空态、清空输入恢复全部、输入即过滤。

新增：

- 标签名 / 项目名能搜到想法
- 搜索结果带高亮
- 默认视图也是分页的：打开页面停在**最底下**（最新一条可见），往上翻到接近顶部时自动加载更旧的一页，且视口不跳
- `/api/search` 返回三类分组

`web/e2e/COVERAGE.md` 的用例计数与概览表同步更新。

### 15.5 契约与 AOT

- `openapi/idea-v1.json` 重新导出并让 `OpenApiDocumentTests` 通过。
- `SkillDocGuardTests` 通过。
- `server/scripts/aot-smoke.sh` 增加 `/api/search` 与 `/api/ideas?q=` 的冒烟 —— **AOT 退化只有这个脚本测得出来**。

## 16. 已知限制与取舍

| 限制 | 取舍 |
|---|---|
| 英文从子串变成词级 | 有意。中文是主战场；两套语义打架的 bug 极难查 |
| 标点与空白不再挡路：搜「咖啡水温」能命中「咖啡，水温」「咖 啡」 | 比今天的 `includes()` **更宽松**（§7.4）。有意 —— 标点是书写痕迹，不该挡路 |
| 「的」这类汉字仍然挡路：搜「咖啡水温」**不**命中「咖啡的水温」 | 与今天的 `includes()` 一致。逐字 token 的必然结果 |
| 多段查询是 AND：搜「咖啡 水温」能命中两者相隔很远的正文 | 比今天的 `includes()` **更宽松**（§7.4）。有意 —— 搜索引擎式的 AND 更符合直觉 |
| 项目改名要重建该项目下所有想法的索引行 | 几百到几千条，毫秒级；换来「搜项目名能搜到想法」 |
| `bm25()` 在逐字 token 上算相关度 | 统计的是「字符」而非「词」，但字符级的 TF/文档长度归一化本身合理；实测正文命中确实排前面 |
| 按相关度排序的分页不省检索 | 微信官方文章明确指出这个事实。我们接受它，换来 agent 侧省上下文 |
| 服务端分页切片在内存标签过滤之后 → 要把全部候选加载进内存 | 数据量几百到几千条，可接受。换来 `hasMore` 判定精确、不会漏命中（§11.1） |
| 索引与业务同事务 → 索引写失败会连累业务写 | 同库同事务换来的强一致值得；索引表启动时就建好，实际失败概率极低 |
| 启动时回填失败 → 应用起不来 | 有意（§10.4）。索引与业务不一致时搜索会给出**错误结果**，那比起不来危险得多 |
| 两个实例同时启动、共用同一个库文件 | 应用按设计是**单实例**（主规格 §3「不做多实例部署、水平扩展」），这个场景不受支持。回填在拿到写锁（Microsoft.Data.Sqlite 的 `BeginTransaction()` 发的是 **BEGIN IMMEDIATE**）后会**再读一次版本号**：第二个实例会等第一个提交、读到新版本、直接 no-op —— 不会重复重建。若等待超过 `busy_timeout`（默认 30 秒）它就启动失败，形式是「起不来」而不是「给出错误结果」 |
| FTS5 无 `offsets()` → 命中位置自己算 | 微信为这个专门重写了辅助函数（他们的 `offsets()` 占 70%+ 耗时）；我们的正文很短，自己算更简单也更准 |

## 17. 需要改写的主规格条目

`docs/superpowers/specs/2026-11-11-idea-manager-design.md` 里下面这几条与本文档冲突，需要改写：

| 位置 | 现状 | 改成 |
|---|---|---|
| §2「目标」的第 4 条 | 「关键词为客户端子串匹配，见 §10.2」 | 关键词走后端全文检索（本文档 §7、§11） |
| §3 非目标 | 「**不做服务端**全文检索（关键词过滤在客户端做，见 §10.2）」 | 删掉这一句 |
| §10.2 过滤 | 「过滤在客户端做……服务端不知道每条连接当前在搜什么」 | 状态/标签/关键词都走后端；服务端**知道**关键词（`q` 参数） |
| §11.4 客户端行为 | 「客户端按当前的项目/状态/标签/关键词条件自行判定是否展示」 | 状态/标签/关键词由服务端过滤；SSE 增量条目仍由客户端**近似**判断 + 防抖重拉（本文档 §13.4–13.5） |
| §20 已定决定一览 | 「关键词过滤：客户端对正文做子串匹配（忽略大小写），不做服务端全文检索」 | 改成指向本文档 |

## 18. 留给实现者的空白

以下几处可以在实现时自行决定，做错了也能改回来：

1. **分词器与偏移计算的具体代码组织**：放一个文件还是拆成「分词 / 切分 / 构造 MATCH / 算偏移」四个纯函数，实现者定。
2. **`ReadTagNamesOfCandidates` 是否要避开重复的 FTS 匹配**（§9.4 的注释）：先按现有写法实现，实测慢再优化。
3. **`total` 的算法**：两次查询（COUNT 一次、取页一次）还是窗口函数 `COUNT(*) OVER()`。默认两次查询 —— 更稳。
4. **`project_fts` / `tag_fts` 的 rowid 取法**：用 `SELECT rowid` 还是别的，只要映射稳定。
5. **前端往回翻的具体实现**：`IntersectionObserver` 的 `rootMargin` 取值（本文档给了顶部 400px 的建议值）、loading 态的呈现方式、哨兵元素插在列表的哪个位置、prepend 后修正 `scrollTop` 的具体写法。
6. **前端高亮的组件拆分**：切分逻辑放 `lib/` 还是组件内。
7. **前端「搜索中」的状态呈现**：加不加 loading 指示、还是让旧列表留在屏幕上直到新结果到达。

## 19. 已定的关键决定一览

| 决定 | 选择 | 来源 |
|---|---|---|
| 端点形态 | **C**：两个都要 —— `/api/ideas?q=` 给页面筛，`/api/search?q=` 给 agent 全库找 | 用户拍板 |
| 英文匹配语义 | **乙**：词级（照微信），放弃英文的任意子串 | 用户拍板 |
| 排序 | **丙**：`/api/search` 按 `bm25()` 相关度，`/api/ideas?q=` 保持 `updated_at ASC, id ASC` | 用户拍板 |
| 检索的实体 | **丙**：想法 + 项目 + 标签，`/api/search` 分三组返回 | 用户拍板 |
| 回收站 | `/api/search` 默认含，每条标 `trashed`；`/api/ideas?q=` 只搜未删的 | 用户拍板（未反对我的提案） |
| 索引同步方式 | 仓储层同步维护，**不用 SQL 触发器** | 用户拍板（未反对我的提案） |
| 前端用哪个端点 | 只用 `/api/ideas?q=`；`/api/search` 是给 agent 的增值 | 我的定（用户未反对） |
| SSE 与过滤下沉 | **甲**：前端保留近似判断 + 防抖 250ms 重拉后端权威结果 | 用户拍板 |
| 命中位置的表示 | **甲**：返回字符偏移 `matches: [{field,start,length}]`，前端渲染 `<mark>`，不用 `v-html` | 用户拍板 |
| 分页 | `/api/search` 完整分页（`limit`/`offset`/`total`/`hasMore`）；`/api/ideas` 加可选 `limit`/`offset`，响应形状不变 | 用户提的需求 + 我的定 |
| 前端分页交互 | **无限滚动**，方向是**往回翻历史**（聊天软件模型：默认停在最下边，往上翻加载更旧的），且**提前预加载**（不等翻到顶） | 用户拍板 + 明确补充（第二轮追加：默认视图也走无限滚动、聊天软件式） |
| 分页启用的时机 | **默认视图也启用**（不是只有搜索时） | 用户拍板（第二轮追加） |
| 中文分词 | 逐字切 + `unicode61` 短语查询；不用 `trigram`（2 字词查不了） | 我的定（基于实测） |
| 索引结构 | 四个 FTS5 虚表 + 一张 `search_meta` 表；不用 external content | 我的定 |
| 项目名 / 标签名 | 进想法的索引（`project` / `tags` 列） | 我的定（用户未反对） |
| 索引与业务的一致性 | 同库同事务，强一致（不抄微信的读写分离） | 我的定 |
| 标点处理 | **半角与全角一律丢弃**（`Rune.IsPunctuation` / `Rune.IsSymbol`），当作分隔符 | 我的定（评审补出全角标点这个坑） |
| 遍历单位 | Unicode 标量值（rune），不是 UTF-16 code unit | 我的定 |
| 关键词条件的 SQL 形态 | `idea.rowid IN (SELECT rowid FROM idea_fts WHERE idea_fts MATCH $q)` —— 子查询而非 JOIN，为的是让主查询与标签查询共用同一份 WHERE | 我的定（评审补出 JOIN 形式与现有 `BuildFilter` 不兼容） |
| 分页切片位置 | **内存标签过滤之后**（SQL 不带 LIMIT，与今天的 `IdeaService.List` 一致 —— 它本来就把全部结果读进内存再检查上限） | 我的定 |
| 分页方向 | **往回翻历史**：`offset` 从**最新**一端数（`/api/ideas`）；`/api/search` 的 `offset` 从**最相关**一端数 | 用户拍板（聊天软件模型）+ 我的定 |
| `too_many_ideas` | 不传 `limit` 时照旧报；传了 `limit` 就不报 | 我的定 |
| `limit` / `offset` 的类型 | `string?`（OpenAPI 里是 `string`），由服务层自己解析：不是数字、越界一律 `invalid_pagination`；**`offset` 单独给只有 `/api/ideas` 算非法**（`/api/search` 的 `limit` 有默认值 20，单独给 `offset` 合法）—— 绑 `int?` 时非数字输入回的是**空 body 没有 code** 的 400（实测，见 §11.5） | 我的定（实测后改，见 §11.5） |
| `trashed` 标志的位置 | `IdeaDto.trashed` / `ProjectDto.trashed` 两个可选字段，不新增 DTO 类型 | 我的定 |
| 回收站口径 | `/api/search` 默认全收（含已删项目下的想法、已删项目），标 `trashed`；`tags[].count` 口径不变 | 我的定 |
| 前端过滤判断的位置 | 只有**一层**，在数据层（`applyEvent` 用 `activeFilter`）；显示层只排序不过滤；`datasetFilter` 移除 | 我的定（评审指出「显示层再过滤会误杀服务端结果」） |
| `matches` 的偏移基准 | 相对**完整 `content`**；前端直接按区间切片段渲染（没有标题层，不需要分组） | 我的定 |
| 启动时回填失败 | **fail fast**：启动失败并打清楚日志 | 我的定 |
| 排序权重 | `bm25(idea_fts, 5.0, 5.0, 3.0)` = 正文/标签/项目 | 我的定 |
| 偏移单位 | UTF-16 code unit（对齐 JS 的 `slice`） | 我的定 |
| 老库迁移 | `search_meta` 版本号 + 启动时同步回填，不建迁移框架 | 我的定 |
