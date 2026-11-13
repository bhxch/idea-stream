# Idea 管理工具 · 前端实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 Vue 3 + TypeScript + Vite 实现 idea 记事本的网页界面：聊天式录入、消息流回看、过滤、多选批量、响应式两版布局、项目/回收站/webhook 管理页，以及 SSE 多端实时同步。

**Architecture:** Vite 构建的 SPA，产物输出到 `server/IdeaApp/wwwroot/`，由 ASP.NET Core 同源托管（history 模式 + `MapFallbackToFile` 兜底）。所有后端交互走 `src/api/client.ts` 的类型化封装；SSE 走 `src/api/events.ts`。业务逻辑（过滤、排序、事件合并）抽成 `src/lib/` 下的纯函数，用 Vitest 覆盖。

**Tech Stack:** Vue 3.5 / TypeScript 5.x / Vite 7 / vue-router 4 / Vitest 3 / Node 24

**Spec:** `docs/superpowers/specs/2026-11-11-idea-manager-design.md`

**Depends on:** `docs/superpowers/plans/2026-11-11-idea-backend.md` 必须先完成（API 契约以该计划 §3 的 DTO 定义为准）。

## Global Constraints

- Node 24、npm 11；依赖只允许 `vue`、`vue-router`、`vite`、`typescript`、`vue-tsc`、`vitest`、`jsdom`、`@vitejs/plugin-vue`、`@vue/test-utils`。
- **不引入 UI 框架**（无 Element Plus / Naive / Tailwind）。样式用原生 CSS + CSS 变量。
- 主题跟随系统 `prefers-color-scheme`，亮/暗两套 CSS 变量，不做手动切换。
- 响应式断点 **1024px**：≥1024px 左侧项目栏；<1024px 顶部横向 Tab。
- AND/OR 偏好存 `localStorage`（键 `idea.tagMode`，值 `or`/`and`），**跨设备不共享**。
- 项目排序方式存后端（`/api/settings/project.sort_mode`），跨设备共享。
- 所有 API 请求带 `credentials: 'same-origin'`（nginx basic auth 需要）。
- 错误处理统一读 ProblemDetails 的 `code` 与 `detail`，在 UI 上以 toast 展示 `detail`。
- 不使用 Pinia；状态用组合式函数 + 模块级单例。
- **Toast 组件的契约（F1 已建，后续 task 直接用，不要重建）**：`web/src/components/ToastHost.vue` 导出 `showToast(message: string, kind?: 'error' | 'info')`（`kind` 默认 `'info'`）。它挂在 `App.vue` 里，全局可用；错误统一用 `kind: 'error'`。**F1 建的 5 个占位页面（Home/Projects/Trash/Webhooks/ProjectWebhooks）在各自的 task 里是「整体替换」，不是新建。**
- 每个 task 结束提交一次，commit message 用 Conventional Commits（中文正文）。

## 后端契约要点（后端 11 个 task 已实现，前端必须按这些写）

**SSE 事件**（`GET /api/events`，`text/event-stream`）：

- 帧格式严格三行 + 空行：`id: <事件id>` / `event: <类型>` / `data: <单行 JSON>`。
- **事件 id 是 32 位无连字符的小写 hex**（单调 UUIDv7，字典序 = 生成序）。重连时用 `Last-Event-ID` 原样回传。
- **`timestamp` 是 `+00:00` 结尾**（STJ 默认 `DateTimeOffset` 格式），**不是 `Z`** —— `Z` 是后端存库用的格式，两者不同。
- **`resync` 是控制帧**：没有 `id:` 行、`data` 是 `{}`。**两种场合服务端会发它**：① 重连时 `Last-Event-ID` 在缓冲里找不到；② 某个订阅者太慢、它的通道被挤掉了一条事件（保持连接的客户端无法自行检出间隙 → 会静默空洞）。**收到 `resync` 就重新拉取当前列表。**
- **`Changed`（`changed` 字段）的语义**：后端**恒定写出这个键**（`JsonSourceGenerationOptions` 没设 `DefaultIgnoreCondition`，null 不会被省略），所以它的值是三态：`null` = 该事件类型没有「改了什么」的概念（如 `idea.created`）；空数组 = 写了但值没变；非空数组 = 该操作写过的字段名。**注意三种口径不同**：`idea.updated`（Update）只列真正变了的字段；`SetTags`/`SetDone`（含批量）**一律**列该字段；项目改名一律列 `name`；**重排的 `["sortOrder"]` 是有条件列的**（只在 `sort_order` 真变时发）。
  → **不要按「`changed` 里有该字段 = 值真变了」写逻辑**；判定三态用 `changed === null` / `changed.length === 0` / 其它，**不是 `undefined`**（TS 类型是 `changed: string[] | null` —— **必填可空、没有 `?`**：线上永远是 `null` / 空数组 / 非空数组，**没有 `undefined`**）。`mergedFrom` 同理（恒存在、可能为 null）。去掉 `?` 的两道编译期护栏：漏写这两个键 → TS2739（只缺一个则是 TS2741）；不判空就 `.length` → TS18047。**注意 `changed === undefined` 不会被 TS 拦下**（相等性比较的可比性检查在目标类型是 null/undefined 时放行），它只是运行时恒假 —— 写错不会报错、只会静默走错分支，所以三态判定必须写 `=== null`。事件里的项目快照是 `ProjectSnapshot { id, name, sortOrder }`，**带 `sortOrder`**（重排时靠它更新本地顺序，别去整表重拉）。
- **重排的事件条数** = `sort_order` 值**真的变了**的项目数，**不是**请求里的 id 数（后端会重写全表 `sort_order`，但只有活跃项目的变化产生事件）。
- **`project.updated` 的 `changed` 可能有第五种取值 `["deletedAt"]`** —— 项目从回收站恢复时发的，前端要认识它。
- **切换排序方式（recent → manual）会发一批 `project.updated`（`changed=["sortOrder"]`）** —— 因为初始化把全表 `sort_order` 重写了一遍。客户端按 sortOrder 重排看到的顺序与切换前一致（不跳位），但这是一次设置操作引发 N 条业务事件，前端要能承受。
- 客户端断开后重连用 `EventSource` 的自动重连即可，但**要自己带 `Last-Event-ID`**（后端支持 `?lastEventId=` 或请求头）。

**API 契约**：

