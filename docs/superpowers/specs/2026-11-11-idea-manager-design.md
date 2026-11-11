# Idea 管理工具 · 设计文档

- 日期：2026-11-11
- 状态：待评审
- 技术栈：.NET 10 Native AOT + Vue 3 + SQLite

---

## 1. 意图与背景

一个**个人自用**的 idea 记事本。单用户、单实例，通过 nginx 反代访问。

核心诉求是「随手记下来」这件事本身要足够轻：底部一个输入框，回车就多一条想法；回头看的时候，所有想法以消息流的形式铺开，**最近更新的永远在最下面** —— 跟聊天记录一样，最新的那条就在手边。

想法不是孤立的：它归属某个项目，带标签，可以标记完成，可以把几条合并成一条更完整的。所有写操作都会广播出去，一部分送到浏览器让**开了多个页面/多台设备的人（其实是你自己）实时同步**，一部分送到 webhook（主要是 Telegram）做外部通知。

项目同时面向两条使用路径：

1. 人 —— 在浏览器里用；
2. agent —— 通过 REST API 用。为此所有能力都必须有对应的 HTTP 接口，并产出符合 OpenAPI 3.1 的文档文件，供 agent 生成 skill。

## 2. 目标

1. .NET 10 Native AOT，发布产物是**一个自包含二进制 + 一个 SQLite 文件**，运行时不需要 .NET SDK、不需要 Node。
2. 聊天式录入 + 消息流回看，排序规则恒定为「最近更新的在最下边」。
3. 项目管理：新建、改名、删除（进回收站）、恢复、手动排序 / 按最近活动排序。
4. 标签（自由文本，全局字典）、完成状态、按状态与标签过滤（标签多选，AND/OR 可切换）。
5. 多选批量操作：标记完成、加标签、删除、**合并**（多条合并成一条新想法）。
6. 事件总线：写操作产生事件 → 广播给浏览器（SSE，实现多端同步）+ 投递给可配置的处理���（当前只有 webhook）。
7. Webhook 在页面上可配置，按全局 / 项目两级作用域；提供 Telegram 模板。
8. REST API 完整覆盖上述能力，并生成 OpenAPI 文档文件提交进仓库。
9. 附带一个可直接使用的 agent skill：把 REST API 包成命令行工具 + 说明书，让 agent 不必手写 HTTP 请求就能管理想法。

## 3. 非目标

- **不做**多人协作、账号体系、权限、分享链接。
- **不做** LLM / 意图解析 —— 输入框里的文字就是想法本身，一字不改地存下来。
- **不做**全文检索、模糊搜索、标签层级、看板视图、附件、富文本编辑器。
- **不做**移动原生 App；移动端只保证网页能用。
- **不做**多实例部署、水平扩展。

## 4. 反目标（做到这些就算失败）

- **不能变成看板**。项目名里的 kanban 是历史遗留，本项目是**消息流**，不是列卡片。
- **不能卡**。webhook 目标挂掉、超时、返回 500，都绝不能影响你新增/编辑的响应速度。
- **不能丢数据**。除了回收站到期自动清理，任何写操作都不做不可逆删除。
- **不能在 AOT 下崩**。不得引入依赖反射、动态代码生成、运行时表达式编译的库。

## 5. 约束

| 约束 | 来源 |
|---|---|
| 单用户，无鉴权 | 决定：不做账号体系 |
| 通过 nginx 反代对外提供服务 | 应用自身只监听回环地址 |
| .NET 10 Native AOT | 决定：JSON 源生成、无反射 DI、手写 SQL |
| 想法是纯文本 | 决定：单一 content 字段，无富文本 |
| 回收站保留期只从配置读，页面不提供设置入口 | 决定：`Trash.RetentionDays` 只在配置文件/环境变量 |
| Telegram 通知参考既有脚本的用法（Bot API `sendMessage`，纯文本，不带 `parse_mode`） | 决定：模板用 JSON body，含 `chat_id` 与可选 `message_thread_id` |
| 访问控制放在 nginx 层；应用层不做鉴权，也不做 Origin/CSRF 校验 | 决定：见 §16.3 的 nginx 配置 |

## 6. 核心概念

| 概念 | 说明 |
|---|---|
| **项目 (project)** | 想法的归属单位。内置一个「收件箱」，是新建想法的默认落点。项目不可无项目态 —— 每条想法必属一个项目。 |
| **想法 (idea)** | 一段正文 + 可选标题 + 若干标签 + 完成标记。可被软删除（进回收站）。 |
| **标签 (tag)** | 自由文本，全局共享字典，按大小写不敏感去重。字典项不随项目删除而消失。 |
| **回收站** | 装两类东西：被单独删除的想法（顶层条目）、被删除的项目（容器项目，下挂它的所有想法）。 |
| **事件 (event)** | 一次写操作产生的记录，带完整实体载荷，供浏览器和 webhook 消费。 |

## 7. 架构总览

```
┌─────────────────────────────────────────────────────────┐
│  浏览器 (Vue 3 SPA)                                     │
│    主页面 / 项目 / 回收站 / Webhook 设置 / 投递记录        │
└───────────────┬──────────────────────┬──────────────────┘
                │ REST (Minimal API)  │ SSE  GET /api/events
                ▼                      ▲
┌─────────────────────────────────────────────────────────┐
│  ASP.NET Core (Native AOT)                              │
│    端点层 → 领域服务 → 仓储(Microsoft.Data.Sqlite)        │
│                    │                                     │
│                    ├──▶ 事件总线                          │
│                    │      ├──▶ SseBroadcastHandler 通道   │
│                    │      └──▶ WebhookDeliveryHandler 通道 │
│                    │             │                       │
│                    │             ▼                       │
│                    │        投递队列 + 重试 + 死信表       │
└─────────────────────────────────────────────────────────┘
                       │
                       ▼
                 SQLite (WAL 模式)
```

