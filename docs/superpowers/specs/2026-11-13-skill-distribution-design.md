# Agent Skill 随服务分发 · 设计文档

- 日期：2026-11-13
- 状态：待评审
- 前置：[`2026-11-11-idea-manager-design.md`](2026-11-11-idea-manager-design.md)（本项目的总设计，本文只解决「skill 怎么分发」这一件事）

---

## 1. 意图与背景

现在的 agent skill 是一份**提交进仓库的静态副本**：`skills/idea-manager/` 下的
`SKILL.md`（说明书）、`scripts/idea.py`（933 行命令行封装）、`references/openapi.json`（契约副本）。
agent 侧把它软链或复制进 `~/.claude/skills/` 或 `~/.zcode/skills/`。

这套做法有一个随时间越来越明显的问题：**skill 描述的是「服务曾经是什么样」，
而 agent 连的是「服务现在是什么样」**。服务改了 API、改了错误码、改了某个命令的语义，
agent 手上的说明书还停在原地，于是 agent 会按旧语义操作 —— 比如以为「覆盖式设标签」
实际已经改成并集追加，或者去调一个已经不存在的路径。

已经漂过的有三处，性质还不一样：

| 内容 | 现状防护 | 漂移后果 |
|---|---|---|
| `references/openapi.json` | **已有字节级守卫**（`OpenApiDocumentTests.The_skill_copy_of_the_contract_is_byte_for_byte_the_same_file`）；服务端本来就实时发 `/openapi/v1.json` | 几乎为零 |
| `SKILL.md` 的散文 | 只有 413 那几个数字被 `RequestBodyLimitGuardTests` 用正则钉住；能力清单、错误码表、语义说明**全是裸奔** | 最真实的漂移源：agent 按错语义写数据 |
| `scripts/idea.py` | 有端到端测试（`skills/idea-manager/tests/test_cli.py`），只覆盖测过的行为 | 中等 |

本设计的方案，两条腿：

1. **skill 的真实内容随服务一起分发**，agent 侧只保留一个指向服务内容的薄壳 ——
   服务端是唯一权威，agent 读到的说明书与它正在用的服务**来自同一次发布**。
2. **删掉命令层**：不再有 `idea.py` 这个 933 行的命令行封装。
   agent 用 `curl` + 服务端实时发的契约直接调 API ——
   命令层重复实现了契约里已有的信息，是最大的一块漂移面。

删掉命令层会带走一样东西：**脱敏**。今天 `idea.py` 的 stdout 会遮掉 webhook 的
`url`（Telegram 的 bot token 就粘在路径里）与 `headers`（`Authorization: Bearer …` 就在里面），
理由写在 SKILL.md 里：「CLI 的 stdout 会进 agent 的上下文与日志，那就是日志」。
裸 curl 会把这两样原样带进上下文。**所以脱敏要搬到服务端**（§9.4）——
这既补上了洞，也让它从「客户端自觉」变成「服务端机制」。

## 2. 目标

1. **消除漂移**：agent 读到的说明书与它正在连的服务来自同一次发布。
2. **渐进式披露**：说明书按层拆分，agent 默认只读索引页，需要哪份细节才取哪份。
3. **agent 侧零业务代码**：不再有命令层，agent 用 curl + 契约直接调 API。
4. **agent 侧几乎不用维护**：安装完以后，服务端升级不需要重新安装 skill。
5. **凭据不过 API 默认边界**：webhook 的 url 与 headers 默认遮蔽回传，
   不主动索取就拿不到原始凭据。
6. **取件失败要能自解释**：取不到说明书时，agent 拿到的是一条可执行的提示，不是空白。
7. **保留构建期守卫**：内容从服务端来，不等于内容可以写错 —— 关键断言仍要在 CI 里过。

## 3. 非目标

- **不追求离线可用**。agent 的每条操作都要打服务端，服务端不可达时说明书留着也没用，
  所以「说明书依赖网络」几乎不损失可用性。为离线保留一份本地副本，等于把漂移又请回来。
- **不做多实例的版本协商**（例如「你手上的说明书属于哪个实例」）。单用户单实例，不做。
- **不做 skill 内容的独立分发渠道**（CDN、npm 包、独立的 skill 服务器）。跟着本服务走。
- **不做通用 API 代理**：不提供「fetch.py --api 替你发请求」这类东西。
  agent 自己发 curl，服务端不替它包装请求。
- **不改 nginx 的认证模型**：`/skill/**` 与 `/api/**` 走同一套 basic auth。
- **不引入第三方依赖**：`fetch.py` 只用 Python 标准库，Python 3.9+ 可跑。
- **除 §9.4 的 webhook 遮蔽外，不改数据 API 的形状**。

## 4. 反目标（做到这些就算失败）

- **不能让 agent 读到「半新半旧」的内容**：手册与契约必须是同一版本。
  宁可整体不可用，也不能让 agent 拿着新版手册去调旧版接口。
- **不能让 skill 目录变成一个隐形的第二份服务**。服务端只发**只读**内容，
  不在 `/skill/**` 上做任何有副作用的事（不建项目、不发 webhook）。