| 端点 | 返回 | 注意 |
|---|---|---|
| `GET /api/ideas` | **裸数组**（不分页） | `projectId`（可空=全部）、`status`（all/open/done）、`tags`（逗号分隔）、`tagMode`（or/and，默认 or）。超过 2000 条返回 400 `too_many_ideas` |
| `POST /api/ideas/bulk/delete` | **200 + 被软删的想法数组** | **不是 204** —— 前端可以直接把它们从本地列表移除 |
| `GET /api/trash` | **两类条目**的数组 | `kind` 为 `project`（容器）或 `idea`（顶层）。**容器条目的条数用 `entry.ideas.length`，不要用 `entry.project.ideaCount`**（后者统计的是未删想法，已删项目恒为 0） |
| `GET /api/webhooks?projectId=` | 裸数组 | **不带 `projectId` 只列全局作用域的**；带了就列该项目的 |
| `POST /api/ideas/{id}/restore` | 200 + 想法 | 目标不在回收站 → 400 `not_in_trash` |
| `DELETE /api/ideas/{id}` | 204 | **幂等**：已在回收站的想法再删一次也是 204，不改 `deleted_at`、不发事件 |

**错误码**（ProblemDetails 的 `code` 扩展字段）：`idea_not_found`、`project_not_found`、`inbox_not_deletable`、`invalid_content`、`invalid_project_name`、`invalid_reorder_ids`、`invalid_status`、`invalid_tag_mode`、`invalid_merge_selection`、`merge_cross_project`、`not_in_trash`、`too_many_ideas`，以及 webhook 相关的 `invalid_webhook_name`/`invalid_webhook_url`/`invalid_webhook_scope`/`invalid_webhook_project`/`invalid_body_template`/`invalid_event_type`/`webhook_not_found`。**全集见 skill 计划的错误码表。**

## Review Focus

1. **SSE 事件与本地乐观更新撞车** —— 自己发起的操作已乐观插入，广播回来时不能出现两条（按事件 id / 想法 id 去重）。
2. **用户向上翻看时来了新事件** —— 绝不能把滚动位置抢走；要出现「↓ 有 N 条新内容」按钮。
3. **过滤状态变了之后来的事件** —— 不符合当前过滤条件的想法不能被塞进消息流（否则用户会看到"已完成"的条目出现在"未完成"视图里）。
4. **正文含 `<b>` `&` 等字符** —— 前端一律按纯文本渲染（`textContent` / 插值），**不得用 `v-html`**，避免 XSS 与内容被吞。
5. **深链 `?idea=<id>`** —— 打开后要滚动定位到那条并高亮；若该 id 不存在或被过滤掉，要优雅降级（不报错、不白屏）。

---

## 文件结构

```
web/
  package.json
  vite.config.ts
  tsconfig.json
  tsconfig.node.json
  index.html
  src/
    main.ts
    App.vue
    router.ts
    styles/theme.css
    styles/base.css
    api/types.ts          # 与后端 DTO 一一对应的 TS 类型
    api/client.ts         # fetch 封装 + ProblemDetails 错误
    api/events.ts         # EventSource 封装（重连 + Last-Event-ID + resync）
    lib/filter.ts         # 纯逻辑：过滤判定 + 排序
    lib/merge.ts          # 纯逻辑：SSE 事件合并进列表
    lib/format.ts         # 相对时间格式化
    stores/projects.ts
    stores/ideas.ts
    stores/settings.ts
    composables/useTagMode.ts
    composables/useScrollFollow.ts
    components/ProjectSidebar.vue
    components/ProjectTabs.vue
    components/MessageList.vue
    components/MessageItem.vue
    components/Composer.vue
    components/FilterBar.vue
    components/TagFilter.vue
    components/TagEditor.vue
    components/BatchBar.vue
    components/MergeDialog.vue
    components/ToastHost.vue
    pages/HomePage.vue
    pages/ProjectsPage.vue
    pages/TrashPage.vue
    pages/WebhooksPage.vue
    pages/ProjectWebhooksPage.vue
    lib/filter.spec.ts
    lib/merge.spec.ts
    lib/format.spec.ts
    composables/useTagMode.spec.ts
```

---

### Task 1 (F1): Vite + Vue 3 骨架、路由、主题与 API 客户端

**Files:**
- Create: `web/package.json`、`web/vite.config.ts`、`web/tsconfig.json`、`web/tsconfig.node.json`、`web/index.html`
- Create: `web/src/main.ts`、`web/src/App.vue`、`web/src/router.ts`
- Create: `web/src/styles/theme.css`、`web/src/styles/base.css`
- Create: `web/src/api/types.ts`、`web/src/api/client.ts`
- Create: `web/src/lib/filter.spec.ts`（先放一个占位测试，Task F4 填内容）
- Modify: `.gitignore`（加 `server/IdeaApp/wwwroot/`）

**Interfaces:**
- Produces:
  - `web/src/api/types.ts` 导出（与后端 DTO 字段名逐字一致）：
    ```ts
    export interface Idea { id: string; projectId: string; title: string | null;
      content: string; tags: string[]; done: boolean;
      createdAt: string; updatedAt: string; }
    export interface Project { id: string; name: string; sortOrder: number;
      ideaCount: number; lastActivityAt: string | null;
      createdAt: string; updatedAt: string; }
    export interface Tag { name: string; count: number; }
    export interface Webhook { id: string; name: string; url: string; method: string;
      headers: Record<string,string>; bodyTemplate: string; events: string[];
      scope: 'global'|'project'; projectId: string | null; enabled: boolean;
      hasSecret: boolean; createdAt: string; updatedAt: string; }
    export interface Delivery { id: string; webhookId: string; eventId: string;
      eventType: string; payload: string; status: string; attempts: number;
      lastError: string | null; createdAt: string; updatedAt: string; }
    export interface TrashEntry { kind: 'project'|'idea'; project: Project | null;
      ideas: Idea[]; idea: Idea | null; }
    export interface ProjectSnapshot { id: string; name: string; sortOrder: number; }
    export interface IdeaEvent { id: string; type: string; timestamp: string;
      project: ProjectSnapshot | null;
      idea: Idea | null; changed: string[] | null; mergedFrom: string[] | null; }
    export interface ApiProblem { title: string; status: number; detail?: string;
      code?: string; }
    ```
  - `web/src/api/client.ts` 导出：
    ```ts
    export class ApiError extends Error { constructor(public problem: ApiProblem) { super(problem.detail ?? problem.title); } }
    export async function apiGet<T>(path: string, params?: Record<string, string | undefined>): Promise<T>;
    export async function apiSend<T>(method: 'POST'|'PUT'|'PATCH'|'DELETE', path: string, body?: unknown): Promise<T>;
    ```
    `apiGet`/`apiSend` 统一带 `credentials: 'same-origin'`；非 2xx 时解析 `application/problem+json` 抛 `ApiError`，解析失败则构造 `{title: statusText, status}`。
  - `web/src/router.ts` 导出 `router`，路由表：
    ```ts
    { path: '/', name: 'home', component: HomePage }
    { path: '/projects', name: 'projects', component: ProjectsPage }
    { path: '/trash', name: 'trash', component: TrashPage }
    { path: '/webhooks', name: 'webhooks', component: WebhooksPage }
    { path: '/projects/:id/webhooks', name: 'project-webhooks', component: ProjectWebhooksPage }
    ```
    用 `createWebHistory()`。
  - `web/vite.config.ts`：`plugins: [vue()]`，`build: { outDir: '../server/IdeaApp/wwwroot', emptyOutDir: true }`，`base: '/'`，`test: { environment: 'node' }`。
  - `web/src/styles/theme.css`：`:root` 定义 `--bg`、`--fg`、`--muted`、`--border`、`--accent`、`--chip-bg`、`--danger` 等变量；`@media (prefers-color-scheme: dark)` 覆盖一套。