**写路径**：端点 → 领域服务 → 在一个 SQLite 事务里完成全部数据库改动并提交 → **提交成功后**把事件写入事件总线 → 立即返回。事件处理完全异步，不在请求链路上。

> 事件不参与 SQLite 事务：`Channel` 无法回滚，若在事务内投递而事务随后回滚，会产生指向不存在数据的幽灵事件。因此投递点固定在提交之后。

**处理器模型**：事件总线下游是 `IEventHandler` 集合，通过 DI 注册。新增一种广播目标 = 实现一个 `IEventHandler` 并注册，不改核心代码。当前两个实现：`SseBroadcastHandler`（推给浏览器）、`WebhookDeliveryHandler`（推给外部 HTTP 端点）。

**扇出而非竞争消费**：每个 handler 有**自己独立的通道**，事件总线把同一条事件写进所有通道。`System.Threading.Channels` 是竞争消费模型，若多个 handler 共读一条通道会各拿一半事件。

## 8. 技术选型

| 选择 | 理由 |
|---|---|
| ASP.NET Core Minimal API + Native AOT | 单二进制部署；Minimal API 是 AOT 下最省事的路由方式 |
| `Microsoft.Data.Sqlite` + 手写 SQL | 不引 ORM，避免反射/动态代理带来的 AOT 风险与启动开销；schema 只有 6 张表，手写可控 |
| JSON 源生成 (`JsonSerializerContext`) | AOT 下 `System.Text.Json` 反射序列化会被裁掉，必须源生成 |
| 手写 DI 注册（不用 `ActivatorUtilities`） | AOT 友好 |
| Vue 3 + TypeScript + Vite | 交互（SSE 重连、乐观更新、拖拽排序、tag 补全）写起来省事；构建产物是纯静态文件，运行时不需要 Node |
| SSE（`text/event-stream`） | 单向推送比 WebSocket 简单，浏览器原生 `EventSource` 自带重连；nginx 侧只需关掉 buffer |
| `System.Threading.Channels` + `BackgroundService` | 进程内异步队列，够用且无额外依赖 |
| 自写 Mustache 占位符渲染器（约 60 行） | 避免第三方模板引擎的 AOT/依赖风险；语法简单可控 |
| 自写单调 UUIDv7 生成器（`MonotonicGuid.NewUuid7()`）作主键 | .NET 的 `Guid.CreateVersion7()` **同一毫秒内不单调**（实测 30 万样本出现 149,895 次相邻逆序，单毫秒最大桶 2422 个），字典序 ≠ 插入序。而「同刻按插入序」是本项目的排序承诺（§10.1），必须按 RFC 9562 §6.2 的计数器法自己实现；无第三方 ULID 依赖 |

## 9. 数据模型

SQLite，WAL 模式，`foreign_keys=ON`。所有时间戳存 ISO-8601 UTC 字符串（`2026-11-11T09:00:00.0000000Z`）。

