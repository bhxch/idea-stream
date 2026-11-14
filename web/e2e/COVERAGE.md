# E2E 覆盖矩阵

Idea Manager 前端的功能点 × 现有 E2E 用例的对照表。

- **覆盖范围**：`web/src/pages`、`web/src/components`、`web/src/composables`、`web/src/stores`、`web/src/router.ts` 里
  用户能直接操作到的功能。
- **用例来源**：`web/e2e/*.spec.ts`。现有规模：`npx playwright test --list` → **125 条用例 / 8 个文件**
  （ideas 20、layout 25、navigation 30、projects 19、webhooks 17、trash 10、fixtures 3、smoke 1）。
- **读法**：「覆盖它的用例」列写用例标题 + 文件；`—` 表示**没有用例覆盖**（漏掉的功能点）。
  漏掉的理由写在「备注」列里，代码位置以 `文件:行` 标出。

> ⚠️ **这是覆盖审计当时的快照**：表里的「漏掉」= 审计那一刻没有任何用例碰过的功能点。
> 审计同时补写的 `extra-1` ~ `extra-6` 六组用例没有回填到下面的表里 ——
> P0 那七条里的前六条，现在已经有用例了：
>
> | 审计里的 P0 漏项 | 现在由谁覆盖 |
> |---|---|
> | 1. 标签过滤的 AND / OR 模式切换 | `extra-1.spec.ts` |
> | 2. AND/OR 偏好写进 localStorage、刷新后仍是 | `extra-2.spec.ts` |
> | 3. Webhook 的自定义 Header 行 | `extra-3.spec.ts` |
> | 4. Webhook 表单里的「启用」复选框 | `extra-4.spec.ts` |
> | 5. Secret 留空 = 不改（三态里的 null） | `extra-5.spec.ts` |
> | 6. Secret「清除已设的密钥」（三态里的空串） | `extra-6.spec.ts` |
>
> 第 7 条（SSE 的项目事件：改名 / 新建 / 删除项目同步到侧栏）与 P1 / P2 / P3 里的条目
> **目前仍没有用例覆盖**。

---

## 一、概览

| 区域 | 功能点数 | 已覆盖 | 漏掉 | 覆盖率 |
|---|---:|---:|---:|---:|
| `/` 想法（HomePage + 想法条目 + 过滤 + 批量） | 53 | 23 | 30 | 43% |
| `/projects` 项目管理 | 16 | 11 | 5 | 69% |
| `/trash` 回收站 | 13 | 11 | 2 | 85% |
| `/webhooks` 全局 webhook | 23 | 14 | 9 | 61% |
| `/projects/:id/webhooks` 项目级 webhook | 5 | 5 | 0 | 100% |
| 外壳 / 路由 / Toast / 排版响应式 | 16 | 16 | 0 | 100% |
| **合计** | **126** | **80** | **46** | **63%** |

> 「漏掉」= 表里「覆盖它的用例」列为 `—` 的行，即**没有任何用例碰过**。
> 另有 **2 条只测了一半**（批量加标签的并集语义、标签补全下拉的内容顺序），
> 它们在表里算「已覆盖」，但断言不到关键语义 —— 见文末 P1-20、P2-26。

---

## 二、覆盖矩阵