- [ ] **Step 1: 初始化项目**

```bash
cd /share/rw/repo/server/kanban
mkdir -p web/src && cd web
npm init -y
npm install vue@^3.5 vue-router@^4
npm install -D vite@^7 typescript@^5 vue-tsc @vitejs/plugin-vue vitest@^3 jsdom @vue/test-utils
```

- [ ] **Step 2: 写失败测试**

`web/src/lib/filter.spec.ts` 此刻只写一个占位断言（Task F4 会替换为真正的用例）：

```ts
import { describe, it, expect } from 'vitest';
describe('lib', () => { it('placeholder', () => { expect(1).toBe(1); }); });
```

- [ ] **Step 3: 运行测试确认失败**

Run: `cd web && npx vitest run` → Expected: FAIL（无 vitest 配置 / 无测试文件被找到）

- [ ] **Step 4: 实现**

按上面的接口定义逐字创建类型与函数。`main.ts` 挂载 router 与 `App.vue`。`App.vue` 只放 `<RouterView />` 与 `<ToastHost />`。

- [ ] **Step 5: 运行测试确认通过**

Run: `cd web && npx vitest run` → Expected: PASS
Run: `cd web && npx vue-tsc --noEmit` → Expected: 无错误

- [ ] **Step 6: 提交**

```bash
git add web/ .gitignore
git commit -m "feat(web): 初始化 Vue3+Vite 骨架、路由、主题与 API 客户端"
```

---


> **jsdom 没有 `EventSource`**（Node 24 与 jsdom 都不提供）—— F2 的 spec **必须 `vi.stubGlobal('EventSource', ...)`** 造一个假的，否则 `new EventSource(...)` 会直接炸。F1 已把 vitest 的 `environment` 改成全局 `jsdom`。
> **`changed` / `mergedFrom` 是必填可空**（`string[] | null`，没有 `?`）—— 后端恒定写出这两个键，运行时永远不是 `undefined`；判定三态用 `=== null` / `length === 0`。**注意：写 `=== undefined` 不会被 TS 拦下**（TS 的相等性比较在目标类型是 null/undefined 时放行，不报 TS2367），它只是运行时恒假的死分支 —— 三态判定一律写 `=== null`。漏写这两个键（TS2739）与不判空就 `.length`（TS18047）才是 TS 真正拦得住的两种错法。
> **`ApiError` 判错要用 `instanceof`，别只看 `status`** —— 有一条路径是「2xx + 非 JSON 响应体」，那时 `status` 是 200。
> **后端的续发位置解析：`Last-Event-ID` 请求头优先、`?lastEventId=` 查询参数兜底**（`EventSource` 不能设自定义头，所以查询参数是浏览器端唯一能用的续发方式）。前端重连的 URL 里**带** `?lastEventId=<最后收到的事件id>`。
> **重连成功后前端仍保留「整表重拉一次」的安全网** —— 后端认了查询参数后这层是冗余的（服务端要么精确续上、要么发 `resync`），但**多一次列表请求 << 静默丢事件**，保留。
### Task 2 (F2): SSE 客户端（重连、Last-Event-ID、resync）

**Files:**
- Create: `web/src/api/events.ts`
- Create: `web/src/lib/merge.spec.ts`（占位）

**Interfaces:**
- Consumes: F1 的 `IdeaEvent` 类型。
- Produces: `web/src/api/events.ts`
  ```ts
  export type EventListener = (e: IdeaEvent) => void;
  export interface EventStream {
    close(): void;
    readonly lastEventId: string | null;
  }
  export function connectEvents(handlers: {
    onEvent: EventListener;
    onResync: () => void;
    onStatus?: (connected: boolean) => void;
  }): EventStream;
  ```
  行为：`new EventSource('/api/events', { withCredentials: true })`；监听 `idea.created`/`idea.updated`/`idea.deleted`/`idea.restored`/`idea.merged`/`project.created`/`project.updated`/`project.deleted` 全部事件类型；`resync` 触发 `onResync`；每次收到事件记录 `lastEventId`；`onerror` 时关闭并按 2s 退避重连，重连时若 `lastEventId` 存在则用 `?lastEventId=<id>` 追加到 URL。

- [ ] **Step 1: 写失败测试** —— 写一个纯逻辑测试占位，真实 SSE 连接靠 Task F11 的手工验证。

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 按接口定义写；重连计时用 `setTimeout`，指数退避上限 30s。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现 SSE 客户端与断线重连"`

---

### Task 3 (F3): 项目状态、想法状态与消息流主页面骨架

**Files:**
- Create: `web/src/stores/projects.ts`、`web/src/stores/ideas.ts`、`web/src/stores/settings.ts`
- Create: `web/src/pages/HomePage.vue`
- Create: `web/src/components/MessageList.vue`、`web/src/components/MessageItem.vue`、`web/src/components/Composer.vue`
- Create: `web/src/lib/format.ts`、`web/src/lib/format.spec.ts`