```sql
CREATE TABLE project (
    id            TEXT PRIMARY KEY,          -- 'inbox' 或 UUIDv7
    name          TEXT NOT NULL,
    sort_order    INTEGER NOT NULL DEFAULT 0,
    deleted_at    TEXT NULL,                 -- 非空 = 在回收站
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX ix_project_order   ON project(deleted_at, sort_order);
CREATE INDEX ix_project_active  ON project(updated_at DESC);

CREATE TABLE idea (
    id            TEXT PRIMARY KEY,
    project_id    TEXT NOT NULL REFERENCES project(id),
    title         TEXT NULL,
    content       TEXT NOT NULL,
    done          INTEGER NOT NULL DEFAULT 0,
    deleted_at    TEXT NULL,                 -- 非空 = 在回收站
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX ix_idea_list       ON idea(project_id, deleted_at, updated_at DESC, id DESC);
CREATE INDEX ix_idea_updated    ON idea(deleted_at, updated_at DESC);
CREATE INDEX ix_idea_project    ON idea(project_id);

CREATE TABLE tag (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE idea_tag (
    idea_id TEXT NOT NULL REFERENCES idea(id) ON DELETE CASCADE,
    tag_id  INTEGER NOT NULL REFERENCES tag(id)  ON DELETE CASCADE,
    PRIMARY KEY (idea_id, tag_id)
);
CREATE INDEX ix_idea_tag_tag ON idea_tag(tag_id);

CREATE TABLE webhook (
    id            TEXT PRIMARY KEY,
    name          TEXT NOT NULL,
    url           TEXT NOT NULL,
    method        TEXT NOT NULL DEFAULT 'POST',
    headers_json  TEXT NOT NULL DEFAULT '{}',   -- { "Header": "value" }
    body_template TEXT NOT NULL,                -- JSON 模板，含 {{...}} 占位符
    events_json   TEXT NOT NULL DEFAULT '[]',   -- 订阅的事件类型数组；[] = 不订阅任何
    scope         TEXT NOT NULL,                -- 'global' | 'project'
    project_id    TEXT NULL REFERENCES project(id) ON DELETE CASCADE,
    secret        TEXT NULL,                    -- 非空则带 HMAC 签名头
    enabled       INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);

CREATE TABLE webhook_delivery (
    id              TEXT PRIMARY KEY,
    webhook_id      TEXT NOT NULL REFERENCES webhook(id) ON DELETE CASCADE,
    event_id        TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    payload         TEXT NOT NULL,              -- 已渲染的请求体
    status          TEXT NOT NULL,              -- 'delivered' | 'failed'
    attempts        INTEGER NOT NULL DEFAULT 0,
    last_error      TEXT NULL,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);
CREATE INDEX ix_delivery_webhook ON webhook_delivery(webhook_id, created_at DESC);

CREATE TABLE setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

> **`webhook` 表没有 `deleted_at`**：项目级 webhook 不作为回收站条目单独存在，而是**随项目隐式失效** —— 所属项目在回收站期间不投递，项目恢复后自动恢复投递，项目到期清理时随项目一并删除（FK 上是 `ON DELETE CASCADE`）。因此回收站页面只有两类条目：已删项目（容器）与顶层想法。

### 9.1 内置收件箱

首次启动时若 `project` 表为空，插入 `id='inbox', name='收件箱'`。收件箱**不可删除**（可以改名）。收件箱参与手动排序。

### 9.2 项目排序方式

存在 `setting` 表，key = `project.sort_mode`，value ∈ `recent` | `manual`。默认 `recent`。

- `recent`：按项目下**未删除**想法的最新 `updated_at` 倒序；项目下没有想法时回落到项目自身的 `updated_at`。收件箱不置顶，按同一规则参与排序。
- `manual`：按 `project.sort_order` 升序。切换到 manual 时用当前显示顺序初始化 `sort_order`；拖拽后写回。

该设置存 DB，因此跨设备一致。

## 10. 核心行为规则

### 10.1 排序与置底

- 列表排序恒为 `updated_at ASC, id ASC`。`id` 必须是**单调 UUIDv7**（自实现的计数器法，见 §8）—— .NET 内置的 `Guid.CreateVersion7()` 在同一毫秒内随机乱序，用它会让「同刻写入的条目」顺序随机跳动，直接破坏「最近更新的在最下边」这个承诺。
- **任何写操作都会刷新 `updated_at`**，并因此把条目移到列表最下面。写操作包括：改正文/标题、设置标签、标记完成/取消完成、合并。
- 「就地下方改写 + 移到最底部」是同一次操作的两个副作用，不保留任何历史版本。

### 10.2 过滤

- **状态过滤**：三态 —— 全部 / 未完成 / 已完成。
- **标签过滤**：多选。`tag_mode` 取 `and` 或 `or`，默认 `or`。
  - `or`：想法的标签集合与选中集合有交集即命中。
  - `and`：选中集合是想法标签集合的子集才命中。
- **项目过滤**：选中项目 = 该项目；「全部项目」= 跨项目聚合视图。
- 三者取交集。
- **过滤在客户端做**。SSE 端点恒定全量推送所有事件，客户端按当前的项目/状态/标签条件自行判定是否展示。理由：AND/OR 偏好存在每台设备各自的 `localStorage` 里，服务端无从得知每条连接当前用哪种模式，也不该替客户端猜。

### 10.3 标签

- 自由文本，不预置。
- 写入时按大小写不敏感去重；同名的 tag 行复用。
- 自动补全：优先列出**当前项目**用过的标签，不足时补全局其它标签。走 `GET /api/tags/suggest`。
- 标签在想法被软删除后仍保留在字典里（`idea_tag` 随 idea 走，idea 彻底删除时才级联清理）。

### 10.4 合并

对 N ≥ 2 条想法执行：

1. 校验：全部存在、未删除、属于同一项目。跨项目合并**不允许**（返回 `merge_cross_project`）。
2. 生成一条**新想法**：id 新建，`project_id` 取源项目，`title` 取第一条源想法的 title（都为空则为空），`content` = 各源 content 按 `updated_at ASC` 顺序以 `\n\n` 拼接，`done` = false，标签 = 所有源标签的**并集**。
3. 源想法**硬删除**（不进回收站），连同 `idea_tag` 级联清理。
4. 产生事件 `idea.merged`，载荷含新想法与被删源 id 列表。

### 10.5 删除与回收站

- **删除想法**：`deleted_at` 置为当前时间 → 顶层回收站条目。
- **删除项目**：`project.deleted_at` 置为当前时间，其下**所有未删除想法**的 `deleted_at` **一并置为同一时刻** → 回收站里的一个容器条目，展开可见其下想法。
- **回收站视图**列出两类条目：容器（已删项目）优先，其次顶层想法。**两类互补、互不重叠**：
  - **顶层想法** = `idea.deleted_at IS NOT NULL` **且所属项目未被删除** —— 即「项目还在、这条想法被删了」。
  - **容器（已删项目）**下挂该项目下 `idea.deleted_at IS NOT NULL` 的想法 —— 即该项目下**仍在回收站里**的想法（无论它们是随项目一起删的，还是在项目被删之前就已被单独删除的）。
  - **同一个想法不会在两处同时出现**：所属项目被删的想法只出现在容器里，不再单列为顶层条目。
  - 推论：**在项目仍在回收站时单独恢复一条想法是有可见效果的** —— 它从容器里消失、回到正常列表（`GET /api/ideas` 不按项目是否在回收站过滤）。
- **恢复想法**：`deleted_at` 置空，`updated_at` 刷新（因此它会出现在列表最下面）。若其所属项目已被删除，则恢复到该项目的想法列表中，项目仍留在回收站 —— 直到项目被恢复或到期清理。
- **恢复项目**：项目及其下**因删除项目而进回收站**的想法一并恢复 —— 判定依据是 `idea.deleted_at == project.deleted_at`（两者在删除项目时被写成同一时刻）。在项目被删**之前**就已被单独删除的想法**不复活**，它们仍以顶层条目的形式留在回收站里。
- **不允许手动彻底删除**。到期自动清理是唯一出口（见 10.6）。

### 10.6 回收站自动清理

- 保留期来自配置 `Trash:RetentionDays`，默认 **30**。**页面不提供设置入口，API 也不提供**。
- 一个后台定时任务（`BackgroundService` + `PeriodicTimer`，每小时跑一次）删除 `deleted_at` 早于「当前时间 − 保留期」的条目：
  - 顶层想法：删除 idea 行（`idea_tag` 级联）。
  - 容器项目：删除项目行及其全部想法（级联），该项目下的 webhook 也随 FK 的 `ON DELETE CASCADE` 一并删除。
- 清理是不可逆的，**不产生事件**（对象已不存在，通知无意义）。

## 11. 事件模型与广播

### 11.1 事件类型

| 事件 | 触发 | 载荷要点 |
|---|---|---|
| `idea.created` | 新建想法 | 完整 idea |
| `idea.updated` | 改正文/标题、设标签、标记完成 | 完整 idea + `changed` 字段名数组 |
| `idea.deleted` | 想法进回收站 | idea id + 项目 |
| `idea.restored` | 想法从回收站恢复 | 完整 idea |
| `idea.merged` | 多条合并成一条 | 新 idea + 被删源 id 列表 |
| `project.created` | 新建项目 | 项目 |
| `project.updated` | 改名、改排序 | 项目 |
| `project.deleted` | 项目进回收站 | 项目 |

### 11.2 事件结构

```json
{
  "id": "0199a7f23c4d7abc8def0123456789ab",
  "type": "idea.updated",
  "timestamp": "2026-11-11T09:00:00+00:00",
  "project": { "id": "inbox", "name": "收件箱" },
  "idea": {
    "id": "0199a7f1-...", "title": null, "content": "…",
    "tags": ["交互", "核心"], "done": false,
    "created_at": "…", "updated_at": "…"
  },
  "changed": ["content"],
  "mergedFrom": ["0199a7f3-…"]
}
```

`mergedFrom` 只在 `idea.merged` 时出现，其余事件省略。

**两个格式细节**（实现即如此，客户端按此解析）：

- **`id` 是 32 位无连字符的小写 hex**（`Guid.ToString("N")` 格式），**不带连字符**。它是单调 UUIDv7，字典序与生成序同向 —— SSE 的 `Last-Event-ID` 原样回传即靠这个。
- **`timestamp` 用 `System.Text.Json` 的默认 `DateTimeOffset` 格式**，形如 `2026-11-11T09:00:00+00:00`（**不是** `...Z` 结尾 —— 那是存库用的格式，两者不同）。

### 11.3 广播机制

- **SSE 端点** `GET /api/events`，`Content-Type: text/event-stream`。**恒定全量推送，不接受任何过滤参数**（见 §10.2）。每个事件一帧：
  ```
  id: 0199a7f23c4d7abc8def0123456789ab
  event: idea.updated
  data: {...}
  ```
  `id` 即事件 id，浏览器重连时带 `Last-Event-ID`，服务端据此补发。
- **心跳**：每 25 秒发一行 `: ping` 注释，防止中间代理掐断空闲连接。
- **服务端事件缓冲**：保留最近 500 条事件用于 `Last-Event-ID` 补发。缓冲**只在内存**，进程重启后必然补发不到 —— 此时服务端发一帧 `event: resync`（**data 是一个空的 JSON 对象 `{}`**，客户端只需触发重新拉取，不必解析内容），客户端收到后重新拉取当前列表。
- **`resync` 是控制帧**，不是业务事件，不出现在 §11.1 的事件类型表里，也不可被 webhook 订阅。
- **服务端主动补 `resync` 的两种场合**：① 断线重连时 `Last-Event-ID` 在缓冲里找不到；② **某个订阅者太慢、它的通道被挤掉了一条事件**时 —— 那条事件仍在缓冲里，所以**重连**能补回来，但**保持连接**的订阅者会一直缺它，而事件 id 不是连续序号、客户端无法自行检出间隙，于是会出现**静默空洞**。所以服务端在挤掉事件时给那个订阅者补发一帧 `resync`，让它重拉一次列表。客户端因此只需要遵守一条规则：**收到 `resync` 就重新拉取当前列表**。
- **扇出而非竞争消费**：每个 `IEventHandler` 有自己独立的通道，事件总线把同一条事件写进所有通道。
- **幂等**：客户端按事件 id 去重；自己发起的操作已乐观更新，收到自己的广播时不再插入第二次。

### 11.4 客户端行为

所有过滤判定都在客户端完成，服务端推送的事件与当前筛选无关。

| 场景 | 行为 |
|---|---|
| 事件属于当前项目且符合当前过滤 | 插入或就地改写，按 `updated_at ASC, id ASC` 重排 |
| 事件属于当前项目但不符合过滤 | 不显示 |
| 事件属于其它项目 | 只更新项目列表（计数、最近活动时间、排序） |
| 用户贴着底部 | 新内容到达后自动跟随滚动 |
| 用户向上翻看 | **不抢滚动**，底部显示「↓ 有 N 条新内容」按钮 |
| 打开页面 | 加载完成后自动滚到底部 |
| 收到 `resync` | 重新拉取当前列表，保留滚动位置 |

## 12. Webhook 处理器

### 12.1 配置项

| 字段 | 说明 |
|---|---|
| 名称 | 自取 |
| URL | 目标地址 |
| 方法 | 默认 POST（保留其它方法） |
| 自定义 Header | 键值对 |
| Body 模板 | JSON 模板，含 `{{...}}` 占位符 |
| 订阅事件 | 勾选关心的事件类型（见 11.1）；空数组 = 不订阅任何 |
| 作用域 | `global`（全局）或 `project`（绑定某项目） |
| 启用 | 总开关 |
| Secret | 填写则附加 `X-Idea-Signature: sha256=<hex(HMAC-SHA256(secret, body))>` |

**默认行为**：

- 作用域为 `global` 的 webhook **默认禁用**，需手动开启。作用域为 `project` 的 webhook **默认启用**。
  （`webhook.enabled` 的数据库列默认值为 1；创建 global webhook 时由应用显式写入 0，两者不冲突。）
- 一个事件命中所有「全局且已启用」的 webhook，以及「绑定到该事件所属项目且已启用」的 webhook。各 webhook 独立投递，互不影响。
- 项目被删除时，其下的 webhook **隐式失效**（所属项目在回收站期间不投递），项目恢复后自动恢复投递，项目到期清理时随项目一并删除。webhook 本身不作为回收站条目出现。全局 webhook 不受项目删除影响。

### 12.2 占位符

| 占位符 | 内容 |
|---|---|
| `{{event}}` | 事件类型名，如 `idea.completed` |
| `{{timestamp}}` | ISO-8601 时间 |
| `{{project}}` | 项目名 |
| `{{project.id}}` | 项目 id |
| `{{idea.id}}` `{{idea.title}}` `{{idea.content}}` `{{idea.tags}}` `{{idea.done}}` | 想法各字段；`{{idea.tags}}` 为逗号分隔 |
| `{{changed}}` | 变更字段名，逗号分隔 |
| `{{idea.url}}` | 该想法的深链绝对链接：`{PublicBaseUrl}/?idea={idea.id}`。打开后前端滚动定位到该条并高亮 |

未识别的占位符原样保留。占位符值按 JSON 字符串转义后注入（正文含引号/换行不会破坏 JSON）。

### 12.3 Telegram 模板

页面上提供「添加 Telegram webhook」按钮，填好以下内容：

- URL：`https://api.telegram.org/bot<BOT_TOKEN>/sendMessage`
- Body：
  ```json
  {
    "chat_id": -1003800671233,
    "message_thread_id": 89,
    "text": "[{{project}}] {{event}}\n{{idea.title}}\n{{idea.content}}\n#{{idea.tags}}"
  }
  ```