- **不能把凭据写进 skill 目录、缓存目录或任何日志**。取件从环境变量读凭据。
- **不能让「取不到说明书」变成静默降级**。取件失败必须报错退出，不能装作没有说明书继续干活。
- **不能让路径打错变成「读到了别的文件」**。`/skill/**` 下的路径打错必须回 404 + 结构化错误，
  不能落到 SPA 兜底 —— 理由与 `Program.cs` 里 `Map("/api/{**rest}")` 那段注释一样：
  agent 只看状态码的话，200 + HTML 会被当成成功。
  **口径的边界**（评审实测后修订）：Kestrel 会先把路径里的点号段**归一化**掉，
  所以 `..` 逃出 `/skill/` 前缀的请求根本不会命中 `/skill/**` 的任何端点 ——
  它落到应用既有的 SPA 兜底（200 + HTML）。这是**全应用一致**的行为（`/api/**` 的路径同样如此），
  不是本设计引入的。本设计保证的是：
  ① **读不到任何文件**（穿越拿不到 `/etc/passwd`）；
  ② **归一化后仍在 `/skill/` 下的路径**（含 `%2f` 那种不被归一化的形态）回 404 + 结构化错误；
  ③ 取件器逐文件校验 sha256，服务端给回来的内容与清单对不上时它报 `skill_integrity_error`，
  不会把 HTML 当手册用。
- **不能让 webhook 的凭据默认回传**。url 里的 bot token、headers 里的 Bearer 令牌，
  不主动索取就拿不到。

## 5. 约束

| 约束 | 来源 |
|---|---|
| 单用户、单实例、访问控制在 nginx 层（basic auth） | 既有设计 §5 |
| .NET 10 Native AOT | 既有设计 §5；服务端发文件不引入反射 |
| skill 侧的脚本只用 Python 标准库 | 既有 skill 计划（Task S0 全局约束） |
| `dotnet publish` 产物 = 一个自包含二进制 + `wwwroot/` | 既有设计 §2；本文再加一个 `skill/` 目录 |
| 服务的对外地址运行期才定（nginx 决定） | 既有设计 §16；所以 agent 侧的地址从环境变量来，不写死 |
| webhook 的更新接口已是三态（`null` = 不改） | `WebhookService.cs:114`；agent 读到遮蔽值也能只改别的东西 |

## 6. 概念

| 概念 | 说明 |
|---|---|
| **内容源** | 仓库里的 `skills/idea-manager/`：索引页、参考文档。**唯一编辑源**，人在 git 里写、review、改 |
| **分发副本** | 发布产物里的 `skill/idea-manager/`：内容源经构建拷过去的副本。服务端只读它 |
| **薄壳** | agent 侧安装的东西：`SKILL.md`（指针）+ `scripts/fetch.py`（取件器）。**不含**手册正文 |
| **清单（manifest）** | 服务端对分发副本的描述：版本串、文件清单、每文件哈希、入口文档路径 |
| **版本** | 清单里的 `version`：分发副本内容的哈希。内容不变则版本不变 —— 不需要人肉维护版本号 |
| **取件** | 按清单把内容取到本地缓存，校验后整体换上，并按需打印 |
| **信任边界** | 取件回来的是**指令**（手册）。它与数据 API 同一个信任域、同一个自托管实例 |

## 7. 总体形态

```
仓库 skills/idea-manager/            ← 唯一编辑源（git）
  SKILL.md                           ← 索引页（渐进式披露的第一层）
  references/*.md                    ← 按需读取的细节
        │
        │  publish（csproj 的 Content + CopyToPublishDirectory）
        ▼
发布产物 skill/idea-manager/          ← 分发副本（服务端只读）
        │
        │  GET /skill/idea-manager/version   → 清单
        │  GET /skill/idea-manager/{path}    → 文件
        ▼
agent 侧 ~/.zcode/skills/idea-manager/   ← 薄壳
  SKILL.md                              ← 指针（frontmatter + 怎么取）
  scripts/fetch.py                      ← 取件器（唯一入口）
        │
        │  fetch.py 按清单同步到 ~/.cache/idea-skill/<版本>/（校验后整体换上）
        ▼
   缓存副本（索引页 + references/）
```

**agent 侧的完整操作序列**：

```bash
# 1. 取索引页（手册）
~/.zcode/skills/idea-manager/scripts/fetch.py

# 2. 按索引页里的路由表，需要哪份细节取哪份
~/.zcode/skills/idea-manager/scripts/fetch.py references/errors.md

# 3. 按索引页给的 curl 姿势直接调 API（内容来自索引页与契约）
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @body.json
```

一次安装、一次升级、零手工同步。

## 8. 渐进式披露的拆分

原则：**agent 的上下文里只该有待会儿真正要用的东西**。所以内容按「加载时机」分四层：

| 层 | 加载时机 | 进不进上下文 | 本方案里的落点 |
|---|---|---|---|
| L0 frontmatter | **永远**（agent 扫本地目录时） | 只进 agent 的 skill 清单 | 薄壳的 `SKILL.md` frontmatter |
| L1 索引页 | skill 被触发时 | 进上下文（一次） | 服务端 `SKILL.md` |
| L2 参考文档 | **按需**（agent 判断需要时） | 进上下文（只读它要的那份） | 服务端 `references/*.md` |
| L3 契约 | **按需** | 进上下文（只读它要的那部分） | 服务端 `/openapi/v1.json` |

> L0 必须在本地：agent 运行时的 skill 清单是**扫本地目录**得到的，它不联网。
> 所以「agent 侧只留一个地址」严格说做不到 —— 至少要留 frontmatter 与取件入口。
> 这是硬约束，不是设计取舍。

### 8.1 拆分后的文件结构

内容源（仓库）：

```
skills/idea-manager/
  SKILL.md                    # L1 索引页：能力清单 + 路由表 + 最易错的坑 + curl 模板
  references/
    calling.md                # L2 怎么用 curl 调：认证、Content-Type、请求体、特殊字符、各方法的例子
    errors.md                 # L2 错误处理：ProblemDetails、code 对照表、413 的两种部署形态
    webhooks.md               # L2 webhook 特有语义：遮蔽、scope/project 对齐、body 模板、事件类型
    api.md                    # L2 契约在哪 + 索引页没列的端点 + SSE
    scenarios.md              # L2 典型场景（curl 版）
```