### 2.1 `/` 想法（HomePage.vue / Composer.vue / MessageItem.vue / MessageList.vue / FilterBar.vue / TagFilter.vue / TagEditor.vue / BatchBar.vue / MergeDialog.vue / ProjectSidebar.vue / ProjectTabs.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/` | 新建想法：点「记下」提交 | ideas.spec.ts:111 | — |
| `/` | 新建想法：Enter 键提交 | — | `Composer.vue:48-53` 的 `onKeydown` 分支没被走到：用例只点按钮 |
| `/` | 新建想法：Shift+Enter 换行（不提交） | — | 同上，`Composer.vue:49` |
| `/` | 「记下」按钮在内容为空时禁用 | — | `Composer.vue:66` 的 `:disabled="text.trim().length === 0 \|\| submitting"` |
| `/` | 提交失败时输入框内容保留 | — | `Composer.vue:37`（`ok` 为 false 不清空）；失败路径要用接口打桩才能造 |
| `/` | 切换完成态 / 取消完成 | ideas.spec.ts:168、ideas.spec.ts:193 | — |
| `/` | 编辑正文：点正文进入编辑态 | ideas.spec.ts:139 | — |
| `/` | 编辑正文：Ctrl+Enter 保存 | ideas.spec.ts:139 | — |
| `/` | 编辑正文：Esc 取消编辑 | — | `MessageItem.vue:290-293`（`onEditorKeydown` 的 Escape 分支） |
| `/` | 编辑正文：失焦自动保存 | — | `MessageItem.vue:576` 的 `@blur="saveEdit"` |
| `/` | 编辑正文：内容没改时不发请求、直接收起 | — | `MessageItem.vue:232-237` |
| `/` | 加标签 / 改标签 / 删标签 | ideas.spec.ts:217、ideas.spec.ts:239、ideas.spec.ts:266 | — |
| `/` | 标签补全下拉出现、且当前项目用过的标签排前面 | projects.spec.ts:340 | 只断言了候选**内容与顺序**，没点过候选项 |
| `/` | 补全：点候选项添加标签 | — | `TagEditor.vue:141-148` 的 `@click="addTag(option)"` |
| `/` | 标签输入：空格 / 逗号分隔成多个标签 | — | `TagEditor.vue:98-102` |
| `/` | 标签输入：大小写不敏感去重 | — | `TagEditor.vue:36-38, 60-63`；后端同口径在 `lib/filter.ts:51-52` |
| `/` | 标签输入：上下键移动高亮候选 | — | `TagEditor.vue:75-81, 108-111` |
| `/` | 标签输入：Esc 清掉半截文字 | — | `TagEditor.vue:103-107` |
| `/` | 收起标签编辑器时「还有未保存的改动」的二次确认 | — | `MessageItem.vue:325-338` 的 `window.confirm`；`ideas.spec.ts:89` 的 `openTagEditor` 辅助函数会**自动接受**对话框，从没断言过这个确认框 |
| `/` | 删除想法：二次确认后删除 | ideas.spec.ts:290 | — |
| `/` | 删除想法：点「取消」不删 | — | `MessageItem.vue:617`；项目/webhook 的「取消」都测了，唯独这条没测 |
| `/` | 多选勾选 / 取消勾选、批量条条数 | ideas.spec.ts:317 | — |
| `/` | 批量标记完成 / 批量撤销完成 | ideas.spec.ts:342 | — |
| `/` | 批量加标签 | ideas.spec.ts:372 | 只断言「都加上新标签」，没断言**原有标签是否保留**（并集语义，`HomePage.vue:641-645` 的 `replace: false`） |
| `/` | 批量删除（二次确认） | ideas.spec.ts:402 | — |
| `/` | 批量删除：点「取消」不删 | — | `BatchBar.vue:151-152` |
| `/` | 批量条「取消选择」清空勾选 | — | `BatchBar.vue:131-133`；**唯一的清除勾选入口**，没被测过 |
| `/` | 合并：对话框预览（条目与初始拼接正文） | ideas.spec.ts:433 | — |
| `/` | 合并：改过正文再合并 | ideas.spec.ts:481 | — |
| `/` | 合并：点「取消」不合并 | — | `MergeDialog.vue:68` |
| `/` | 合并按钮在只勾一条时禁用 | — | `BatchBar.vue:109`（`:disabled="busy \|\| count < 2"`） |
| `/` | 合并结果滚到底部并高亮定位 | — | `HomePage.vue:699` 设 `focusIdeaId`，`useDeepLink.ts:111-139` 定位；现有合并用例只断言条目出现 |
| `/` | 按项目筛选（侧栏切项目） | ideas.spec.ts:509、projects.spec.ts:240、projects.spec.ts:271 | — |
| `/` | 按状态筛选：全部 / 未完成 / 已完成 | ideas.spec.ts:549 | — |
| `/` | 按标签筛选：勾选标签 | ideas.spec.ts:527 | — |
| `/` | 项目视图里给想法加标签（切到该项目后操作） | projects.spec.ts:297 | — |
| `/` | 项目视图里删掉想法的标签 | projects.spec.ts:319 | — |
| `/` | 切到没有想法的项目：想法列表空状态 | projects.spec.ts:370 | — |
| `/` | 清除筛选：取消标签与状态筛选后列表恢复全部 | ideas.spec.ts:568 | — |
| `/` | **标签 AND/OR 模式切换（任一 / 全部）** | — | `TagFilter.vue:37-39, 90-101` 的 `toggleMode` 按钮；AND 模式必须**同时**命中全部选中标签才显示（`lib/filter.ts:54-56`），语义完全没测 |
| `/` | AND/OR 偏好写进 localStorage、刷新后仍是 | — | `composables/useTagMode.ts:23-31` |
| `/` | 标签过滤面板：点组件外部收起 / Esc 收起 | — | `TagFilter.vue:50-66` |
| `/` | 深链 `?idea=<id>` 直达并高亮 | ideas.spec.ts:594 | — |
| `/` | 页面载入后自动滚到底部 | — | `HomePage.vue:177-179` 的 `scrollToBottom()` |
| `/` | 上翻时不抢滚动，出现「↓ 有 N 条新内容」按钮 | — | `HomePage.vue:979-986` + `composables/useScrollFollow.ts:100-113` |
| `/` | SSE：别的上下文新建的想法自动出现 | ideas.spec.ts:643 | — |
| `/` | SSE：**项目**改名 / 新建 / 删除同步到侧栏 | — | `HomePage.vue:736-750`（`project.*` 分流）、`HomePage.vue:840-877`（就地改名 / 删项目时切回「全部项目」） |
| `/` | 项目侧栏「＋」入口到项目管理页 | navigation.spec.ts:159 | — |
| `/` | 窄屏 Tab 条的「＋」「⋯」按钮 | — | `ProjectTabs.vue:128-135`（`emit('new-project')` / `emit('open-menu')`） |
| `/` | 侧栏 / Tab 上的项目条数与「全部项目」总数 | — | `ProjectSidebar.vue:35,122,145`、`ProjectTabs.vue:31,110,123` |
| `/` | 「全部项目」命中数超上限的兜底提示 | — | `HomePage.vue:318-336`（`recoverFromTooMany`） |
| `/` | 切换项目失败时回滚到上一个项目 | — | `HomePage.vue:198-214`（`switchProject` 的 catch 分支） |
| `/` | 想法带 title 时：标题单独显示、正文完整显示 | — | `MessageItem.vue:171-197`；现有用例只覆盖「无 title、首行当视觉标题」 |