- `message_thread_id` 可选（仅论坛话题需要），留空则发到群主话题。

约定（对齐既有通知脚本）：**不带 `parse_mode`，纯文本**；正文首行 `[项目] 事件`，末行 `#标签`。

> bot token 是秘密，不写进仓库、不进文档示例，页面上由用户自己填。

### 12.4 投递与重试

- 投递是**异步**的：事件 → 投递队列（`Channel`，容量 1000）→ `BackgroundService` 消费 → HTTP POST。webhook 队列与 SSE 通道**彼此独立**，webhook 卡住不影响浏览器推送。
- 队列满时**丢弃新事件并记日志**，绝不阻塞写路径（写路径不等投递）。
- 超时默认 10 秒。
- 失败重试 **3 次**（`Webhook:MaxAttempts`，**含首次尝试**），退避 1s / 5s / 30s。
- **投递记录生命周期**：每次尝试都更新同一行，`attempts` 累加（口径 = 总尝试次数，含首次），`last_error` 记录最近一次错误；终态写 `status='delivered'` 或 `status='failed'`。一条记录对应**一次事件投递**，不是每次尝试一行。
- **手动重投**：重发表里**已渲染的 `payload`**，不按当前模板重新渲染（表里没存事件全量数据，重渲染的输入并不存在）。重投后 `attempts` 继续累加。
- 每个 webhook 只保留最近 `Webhook:MaxDeliveryRecords`（默认 500）条投递记录，超出的删除最旧的。