薄壳（agent 侧安装）：

```
~/.zcode/skills/idea-manager/
  SKILL.md                    # L0 frontmatter + 指针（不含正文）
  scripts/
    fetch.py                  # L3 取件器（唯一入口）
```

薄壳的**源**在仓库的 `skills/idea-manager-client/`（与内容源分开，
免得被当成分发内容拷进产物 —— csproj 的 glob 是 `skills/idea-manager/**`，
按目录段精确匹配，`idea-manager-client` 不在其中）。

**`references/` 是平铺的**：只有上面这五份 `.md`，**下面不许再开子目录**。
这是 §14「内容源形状」那条守卫断言的不变量 —— 守卫比书面契约严是最糟的做法，
所以这条在这里写明。

> 分发链路本身**支持**嵌套（csproj 的 `references/**/*.md` 会保留目录层级，
> `SkillCatalog` 递归枚举、`fetch.py` 按相对路径取文件）。真要拆子目录时，
> 改这条与那条守卫一起改 —— 那是一次有意识的结构调整，不是意外。
>
> **点开头的目录是例外**：`.cache/`、`.vscode/` 这类编辑器与工具留下的残留不算内容源的一部分，
> 守卫不认它们。但注意 MSBuild 的 glob 与 .NET 的递归枚举**都会**把点目录里的 `.md` 收进去 ——
> 所以「点目录不是内容」只对**形状**成立，里面的 `.md` 仍然受手册的各条守卫管。

### 8.2 索引页（`SKILL.md`）该放什么

索引页是 agent 触发 skill 后唯一必然读的一页，它要回答四件事：
**有哪些能力**、**怎么调**、**细节去哪找**、**最易错的是什么**。控制在 **150 行以内**。

| 章节 | 内容 | 行数量级 |
|---|---|---|
| 这是什么 | 一句话 + 内容来自服务端 | 5 |
| 连接配置 | 4 个环境变量（`IDEA_BASE_URL` / `IDEA_USER` / `IDEA_PASS` / `IDEA_TOKEN`） | 10 |
| curl 模板 | 一个最小可用的调用样板（方法 + 认证 + Content-Type + body） | 15 |
| 能力清单 | 能力 → **方法 + 路径**（表格，一行一个） | 35 |
| 路由表 | 「要 X → 读 `references/X.md`」 | 15 |
| 最易错的坑 | 不读就会做错的那几条（见 §8.3） | 20 |
| 错误的最短约定 | 4xx/5xx 回 ProblemDetails，带机器可读的 `code` | 10 |
| 契约位置 | `/openapi/v1.json`（取件方式） | 5 |

能力清单里的 API 路径**统一写成 `方法 + 路径`**（如 `POST /api/ideas`），
守卫要机器抽取这批路径做双向比对（§14）。

### 8.3 「最易错的坑」为什么必须留在索引页

渐进式披露有个陷阱：**被拆到 reference 里、而 agent 不知道该去读的内容，等于没了**。
所以「不做就会做错」的少数几条必须留在索引页。挑出来的标准是
「照着旧语义执行会写坏数据」，而不是「解释性内容」：

1. **覆盖式设标签**是 `PUT /api/ideas/{id}/tags`；**并集追加**是
   `POST /api/ideas/bulk/tags`（但它给 `replace: true` 时也是覆盖式 ——
   `tags` 漏了就把标签清空）。用错了会丢标签。
2. **`PATCH` 只发要改的字段**：`null` = 不改；**空串 / 空数组常常是「真的清空」**
   （想法的 `title` 空串 = 清成无标题；webhook 的 `headers` 给 `{}` = 清空请求头）——
   但**不是所有字段都吃这一套**：想法的 `content` 与项目的 `name` 给空串是 400，
   项目的 `name` 还**必填**（不给就是 400 `invalid_project_name`）。
   逐端点的语义见 `references/api.md`。
3. `POST /api/webhooks` **必须给 body 模板**；`scope=global` **不能**带 `projectId`，
   `scope=project` **必须**带一个存在的项目 id。
4. `POST /api/ideas/merge` 会**硬删**源想法（不在回收站里）。
5. **webhook 的 url 与 headers 默认是遮蔽版**（§9.4）：拿到的不是原值，
   别拿它去当"现在的地址"。
6. **`done` 必填**：`POST /api/ideas/{id}/complete` 与 `POST /api/ideas/bulk/complete`
   不给 `done` = `false` = **取消完成**，服务端**不报错**。要标完成就给 `{"done":true}`。

其余解释性内容（每个参数的细节、错误码全表、遮蔽规则）都拆到 reference。

### 8.4 路由表的写法

索引页里要有一张「要什么 → 读哪份」的表，agent 照着取：

| agent 想做的事 | 取哪份 |
|---|---|
| 请求体怎么写、特殊字符怎么办、认证怎么带 | `references/calling.md` |
| 命令报错了、拿到一个 `code`、要判断怎么处理 | `references/errors.md` |
| 配 webhook、订阅事件、url 与 headers 的遮蔽 | `references/webhooks.md` |
| 某个端点的完整参数与 schema、索引页没列的端点、SSE | `references/api.md` |
| 要一个能照抄的完整例子 | `references/scenarios.md` |

### 8.5 薄壳里不许出现的东西

薄壳是 agent 在取到正文之前唯一读到的东西，它**必须与内容源解耦**：

- 薄壳里**不许出现任何服务端文件名**（不许写 `references/errors.md`、不许写 `SKILL.md`）。
  服务端哪天改了拆分，薄壳会指向不存在的文件 —— 而薄壳恰恰是不该跟着变的那一份。
  路由表在**服务端**的索引页里，薄壳只说「先取索引页」。