### 2.2 `/projects` 项目管理（ProjectsPage.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/projects` | 新建项目 | projects.spec.ts:29 | — |
| `/projects` | 新建的项目在宽屏侧栏与窄屏顶部 Tab 里都能看到 | projects.spec.ts:52 | — |
| `/projects` | 新建项目：名称为空给提示 | projects.spec.ts:42 | — |
| `/projects` | 改名（保存） | projects.spec.ts:70 | — |
| `/projects` | 改名：点「取消」 | projects.spec.ts:83 | — |
| `/projects` | 改名：输入框回车提交 | — | `ProjectsPage.vue:279` 的 `@keydown.enter` |
| `/projects` | 改名：名称为空给提示 | — | `ProjectsPage.vue:92-96` |
| `/projects` | 删除（二次确认） | projects.spec.ts:96 | — |
| `/projects` | 删除：点「取消」 | projects.spec.ts:110 | — |
| `/projects` | 内置收件箱不可删除 | projects.spec.ts:124 | — |
| `/projects` | 排序方式：切手动 / 切最近活动 | projects.spec.ts:140、projects.spec.ts:153 | — |
| `/projects` | 拖拽重排（往下拖 → 落到目标之后） | projects.spec.ts:168、projects.spec.ts:203 | — |
| `/projects` | 拖拽重排（**往上拖** → 落到目标之前） | — | `lib/reorder.ts:49` 的 `insertAt` 两个方向只测了一个（四个落点组合只覆盖了「源在目标之前」） |
| `/projects` | 切排序方式后顺序不跳位 | — | `ProjectsPage.vue:183-193` 的注释描述的语义 |
| `/projects` | 项目条数与最近活动时间显示 | — | `ProjectsPage.vue:282-285` |
| `/projects` | 空状态「还没有项目。」 | projects.spec.ts:363 | — |

### 2.3 `/trash` 回收站（TrashPage.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/trash` | 删除的想法出现在回收站 | trash.spec.ts:104 | — |
| `/trash` | 顶层条目显示「原属项目：」 | trash.spec.ts:149 | — |
| `/trash` | 删项目不牵连还活着的项目 | trash.spec.ts:114 | — |
| `/trash` | 容器条目：项目名 + 「N 条想法」 | trash.spec.ts:128 | — |
| `/trash` | 容器展开，看得见下挂的想法 | trash.spec.ts:128、trash.spec.ts:211 | — |
| `/trash` | 容器**收起** | — | `TrashPage.vue:137-139` 同一个按钮在「展开 / 收起」之间切；只测过「展开」 |
| `/trash` | 恢复想法（顶层条目） | trash.spec.ts:165 | — |
| `/trash` | 恢复项目（连带恢复下挂想法） | trash.spec.ts:188 | — |
| `/trash` | 容器里的想法单独恢复 | trash.spec.ts:211 | — |
| `/trash` | 恢复请求在飞时按钮禁用 | — | `TrashPage.vue:154, 172, 200` 的 `:disabled="busyId !== null"` |
| `/trash` | 没有「彻底删除」，到期自动清理的说明 | trash.spec.ts:236 | — |
| `/trash` | 回收站里的内容刷新后仍在（不会自己消失） | trash.spec.ts:252 | — |
| `/trash` | 回收站为空时的空状态 | trash.spec.ts:266 | — |