### 12.5 处理器接口

```csharp
public interface IEventHandler
{
    ValueTask HandleAsync(IdeaEvent evt, CancellationToken ct);
}
```

通过 DI 注册为 `IEnumerable<IEventHandler>`。新增广播目标 = 实现该接口 + 注册。核心代码不感知具体处理器。

## 13. HTTP API

全部端点在 `/api` 下，产出 OpenAPI 3.1 文档。

### 13.1 项目

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/projects` | 列表（含未删除想法数、最近活动时间），按当前排序方式返回 |
| POST | `/api/projects` | 创建 `{ name }` |
| PATCH | `/api/projects/{id}` | 改名 `{ name }` |
| DELETE | `/api/projects/{id}` | 进回收站 |
| POST | `/api/projects/{id}/restore` | 从回收站恢复（连带其下想法） |
| POST | `/api/projects/reorder` | 手动排序，body 为 `{ ids: [...] }` |

### 13.2 想法

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/ideas` | 查询，**不分页**，返回全部命中项的裸数组。参数：`projectId`（可空 = 全部）、`status`（`all`/`open`/`done`）、`tags`（逗号分隔）、`tagMode`（`or`/`and`）。命中数超过 `Api:MaxIdeasPerResponse`（默认 2000）时返回 400，提示收窄条件 |
| POST | `/api/ideas` | 创建 `{ projectId, content, title?, tags? }` |
| PATCH | `/api/ideas/{id}` | 改 `{ title?, content? }` |
| PUT | `/api/ideas/{id}/tags` | **覆盖式**设置标签，body `{ tags: [...] }` |
| POST | `/api/ideas/{id}/complete` | 标记完成，body `{ done: bool }` |
| DELETE | `/api/ideas/{id}` | 进回收站 |
| POST | `/api/ideas/{id}/restore` | 从回收站恢复 |


> **不分页的理由**：排序恒为 `updated_at ASC`（新的在下），翻页方向天然别扭；而这是个个人工具，命中数通常在几百条量级，一次全量返回既简单又让 agent 解析省事。超过硬上限时要求收窄条件，而不是静默截断。

### 13.3 批量

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/ideas/bulk/complete` | `{ ids: [], done: bool }` |
| POST | `/api/ideas/bulk/tags` | `{ ids: [], tags: [], replace? }`（默认并集式追加，`replace: true` 则覆盖） |
| POST | `/api/ideas/bulk/delete` | `{ ids: [] }` |
| POST | `/api/ideas/merge` | `{ ids: [], content?, title? }`；`content` 省略则用自动拼接结果 |

### 13.4 其它

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/events` | SSE 事件流（无过滤参数） |
| GET | `/api/trash` | 回收站条目列表（**容器 + 顶层两类**）。它不是「想法列表的一个筛选」，所以路径不带 `ideas` |
| GET | `/api/tags` | 标签字典（含使用计数）。**计数只统计未删除的想法** —— 已进回收站的想法不计入，但标签本身仍留在字典里（计数为 0），因为字典是全局的、随想法走 |
| GET | `/api/tags/suggest` | 标签补全。参数 `projectId`（可选）、`prefix`（可选）。**优先返回当前项目用过的标签**，再补全局其它标签，按大小写不敏感去重 |
| GET | `/api/settings` | `setting` 表里的应用偏好（如 `project.sort_mode`）。**不含配置文件里的任何项** |
| PUT | `/api/settings/{key}` | 改 `setting` 表里的单个键。**只接受 `setting` 表已定义的键**，其它一律 400 |
| GET/POST | `/api/webhooks` | 全局 webhook 列表 / 创建 |
| PATCH/DELETE | `/api/webhooks/{id}` | 改 / 删 |
| GET | `/api/projects/{id}/webhooks` | 该项目下的 webhook |
| GET | `/api/webhooks/{id}/deliveries` | 投递记录 |
| POST | `/api/webhooks/{id}/deliveries/{deliveryId}/retry` | 手动重投 |