- 薄壳里**不许出现任何 API 路径**（不许写 `/api/ideas`）。路径表在服务端索引页里。
- 薄壳里只允许出现：frontmatter、取件器的用法、`IDEA_*` 环境变量名、取件失败的排查提示。

> **边界**：`fetch.py` 是**协议实现**，不受上面两条约束 ——
> 它可以出现入口文件名，因为协议本身就得知道入口在哪。
> 本设计让协议**由清单驱动**：清单里声明 `entry`（索引页）的路径，
> 所以连 `fetch.py` 都不用把那个名字写死。
> 「薄壳不许出现服务端文件名」这条守卫**只扫薄壳的 `SKILL.md`**（§14）。

## 9. 服务端职责

### 9.1 `/skill/**` 端点

新增一组**只读**端点，路径前缀 `/skill/`（不进 `/api/**`：数据 API 的契约不因它们变浑浊）：

| 端点 | 作用 |
|---|---|
| `GET /skill/idea-manager/version` | 取清单（版本 + 文件清单 + 每文件哈希 + 入口路径） |
| `GET /skill/idea-manager/{**path}` | 取分发副本里的某个文件（相对 skill 根目录） |

外加一条兜底（**必须有**，理由见 §4）：

| 端点 | 行为 |
|---|---|
| `GET /skill/{**rest}` | 404 + ProblemDetails `skill_endpoint_not_found`（路径打错、skill 名字拼错） |

> `version` 是字面量段、`{**path}` 是 catch-all，字面量优先 —— 两者共存不冲突。

**清单的形状**：

```json
{
  "version": "3f2a…",
  "entry": "SKILL.md",
  "files": [
    { "path": "SKILL.md",              "sha256": "…", "size": 5123 },
    { "path": "references/calling.md", "sha256": "…", "size": 9012 }
  ]
}
```

- `version` = 参与分发的所有文件的 `(path, sha256)` 排序后拼接再哈希 ——
  **同一个内容永远得到同一个版本串**，不需要人肉维护版本号。
- `entry` = 索引页在分发副本里的相对路径。协议数据驱动，改文件名不用改 `fetch.py`。
- `files` 里**不含**开发侧文件（`tests/`、`__pycache__/`）。

**取文件的要点**：

- **路径穿越防护**：把相对路径拼到 skill 根目录后取 `Path.GetFullPath`，
  **必须仍以根目录 + 目录分隔符开头**，否则 404。
  一个能从 URL 里读任意文件的端点是严重漏洞 —— 穿越尝试一律回
  `skill_file_not_found`，不泄露路径是否存在。
- **内容类型**：`.md` → `text/markdown; charset=utf-8`，`.json` → `application/json`。
- **ETag**：用文件内容的 sha256。取件器带 `If-None-Match`，服务端回 304 → 省带宽。
  （可选，不做也不影响正确性。）
- **目录缺失时不要 500**：开发期或部署漏了分发副本时，回 404 + 结构化错误，
  不能抛异常（既有教训：`.NET 10 的 wwwroot 缺失时装 NullFileProvider` —— 静默失败比报错更坏）。

**错误响应**：沿用应用既有约定 —— `ApiException` + `ApiResults.Run` → RFC 9457 的
ProblemDetails，带扩展字段 `code`。新增三个错误码，**登记进 `ApiErrorCodes`**：

| code | 状态码 | 含义 |
|---|---|---|
| `skill_not_distributed` | 404 | 服务端没有分发 skill 内容（部署漏了 `skill/` 目录，或 skill 根目录配错） |
| `skill_file_not_found` | 404 | 分发副本里没有这个文件（含路径穿越尝试） |
| `skill_endpoint_not_found` | 404 | `/skill/**` 下的路径没有对应端点 |

> 登记进 `ApiErrorCodes` 是有意的：那份清单本来就管着「契约里的错误码 ↔ 代码里抛的错误码」，
> `ApiErrorCodeTests.Documented_codes_match_the_ones_actually_thrown` 会自动把它们纳入核对。
> 手册的 `references/errors.md` 里对应地分成两张表：数据 API 的错误码、取件相关的错误码，
> 守卫断言两表的并集 == `ApiErrorCodes.All`。

**进不进 OpenAPI 契约**：**进**。契约是服务表面的如实快照，`/skill/**` 是服务表面的一部分。
让它进契约（重新导出 `openapi/idea-v1.json`），免去「哪些端点进契约、哪些不进」这类特殊规则。

### 9.2 分发副本怎么进产物

- `IdeaApp.csproj` 里把 `skills/idea-manager/**` 作为 `Content` 带上
  `CopyToOutputDirectory` 与 `CopyToPublishDirectory`，`Link` 落到 `skill/idea-manager/`。
- **排除**开发侧文件：`tests/**`、`__pycache__/**`。
  （`__pycache__` 已被 `.gitignore` 排除，但构建输出目录里可能有 —— 拷贝时按名字排掉。）
- **skill 根目录做成配置项** `Skill:Path`（沿用既有的 `IDEA_` 前缀 + `__` 分层约定），
  默认值 `Path.Combine(AppContext.BaseDirectory, "skill", "idea-manager")`；
  相对路径按「应用目录」解析（与 `Database:Path` 同一条约定）。
  - 做成配置项是为了**测试**：集成测试把 `Skill:Path` 指到仓库里的 `skills/idea-manager/`，
    就不依赖「被引用项目的 `Content` 会不会流到测试项目输出目录」这条构建行为
    （那条行为随 SDK 版本与 `Link` 元数据而变，不可靠）。
  - 发布产物仍走 csproj 的 `Content` —— 部署时不需要配任何东西。