### 2.4 `/webhooks` 全局 webhook（WebhookManager.vue / WebhookForm.vue / DeliveryList.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/webhooks` | 空状态「还没有 webhook。」+「新建 webhook」入口 | webhooks.spec.ts:200 | — |
| `/webhooks` | 新建 webhook（名称/URL/Body 模板/订阅事件） | webhooks.spec.ts:209 | — |
| `/webhooks` | Telegram 模板按钮预填 URL 与 Body 模板 | webhooks.spec.ts:234 | — |
| `/webhooks` | 启用 / 禁用（列表按钮） | webhooks.spec.ts:255 | — |
| `/webhooks` | 表单里的「启用」复选框（编辑时改启用状态） | — | `WebhookForm.vue:326-338`；列表上的按钮测了，表单里的复选框没测 |
| `/webhooks` | 编辑：改名称与 URL | webhooks.spec.ts:330 | — |
| `/webhooks` | 编辑：点「取消」不保存 | — | `WebhookForm.vue:358` |
| `/webhooks` | 删除（二次确认 + 取消） | webhooks.spec.ts:350 | — |
| `/webhooks` | 表单校验：名称为空 | webhooks.spec.ts:269 | — |
| `/webhooks` | 表单校验：URL 为空 | webhooks.spec.ts:283 | — |
| `/webhooks` | 表单校验：URL 不是 http(s) 绝对地址 | webhooks.spec.ts:295 | — |
| `/webhooks` | 表单校验：Body 模板为空 | — | `WebhookForm.vue:178-182` |
| `/webhooks` | 表单校验：一个事件都不订阅 | webhooks.spec.ts:313 | — |
| `/webhooks` | **自定义 Header 行**（加一行 / 删一行 / 键值） | — | `WebhookForm.vue:271-302`（`addHeader`/`removeHeader`/`buildHeaders`）；表单里一整块 UI，一行没测 |
| `/webhooks` | **请求方法下拉**（POST/GET/PUT/PATCH/DELETE） | — | `WebhookForm.vue:257-268` |
| `/webhooks` | **订阅事件的完整清单**（8 种事件、多选） | — | `lib/webhook.ts:9-30` 列出 8 种；用例只勾过「想法新建」一种 |
| `/webhooks` | Secret：新建时填密钥 → 显示「已设密钥」、明文不外泄 | webhooks.spec.ts:372 | — |
| `/webhooks` | Secret：编辑时**留空表示不改** | — | `WebhookForm.vue:126-134` 的三态（`null` / `""` / 非空） |
| `/webhooks` | Secret：编辑时「清除已设的密钥」 | — | `WebhookForm.vue:351-354`；勾「清除」与填新值互斥（`WebhookForm.vue:86-96`）也没测 |
| `/webhooks` | 列表徽标：已启用/已禁用、无密钥 | webhooks.spec.ts:209、webhooks.spec.ts:255 | 「不订阅任何事件」徽标（`WebhookManager.vue:158`）没测 |
| `/webhooks` | 投递记录：空状态 | webhooks.spec.ts:393 | — |
| `/webhooks` | 投递记录：事件触发后列出记录 + 「重投」再投一次 | webhooks.spec.ts:403 | — |
| `/webhooks` | 投递记录：失败态（`lastError`）与重投失败提示 | — | `DeliveryList.vue:75` |

### 2.5 `/projects/:id/webhooks` 项目级 webhook（ProjectWebhooksPage.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/projects/:id/webhooks` | 打开页面，标题带项目名 | webhooks.spec.ts:438 | — |
| `/projects/:id/webhooks` | 指向不存在的项目时页面不崩 | navigation.spec.ts:213 | — |
| `/projects/:id/webhooks` | 新建项目级 webhook（默认已启用） | webhooks.spec.ts:448 | — |
| `/projects/:id/webhooks` | 作用域隔离（全局的只在全局页，项目的只在项目页） | webhooks.spec.ts:476 | — |
| `/projects/:id/webhooks` | 删除项目级 webhook | webhooks.spec.ts:501 | — |