**Interfaces:**
- Consumes: F1 的 `client.ts`/`types.ts`。
- Produces:
  - `web/src/stores/projects.ts`：模块级单例，导出 `projects`（ref）、`loadProjects()`、`currentProjectId`（ref）、`setCurrentProject(id)`、`projectSortMode`（ref `'recent'|'manual'`）、`loadSortMode()`、`saveSortMode(mode)`。
  - `web/src/stores/ideas.ts`：`ideas`（ref）、`loading`、`loadIdeas(projectId: string | null)`、`applyLocalUpsert(idea)`、`removeLocal(id)`。
    —— **注意签名只有 `projectId`，没有 `filter`**：过滤在客户端做（AND/OR 偏好在各设备自己的 localStorage 里，服务端按它自己的 `or` 处理，与本机选的 `and` 会打架）。把 `filter` 传给后端会让用户看到的 AND 语义失效。
  - `web/src/lib/format.ts`：`formatRelativeTime(iso: string, now?: Date): string` —— 输出 `刚刚` / `N 分钟前` / `N 小时前` / `N 天前` / `M月D日`。
  - 组件 props：`MessageList` 收 `ideas: Idea[]`、`selectedIds: Set<string>`、`atBottom: boolean`，emit `toggle-select`、`delete`、`toggle-done`。
    **F5 之后新增（都是必填 prop 回调，不是 emit）**：`suggest(prefix: string, projectId: string | null): Promise<string[]>`（标签补全）、`onSaveContent(id: string, content: string): Promise<SaveOutcome>`（保存正文，**返回三态** `'saved' | 'failed' | 'busy'` —— emit 拿不到父组件处理器的返回值，而「存好了才退出编辑态、失败留在里面」必须有返回值；三态是因为 `'busy'`（**压根没发出去**，被同一条目上的在飞保护挡下）必须跟 `'failed'`（请求发出去了、失败了）分开：混成一个 false 的话，被挡下的那次改动就既没落库也没提示 —— 静默丢）、`onSaveTags(id: string, tags: string[], options?: SaveOptions): Promise<SaveOutcome>`（`options.silent` = 失败时不弹提示，给「捡回上次没存上的那版后自动补存」用：用户只是打开编辑器看看，不该看到「保存标签失败」）、`busyIds: Set<string>`（在飞保护）。`SaveOutcome` / `SaveOptions` 定义在 `web/src/lib/save.ts`。`MessageItem` 同理：`busy?: boolean` + `onSaveContent` / `onSaveTags` 回调 prop。
  - 组件 props：`Composer` 收 **`onSubmit: (content: string) => Promise<boolean>`**（**不是 emit**）。
    —— 提交回调返回 Promise 表示成败：**成功之后才清空输入框并保持焦点**；失败时内容原样留在框里（用户刚敲的正文不能因为一次网络失败就没了）。
    `Composer` 内部另有 `submitting` 标志防重复提交。

- [ ] **Step 1: 写失败测试**

`web/src/lib/format.spec.ts`：

```ts
it('formatRelativeTime', () => {
  const now = new Date('2026-11-11T12:00:00Z');
  expect(formatRelativeTime('2026-11-11T11:59:30Z', now)).toBe('刚刚');
  expect(formatRelativeTime('2026-11-11T11:30:00Z', now)).toBe('30 分钟前');
  expect(formatRelativeTime('2026-11-11T09:00:00Z', now)).toBe('3 小时前');
  expect(formatRelativeTime('2026-11-10T12:00:00Z', now)).toBe('昨天');
  expect(formatRelativeTime('2026-11-01T12:00:00Z', now)).toBe('11月1日');
});
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL（模块不存在）
- [ ] **Step 3: 实现**
  - `MessageList` 按 `ideas` 数组顺序渲染（顺序由后端保证，**前端不重排**）。
  - `MessageItem` 用插值渲染正文（**禁止 `v-html`**），标题为空时正文首行作为视觉标题。
  - `Composer` 是 textarea，`Enter` 提交（`Shift+Enter` 换行），提交后清空并保持焦点。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现主页面骨架、项目与想法状态"`

---

### Task 4 (F4): 过滤逻辑（状态、标签、AND/OR）与过滤栏

**Files:**
- Create: `web/src/lib/filter.ts`
- Modify: `web/src/lib/filter.spec.ts`（替换占位）
- Create: `web/src/composables/useTagMode.ts`、`web/src/composables/useTagMode.spec.ts`
- Create: `web/src/components/FilterBar.vue`、`web/src/components/TagFilter.vue`
- Modify: `web/src/pages/HomePage.vue`

**Interfaces:**
- Produces:
  - `web/src/lib/filter.ts`：
    ```ts
    export type StatusFilter = 'all' | 'open' | 'done';
    export type TagMode = 'or' | 'and';
    export interface IdeaFilter { projectId: string | null; status: StatusFilter;
      tags: string[]; tagMode: TagMode; }
    export function matchesFilter(idea: Idea, f: IdeaFilter): boolean;
    export function sortIdeas(ideas: Idea[]): Idea[];   // updatedAt ASC, id ASC
    ```
    标签比较大小写不敏感。`sortIdeas` 返回新数组，不改原数组。
  - `web/src/composables/useTagMode.ts`：`export function useTagMode(): { tagMode: Ref<TagMode>; setTagMode(m: TagMode): void; }`，初值从 `localStorage.getItem('idea.tagMode')` 读（非法值回落 `or`），`setTagMode` 写回 `localStorage`。
  - `FilterBar` props：`filter: IdeaFilter`、`allTags: Tag[]`，emit `update:filter`。
  - `TagFilter` props：`allTags: Tag[]`、`selected: string[]`、`mode: TagMode`，emit `update:selected`、`update:mode`；**AND/OR 用一个按钮切换**，按钮上显示当前模式。

- [ ] **Step 1: 写失败测试**

`web/src/lib/filter.spec.ts`：

```ts
it('matchesFilter or 模式：任一标签命中即可', () => { /* idea tags ['a','b'], filter tags ['b','c'] mode 'or' → true */ });
it('matchesFilter and 模式：必须包含全部选中标签', () => { /* tags ['a','b'], filter ['a','b','c'] and → false */ });
it('matchesFilter 状态过滤：open 排除 done', () => { /* done=true, status 'open' → false */ });
it('matchesFilter 标签比较大小写不敏感', () => { /* tags ['A'], filter ['a'] → true */ });
it('sortIdeas 按 updatedAt 升序、id 升序', () => { /* 构造乱序数组 → 期望顺序 */ });
it('sortIdeas 不修改入参', () => { /* 断言原数组顺序不变 */ });
```

`web/src/composables/useTagMode.spec.ts`：

```ts
it('useTagMode 默认 or 并写入 localStorage', () => { /* 模拟 localStorage */ });
it('useTagMode 读取已存的 and', () => { /* 预置 localStorage['idea.tagMode']='and' */ });
it('useTagMode 非法值回落 or', () => { /* 预置 'xxx' */ });
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 按接口定义。`useTagMode.spec.ts` 需要在测试里给 `globalThis.localStorage` 装一个内存实现。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现过滤逻辑与过滤栏"`

---

### Task 5 (F5): 条目交互（编辑、标签、完成、删除）