### 9.3 契约副本的处理

`skills/idea-manager/references/openapi.json` 这份手工 `cp` 的副本**删掉**：
服务端本来就实时发 `/openapi/v1.json`，而且「运行时文档 == 仓库里的契约文件」这条守卫
（`OpenApiDocumentTests.Committed_document_matches_the_running_app`）已经在跑。

- 手册改为指向 `/openapi/v1.json`，取件方式：`fetch.py /openapi/v1.json`。
- 那条「skill 副本逐字相同」的守卫测试随之删除（它守的东西已经不存在了），
  换成一条「skill 目录里不再有契约副本」的断言 —— 防止以后有人又 `cp` 一份回来。
- **手册里不写死「NN 条路径」这类数字**：契约一变它就过期。写「完整契约见 `/openapi/v1.json`」。

### 9.4 webhook 凭据默认遮蔽（数据 API 的唯一改动）

删掉命令行封装之后，脱敏从客户端搬到服务端 —— **机制，而不是约定**。

**规则**：凡是把 webhook 返回给客户端的读接口，**默认返回遮蔽版**；
客户端要完整值，得显式索取。

| 端点 | 默认 | 显式索取完整值 |
|---|---|---|
| `GET /api/webhooks`（含 `projectId` 变体） | 遮蔽 | `?unmask=true` |
| `GET /api/webhooks/{id}` | 遮蔽 | `?unmask=true` |

写接口（`POST` / `PATCH`）的**响应一律遮蔽** —— 写操作的响应里没必要带完整凭据。
（这意味着前端在写完之后要重新拉一次列表，见 §9.5。）

**遮蔽规则**（服务端实现，比原 CLI 的规则简单）：

| 字段 | 遮蔽后 |
|---|---|
| `url` | 保留 `scheme://host[:port]`；**路径每一段都换成 `***`**（段数保留）；query、fragment、userinfo 一律丢弃 |
| `headers` | 只保留键名，值一律 `***` |

> 原 CLI 的规则更讲究（结构词白名单、`<BOT_TOKEN>` 占位符保留、`/bot<token>` → `bot***`），
> 那是为了让人看得懂「这是个什么形状的地址」。服务端遮蔽版是给 agent 用的：
> agent 只需要知道「有个 webhook 指向哪个 host」，不需要看懂路径结构 —— 所以简单即可。
> 需要看真实形状的人（页面上的人）用 `?unmask=true`。
> `GET /api/webhooks/templates/telegram` 返回的是**预设**（url 里是 `<BOT_TOKEN>` 占位符，不是真凭据），
> 不遮蔽。

**为什么不需要「写只读」那套三态**：更新接口**已经是**三态 ——
`WebhookService.cs:114`：`Url = request.Url is null ? existing.Url : NormalizeUrl(request.Url)`。
所以 agent 读到遮蔽版之后，照样可以只改 events、只改 body 模板、只改 enabled，
把 `url` 与 `headers` 整个略过（不传那两个键）—— 不会把遮蔽值写回去。

**这层的取舍要说清楚**：

- `?unmask=true` 是**公开的**（任何有凭据的客户端都能要）。它防的是**顺手泄漏**
  （agent 读列表就把 token 抄进上下文），不是**蓄意窃取**（agent 明确要一个凭据）。
  这与原 CLI 的默认遮蔽是同一档保护，只是从客户端挪到了服务端。
- 原 CLI 下 agent **永远**拿不到原始 url；改成服务端默认遮蔽后，agent 主动加
  `?unmask=true` 就能拿到。手册会写明「除非用户明确要看原始 url，否则不要加这个参数」。
- 真要彻底封死（连用户自己也不给看），就把 `?unmask=true` 去掉，页面改成
  「url 留空表示不改」—— 那是另一种产品取舍，本文不做。

### 9.5 前端跟着改的地方

- `web/src/components/WebhookManager.vue` 的 `load()`（第 44-45 行的两次 `apiGet('/webhooks')`）
  带上 `unmask=true` —— 页面是给人看的，要看到真实配置、编辑表单要预填原值。
- `toggleEnabled()` 用 `PATCH` 的响应更新本地状态，而写接口的响应是遮蔽版 →
  改完直接重新 `load()`（多一次 GET，换来状态一致），不要拿遮蔽版覆盖列表里的行。
- `WebhookForm.vue` 的预填与提交逻辑**不用改**：它拿到的是完整值，提交回去的也是完整值。

## 10. agent 侧职责

### 10.1 薄壳 `SKILL.md`

内容就是 §8.5 那几条：frontmatter + 取件用法 + 环境变量 + 排查提示。
**不含任何正文**。

frontmatter 与内容源 `skills/idea-manager/SKILL.md` 的 frontmatter **必须一致** ——
用一条守卫测试钉住（`name` 与 `description` 逐字相同）。
description 描述的是能力，能力变了 description 就该变；这条守卫会逼着人同步，
而薄壳是唯一需要 agent 侧更新的东西 —— 同步它是有意的、可控的。

### 10.2 取件器 `fetch.py`

纯标准库，Python 3.9+。三种用法（第三种是第二种在绝对路径上的特例）：

| 用法 | 行为 |
|---|---|
| `fetch.py` | 同步后打印索引页（清单里的 `entry`） |
| `fetch.py <相对路径>` | 同步后打印该文件（`references/errors.md` …） |
| `fetch.py /<绝对路径>` | 打印服务端的绝对路径（`/openapi/v1.json`） |

**它只做取件，不发业务请求** —— 业务请求由 agent 自己用 curl 发（§11）。