### 2.6 外壳 / 路由 / 全局提示 / 排版

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| 全局 | 底座冒烟：/api/health + 首页挂载 | smoke.spec.ts:13 | — |
| 全局 | 夹具复位契约（自检） | fixtures.spec.ts:10、fixtures.spec.ts:31、fixtures.spec.ts:51 | — |
| 全局 | 管理页导航条渲染与三个入口 | navigation.spec.ts:88（三条） | — |
| 全局 | 导航条三个链接的 href | navigation.spec.ts:96 | — |
| 全局 | 当前页高亮（含项目级 webhook 页点亮点是「Webhook」） | navigation.spec.ts:119（三条）、navigation.spec.ts:130 | — |
| 全局 | 点导航跳转后路由与内容对应 | navigation.spec.ts:149（三条） | — |
| 全局 | 五个路由深链直接访问不 404 | navigation.spec.ts:177（四条）、navigation.spec.ts:191 | — |
| 全局 | 深链刷新不 404：刷新后还是同一个页面 | navigation.spec.ts:202 | — |
| 全局 | 浏览器前进 / 后退 | navigation.spec.ts:222、navigation.spec.ts:239 | — |
| 全局 | 主页面没有管理页导航条（外壳不同） | navigation.spec.ts:104 | — |
| 全局 | 未知路由兜底回首页（不白屏） | navigation.spec.ts:254、navigation.spec.ts:262 | — |
| 全局 | ToastHost：常驻、无消息时为空、错误提示、4 秒后自动消失 | navigation.spec.ts:285、navigation.spec.ts:294、navigation.spec.ts:307 | — |
| 全局 | 加载态「加载中…」 | navigation.spec.ts:321 | — |
| 全局 | 空状态文案（想法 / webhook） | navigation.spec.ts:340、navigation.spec.ts:353 | — |
| 全局 | 排版与响应式：5 路由 × 4 视口 | layout.spec.ts:505（20 条） | — |
| 全局 | 移动端点按目标 ≥ 32×32（5 路由） | layout.spec.ts:535（5 条） | — |

---

## 三、漏掉的功能点（按重要程度）

下面把 **46 条漏掉的功能点**（与上表的 `—` 一一对应）连同 **1 条只测了一半的**（第 20 条）全部列出，
共 **47 条**，按重要程度分四档。

### P0 — 核心语义 / 整块 UI / 跨设备同步（7 条）

1. **标签过滤的 AND / OR 模式切换**
   - 位置：`web/src/components/TagFilter.vue:37-39`（`toggleMode`）、`:90-101`（按钮）；
     匹配语义在 `web/src/lib/filter.ts:54-56`。
   - 为什么算漏掉：`ideas.spec.ts:527` 只覆盖了默认的 OR（勾两个标签 → 显示 2 条）。
     AND 模式下必须**同时**命中全部选中标签，这个分支一条用例都没走过；按钮本身也从未被点过。

2. **AND / OR 偏好写进 localStorage、刷新后仍是**
   - 位置：`web/src/composables/useTagMode.ts:13-31`（读/写）、`HomePage.vue:365-371`（切模式时落盘）。
   - 为什么算漏掉：这是**每台设备各自的偏好**，写失败（隐私模式）还有专门的兜底分支
     （`useTagMode.ts:24-30`），一条用例都没有。

3. **Webhook 的自定义 Header 行**（加一行 / 删一行 / 键值）
   - 位置：`web/src/components/WebhookForm.vue:271-302`（字段集 UI）、`:136-145`（增删行）、
     `:98-109`（`buildHeaders` 组装，空键的行忽略）。
   - 为什么算漏掉：表单里一整块可见 UI，保存时会被发到后端，但 `webhooks.spec.ts` 的建 webhook
     用例只填了名称/URL/Body 模板/事件，Header 从没被碰过。

4. **Webhook 表单里的「启用」复选框**（编辑时改启用状态）
   - 位置：`web/src/components/WebhookForm.vue:326-338`。
   - 为什么算漏掉：列表上的「启用/禁用」按钮测了（webhooks.spec.ts:255），**表单里的开关**没测 ——
     两条路径写的字段不同（表单写 `enabled`，按钮走 `PATCH /webhooks/{id}`）。

5. **Secret 留空 = 不改**（三态里的 `null`）
   - 位置：`web/src/components/WebhookForm.vue:126-134`（`secretValue()`）、`:340-349`（输入框 placeholder）。
   - 为什么算漏掉：Secret 只测了「新建时填 → 显示已设密钥」（webhooks.spec.ts:372）。
     编辑时把输入框留空**不该**清掉已有密钥 —— 这条语义没被钉住。