> **既有契约（F3+F4 已建，F5 在此之上改，不要推翻）**：`MessageItem` 已有的 `busy?: boolean` prop（在飞时禁用按钮）与 `MessageList` 已有的**必填** `busyIds: Set<string>` prop —— F5 新加的交互（编辑/标签/删除）也要走同一套「在飞保护」，并把在飞条目的 id 加进 `busyIds`。
> `Composer` 的提交契约是 **prop `onSubmit: (content) => Promise<boolean>`**（不是 emit），清空输入框发生在**成功之后**。

**Files:**
- Create: `web/src/components/TagEditor.vue`
- Modify: `web/src/components/MessageItem.vue`、`web/src/pages/HomePage.vue`

**Interfaces:**
- Consumes: F3 的组件、F4 的过滤逻辑。
- Produces:
  - `TagEditor` props：`value: string[]`、`suggest: (prefix: string) => string[]`；emit `update:value`。输入时显示补全下拉，回车添加，空格/逗号分隔多标签，已选标签可点 × 删除。
  - `MessageItem` 新增行为：点击正文 → 进入编辑态（textarea），`Ctrl+Enter` 或失焦保存，`Esc` 取消；标题为空时用正文首行做视觉标题；左侧复选框切换选中；「✓ 完成」按钮切换完成态；「🗑 删除」二次确认后删除。
  - `MessageItem` 的**标签编辑器收起前要确认**：草稿与服务端当前值不一致（= 还有没存上的改动）时，用 `window.confirm('还有未保存的改动，确定收起？…')` 再问一次 —— 不用 toast（那是一闪而过的，用户稍后就忘了刚才有东西没存上）。用户不点头就不收起（草稿留在编辑器里，可以直接再存一次）；点了「确定」才收起，但那份没存上的改动**不扔**，下次打开标签编辑器会捡回来再存一次。比较按**集合**比（顺序/大小写/首尾空白都不算）—— 服务端存的是规范化后的结果（去重 + 按名字排序），比数组会每次都误判成「有未保存的改动」。

- [ ] **Step 1: 写失败测试** —— 本 task 的交互以组件级测试覆盖（`@vue/test-utils`），至少覆盖：
  - `MessageItem` 渲染正文时特殊字符不被解析为 HTML（断言 `wrapper.find('.body').element.innerHTML` 不含 `<b>` 标签）。
  - `TagEditor` 输入并回车后 emit `update:value` 带新标签。

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 保存编辑后调用 `PATCH /api/ideas/{id}`，用返回的 `Idea` 覆盖本地（`applyLocalUpsert`），**不自己猜 updatedAt**。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现想法条目的编辑、标签、完成与删除交互"`

---

### Task 6 (F6): 多选批量操作与合并流程

> **F6 实施后新增的契约（BatchBar 的 `add-tags` 带回执）**：`BatchBar` 的 `add-tags` 事件是
> `add-tags: [tags: string[], ack: (ok: boolean) => void]` —— 父组件无论成败都要调 `ack`，
> `BatchBar` 收到回执后才清草稿/收面板（**失败时草稿不丢**）。

**Files:**
- Create: `web/src/components/BatchBar.vue`、`web/src/components/MergeDialog.vue`
- Modify: `web/src/pages/HomePage.vue`

**Interfaces:**
- Consumes: F5 的条目交互。
- Produces:
  - `BatchBar` props：`count: number`，emit `complete`、`undo-complete`、`merge`、`delete`、`add-tags`、`clear`。
  - `MergeDialog` props：`open: boolean`、`ideas: Idea[]`，emit `confirm(content: string)`、`cancel`；显示选中条目的正文预览，初始 content 为各条以 `\n\n` 拼接（与后端一致），可编辑后确认。
  - `HomePage` 维护 `selectedIds: Ref<Set<string>>`；批量完成调 `POST /api/ideas/bulk/complete`，批量标签调 `POST /api/ideas/bulk/tags`，批量删除调 `POST /api/ideas/bulk/delete`，合并调 `POST /api/ideas/merge`。

- [ ] **Step 1: 写失败测试** —— `MergeDialog` 组件测试：传两条想法，断言初始 textarea 值为 `"A\n\nB"`；确认后 emit `confirm` 带改后的内容。

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 合并成功后用返回的 `Idea` 替换本地列表（源条目移除、结果置底），并把该条目的 id 记入 `focusIdeaId` 供 Task F11 滚动定位。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现多选批量操作与合并流程"`

---

### Task 7 (F7): 响应式两版布局

**Files:**
- Create: `web/src/components/ProjectSidebar.vue`、`web/src/components/ProjectTabs.vue`
- Modify: `web/src/pages/HomePage.vue`（**两版布局的样式写在它自己的 scoped style 里**，不进 base.css —— base.css 只管全局默认值）
- Modify: `web/src/styles/base.css`（**只加一行注释**：1024px 是全局断点，`.page` 的内边距规则与 HomePage 的两版布局共用同一个值，改一处要同步另一处）

**Interfaces:**
- Produces:
  - `ProjectSidebar` props：`projects: Project[]`、`currentId: string | null`、`mode: 'recent'|'manual'`，emit `select`、`new-project`、`reorder`、`change-mode`。
  - `ProjectTabs` props同 `ProjectSidebar`（不含 `reorder`），emit `select`、`new-project`、`open-menu`。
  - 布局实现方式：HomePage 用 CSS 媒体查询切换两个容器 —— **同一份项目数据渲染两次**（宽屏 `ProjectSidebar`、窄屏 `ProjectTabs`），用 `display: none` 在断点两侧互斥显示；筛选栏在窄屏时移到输入框上方（DOM 顺序用 CSS `order` 或 grid 区域调整）。

- [ ] **Step 1: 写失败测试** —— 用 `@vue/test-utils` 断言两个组件都渲染项目名与条数；断点行为靠 CSS，手测。

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 媒体查询 `@media (max-width: 1023px)` 隐藏侧栏、显示 Tab；`@media (min-width: 1024px)` 反之。窄屏 Tab 容器 `overflow-x: auto`。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现 1024px 断点的响应式两版布局"`

---

### Task 8 (F8): 项目管理页

> **F7 已建、F8 要沿用/可能要改的**：
> - 主页面的项目导航已由 `ProjectSidebar`（宽屏）/ `ProjectTabs`（窄屏）承担，**header 里的项目下拉框已被移除**（F7 实施时删的，6 条既有用例改成点侧栏）。F8 若要把项目下拉加回来，先想清楚与侧栏的分工。
> - 侧栏的「排序方式」按钮与**拖拽重排已接线**（F7 做的），F8 改 `ProjectSidebar` 时形态可能要换 —— 但**别把这两个功能弄丢**。
> - `new-project` / `open-menu` 在主页面上目前只是跳 `/projects`（管理动作归 F8）。