**同步的原子性**（反目标 §4 第一条靠它保证）：

1. 取清单。缓存目录名 == 清单的 `version` → 已是最新的，直接用。
2. 不一致 → 把清单里 `files` 的每个文件下载到 `<缓存>/.tmp-<version>/`。
3. **逐个校验 sha256**。任何一个对不上 → **整个同步作废**，保留旧版本，报错退出
   （宁可整体不可用，也不要半新半旧）。
4. 全部校验通过 → 把 tmp 目录 `rename` 成 `<缓存>/<version>`（同分区 rename 是原子的）
   → 缓存目录**整体**切换。

**其他要点**：

- **凭据从环境变量读**：`IDEA_BASE_URL`（必填）、`IDEA_USER`/`IDEA_PASS`（basic auth）、
  `IDEA_TOKEN`（`X-Api-Key`）—— 与 agent 调 API 时用的是同一套，多一套要用户记。
- **绝不回显凭据**：错误信息里不带 URL 的 userinfo、不带密码、不回显请求头。
- **缓存**：`~/.cache/idea-skill/`（`XDG_CACHE_HOME` 优先）。
  旧版本目录按 mtime 清掉（保留最近 2 个）。
- **版本检查**：每次调用前取一次清单。
- **降级**：取清单**失败**（网络问题）时，若本地有缓存 → 用缓存并在 stderr 打一行提示；
  没有缓存 → 报错退出。取件器自己失败时退出码非 0（§13）。

### 10.3 安装

安装产物是**拷贝**，不是软链 —— 薄壳和内容源不是同一个东西，软链会把内容源里的
`SKILL.md`（索引页）当成薄壳装过去。

- 提供一个安装脚本（`skills/install-agent-skill.sh`），把薄壳拷到
  `~/.zcode/skills/idea-manager/` 或 `~/.claude/skills/idea-manager/`。
- 薄壳源在仓库的 `skills/idea-manager-client/`（与内容源分开，见 §8.1）。
- README 的安装章节要改：不再教人软链 `skills/idea-manager/`。

## 11. 删掉命令行封装

**决定**：`skills/idea-manager/scripts/idea.py`、`skills/idea-manager/tests/test_cli.py`、
`skills/idea-manager/scripts/run-tests.sh` **全部删除**。agent 用 curl + 契约直接调 API。

**理由**：

- 命令层（25 个子命令的参数与语义）是契约里已有的信息的重复实现 ——
  契约说「`POST /api/ideas/bulk/tags` 的 body 是 `{"ideaIds":[…],"tags":"a,b"}`」，
  CLI 又用 argparse 复述一遍，还复述得不一样。**两份会漂。**
- 它是整个 skill 里最大的一块维护面：933 行代码 + 一个端到端测试套件。
- agent 现在的能力（读 JSON 契约、发 curl、构造请求体）足够，不需要这层抽象。

**删掉的东西各有各的去处**：

| CLI 提供的 | 现在怎么办 |
|---|---|
| 子命令与参数 | 契约（`/openapi/v1.json`）给形状，手册给语义 |
| `--format json` | curl 的响应体就是 JSON（agent 直接解析） |
| `--format text`（给人看的摘要） | **删掉**。它是给人看的，agent 用不着；人在页面上看 |
| 脱敏 | 搬到服务端（§9.4） |
| 凭据从环境变量读 | 手册教 `curl -u "$IDEA_USER:$IDEA_PASS"`（值不进命令行明文，但**会进进程 argv**，`ps` 可见 —— 单机自用可接受） |
| 特殊字符原样送达 | 手册教用 `--data-binary @body.json`（先写文件）或 quoted heredoc |
| 错误归一（`http_413` 之类） | agent 直接看到 ProblemDetails 的 `code`（更直接）；413 的两条部署路径差异由手册说明 |
| 非 0 退出码 = 失败 | curl 的 `--fail-with-body`（curl ≥ 7.76）给非 2xx 时退出码 22 且保留响应体 |

**手册要教 agent 的基本姿势**（写进索引页的 curl 模板 + `references/calling.md`）：

```bash
# GET：列表
curl -sS --fail-with-body "$IDEA_BASE_URL/api/ideas?status=open" \
  -u "$IDEA_USER:$IDEA_PASS"

# POST：请求体走文件（特殊字符最稳）
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @body.json

# PATCH：只发要改的字段（不发的键一个都别带）
curl -sS --fail-with-body -X PATCH "$IDEA_BASE_URL/api/ideas/$ID" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @patch.json
```

注意事项（写进手册）：

- **有凭据才加 `-u`**；`IDEA_USER`/`IDEA_PASS` 都没有时不要传 `-u`。
  `IDEA_TOKEN` 有值时额外加 `-H "X-Api-Key: $IDEA_TOKEN"`。
- **别把凭据写进 URL**（userinfo 会被 nginx 写进访问日志）。
- **请求体里的换行用 JSON 转义**（`\n`），不要用字面换行；shell 用 quoted heredoc
  （`<<'JSON'`）避免变量被展开。
- **不要加 `-f`**（老 curl 的 `--fail` 会丢掉响应体）；用 `--fail-with-body`，
  或者干脆不加，让 agent 自己看状态码。

## 12. 认证与凭据

- `/skill/**` 与 `/api/**` 走同一套 nginx basic auth —— **nginx 配置不用改**。
- agent 调 API 时用 `IDEA_USER`/`IDEA_PASS` 发 basic auth，用 `IDEA_TOKEN` 发 `X-Api-Key`。
- **不把凭据放进 URL**：URL 里带 userinfo 会被写进 nginx 的访问日志。
- 取件器的命令行**不接受**密码（与原 CLI 同一条约定）。
- 缓存目录里**不落**凭据（只落内容本身）。
- webhook 的凭据**默认不回传**（§9.4）。