6. **Secret「清除已设的密钥」勾选项**（含与新填值的互斥）
   - 位置：`web/src/components/WebhookForm.vue:351-354`（勾选框）、`:86-96`（「勾清除」与「填新值」互斥）。
   - 为什么算漏掉：清除密钥是三态里的 `""`，与互斥逻辑一起没测。

7. **SSE 的项目事件**（改名 / 新建 / 删除项目同步到侧栏）
   - 位置：`web/src/pages/HomePage.vue:736-750`（`project.*` 分流）、`:831-877`（`applyProjectEvent`：
     就地改名、删项目时切回「全部项目」、本地没有则整表重拉）。
   - 为什么算漏掉：`ideas.spec.ts:643` 只覆盖了 `idea.created` 一类事件。改项目名、删项目这类
     **跨设备同步**是首页最容易被改坏的地方（`applyProjectEvent` 里还带着 `switchProject` 回滚分支）。

### P1 — 用户每天点得到、现在几乎没测（14 条）

8. **批量条「取消选择」按钮**
   - 位置：`web/src/components/BatchBar.vue:131-133`。
   - 为什么算漏掉：它是**唯一**清空勾选的操作（勾错一条只能一条条取消）。`ideas.spec.ts:317`
     只验证了「勾上 → 批量条出现 → 逐个取消 → 条数变」，从没点过这个按钮。

9. **删除想法的「取消」二次确认**
   - 位置：`web/src/components/MessageItem.vue:610-618`。
   - 为什么算漏掉：项目删除（projects.spec.ts:110）与 webhook 删除（webhooks.spec.ts:350）都测了
     「取消」这条破坏性操作的退路，唯独想法删除没测 —— 而想法删除是**唯一**会让内容进回收站的操作。

10. **批量删除的「取消」**
    - 位置：`web/src/components/BatchBar.vue:147-153`。
    - 为什么算漏掉：`ideas.spec.ts:402` 只走通了「确认删除」，没走「取消」这条退路。

11. **合并对话框的「取消」**
    - 位置：`web/src/components/MergeDialog.vue:68`（`@click="emit('cancel')"`）、`HomePage.vue:1016`。
    - 为什么算漏掉：`ideas.spec.ts:433` 只走通了「合并成一条」；取消后对话框应关闭、勾选应保留。

12. **「合并」按钮在只勾一条时禁用**
    - 位置：`web/src/components/BatchBar.vue:106-114`（`:disabled="busy || count < 2"`）。
    - 为什么算漏掉：合并至少要两条（`HomePage.vue:689` 有同样的守卫），禁用态没被钉住。

13. **Composer 的 Enter 提交**
    - 位置：`web/src/components/Composer.vue:48-53`。
    - 为什么算漏掉：输入框 placeholder 明确告诉用户「Enter 提交」（`Composer.vue:63`），
      但 `ideas.spec.ts:111` 只点按钮 —— 用户最常用的提交方式没被测。

14. **Composer 的 Shift+Enter 换行（不提交）**
    - 位置：`web/src/components/Composer.vue:49`。
    - 为什么算漏掉：同上；「换行不提交」是防误提交的约定。

15. **编辑正文：Esc 取消编辑**
    - 位置：`web/src/components/MessageItem.vue:284-294`（`onEditorKeydown` 的 Escape 分支）、`:206-212`。
    - 为什么算漏掉：Ctrl+Enter 测了（ideas.spec.ts:139），Esc 是「放弃这次编辑」的唯一显式入口。

16. **编辑正文：失焦自动保存**
    - 位置：`web/src/components/MessageItem.vue:576`（`@blur="saveEdit"`）。
    - 为什么算漏掉：点条目别处就存盘，用户最容易「以为没保存」的场景。

17. **项目重命名：输入框回车提交**
    - 位置：`web/src/pages/ProjectsPage.vue:279`（`@keydown.enter="onSaveRename(project)"`）。
    - 为什么算漏掉：`projects.spec.ts:70` 只点「保存」按钮。

18. **项目改名为空时给出提示**
    - 位置：`web/src/pages/ProjectsPage.vue:92-96`。
    - 为什么算漏掉：与新建项目的空名提示（projects.spec.ts:42）同源，但改名这条分支没测。

19. **拖拽重排的「往上拖」方向**（源在目标之后 → 落到目标之前）
    - 位置：`web/src/lib/reorder.ts:28-52`（`reorderToTarget`：`from < to ? target + 1 : target`）。
    - 为什么算漏掉：`projects.spec.ts:168` 与 `:203` 只测了「往下拖 → 落到目标之后」；
      文件头注释列的四种组合里，「往上拖」一种没有用例。