**Files:**
- Create: `web/src/pages/ProjectsPage.vue`
- Modify: `web/src/components/ProjectSidebar.vue`（排序方式切换下拉）

**Interfaces:**
- Consumes: F1 的 `client.ts`、F3 的 `stores/projects.ts`。
- Produces: `ProjectsPage` 提供：新建项目（输入框 + 按钮）、改名（行内编辑）、删除（二次确认）、拖拽排序（HTML5 drag & drop，仅 `manual` 模式下可拖）、排序方式切换（`recent` / `manual`）。
  - 拖拽完成后调 `POST /api/projects/reorder`，body `{ ids: [...] }`。

- [ ] **Step 1: 写失败测试** —— 组件测试：新建项目输入并提交后 emit/调用正确；排序方式切换触发 `PUT /api/settings/project.sort_mode`。

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 拖拽用 `draggable="true"` + `dragstart`/`dragover`/`drop`。

> **落点语义（控制器裁定 R153，2026-11-13）**：**源在目标之前（往下拖）→ 落到目标之后；源在目标之后（往上拖）→ 落到目标之前。**
> 四种方向组合都要有效果，**不许有「拖了等于没动」的死角** —— 拖到紧邻的下一项必须真的挪一格。
> 实现上：移除源之后目标的**新下标**是 `target`；源原来的下标小于目标原来的下标时插到 `target + 1`，否则插到 `target`。
> **这份语义在侧栏与项目管理页之间只有一份实现**（`web/src/lib/reorder.ts`）—— 两处各写一遍，迟早有一处变成另一种语义，同一份数据在两个界面里拖出来的顺序对不上。
>
> **排序方式切换（R159 修正，2026-11-13）**：**前端只调 `PUT /api/settings/project.sort_mode`，不要自己再发一次 `POST /api/projects/reorder`。**
> **后端已经做了初始化**：`ProjectService.SetSortMode` 在切到 `manual` 时会用**当前显示顺序**（此刻 `List()` 给出的顺序，也就是用户正看到的顺序）调 `Reorder(ids)` 落库，再写设置 —— 所以切过去后顺序与切换前一致，不会跳位。
> 前端再发一次反而有害：它用的是**前端缓存的**项目列表，别处（另一台设备）新建的项目不在这个列表里，而后端的 `Reorder` 会把「没被列出的接在后面」—— 那是真的会把顺序改坏。
> 侧栏的排序方式按钮与项目管理页的切换按钮因此是**同一条路径**：`saveSortMode(mode)` → `loadProjects()`。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现项目管理页与拖拽排序"`

---

### Task 9 (F9): 回收站页

**Files:**
- Create: `web/src/pages/TrashPage.vue`

**Interfaces:**
- Produces: `TrashPage` 调 `GET /api/trash`，渲染两类条目：容器（已删项目，展开显示其下想法）与顶层想法；每条提供「恢复」按钮（项目用 `POST /api/projects/{id}/restore`，想法用 `POST /api/ideas/{id}/restore`）。**不提供任何「彻底删除」按钮**。

- [ ] **Step 1: 写失败测试** —— 组件测试：给定两条 trash entry，断言渲染出容器与顶层条目；断言页面里**不存在**「彻底删除」字样的按钮。
- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现**
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现回收站页面"`

> **补充约定（控制器裁定 R154/R158，2026-11-13）**：
> - **容器条目的条数用 `entry.ideas.length`**，不要用 `entry.project.ideaCount`（后者统计的是未删想法，已删项目恒为 0）。
> - 容器里的**每条想法也能单独恢复**（spec §10.5：在项目仍在回收站时单独恢复一条想法是有可见效果的）。
> - 页面要有一句简短说明「回收站里的内容到期后会自动清理」—— 但**不能出现「彻底删除」四个字**（那条断言要继续成立），也**不要写具体天数**（保留期由服务端配置，页面与 API 都不给入口）。
> - 恢复失败（400 `not_in_trash`）要提示，不能静默。

---

### Task 10 (F10): Webhook 配置页与投递记录

**Files:**
- Create: `web/src/pages/WebhooksPage.vue`、`web/src/pages/ProjectWebhooksPage.vue`
- Create: `web/src/components/WebhookForm.vue`、`web/src/components/DeliveryList.vue`

**Interfaces:**
- Consumes: F1 的 `client.ts`/`types.ts`。
- Produces:
  - `WebhookForm` props：`webhook: Webhook | null`、`scope: 'global'|'project'`、`projectId?: string`，emit `saved`、`cancel`。字段：名称、URL、方法、自定义 Header（KV 编辑）、Body 模板（textarea）、订阅事件（多选，8 种事件类型）、启用开关、Secret（密码框，留空表示不改）。
  - 「添加 Telegram webhook」按钮：点 `GET /api/webhooks/templates/telegram`，用返回的 `url` 与 `bodyTemplate` 预填表单。
  - `DeliveryList` props：`webhookId: string`，展示 `GET /api/webhooks/{id}/deliveries` 的结果，每行有「重投」按钮（`POST /api/webhooks/{id}/deliveries/{deliveryId}/retry`）。
  - `WebhooksPage` 管全局（scope=global）；`ProjectWebhooksPage` 管单项目（scope=project，projectId 来自路由参数）。

- [ ] **Step 1: 写失败测试** —— `WebhookForm` 组件测试：点「Telegram 模板」按钮后 URL 与 Body 模板被预填（断言 body 模板含 `message_thread_id` 与 `{{idea.content}}`）。
- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— 新建 webhook 时不传 `enabled`，由后端按 scope 决定默认值。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 实现 webhook 配置页与投递记录"`

> **补充约定（控制器裁定 R155/R157，2026-11-13）**：
> - **删除 webhook 要二次确认**（删它是破坏性操作：配置与投递历史一起没了）。项目里删想法、删项目都有二次确认，保持一致。
> - **Secret 三态**按后端实际语义实现：`null` = 不改、`""` = 清空、非空 = 替换。前端除「留空 = 不改」外另给一个「清除密钥」入口。
> - ⚠️ **不要把 webhook 的 `url` 与 `headers` 打进控制台日志、错误信息或 toast** —— Telegram 的 bot token 就在 URL 路径里，而 API 又把 `url` 与 `headers` 原样回传。表单里该显示还是显示（用户自己填的）。
> - 全局页与项目页的逻辑**共用一份实现**（`WebhookManager` 之类），别复制两份。

---

### Task 11 (F11): SSE 接线、事件合并、滚动跟随与深链定位