## 13. 失败模式与错误约定

**取件器**自己失败时：一行 `code: detail` 到 stderr，退出码非 0。

| 情况 | 码 | 退出码 |
|---|---|---|
| 缺 `IDEA_BASE_URL` | `missing_config` | 1 |
| `IDEA_BASE_URL` 非法 | `invalid_config` | 1 |
| 网络不可达 / 超时 / 连接被拒 | `network_error` | 2 |
| 认证失败（401/403） | `auth_error` | 1 |
| 服务端没分发 skill 内容（404 + `skill_not_distributed`） | `skill_not_distributed` | 1 |
| 要取的文件不存在（404 + `skill_file_not_found`） | `not_found` | 1 |
| 同步时哈希对不上（内容在传输中变了） | `skill_integrity_error` | 1 |

**agent 直接调 API 时**：服务端按既有约定回 RFC 9457 的 ProblemDetails，
带机器可读的扩展字段 `code`。agent 按 `code` 分支，别去匹配 `detail` 的文字
（detail 是给人看的，会改）。错误码全表在 `references/errors.md`。

薄壳里要有取件那几种情况的一句话排查提示（`IDEA_BASE_URL` 有没有设、密码对不对、
服务端是不是没带 skill 目录）—— 这些是**稳定信息**，不随服务端内容变。

## 14. 构建期守卫

内容从服务端来，不等于内容可以写错。**这些守卫要新增或调整**：

| 守卫 | 断言 | 为什么 |
|---|---|---|
| 手册 ↔ 端点（双向） | 手册里出现的每个 `方法 /api/...` 必须在端点集合里；端点集合里每个端点必须在手册里被提到（索引页能力清单或 `references/api.md`） | 双向都堵死：既不让手册描述不存在的接口，也不让服务加了能力而手册没写 |
| 错误码表 ↔ `ApiErrorCodes` | `references/errors.md` 的**「## 服务端错误码」那张表**的 code 集合 == `ApiErrorCodes.All`（表二装的是取件器自己的码，不参与比对；`internal_error` 不在 `All` 里，在正文说明即可、不进表格行） | 杀掉「错误码漂移」 |
| frontmatter 一致 | 薄壳的 frontmatter == 内容源 `SKILL.md` 的 frontmatter | 薄壳是 agent 侧唯一要维护的东西，description 不该悄悄过期 |
| 薄壳正文无 API 路径 | 薄壳 `SKILL.md` 的 **frontmatter 之后的正文**里，不出现任何 `/api/...` 路径 | §8.5 的可执行版本 |
| 契约不再有副本 | `skills/idea-manager/references/openapi.json` 不存在；内容源里没有第二份契约 | 防止有人又 `cp` 一份回来，重新制造漂移 |
| 内容源形状 | 内容源目录的直接子项只允许 `SKILL.md` 与 `references/`；`references/` 下只允许 `.md`（点开头的编辑器残留除外） | 防止命令层那样的开发残留混进分发副本 |
| 索引页路由表的指向 | 索引页里提到的每个 `references/X.md` 都必须存在；`references/` 下的每份 `.md` 都必须在索引页被提到 | 防止「agent 照路由表去取一份不存在的参考」——指针悬空是最难查的一类漂移 |
| 手册不写死路径条数 | 手册里不出现「NN 条路径」这类数字 | 契约一变它就过期 |
| 索引页体量 | 索引页行数 ≤ 150 | 渐进式披露的自我约束：索引页膨胀了就说明该拆 |
| 分发副本完整 | 构建输出 / publish 产物里的 `skill/idea-manager/` 含 `SKILL.md`、`references/*.md`，且不含 `tests/`、`__pycache__` | 防止「发布漏文件」这类静默失败 |
| 目录缺失不 500 | `Skill:Path` 指向不存在的目录时，`/skill/...` 回 404 `skill_not_distributed` | 静默失败比报错更坏（既有教训） |
| 穿越防护 | `/skill/idea-manager/../../etc/passwd` → 404 `skill_file_not_found`，不是文件内容 | 一个能读任意文件的端点是严重漏洞 |
| **webhook 遮蔽** | `GET /api/webhooks`（与 `/{id}`）的响应里**不含**原始 url 里的 token 片段与 headers 的值；带 `?unmask=true` 时原样返回 | 脱敏从「客户端自觉」变成「服务端机制」之后，这条就是唯一的防线 |
| 遮蔽不误伤 | 遮蔽后的 url 仍带 scheme 与 host；`templates/telegram` 的占位符 url 不被遮 | 遮蔽过头会让 agent 判断不出目标服务 |

已有的 `RequestBodyLimitGuardTests`（盯 SKILL.md 里的 413 数字）要跟着改：
它现在解析 `skills/idea-manager/SKILL.md`，拆分后 413 的说明在 `references/errors.md` 里。

## 15. 迁移路径

按这个顺序做，每一步都能独立验证：

1. **纯拆分**（不动分发）：把 `SKILL.md` 拆成索引页 + `references/*.md`，
   内容一字不改地搬过去；同步调整 `RequestBodyLimitGuardTests` 的读取路径。
   这一步单独就能让手册变短，agent 的默认上下文变小。
2. **服务端发内容**：加 `/skill/**` 端点 + 清单 + csproj 分发 + `Skill:Path` 配置项
   + 路径穿越防护 + 相关守卫。