20. **批量加标签的「并集式追加」语义**（*只测了一半*）
    - 位置：`web/src/pages/HomePage.vue:632-654`（`replace: false`）、`BatchBar.vue:60-72`（回执后清草稿）。
    - 为什么算漏掉：`ideas.spec.ts:372` 只断言「两条都加上了新标签」，没断言**它们原有的标签还在**
      （覆盖式 vs 并集式是两种完全不同的语义，后端 `replace` 参数就在这一层）。

21. **「记下」按钮在内容为空时禁用**
    - 位置：`web/src/components/Composer.vue:66`（`:disabled="text.trim().length === 0 || submitting"`）。
    - 为什么算漏掉：空内容提交被 `submit()` 的 `!content` 挡住（`Composer.vue:29`），
      但按钮**看起来可不可点**没有用例钉住 —— 用户点不动与点了没反应是两种体验。

### P2 — 交互细节，坏了不易发现（13 条）

22. **标签输入：空格 / 逗号分隔成多个标签**
    - 位置：`web/src/components/TagEditor.vue:98-102`。
    - 为什么算漏掉：`addTag` 的「一次加一个」的路径测了（Enter），一次敲多个的路径没测。

23. **标签输入：大小写不敏感去重**
    - 位置：`web/src/components/TagEditor.vue:36-38`（`isSameTag`）、`:60-63`（重复则忽略）。
    - 为什么算漏掉：后端也是这个口径（`lib/filter.ts:51-52`），前后端口径不一致会让用户
      「刚存好就被问还有没有未保存改动」（`MessageItem.vue:147-151`）。

24. **标签输入：上下键移动高亮候选**
    - 位置：`web/src/components/TagEditor.vue:75-81`（`moveHighlight`）、`:108-111`（键盘映射）。
    - 为什么算漏掉：键盘选候选是补全面板的主交互。

25. **标签输入：Esc 清掉半截文字**
    - 位置：`web/src/components/TagEditor.vue:103-107`。
    - 为什么算漏掉：与 TagFilter 的 Esc（收起面板）不同，两处都叫 Esc 但行为不一样，没测就分不清。

26. **补全下拉里点候选项添加标签**（*只测了一半*）
    - 位置：`web/src/components/TagEditor.vue:141-148`（`@click="addTag(option)"`）。
    - 为什么算漏掉：`projects.spec.ts:340` 只断言了候选**内容与顺序**，从没有点过候选项。

27. **收起标签编辑器时的「还有未保存的改动」确认框**
    - 位置：`web/src/components/MessageItem.vue:325-338`（`window.confirm`）。
    - 为什么算漏掉：`ideas.spec.ts:89` 的 `openTagEditor` 辅助函数会**自动接受**对话框，从不断言这个确认流程 ——
     「不静默丢改动」是这段代码存在的理由（`MessageItem.vue:317-324` 的注释）。

28. **标签过滤面板：点组件外部收起 / Esc 收起**
    - 位置：`web/src/components/TagFilter.vue:50-66`。
    - 为什么算漏掉：不下拉会一直挂在那儿，是「点了没反应」的经典来源。

29. **Webhook 订阅事件的完整清单**（8 种）与多选
    - 位置：`web/src/lib/webhook.ts:9-30`（8 种事件与中文标签）、`WebhookForm.vue:317-324`。
    - 为什么算漏掉：用例只勾过「想法新建」一种（webhooks.spec.ts:209 的新建用例、:403 的投递用例）。

30. **Webhook 请求方法下拉**（POST / GET / PUT / PATCH / DELETE）
    - 位置：`web/src/components/WebhookForm.vue:257-268`。
    - 为什么算漏掉：方法会原样发到后端，没测过非 POST 的取值。

31. **Webhook Body 模板为空的校验**
    - 位置：`web/src/components/WebhookForm.vue:178-182`。
    - 为什么算漏掉：其余三条表单校验（名称空 / URL 空 / 未订阅事件）都测了，唯独这条没有。

32. **Webhook 编辑表单的「取消」**
    - 位置：`web/src/components/WebhookForm.vue:358`（`@click="emit('cancel')"`）。
    - 为什么算漏掉：新建表单的关闭路径（保存后自动关）测了，手动取消没测。

33. **回收站容器的「收起」**
    - 位置：`web/src/pages/TrashPage.vue:137-139`（同一个按钮在「展开 / 收起」之间切文案）。
    - 为什么算漏掉：`trash.spec.ts:128` 只点过「展开」。