> **配置文件里的项（`Trash.RetentionDays`、`Webhook:*`、`PublicBaseUrl` 等）不通过 API 暴露，也不可经 API 修改。** 回收站保留期是唯一会导致不可逆删除的开关，按需求只从配置文件/环境变量读取；页面和 API 都不提供设置入口 —— 绕开它就等于绕开了这条约束。

### 13.5 响应与错误约定

- **创建**（`POST /api/ideas`、`POST /api/projects` 等）返回 `201 Created`，body 是创建后的完整实体。
- **更新 / 动作**（`PATCH`、`POST .../complete`、`POST .../restore`、批量与合并）返回 `200`，body 是受影响实体的完整表示（批量返回数组）。
- **删除**（`DELETE`）返回 `204 No Content`。
- **列表**端点返回**裸数组**，不套 `{items,total}` 信封。
- **错误**统一用 RFC 9457 的 ProblemDetails（`application/problem+json`）：`type`、`title`、`status`、`detail`，外加一个扩展字段 `code`（稳定的机器可读错误码，如 `idea_not_found`、`merge_cross_project`、`invalid_tag_mode`、`setting_key_not_writable`）。

### 13.6 OpenAPI 文档

- 用 .NET 10 内置的 `AddOpenApi()` / `MapOpenApi()` 生成。
- 提交到仓库的文件：**`openapi/idea-v1.json`**。由 `dotnet run --project tools/ExportOpenApi`（一个小工具）导出，或在启动时用环境变量 `IDEA_EXPORT_OPENAPI=<path>` 让应用把生成结果写到磁盘。**导出内容是构建产物快照，提交它是为了让 agent 有一个稳定可读的接口契约。**
- agent 的 skill 基于这个文件生成，不依赖运行时反射。

## 14. 前端

### 14.1 页面

| 路由 | 内容 |
|---|---|
| `/` | 主页面：项目切换 + 消息流 + 输入框 + 过滤栏 + 多选操作条。带 `?idea={id}` 时加载后滚动定位到该条并高亮（`{{idea.url}}` 深链的落点） |
| `/projects` | 项目管理：新建、改名、删除、拖拽排序、排序方式切换 |
| `/trash` | 回收站：容器（已删项目）与顶层想法，恢复 |
| `/webhooks` | 全局 webhook 配置（含投递记录 / 死信 / 手动重投） |
| `/projects/:id/webhooks` | 项目级 webhook 配置 |

前端使用 **history 模式**（`/projects`、`/trash`、`/webhooks` 等真实路径）。服务端用 `MapFallbackToFile("index.html")` 兜底未匹配的 GET 请求，因此 **nginx 不需要 `try_files`**，全部转发给应用即可。

### 14.2 响应式

断点 **1024px**：

- **≥ 1024px**：左侧项目栏（项目名 + 未删除想法数 + 最近活动时间）+ 中间消息流。
- **< 1024px**：项目收成顶部横向 Tab（可左右滑动）+ 全宽消息流。过滤栏移到输入框上方。

### 14.3 交互要点

- **输入框**：多行 textarea，`Enter` 提交、`Shift+Enter` 换行。提交后清空、聚焦回输入框。提交的是纯正文（`content`），标题留空。
- **消息条目**：勾选框（多选）、标题（可空）、正文、标签 chip、完成标记、相对时间。点击正文进入编辑态（inline textarea），失焦或 Ctrl+Enter 保存。
- **多选**：勾选任意条目后底部浮出操作条 —— 标记完成 / 合并 / 删除 / 加标签 / 取消选择。
- **合并**：勾选 ≥ 2 条 → 点「合并」→ 生成新条目 → 自动进入该新条目的编辑态，正文是拼接结果。
- **过滤栏**：状态三态按钮组 + 标签多选下拉 + **AND/OR 切换按钮**。
  - AND/OR 选择记在 `localStorage`（前端偏好，跨设备不共享）。
- **标签编辑**：输入框 + 自动补全（下拉），回车确认，支持删除 chip。
- **完成标记**：条目左侧勾选框，切换即刷新 `updated_at` 并置底。
- **删除**：二次确认后进回收站。
- **滚动**：见 §11.4。

### 14.4 主题

跟随系统 `prefers-color-scheme`，提供亮/暗两套 CSS 变量。无 UI 框架，用原生 CSS + 少量自定义组件。

## 15. 配置项

`appsettings.json` + 环境变量覆盖（`IDEA_` 前缀，双下划线表示层级）。

| 键 | 默认 | 说明 |
|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | Kestrel 监听地址（只绑回环） |
| `Database:Path` | `data/idea.db` | SQLite 文件路径，相对路径以应用目录为基准 |
| `Trash:RetentionDays` | `30` | 回收站保留天数。**页面与 API 都不可改** |
| `PublicBaseUrl` | 空 | 用于生成 `{{idea.url}}`，如 `https://ideas.example.com` |
| `Api:MaxIdeasPerResponse` | `2000` | 单次列表返回的最大条数，超过则 400 |
| `Webhook:TimeoutSeconds` | `10` | 单次投递超时 |
| `Webhook:MaxAttempts` | `3` | 失败重试次数（**含首次尝试**） |
| `Webhook:MaxDeliveryRecords` | `500` | 每个 webhook 保留的投递记录条数 |
| `Sse:BufferSize` | `500` | 服务端事件缓冲条数（供 `Last-Event-ID` 补发） |
| `Sse:HeartbeatSeconds` | `25` | SSE 心跳间隔（发 `: ping` 注释帧，防中间代理掐断空闲连接） |
| `Cors:Origins` | 空 | 预留：仅当前端独立部署时才需要；同源部署留空 |