3. **取件器与薄壳**：写 `fetch.py`、写薄壳、写安装脚本。
4. **切引用**：手册里的契约指向改为 `/openapi/v1.json`，删掉契约副本与它的守卫。
5. **能力表 / 错误码表守卫**：把 §14 的新守卫加上。
6. **webhook 默认遮蔽**（§9.4）：服务端加遮蔽 + `?unmask=true` + 前端带上参数 +
   `toggleEnabled` 改成重新拉列表 + 遮蔽守卫。
7. **删掉命令行封装**：删 `idea.py` / `test_cli.py` / `run-tests.sh`；
   手册从「子命令手册」改写成「curl 调用指南」（§11）。
   —— 这一步与 1~4 有依赖（手册要先能独立成立），但**可以先做 6 再做 7**：
   6 做完，CLI 的脱敏职责就交出去了。
8. **重新导出契约**：`/skill/**` 进契约、webhook 加了 query 参数之后，
   `openapi/idea-v1.json` 要重新导出（`dotnet run --project tools/ExportOpenApi`），
   否则 `Committed_document_matches_the_running_app` 会红。
9. **改文档与注释**（这些指针会随拆分失效，容易漏）：

   | 位置 | 现在的指针 | 改成 |
   |---|---|---|
   | `docs/nginx/idea.conf` 注释 | `skills/idea-manager/SKILL.md` 的「HTTP 413」说明 | `references/errors.md` 的 413 说明 |
   | `server/IdeaApp.Tests/PayloadTooLargeTests.cs` 注释 | `skills/idea-manager/SKILL.md` | `references/errors.md` |
   | `server/IdeaApp.Tests/RequestBodyLimitGuardTests.cs` | 读 `skills/idea-manager/SKILL.md` | 读 `references/errors.md` |
   | `server/IdeaApp/Contracts/ApiErrorCodes.cs` 注释 | 「skill 计划的错误码对照表以它为准」 | 指向新的 `references/errors.md` |
   | `README.md` | agent skill 一节（安装方式、文件清单、命令示例） | 新形态 |
   | `docs/superpowers/specs/2026-11-11-idea-manager-design.md` §17 | 「Agent Skill 交付物」写的是三件套 | 指向本文 |

## 16. 留给实现者的空白

实现者可以自行决定、不改变本设计的东西：

- `fetch.py` 的具体代码结构（类还是函数、缓存怎么组织、要不要拆模块）。
- 缓存目录的清理策略细节（保留几个版本、多久清一次）。
- 版本哈希的具体算法组合方式（哪些文件参与哈希、路径怎么拼）——
  只要**同一个内容永远得到同一个版本串**即可。
- 索引页与各 reference 的具体措辞与行数分配（只要 §8.2 的章节齐、§8.5 的约束满足）。
- 遮蔽的具体形态（段数是否保留、`***` 的写法）—— 只要满足 §9.4 的表。
- 薄壳安装脚本是 bash 还是 python（要求：幂等、能在没有 skill 的机器上跑）。
- 是否给 `/skill/**` 加 ETag / `Cache-Control`。
- 取件器要不要支持 `--offline`（只读缓存不联网）。
- agent 侧除了 `fetch.py` 还要不要别的辅助脚本（例如「把 JSON 写进临时文件」的小工具）——
  默认不要，能靠 agent 自己的文件工具解决。

## 17. 验收口径

1. `dotnet publish` 产物里有 `skill/idea-manager/SKILL.md`、`skill/idea-manager/references/*.md`，
   且**没有** `tests/`、`__pycache__`。
2. `GET /skill/idea-manager/SKILL.md` 返回 200 + `text/markdown`；内容与仓库里那份逐字相同。
3. 路径穿越的三种形态各有确定结果（Kestrel 先归一化点号段）：
   `..%2f..%2f` 那种不被归一化的形态 → 404 `skill_file_not_found`（不是文件内容）；
   `..` 逃出 `/skill/` 前缀的形态 → 归一化后落到应用既有的 SPA 兜底（200 + HTML，**读不到任何文件**）；
   归一化后仍在 `/skill/` 下的形态 → 200 + 手册原文。三条都写进了 `references/errors.md`。
4. `GET /skill/idea-manager/version` 返回一份清单；清单里的 `files` 与磁盘上的分发副本一致；
   改动内容源后版本串变化。
5. skill 目录不存在时，`GET /skill/idea-manager/SKILL.md` → 404 `skill_not_distributed`（不是 500）。
6. `GET /skill/idea-manger/SKILL.md`（拼错）与 `GET /skill` → 404 + ProblemDetails，
   **不是** 200 + HTML（SPA 兜底不许接住它们）。
7. 薄壳 `SKILL.md` 里不含任何服务端文件名与 API 路径；frontmatter 与内容源逐字相同。
8. `fetch.py` 在 `IDEA_BASE_URL` 缺失时报 `missing_config` 且退出码非 0；错误信息里没有凭据。
9. **混合版本不可能**：同步中途失败（人为改坏一个文件）→ 旧版本仍在，
   并且报错说明是完整性问题。
10. 端到端：改掉服务端某个端点的语义 → 重新发布 → `fetch.py` 取到的手册就是新语义，
    **不需要重新安装 agent 侧的 skill**。
11. 索引页 ≤ 150 行；`references/` 里每份文件都能被 agent 单独取到并读懂。
12. `GET /api/webhooks` 的响应里**没有**原始 token 片段；带 `?unmask=true` 时有。
13. 页面（前端）仍能看到并编辑完整的 webhook url —— `unmask=true` 生效，
    编辑保存后 webhook 的 url 不被遮蔽值覆盖。
14. `skills/idea-manager/scripts/idea.py`、`tests/test_cli.py`、`scripts/run-tests.sh` 已删除；
    仓库里没有第二份命令层。
15. §14 的守卫全绿。