> **⚠️ 在飞保护的雷（F5+F6 实施者发现，控制器裁定写进计划）**：`HomePage` 的 `busyIds` **每次 `markBusy`/`release` 都必须造一个全新的 `Set`** —— Vue 的 `Set` 响应式只追踪 `has`，**不追踪 `add`/`delete`**；若有人改成原地 `add`/`delete`，整条在飞保护会静默失效（按钮不禁用、补存不再重入队）。F11 若要动 `HomePage` 的 `busyIds`，请保留这个约定并加注释。
>
> **F5+F6 已建、F11 要消费的既有契约**：
> - `MessageList` 与 `MessageItem` 的编辑/标签保存是**回调 prop**（`onSaveContent`/`onSaveTags`，返回 `Promise<SaveOutcome>`，三态 `'saved' | 'failed' | 'busy'`），**不是 emit** —— F3 骨架里的占位 emit `edit`/`edit-tags` 已在 F5 删除（没有承载者，且 emit 拿不到返回值）。`'busy'` 表示**压根没发出去**（被同一条目上的在飞保护挡下）：调用方要能分清它和 `'failed'`，否则被挡下的那次改动会静默丢掉。
> - `stores/ideas.ts` 的 `focusIdeaId`（合并后新条目的 id）**不会自动复位** —— F11 消费时必须能容忍「id 不在列表里」（例如该条目已被删或不在当前过滤结果内）。
>
> **F11 评审后的控制器裁定（2026-11-13，实施者照此修）**：
> - **滚动与定位必须发生在 DOM 更新之后。** Vue 的 `watch` 默认 `flush:'pre'` —— 回调在组件重渲染**之前**跑：此时新条目还没进 DOM，`querySelector` 找不到它，`scrollHeight` 也还是旧值。定位/滚动一律用 `flush:'post'` 或 `await nextTick()`；`init()` 里「打开页面就滚到底」那一步，遇到深链锚点时不能把锚点挤出视野。
> - **`applyEvent` 的调用方只传项目维度的过滤条件**（`projectId` 照旧，`status:'all'`、`tags:[]`），状态/标签的过滤交给显示层（`visibleIdeas`）做。理由：现在把「改成不符合过滤」的条目从**数据集**里删掉，用户切回别的过滤条件时那条就找不回来了（要整表重拉才回来）—— 这与「过滤在客户端做」的前提冲突。`applyEvent` 纯函数自身的契约与它的测试**不动**。
> - **项目事件的失败必须有兜底**：`loadProjects()` / `switchProject()` 抛错不能变成未处理的 Promise rejection，页面上也要给提示。
> - **滚动跟随与深链定位抽成可单测的 composable** —— 不要继续往 `HomePage.vue` 里堆（它已经 1151 行了）。

**Files:**
- Modify: `web/src/lib/merge.ts`（从占位改为实现）
- Modify: `web/src/lib/merge.spec.ts`
- Create: `web/src/composables/useScrollFollow.ts`
- Create: `web/src/composables/useScrollFollow.spec.ts`
- Modify: `web/src/pages/HomePage.vue`、`web/src/components/MessageList.vue`、`web/src/components/MessageItem.vue`

**Interfaces:**
- Consumes: F2 的 `connectEvents`、F4 的 `matchesFilter`/`sortIdeas`。
- Produces:
  - `web/src/lib/merge.ts`：
    ```ts
    export function applyEvent(ideas: Idea[], e: IdeaEvent, f: IdeaFilter): Idea[];
    export function applyResync(ideas: Idea[], fresh: Idea[]): Idea[];
    ```
    `applyEvent` 规则：`idea.created` → 追加；`idea.updated` → 按 id 替换并重排；`idea.deleted` → 移除；`idea.restored` → 追加；`idea.merged` → 移除 `mergedFrom` 里的 id、加上新 idea；`project.*` → 返回原数组（项目变更由项目 store 处理）。**任何情况下先按 `matchesFilter` 判断是否展示**；不匹配就只做「移除」类操作，不做插入。重复 id 用 `Map` 去重。
  - `web/src/composables/useScrollFollow.ts`：`export function useScrollFollow(container: Ref<HTMLElement|null>)`，返回 `{ atBottom: Ref<boolean>, onNewContent(): void, scrollToBottom(): void, pendingCount: Ref<number> }`；`atBottom` 由 scroll 事件计算（距底部 ≤ 32px 视为贴底）；`onNewContent` 在贴底时滚动到底部，否则 `pendingCount++`。
  - 深链：HomePage 读 `route.query.idea`，加载完成后找到该 idea → 滚动到它并给该条目加 `highlight` 类（1.5s 后移除）；找不到则静默忽略。

- [ ] **Step 1: 写失败测试**

`web/src/lib/merge.spec.ts`：

```ts
it('applyEvent idea.created 追加到列表末尾', () => { /* */ });
it('applyEvent idea.updated 替换并重排到末尾', () => { /* 改 updatedAt 后应到末尾 */ });
it('applyEvent idea.merged 移除源 id 并加入新条目', () => { /* mergedFrom 两个 id */ });
it('applyEvent 不符合过滤条件的 idea 不插入', () => { /* status done + 过滤 open */ });
it('applyEvent 对已存在的同一 id 不产生重复', () => { /* 连续 apply 同一个 created 事件两次 */ });
it('applyResync 用新列表整体替换', () => { /* */ });
```

深链定位抽成纯函数以便测试，`web/src/lib/merge.ts` 追加：

```ts
export function findAnchorIndex(ideas: Idea[], id: string | undefined): number;
// 找到返回下标；找不到或 id 为 undefined 返回 -1
```

在 `merge.spec.ts` 里加：

```ts
it('findAnchorIndex 找到目标返回下标', () => { /* */ });
it('findAnchorIndex 找不到返回 -1', () => { /* */ });
it('findAnchorIndex id 为 undefined 返回 -1', () => { /* */ });
```

`web/src/composables/useScrollFollow.spec.ts`（用 jsdom 环境，测试文件顶部加 `// @vitest-environment jsdom`）：

```ts
it('贴底时新内容自动滚动到底部', () => { /* 构造 scrollHeight=1000 clientHeight=400 scrollTop=600（距底 0）→ onNewContent 后 scrollTop 变成 600 */ });
it('未贴底时不抢滚动，pendingCount 增加', () => { /* scrollTop=0 → onNewContent 后 scrollTop 不变、pendingCount==1 */ });
it('距底 32px 以内视为贴底', () => { /* scrollTop=568（距底 32）→ 视为贴底 */ });
```