## 16. 部署

### 16.1 构建

```bash
# 前端
cd web && npm ci && npm run build        # 产物输出到 server/IdeaApp/wwwroot/

# 后端
dotnet publish server/IdeaApp -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

发布产物：`server/IdeaApp/bin/Release/net10.0/linux-x64/publish/` 下的自包含二进制 + `wwwroot/`。

### 16.2 运行

```bash
IDEA_Urls=http://127.0.0.1:5080 \
IDEA_Trash__RetentionDays=30 \
IDEA_PublicBaseUrl=https://ideas.example.com \
./IdeaApp
```

首次启动自动建库、建内置收件箱。

### 16.3 nginx 反代

```nginx
upstream idea_app { server 127.0.0.1:5080; keepalive 32; }

server {
    listen 443 ssl;
    http2 on;                       # nginx ≥ 1.25.1
    server_name ideas.example.com;

    ssl_certificate     /etc/letsencrypt/live/ideas.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/ideas.example.com/privkey.pem;

    # 访问控制放在 nginx 层，应用本身不做鉴权
    auth_basic           "Idea";
    auth_basic_user_file /etc/nginx/.htpasswd;

    client_max_body_size 4m;

    location / {
        # 前端用 history 模式，深链 /projects、/trash 等由应用侧的
        # MapFallbackToFile("index.html") 兜底，nginx 不需要 try_files
        proxy_pass http://idea_app;
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
        proxy_set_header Connection        "";

        # SSE 必须关缓冲，否则实时推送会被攒着不发
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
```

要点：

- **访问控制在 nginx 层**：应用不做鉴权、也不做 Origin/CSRF 校验。默认用 basic auth 保护整站：
  ```bash
  htpasswd -c /etc/nginx/.htpasswd ideauser
  ```
  agent 走同一套凭证（见 §17 skill 的 `IDEA_USER` / `IDEA_PASS`）。若更希望「UI 要登录、API 对 agent 开放」，可以只给 `location /` 加 `auth_basic`，`location /api/` 另配 IP 白名单或 `map $http_x_api_key` 校验 —— 但那样 API 等于无保护，公网暴露时慎重。
- Kestrel 只绑 `127.0.0.1`，直接对外暴露会绕过 nginx 的 TLS。
- 应用侧启用 `UseForwardedHeaders()`。Kestrel 默认信任回环代理；nginx 在别的机器时必须在代码里显式配 `KnownProxies` / `KnownNetworks`，否则 `X-Forwarded-Proto` 不生效，HTTPS 重定向会循环。
- 全局开了 gzip 时，对 `text/event-stream` 排除，否则事件会被压缩缓冲、推送延迟。
- SSE 响应加 `X-Accel-Buffering: no`（应用侧设置，双保险）。

## 17. Agent Skill 交付物

随项目一起交付一个可直接使用的 agent skill，放在仓库的 `skills/idea-manager/`：

```
skills/idea-manager/
  SKILL.md                  # 说明书：能力清单 + 典型用法 + 错误码对照
  scripts/idea.py           # 命令行封装，纯标准库（urllib），无第三方依赖
  references/openapi.json   # openapi/idea-v1.json 的副本
```

**调用形态**：`scripts/idea.py` 用 argparse 做子命令，覆盖 §13 的全部能力：

```
idea.py projects                              # 项目列表
idea.py project add "项目名"
idea.py project rename <id> "新名字"
idea.py project delete <id>
idea.py project restore <id>
idea.py list [--project <id>] [--status all|open|done] [--tags a,b] [--tag-mode or|and]
idea.py add "正文" [--project <id>] [--title "标题"] [--tags a,b]
idea.py update <id> [--content "新正文"] [--title "新标题"]
idea.py tags <id> "标签1,标签2"               # 覆盖式
idea.py done <id> [--undo]
idea.py delete <id>
idea.py restore <id>
idea.py merge <id1> <id2> [...] [--content "合并后的正文"]
idea.py bulk done <id...> [--undo]
idea.py bulk tags <id...> "a,b"
idea.py bulk delete <id...>
idea.py trash
idea.py taglist
idea.py webhook list|add|update|delete
idea.py telegram-template                     # 打印 Telegram webhook 模板
```

**连接配置**（环境变量，不进仓库）：

| 变量 | 说明 |
|---|---|
| `IDEA_BASE_URL` | 如 `https://ideas.example.com` |
| `IDEA_USER` | nginx basic auth 用户名 |
| `IDEA_PASS` | nginx basic auth 密码 |
| `IDEA_TOKEN` | 可选。若 nginx 侧另外校验 `X-Api-Key` 头则填 |

**输出**：默认输出 JSON，便于 agent 直接解析；`--format text` 给人类看。出错时把 ProblemDetails 的 `code` 与 `detail` 原样打到 stderr，并以非 0 退出。

**自检**：skill 附带一条自检命令（`idea.py list`），验证目标实例可达且凭证正确。

**安装位置**：skill 目录可直接软链到 `~/.claude/skills/idea-manager` 或 `~/.zcode/skills/idea-manager`，两者格式一致。

---

## 18. 测试策略

| 层级 | 内容 |
|---|---|
| 单元测试 | 占位符渲染与转义、标签并集、合并正文拼接、排序规则（含同刻兜底）、AND/OR 过滤判定、回收站到期判定、`changed` 字段推导、恢复项目时的「只恢复因项目删除而进回收站的想法」判定 |
| 集成测试 | 用真实 Kestrel 起服务的 `WebApplicationFactory`（测试项目不开 AOT）跑 REST 全流程：建项目 → 建想法 → 编辑置底 → 批量合并 → 删除进回收站 → 恢复 → 过滤 → 回收站清理任务 |
| SSE 集成测试 | 订阅事件流，触发写操作，断言收到的事件类型与载荷；断言 SSE 帧格式与心跳；断言 `Last-Event-ID` 补发与越界时的 `resync` 帧 |
| webhook 集成测试 | 起一个本地假 HTTP 端点，断言收到的 body 是渲染后的模板、Header 正确、失败重试与死信落库、`attempts` 口径正确、手动重投重发原 payload |
| 并发/顺序测试 | 断言「提交后才投递事件」，事务回滚不产生幽灵事件；断言同一事件被 SSE 与 webhook 两个 handler 各收到一次（扇出而非竞争消费） |
| 前端 | 用 Vitest 测纯逻辑（过滤器、排序、SSE 事件与本地列表的合并）；UI 手测 |
| skill | 对 `scripts/idea.py` 做端到端测试：起一个真实实例，跑完 §17 列出的每个子命令，断言输出 JSON 与接口返回一致、错误码正确透传 |

## 19. 留给实现者的空白

实现者可以自行决定、不会改变本设计的东西：

- 领域服务的内部类划分与文件组织。
- 手写 SQL 的具体写法、连接工厂与事务封装形式。
- 前端组件拆分、CSS 组织方式、图标方案。
- 标签输入框的补全交互细节。
- `MonotonicGuid.NewUuid7()` 的具体字节布局（实现者按此写，测试用已知答案向量钉住）。
- 想法条目的 `title` 是否在列表里单独占一行（空标题时回落到正文首行）。

## 20. 已定的关键决定一览（含决策来源）

| 决定 | 来源 |
|---|---|
| 单用户自托管，无鉴权，nginx 反代 | 需求 |
| 聊天框只做记事，不接 LLM | 需求 |
| 完整 REST API + OpenAPI 文件，供 agent 生成 skill | 需求 |
| 编辑就地改写 + 整体置底；任何写操作都刷新 `updated_at` | 对话 |
| 合并生成全新条目，源条目硬删，标签取并集 | 对话 |
| 项目强制归属 + 内置收件箱；删除项目不删数据 | 对话 |
| 回收站装「删除的想法」和「删除的项目（含其想法）」；按配置保留期自动彻底删除，不允许手动彻底删除 | 对话 |
| 回收站保留期只从配置读，页面与 API 都不提供设置入口 | 对话 |
| 标签过滤 AND/OR 可切换，前端记忆偏好 | 对话 |
| 事件广播 + 可配置处理器，当前只有 webhook | 需求 |
| 全局 webhook 默认禁用；项目级 webhook 默认启用 | 对话 |
| Telegram 模板用 Bot API `sendMessage`，含 `chat_id` 与可选 `message_thread_id`，纯文本不带 `parse_mode` | 对话 + 既有脚本 |
| webhook 异步投递，带重试与死信 | 对话 |
| 前端 Vue 3 + TS + Vite | 对话 |
| 主界面两版布局都要，按宽度自适应，断点 1024px | 对话 |
| 项目排序两种模式可切，默认「最近活动」 | 对话 |
| 多端实时同步：SSE 广播，事件带完整实体 | 对话 |
| 访问控制放 nginx 层（basic auth），应用不做鉴权也不做 CSRF 校验 | 评审后确认 |
| 本次交付包含 agent skill（SKILL.md + 命令行封装 + 说明书） | 评审后确认 |
| 恢复项目**不**复活「先于删项目被单独删除」的想法 | 评审后确认 |

### 20.1 评审后由设计者自行敲定的补充决定

| 决定 | 理由 |
|---|---|
| 列表接口**不分页**，全量返回裸数组，超过 2000 条返回 400 | 排序恒为 `updated_at ASC` 与「页码从哪一端翻」天然矛盾；个人工具量级不需要分页 |
| SSE **恒定全量推送，过滤在客户端做** | AND/OR 偏好存在各设备 `localStorage`，服务端无从得知 |
| 事件**不参与** SQLite 事务，提交成功后才投递 | `Channel` 无法随事务回滚，否则产生幽灵事件 |
| 事件总线**扇出**：每个 handler 一条独立通道 | `Channels` 是竞争消费，共读一条通道会各拿一半事件 |
| 同刻排序兜底用 `id ASC`（要求 id 是**单调** UUIDv7，自实现计数器法） | 「同刻插入的靠后」要求后插入者更大更靠下；.NET 内置 `Guid.CreateVersion7()` 同毫秒内乱序，不满足 |
| 项目级 webhook **不单独进回收站**，随项目隐式失效、随项目删除 | 回收站按需求只装「删除的想法」和「删除的项目」 |
| 恢复项目**只恢复因删除项目而进回收站的想法**（`idea.deleted_at == project.deleted_at`） | 不复活用户明确单独删掉的东西 |
| 投递记录**每次尝试更新同一行**，`attempts` = 总尝试次数（含首次） | 记录对应一次事件投递，不是每次尝试一行 |
| 手动重投**重发已渲染的 payload** | 表里没存事件全量数据，重渲染的输入不存在 |
| webhook 队列**有界（1000）**，满则丢弃并记日志 | 写路径绝不因投递而阻塞 |
| 回收站想法的判定用 `deleted_at` 时间戳区分来源 | 一个想法不会在回收站里出现两次 |
| `{{idea.url}}` 指向 `{PublicBaseUrl}/?idea={id}`，前端 history 模式 + `MapFallbackToFile` | 深链要能定位到具体条目 |
| 错误统一用 ProblemDetails + `code` 扩展字段 | openapi 文件是给 agent 用的契约，需要稳定的机器可读错误码 |