34. **编辑正文：内容没改时不发请求、直接收起**
    - 位置：`web/src/components/MessageItem.vue:232-237`。
    - 为什么算漏掉：这条分支省掉一次请求，也是「在飞保护」之外的一道判断。

### P3 — 展示层与边界路径（13 条）

35. **窄屏 Tab 条的「＋」「⋯」按钮**
    - 位置：`web/src/components/ProjectTabs.vue:128-135`（`emit('new-project')` / `emit('open-menu')`）。
    - 为什么算漏掉：`navigation.spec.ts:159` 只测了宽屏侧栏的「＋」，窄屏下的两个入口没测。

36. **侧栏 / Tab 上的项目条数与「全部项目」总数**
    - 位置：`web/src/components/ProjectSidebar.vue:35`（总数）、`:122,145`（各项目条数）、
      `web/src/components/ProjectTabs.vue:31,110,123`。
    - 为什么算漏掉：条数是用户判断「这个项目里有什么」的主要依据。

37. **/projects 上的项目条数与最近活动时间**
    - 位置：`web/src/pages/ProjectsPage.vue:282-285`。
    - 为什么算漏掉：同上；`formatRelativeTime` 的输出也没被断言过。

38. **想法带 title 时：标题单独显示、正文完整显示**
    - 位置：`web/src/components/MessageItem.vue:171-197`（`visualTitle` / `bodyContent`）。
    - 为什么算漏掉：现有用例只覆盖「无 title、首行当视觉标题」（ideas.spec.ts:111-136）；
      `seed.idea` 支持传 title，这条路径没用上。

39. **页面载入后自动滚到底部**
    - 位置：`web/src/pages/HomePage.vue:172-179`（`scrollToBottom()`）。
    - 为什么算漏掉：开页就看到最新一条是首页的核心体验。

40. **上翻时不抢滚动，出现「↓ 有 N 条新内容」按钮**
    - 位置：`web/src/pages/HomePage.vue:979-986`、`web/src/composables/useScrollFollow.ts:100-138`。
    - 为什么算漏掉：`useScrollFollow` 的整套逻辑（贴底判定、积攒计数、按钮回到底部）一条用例都没有。

41. **合并结果滚到底部并高亮定位**
    - 位置：`web/src/pages/HomePage.vue:699`（设 `focusIdeaId`）、`web/src/composables/useDeepLink.ts:111-139`。
    - 为什么算漏掉：合并结果出现在列表最下面，用户多半没看见；
      `ideas.spec.ts:433` 只断言了条目出现，没断言它被滚到视野里并高亮。

42. **投递记录的失败态（`lastError`）与重投失败提示**
    - 位置：`web/src/components/DeliveryList.vue:75`、`:34-51`。
    - 为什么算漏掉：投递失败是 webhook 使用中最常见的现场，只测了成功路径。

43. **回收站恢复请求在飞时按钮禁用**
    - 位置：`web/src/pages/TrashPage.vue:154, 172, 200`（`:disabled="busyId !== null"`）。
    - 为什么算漏掉：防重复提交，用户连点「恢复」时不该发两次。

44. **「全部项目」命中数超上限的兜底提示**
    - 位置：`web/src/pages/HomePage.vue:318-336`（`recoverFromTooMany`）、`:339-347`（提示文案）。
    - 为什么算漏掉：这是首页唯一一条「应用替用户做决定」的分支（自动切到最近有活动的项目）。

45. **切换项目失败时回滚到上一个项目**
    - 位置：`web/src/pages/HomePage.vue:198-214`（`switchProject` 的 catch 分支）。
    - 为什么算漏掉：回滚错了就是「列表停在错的数据上」，而且是静默的。

46. **切换项目排序方式后顺序不跳位**
    - 位置：`web/src/pages/ProjectsPage.vue:183-193`（注释描述的语义）、`server/.../ProjectService.SetSortMode`。
    - 为什么算漏掉：`projects.spec.ts:140` 只断言「页面顺序与后端给的一致」，没断言「与切换**前**一致」。

47. **提交失败时输入框内容保留**
    - 位置：`web/src/components/Composer.vue:37`（`ok` 为 false 不清空）、`HomePage.vue:381-401`。
    - 为什么算漏掉：用户刚敲的正文不能因为一次网络失败就没了；失败路径要用接口打桩才能造。

---

## 四、复现方式

```bash
# 现有用例清单（不跑用例，只列出）
cd web && npx playwright test --list

# 跑全量
bash web/e2e/run-e2e.sh
```

> ⚠️ 5299 端口是共享的，同一时刻只跑一轮 —— 详见 `web/e2e/README.md` 的「并发」一节。