- [ ] **Step 2: 运行测试确认失败** → Expected: FAIL
- [ ] **Step 3: 实现** —— HomePage 在 `onMounted` 里 `connectEvents({ onEvent, onResync })`，`onEvent` 里调用 `applyEvent` 并把结果写回 `ideas`；`onResync` 里重新 `GET /api/ideas` 并调用 `applyResync`。
- [ ] **Step 4: 运行测试确认通过** → Expected: PASS
- [ ] **Step 5: 提交** → `git commit -m "feat(web): 接入 SSE 事件合并、滚动跟随与深链定位"`

---

### Task 12 (F12): 构建集成与整体冒烟

**Files:**
- Modify: `web/vite.config.ts`（确认 outDir）
- Create: `web/README.md`
- Modify: `README.md`（根）
- Modify: `server/IdeaApp/Program.cs`（**组合根增量接线**：确保 `wwwroot/` 存在 + `UseStaticFiles` + `MapFallbackToFile`）—— ⚠️ **只增量加，不要整体替换组合根**，它含前 14 个后端 task 累积的注册

**Interfaces:**
- Produces: `npm run build` 产出 `server/IdeaApp/wwwroot/index.html` 与 `assets/`。

- [ ] **Step 1: 配置构建产物**

`web/vite.config.ts`：

```ts
export default defineConfig({
  plugins: [vue()],
  base: '/',
  build: { outDir: '../server/IdeaApp/wwwroot', emptyOutDir: true },
  test: { environment: 'node' },
});
```

`web/package.json` 的 scripts：

```json
{
  "dev": "vite",
  "build": "vite build",
  "preview": "vite preview",
  "typecheck": "vue-tsc --noEmit",
  "test": "vitest run"
}
```

- [ ] **Step 1b: 接线静态文件与 SPA 兜底（后端组合根）**

前端产物落在 `server/IdeaApp/wwwroot/`，**后端要能把它发出去**。在 `server/IdeaApp/Program.cs` 里加两行（**增量加，不要整体替换组合根** —— 它含前 14 个 task 累积的注册）：

```csharp
// wwwroot 是前端构建产物的落点，而它被 .gitignore 排除（git 不跟踪空目录）
// —— 新克隆的仓库里它不存在。
//
// ⚠️ **光 Directory.CreateDirectory 还不够**（R162，2026-11-13，实施者实测）：
// `WebRootFileProvider` 是在 `WebApplication.CreateBuilder()` 里就定下来的，
// 那一刻目录不存在，框架把它留成 `null`（只打一条 warning，**不抛异常**）——
// 而那个 provider 是进程内固定的：后面再把目录与产物补上，静态文件照样 404
//（`/` 一律 404，页面白屏）。所以要**显式指一个 `PhysicalFileProvider`**，
// 「先起后端、后构建」这条路才真的通。
var webRoot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(webRoot);
builder.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);

app.UseStaticFiles();                 // 必须在 MapFallbackToFile 之前
app.MapFallbackToFile("index.html");  // history 模式的深链兜底
```

`UseStaticFiles` 没有它，`wwwroot/assets/` 下的 JS/CSS 全部 404（前端页面会白屏）；`MapFallbackToFile` 没有它，`/projects`、`/trash` 这类前端路由直接 404。

**`MapFallbackToFile` 必须是最后一个映射** —— 它是 catch-all，放在它后面的 `MapXxxEndpoints()` 永远不会命中。

**未匹配的 `/api/**` 不能被它吞掉（R163，2026-11-13，评审裁定）**：
`MapFallbackToFile` 兜的是**所有**没匹配到端点的 GET —— 包括 `/api/xxx`。
在本 task 之前（没有静态文件中间件、也没有这个 catch-all），未匹配路径回的是 404；
接上之后变成 **200 + index.html**，而这个 API 是**给 agent 读的**（契约在
`openapi/idea-v1.json`，agent 只看状态码会把「路径打错」当成成功）。

修法：**在 `MapFallbackToFile` 之前**先注册一个只管 `/api` 的兜底，让未匹配的
API 路径回 **404 + `application/problem+json`**（带 `code`），而不是 HTML：

```csharp
// 未匹配的 /api/** 要按 API 契约回 404 ProblemDetails，不能落到下面的 SPA 兜底：
// 那个 catch-all 会把「路径打错」变成 200 + HTML，而这个 API 是给 agent 读的。
app.MapFallback("/api/{**rest}", () => ApiResults.Run(() =>
    throw new ApiException(404, "api_not_found", "请求的接口不存在。")));

app.MapFallbackToFile("index.html");
```

配套：
- 新错误码 `api_not_found` 要登记进 `server/IdeaApp/Contracts/ApiErrorCodes.cs`
  （**必须**用 `ApiException` 抛 —— 守卫测试 `ApiErrorCodeTests` 扫的是 `ApiException` 调用点，
  用别的办法产出会被当成「登记了却没人抛」）；
- 登记之后要**重新导出 `openapi/idea-v1.json`**（契约里的错误码全集从 `ApiErrorCodes` 生成），
  并同步 skill 计划的错误码对照表；
- 补一条**hermetic** 的后端测试：「未匹配的 `/api/**` 回 404 + ProblemDetails，不是 HTML」。

- [ ] **Step 2: 构建并检查产物**

Run: `cd web && npm run build` → Expected: `server/IdeaApp/wwwroot/index.html` 与 `assets/` 存在
Run: `ls server/IdeaApp/wwwroot` → Expected: 至少 `index.html` 与 `assets`

- [ ] **Step 3: 端到端冒烟**

Run: `dotnet run --project server/IdeaApp`（另开终端）
Run: `curl -s http://127.0.0.1:5080/api/health` → `{"status":"ok"}`
Run: `curl -s http://127.0.0.1:5080/ | head -c 100` → Expected: 返回前端 HTML（不是 404）
Run: `curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:5080/projects` → Expected: `200`（history 路由兜底）

- [ ] **Step 4: 全量前端测试与类型检查**

Run: `cd web && npm test && npm run typecheck` → Expected: 全部通过、无类型错误

- [ ] **Step 5: 提交**

```bash
git add web/ README.md server/IdeaApp/Program.cs
git commit -m "chore(web): 集成 Vite 构建产物到后端 wwwroot 并补齐说明"
```

> ⚠️ **`server/IdeaApp/wwwroot/` 被 `.gitignore` 排除**（git 不跟踪空目录）——
> 构建产物**不进仓库**，新克隆的仓库里要先 `npm run build` 才有产物。
> `Program.cs` 里要**先 `Directory.CreateDirectory` 确保它存在**、并**显式指一个
> `PhysicalFileProvider`**，再挂 `UseStaticFiles` —— 只建目录不够，原因见 Step 1b。
