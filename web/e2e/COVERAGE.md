# E2E 覆盖矩阵

Idea Manager 前端的功能点 × 现有 E2E 用例的对照表。

- **覆盖范围**：`web/src/pages`、`web/src/components`、`web/src/composables`、`web/src/stores`、`web/src/router.ts` 里
  用户能直接操作到的功能。
- **用例来源**：`web/e2e/*.spec.ts`。现有规模：`npx playwright test --list` → **225 条用例 / 15 个文件**
  （layout 48、navigation 35、ideas 29、webhooks 24、projects 22、trash 11、search 10、extra-1 8、
  extra-3 8、extra-4 8、extra-6 8、extra-2 5、extra-5 4、fixtures 4、smoke 1）。
- **读法**：「覆盖它的用例」列写用例标题 + 文件；`—` 表示**没有用例覆盖**（漏掉的功能点）。
  漏掉的理由写在「备注」列里，代码位置以 `文件:行` 标出。

> ⚠️ **表里的「漏掉」= 覆盖审计那一刻没有任何用例碰过的功能点**。
> 审计之后补写的用例分两批，矩阵里的 `—` 没有全部回填：
>
> | 批次 | 补写了什么 | 回填到下面的表里了吗 |
> |---|---|---|
> | 审计同批：`extra-1` ~ `extra-6` | P0 那七条里的前六条（见下表） | ❌ 没有 |
> | 本轮：前端一批行为改动之后补写 | 关键词搜索、「清除」按钮、加载态不闪空态、回首页入口、aria-current、项目级 webhook 入口、项目管理页空状态、webhook URL 前端校验、成功提示、投递记录刷新、移动端点按目标、输入框单层边框、渐隐带 | ✅ 已回填 |
> | 本轮：搜索改到服务端之后 | 标签名 / 项目名能搜到、命中带高亮、英文是词级、中文是词元相邻、多段之间是 AND、往回翻历史（打开页面停在最下边、接近顶部自动加载更旧的一页） | ✅ 已回填 |
> | 本轮：前端布局重设计（新外壳）之后 | 宽屏侧栏贴左且主区铺到右边缘、一屏 ≥10 条、两列交错、平板两列无侧栏、窄屏单列、窄屏钉死的外壳只有 2 行（过滤行随内容滚走）、一屏 ≥5 条、卡片没有标题层、编号紧挨时间、顶栏「⋯」一步进回收站、窄屏 🔍 聚焦搜索框、显形规则（桌面悬停 / 键盘聚焦 / 窄屏常驻 / 无悬停能力设备常驻）、顶栏新触发器够大、新记的一条落在最下面且左右按奇偶交替、编辑老想法后它跳到最下面且下方卡片左右互换、管理页铺满且 Webhook 表单保持窄 | ✅ 已回填 |
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
> 第 7 条（SSE 的项目事件：改名 / 新建 / 删除项目同步到侧栏）与 P1 / P2 / P3 里**其余**的条目
> **目前仍没有用例覆盖**。

---

## 一、概览

| 区域 | 功能点数 | 已覆盖 | 漏掉 | 覆盖率 |
|---|---:|---:|---:|---:|
| `/` 想法（HomePage + 想法条目 + 过滤 + 批量） | 87 | 59 | 28 | 68% |
| `/projects` 项目管理 | 17 | 12 | 5 | 71% |
| `/trash` 回收站 | 13 | 11 | 2 | 85% |
| `/webhooks` 全局 webhook | 33 | 24 | 9 | 73% |
| `/projects/:id/webhooks` 项目级 webhook | 5 | 5 | 0 | 100% |
| 外壳 / 路由 / Toast / 排版响应式 | 22 | 22 | 0 | 100% |
| **合计** | **177** | **133** | **44** | **75%** |

> 「漏掉」= 表里「覆盖它的用例」列为 `—` 的行，即**没有任何用例碰过**。
> 另有 **2 条只测了一半**（批量加标签的并集语义、标签补全下拉的内容顺序），
> 它们在表里算「已覆盖」，但断言不到关键语义 —— 见文末 P1-20、P2-26。

---

## 二、覆盖矩阵

### 2.1 `/` 想法（HomePage.vue / Composer.vue / MessageItem.vue / MessageList.vue / FilterBar.vue / TagFilter.vue / TagEditor.vue / BatchBar.vue / MergeDialog.vue / ProjectSidebar.vue / ProjectDropdown.vue / MoreMenu.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/` | 新建想法：点「记下」提交 | ideas.spec.ts:84 | — |
| `/` | 新建想法：Enter 键提交 | — | `Composer.vue:48-53` 的 `onKeydown` 分支没被走到：用例只点按钮 |
| `/` | 新建想法：Shift+Enter 换行（不提交） | — | 同上，`Composer.vue:49` |
| `/` | 「记下」按钮在内容为空时禁用 | — | `Composer.vue:66` 的 `:disabled="text.trim().length === 0 \|\| submitting"` |
| `/` | 提交失败时输入框内容保留 | — | `Composer.vue:37`（`ok` 为 false 不清空）；失败路径要用接口打桩才能造 |
| `/` | 切换完成态 / 取消完成 | ideas.spec.ts:142、ideas.spec.ts:167 | — |
| `/` | 编辑正文：点正文进入编辑态 | ideas.spec.ts:113 | — |
| `/` | 编辑正文：Ctrl+Enter 保存 | ideas.spec.ts:113 | — |
| `/` | 编辑正文：Esc 取消编辑 | — | `MessageItem.vue:297-300`（`onEditorKeydown` 的 Escape 分支） |
| `/` | 编辑正文：失焦自动保存 | — | `MessageItem.vue:576` 的 `@blur="saveEdit"` |
| `/` | 编辑正文：内容没改时不发请求、直接收起 | — | `MessageItem.vue:239-244` |
| `/` | 加标签 / 改标签 / 删标签 | ideas.spec.ts:191、ideas.spec.ts:213、ideas.spec.ts:240 | — |
| `/` | 标签补全下拉出现、且当前项目用过的标签排前面 | projects.spec.ts:341 | 只断言了候选**内容与顺序**，没点过候选项 |
| `/` | 补全：点候选项添加标签 | — | `TagEditor.vue:141-148` 的 `@click="addTag(option)"` |
| `/` | 标签输入：空格 / 逗号分隔成多个标签 | — | `TagEditor.vue:98-102` |
| `/` | 标签输入：大小写不敏感去重 | — | `TagEditor.vue:36-38, 60-63`；后端同口径在 `lib/filter.ts:51-52` |
| `/` | 标签输入：上下键移动高亮候选 | — | `TagEditor.vue:75-81, 108-111` |
| `/` | 标签输入：Esc 清掉半截文字 | — | `TagEditor.vue:103-107` |
| `/` | 收起标签编辑器时「还有未保存的改动」的二次确认 | — | `MessageItem.vue:333-345` 的 `window.confirm`；`ideas.spec.ts:62` 的 `openTagEditor` 辅助函数会**自动接受**对话框，从没断言过这个确认框 |
| `/` | 删除想法：二次确认后删除 | ideas.spec.ts:264 | — |
| `/` | 删除想法：点「取消」不删 | — | `MessageItem.vue:664`（确认条里的「取消」按钮）；项目/webhook 的「取消」都测了，唯独这条没测 |
| `/` | 多选勾选 / 取消勾选、批量条条数 | ideas.spec.ts:291 | — |
| `/` | 批量标记完成 / 批量撤销完成 | ideas.spec.ts:316 | — |
| `/` | 批量加标签 | ideas.spec.ts:346 | 只断言「都加上新标签」，没断言**原有标签是否保留**（并集语义，`HomePage.vue:856` 的 `replace: false`） |
| `/` | 批量删除（二次确认） | ideas.spec.ts:376 | — |
| `/` | 批量删除：点「取消」不删 | — | `BatchBar.vue:151-152` |
| `/` | 批量条「取消选择」清空勾选 | — | `BatchBar.vue:131-133`；**唯一的清除勾选入口**，没被测过 |
| `/` | 合并：对话框预览（条目与初始拼接正文） | ideas.spec.ts:407 | — |
| `/` | 合并：改过正文再合并 | ideas.spec.ts:460 | — |
| `/` | 合并：点「取消」不合并 | — | `MergeDialog.vue:68` |
| `/` | 合并按钮在只勾一条时禁用 | — | `BatchBar.vue:109`（`:disabled="busy \|\| count < 2"`） |
| `/` | 合并结果滚到底部并高亮定位 | — | `HomePage.vue:911` 设 `focusIdeaId`，`useDeepLink.ts:111-139` 定位；现有合并用例只断言条目出现 |
| `/` | 按项目筛选（侧栏切项目） | ideas.spec.ts:488、projects.spec.ts:241、projects.spec.ts:272 | — |
| `/` | 按状态筛选：全部 / 未完成 / 已完成 | ideas.spec.ts:528 | — |
| `/` | 按标签筛选：勾选标签 | ideas.spec.ts:506 | — |
| `/` | 项目视图里给想法加标签（切到该项目后操作） | projects.spec.ts:298 | — |
| `/` | 项目视图里删掉想法的标签 | projects.spec.ts:320 | — |
| `/` | 切到没有想法的项目：想法列表空状态 | projects.spec.ts:383 | — |
| `/` | 清除筛选：取消标签与状态筛选后列表恢复全部 | ideas.spec.ts:547 | — |
| `/` | 关键词搜索：输入即过滤（列表只剩命中的那几条） | ideas.spec.ts:594 | — |
| `/` | 关键词搜索：忽略大小写 | ideas.spec.ts:607 | — |
| `/` | 关键词搜索：搜不到时显示空状态 | ideas.spec.ts:620 | — |
| `/` | 关键词搜索：清空输入后列表恢复全部 | ideas.spec.ts:631 | — |
| `/` | 关键词搜索：**标签名**也能搜到 | search.spec.ts:60 | — |
| `/` | 关键词搜索：**项目名**也能搜到 | search.spec.ts:78 | — |
| `/` | 关键词搜索：命中带高亮（正文里包 `<mark>`） | search.spec.ts:92 | — |
| `/` | 关键词搜索：英文是**词级**（`hel` 搜不到 `hello`） | search.spec.ts:104 | — |
| `/` | 关键词搜索：多段之间是 **AND**（`咖啡 水温` 两段都要有） | search.spec.ts:120 | — |
| `/` | 关键词搜索：中文是**词元相邻**（标点不挡路 / 中间隔一个字就不算） | search.spec.ts:134 | 唯一能分开「词元相邻」与「子串」的用例：`咖啡水温` 命中 `咖啡，水温`、不命中 `咖啡的水温` |
| `/` | 「清除」按钮：没有过滤条件时不出现 | ideas.spec.ts:646 | — |
| `/` | 「清除」按钮：状态 / 标签 / 关键词任一生效时出现 | ideas.spec.ts:654、ideas.spec.ts:663、ideas.spec.ts:676 | — |
| `/` | 「清除」按钮：一下把状态、标签、关键词一起清掉 | ideas.spec.ts:685 | — |
| `/` | **标签 AND/OR 模式切换（任一 / 全部）** | — | `TagFilter.vue:37-39, 90-101` 的 `toggleMode` 按钮；AND 模式必须**同时**命中全部选中标签才显示（`lib/filter.ts:54-56`），语义完全没测 |
| `/` | AND/OR 偏好写进 localStorage、刷新后仍是 | — | `composables/useTagMode.ts:23-31` |
| `/` | 标签过滤面板：点组件外部收起 / Esc 收起 | — | `TagFilter.vue:50-66` |
| `/` | 深链 `?idea=<id>` 直达并高亮 | ideas.spec.ts:718 | — |
| `/` | 页面载入后自动滚到底部（最新一条在视口里） | search.spec.ts:152 | 顺带断言最老的一条**不在**视口里 —— 内容没溢出时「停在底部」是废话 |
| `/` | 往回翻历史：接近顶部自动加载更旧的一页 | search.spec.ts:170 | 断的是**服务端分页的方向**（`offset` 从**最新**一端数）与「往回翻之后视口没跳走」。「更旧的一页拼在前面还是后面」显示层分不出来（它按 `updated_at` 重排），由 `web/src/pages/HomePage.spec.ts` 的单测钉住 |
| `/` | 上翻时不抢滚动，出现「↓ 有 N 条新内容」按钮 | — | `HomePage.vue:998` + `composables/useScrollFollow.ts:100-113` |
| `/` | SSE：别的上下文新建的想法自动出现 | ideas.spec.ts:767 | — |
| `/` | SSE：**项目**改名 / 新建 / 删除同步到侧栏 | — | `HomePage.vue:987-991`（`project.*` 分流）、`HomePage.vue:1113-1150`（就地改名 / 删项目时切回「全部项目」） |
| `/` | 项目侧栏「＋」入口到项目管理页 | navigation.spec.ts:219 | — |
| `/` | 顶栏「⋯」菜单：项目管理 / 回收站 / Webhook | — | 「回收站」那一步已有用例（layout.spec.ts:1121）；「项目管理」「Webhook」两个入口还没点过 —— `MoreMenu.vue`（主页的管理页入口；窄屏下进回收站从两步变一步，规格 §D11）。`web/src/components/MoreMenu.spec.ts` 覆盖了组件本身 |
| `/` | 侧栏上的项目条数与「全部项目」总数 | — | `ProjectSidebar.vue:35,122,145`（窄屏的项目下拉里不显示条数） |
| `/` | 「全部项目」命中数超上限的兜底提示 | — | `HomePage.vue:531-549`（`recoverFromTooMany`） |
| `/` | 切换项目失败时回滚到上一个项目 | — | `HomePage.vue:397-401`（`switchProject` 的 catch 分支） |
| `/` | 底部输入框只有一层边框（外壳不画边框，输入框自己画一层） | layout.spec.ts:928 | — |
| `/` | 窄屏顶栏的项目下拉：切项目（列出全部项目与各项目） | projects.spec.ts:53、projects.spec.ts:302 | — |
| `/` | 加载态不闪空状态（「加载中…」不与「这里还没有想法。」同时出现） | navigation.spec.ts:400 | — |
| `/` | **宽屏**：侧栏贴左、主区铺到右边缘 | layout.spec.ts:985 | — |
| `/` | **宽屏**：一屏能看到 ≥10 条想法 | layout.spec.ts:1001 | 数的是**在视口里**的卡片 —— `:visible` 只说明「没被藏起来」，屏幕外但没藏的也算 |
| `/` | **宽屏**：想法排成两列（奇数在左、偶数在右） | layout.spec.ts:1011、layout.spec.ts:1226 | — |
| `/` | **平板 768×1024**：两列、无侧栏 | layout.spec.ts:1026 | — |
| `/` | **窄屏 <768px**：单列 | layout.spec.ts:1044 | — |
| `/` | **窄屏**：没有想法被藏起来 | layout.spec.ts:1061 | 断言的是 `:visible` 的**条数**（14 条一条不少） |
| `/` | **窄屏**：钉死的外壳只有 2 行（过滤行随内容滚走） | layout.spec.ts:1070 | 判据是「过滤行的矩形与视口不相交」 |
| `/` | **窄屏**：一屏能看到 ≥5 条想法 | layout.spec.ts:1092 | — |
| `/` | 卡片只有一整块正文，没有标题层 | layout.spec.ts:1102 | — |
| `/` | 编号紧挨时间（meta 行里） | layout.spec.ts:1109 | — |
| `/` | 窄屏点 🔍：滚回列表顶部并聚焦搜索框 | layout.spec.ts:1129 | — |
| `/` | 桌面端：勾选框与 🏷🗑 悬停才显形、**键盘聚焦也显形** | layout.spec.ts:1148 | 显形是纯 CSS 媒体查询 + 伪类，单测（jsdom）读不出来 —— 只有 e2e 钉得住 |
| `/` | 窄屏：勾选框与 🏷🗑 常驻 | layout.spec.ts:1173 | — |
| `/` | **无悬停能力**的设备：勾选框与 🏷🗑 常驻 | layout.spec.ts:1186 | 判据是「这台设备有没有悬停能力」（`(hover: none)`），不是屏宽 |
| `/` | 窄屏：顶栏的新触发器（⋯ / 🔍 / 项目下拉）够大 | layout.spec.ts:1218 | — |
| `/` | 新记的一条落在**最下面**，左右按奇偶交替 | layout.spec.ts:1226 | 14 条 → 第 15 条是奇数位（左格）、第 16 条是偶数位（右格） |
| `/` | 编辑一条靠前的想法：它跳到最下面，其下方卡片左右互换 | layout.spec.ts:1241 | 规格 §D6 明确批准的取舍（不是 bug） |
| `/` | 正文被 3 行截断：「文本没被裁掉」只对**编辑态**成立（普通正文 + 超长不换行那条，两条都查） | layout.spec.ts:694（20 条）、layout.spec.ts:724 | 规格 §6.11：截断后读全文的唯一入口是「点正文进编辑态」 |

### 2.2 `/projects` 项目管理（ProjectsPage.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/projects` | 新建项目 | projects.spec.ts:29 | — |
| `/projects` | 新建的项目在宽屏侧栏与窄屏顶栏的项目下拉里都能看到 | projects.spec.ts:53 | — |
| `/projects` | 新建项目：名称为空给提示 | projects.spec.ts:43 | — |
| `/projects` | 改名（保存） | projects.spec.ts:71 | — |
| `/projects` | 改名：点「取消」 | projects.spec.ts:84 | — |
| `/projects` | 改名：输入框回车提交 | — | `ProjectsPage.vue:279` 的 `@keydown.enter` |
| `/projects` | 改名：名称为空给提示 | — | `ProjectsPage.vue:92-96` |
| `/projects` | 删除（二次确认） | projects.spec.ts:97 | — |
| `/projects` | 删除：点「取消」 | projects.spec.ts:111 | — |
| `/projects` | 内置收件箱不可删除 | projects.spec.ts:125 | — |
| `/projects` | 排序方式：切手动 / 切最近活动 | projects.spec.ts:141、projects.spec.ts:154 | — |
| `/projects` | 拖拽重排（往下拖 → 落到目标之后） | projects.spec.ts:169、projects.spec.ts:204 | — |
| `/projects` | 拖拽重排（**往上拖** → 落到目标之前） | — | `lib/reorder.ts:49` 的 `insertAt` 两个方向只测了一个（四个落点组合只覆盖了「源在目标之前」） |
| `/projects` | 切排序方式后顺序不跳位 | — | `ProjectsPage.vue:183-193` 的注释描述的语义 |
| `/projects` | 项目条数与最近活动时间显示 | — | `ProjectsPage.vue:282-285` |
| `/projects` | 空状态「还没有其它项目。」（只有收件箱时显示 / 有别的项目时不显示） | projects.spec.ts:364、projects.spec.ts:373 | — |
| `/projects` | 项目级 webhook 入口：每个项目行的「Webhook」链接 → `/projects/<id>/webhooks` | projects.spec.ts:394 | — |

### 2.3 `/trash` 回收站（TrashPage.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/trash` | 删除的想法出现在回收站 | trash.spec.ts:105 | — |
| `/trash` | 顶层条目显示「原属项目：」 | trash.spec.ts:150 | — |
| `/trash` | 删项目不牵连还活着的项目 | trash.spec.ts:115 | — |
| `/trash` | 容器条目：项目名 + 「N 条想法」 | trash.spec.ts:129 | — |
| `/trash` | 容器展开，看得见下挂的想法 | trash.spec.ts:129、trash.spec.ts:216 | — |
| `/trash` | 容器**收起** | — | `TrashPage.vue:137-139` 同一个按钮在「展开 / 收起」之间切；只测过「展开」 |
| `/trash` | 恢复想法（顶层条目） | trash.spec.ts:166 | — |
| `/trash` | 恢复项目（连带恢复下挂想法） | trash.spec.ts:193 | — |
| `/trash` | 容器里的想法单独恢复 | trash.spec.ts:216 | — |
| `/trash` | 恢复请求在飞时按钮禁用 | — | `TrashPage.vue:154, 172, 200` 的 `:disabled="busyId !== null"` |
| `/trash` | 没有「彻底删除」，到期自动清理的说明 | trash.spec.ts:241 | — |
| `/trash` | 回收站里的内容刷新后仍在（不会自己消失） | trash.spec.ts:257 | — |
| `/trash` | 回收站为空时的空状态 | trash.spec.ts:271 | — |

### 2.4 `/webhooks` 全局 webhook（WebhookManager.vue / WebhookForm.vue / DeliveryList.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/webhooks` | 空状态「还没有 webhook。」+「新建 webhook」入口 | webhooks.spec.ts:212 | — |
| `/webhooks` | 新建 webhook（名称/URL/Body 模板/订阅事件） | webhooks.spec.ts:221 | — |
| `/webhooks` | Telegram 模板按钮预填 URL 与 Body 模板 | webhooks.spec.ts:246 | — |
| `/webhooks` | 启用 / 禁用（列表按钮） | webhooks.spec.ts:267 | — |
| `/webhooks` | 表单里的「启用」复选框（编辑时改启用状态） | — | `WebhookForm.vue:326-338`；列表上的按钮测了，表单里的复选框没测 |
| `/webhooks` | 编辑：改名称与 URL | webhooks.spec.ts:375 | — |
| `/webhooks` | 编辑：点「取消」不保存 | — | `WebhookForm.vue:358` |
| `/webhooks` | 删除（二次确认 + 取消） | webhooks.spec.ts:459 | — |
| `/webhooks` | 表单校验：名称为空 | webhooks.spec.ts:281 | — |
| `/webhooks` | 表单校验：URL 为空 | webhooks.spec.ts:295 | — |
| `/webhooks` | 表单校验：URL 不是 http(s) 绝对地址 | webhooks.spec.ts:307 | — |
| `/webhooks` | 表单校验：URL 非法时**前端先拦下**（请求根本没发出去） | webhooks.spec.ts:307 | — |
| `/webhooks` | 表单校验：URL 合法时正常保存 | webhooks.spec.ts:338 | — |
| `/webhooks` | 表单校验：Body 模板为空 | — | `WebhookForm.vue:178-182` |
| `/webhooks` | 表单校验：一个事件都不订阅 | webhooks.spec.ts:358 | — |
| `/webhooks` | **自定义 Header 行**（加一行 / 删一行 / 键值） | — | `WebhookForm.vue:271-302`（`addHeader`/`removeHeader`/`buildHeaders`）；表单里一整块 UI，一行没测 |
| `/webhooks` | **请求方法下拉**（POST/GET/PUT/PATCH/DELETE） | — | `WebhookForm.vue:257-268` |
| `/webhooks` | **订阅事件的完整清单**（8 种事件、多选） | — | `lib/webhook.ts:9-30` 列出 8 种；用例只勾过「想法新建」一种 |
| `/webhooks` | Secret：新建时填密钥 → 显示「已设密钥」、明文不外泄 | webhooks.spec.ts:481 | — |
| `/webhooks` | Secret：编辑时**留空表示不改** | — | `WebhookForm.vue:126-134` 的三态（`null` / `""` / 非空） |
| `/webhooks` | Secret：编辑时「清除已设的密钥」 | — | `WebhookForm.vue:351-354`；勾「清除」与填新值互斥（`WebhookForm.vue:86-96`）也没测 |
| `/webhooks` | 列表徽标：已启用/已禁用、无密钥 | webhooks.spec.ts:221、webhooks.spec.ts:267 | 「不订阅任何事件」徽标（`WebhookManager.vue:158`）没测 |
| `/webhooks` | 投递记录：空状态 | webhooks.spec.ts:502 | — |
| `/webhooks` | 投递记录：事件触发后列出记录 + 「重投」再投一次 | webhooks.spec.ts:591 | — |
| `/webhooks` | 投递记录：失败态（`lastError`）与重投失败提示 | — | `DeliveryList.vue:75` |
| `/webhooks` | 成功提示：新建 → info「webhook 已创建」 | webhooks.spec.ts:395 | — |
| `/webhooks` | 成功提示：编辑 → info「webhook 已保存」 | webhooks.spec.ts:414 | — |
| `/webhooks` | 成功提示：删除 → info「webhook 已删除」 | webhooks.spec.ts:427 | — |
| `/projects` | 成功提示：新建 / 改名 / 删除 → info 提示 | projects.spec.ts:84 | — |
| `/trash` | 成功提示：恢复项目 / 恢复想法 → info 提示 | trash.spec.ts:167 | — |
| `/webhooks` | info 提示与 error 提示并存（两种 class 不同，互不顶掉） | webhooks.spec.ts:439 | — |
| `/webhooks` | 投递记录：「刷新」按钮重新拉一次，显示新记录 | webhooks.spec.ts:512 | — |
| `/webhooks` | 投递记录：拉取中「刷新」按钮是禁用的 | webhooks.spec.ts:562 | — |

### 2.5 `/projects/:id/webhooks` 项目级 webhook（ProjectWebhooksPage.vue）

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| `/projects/:id/webhooks` | 打开页面，标题带项目名 | webhooks.spec.ts:627 | — |
| `/projects/:id/webhooks` | 指向不存在的项目时页面不崩 | navigation.spec.ts:273 | — |
| `/projects/:id/webhooks` | 新建项目级 webhook（默认已启用） | webhooks.spec.ts:637 | — |
| `/projects/:id/webhooks` | 作用域隔离（全局的只在全局页，项目的只在项目页） | webhooks.spec.ts:665 | — |
| `/projects/:id/webhooks` | 删除项目级 webhook | webhooks.spec.ts:690 | — |

### 2.6 外壳 / 路由 / 全局提示 / 排版

| 页面 | 功能点 | 覆盖它的用例 | 备注 |
|---|---|---|---|
| 全局 | 底座冒烟：/api/health + 首页挂载 | smoke.spec.ts:13 | — |
| 全局 | 夹具复位契约（自检） | fixtures.spec.ts:10、fixtures.spec.ts:31、fixtures.spec.ts:51 | — |
| 全局 | 管理页导航条渲染与四个入口 | navigation.spec.ts:90（三条） | — |
| 全局 | 导航条四个链接的 href（「想法」指向 /） | navigation.spec.ts:101 | — |
| 全局 | 当前页高亮（含项目级 webhook 页点亮点是「Webhook」） | navigation.spec.ts:138（三条）、navigation.spec.ts:149 | — |
| 全局 | aria-current：当前页的导航项（项目级 webhook 页点亮点也是「Webhook」） | navigation.spec.ts:149 | — |
| 全局 | aria-current：当前项目的侧栏项（窄屏的项目下拉显示当前项目名） | navigation.spec.ts:165 | — |
| 全局 | 回首页入口：从 /projects、/trash、/webhooks 点「想法」回到首页 | navigation.spec.ts:190（三条） | — |
| 全局 | 点导航跳转后路由与内容对应 | navigation.spec.ts:209（三条） | — |
| 全局 | 五个路由深链直接访问不 404 | navigation.spec.ts:237（四条）、navigation.spec.ts:251 | — |
| 全局 | 深链刷新不 404：刷新后还是同一个页面 | navigation.spec.ts:262 | — |
| 全局 | 浏览器前进 / 后退 | navigation.spec.ts:282、navigation.spec.ts:299 | — |
| 全局 | 主页面没有管理页导航条（外壳不同） | navigation.spec.ts:111 | — |
| 全局 | 主页面只有一套项目导航（侧栏 aria-label="项目"）；窄屏的项目切换收成顶栏里的下拉（按钮，不是导航地标） | navigation.spec.ts:111 | — |
| 全局 | 未知路由兜底回首页（不白屏） | navigation.spec.ts:314、navigation.spec.ts:322 | — |
| 全局 | ToastHost：常驻、无消息时为空、错误提示、4 秒后自动消失 | navigation.spec.ts:345、navigation.spec.ts:354、navigation.spec.ts:367 | — |
| 全局 | 加载态「加载中…」 | navigation.spec.ts:381 | — |
| 全局 | 空状态文案（想法 / webhook） | navigation.spec.ts:457、navigation.spec.ts:470 | — |
| 全局 | 排版与响应式：5 路由 × 4 视口 | layout.spec.ts:694（20 条） | — |
| 全局 | 移动端点按目标 ≥ 32×32（5 路由） | layout.spec.ts:735（5 条） | — |
| 全局 | 移动端点按目标：新加的「Webhook」入口与「想法」入口 ≥ 32×32 | layout.spec.ts:837、layout.spec.ts:854 | — |
| 全局 | 管理页铺满（回收站 / 项目管理），Webhook 表单保持窄 | layout.spec.ts:1279 | — |

---

## 三、漏掉的功能点（按重要程度）

下面把 **44 条漏掉的功能点**（与上表的 `—` 一一对应）连同 **1 条只测了一半的**（第 20 条）全部列出，
共 **45 条**，按重要程度分四档。

### P0 — 核心语义 / 整块 UI / 跨设备同步（7 条）

1. **标签过滤的 AND / OR 模式切换**
   - 位置：`web/src/components/TagFilter.vue:37-39`（`toggleMode`）、`:90-101`（按钮）；
     匹配语义在 `web/src/lib/filter.ts:54-56`。
   - 为什么算漏掉：`ideas.spec.ts:506` 只覆盖了默认的 OR（勾两个标签 → 显示 2 条）。
     AND 模式下必须**同时**命中全部选中标签，这个分支一条用例都没走过；按钮本身也从未被点过。

2. **AND / OR 偏好写进 localStorage、刷新后仍是**
   - 位置：`web/src/composables/useTagMode.ts:13-31`（读/写）、`HomePage.vue:578-583`（切模式时落盘）。
   - 为什么算漏掉：这是**每台设备各自的偏好**，写失败（隐私模式）还有专门的兜底分支
     （`useTagMode.ts:24-30`），一条用例都没有。

3. **Webhook 的自定义 Header 行**（加一行 / 删一行 / 键值）
   - 位置：`web/src/components/WebhookForm.vue:271-302`（字段集 UI）、`:136-145`（增删行）、
     `:98-109`（`buildHeaders` 组装，空键的行忽略）。
   - 为什么算漏掉：表单里一整块可见 UI，保存时会被发到后端，但 `webhooks.spec.ts` 的建 webhook
     用例只填了名称/URL/Body 模板/事件，Header 从没被碰过。

4. **Webhook 表单里的「启用」复选框**（编辑时改启用状态）
   - 位置：`web/src/components/WebhookForm.vue:326-338`。
   - 为什么算漏掉：列表上的「启用/禁用」按钮测了（webhooks.spec.ts:267），**表单里的开关**没测 ——
     两条路径写的字段不同（表单写 `enabled`，按钮走 `PATCH /webhooks/{id}`）。

5. **Secret 留空 = 不改**（三态里的 `null`）
   - 位置：`web/src/components/WebhookForm.vue:126-134`（`secretValue()`）、`:340-349`（输入框 placeholder）。
   - 为什么算漏掉：Secret 只测了「新建时填 → 显示已设密钥」（webhooks.spec.ts:481）。
     编辑时把输入框留空**不该**清掉已有密钥 —— 这条语义没被钉住。

6. **Secret「清除已设的密钥」勾选项**（含与新填值的互斥）
   - 位置：`web/src/components/WebhookForm.vue:351-354`（勾选框）、`:86-96`（「勾清除」与「填新值」互斥）。
   - 为什么算漏掉：清除密钥是三态里的 `""`，与互斥逻辑一起没测。

7. **SSE 的项目事件**（改名 / 新建 / 删除项目同步到侧栏）
   - 位置：`web/src/pages/HomePage.vue:987-991`（`project.*` 分流）、`:1113-1150`（`applyProjectEvent`：
     就地改名、删项目时切回「全部项目」、本地没有则整表重拉）。
   - 为什么算漏掉：`ideas.spec.ts:767` 只覆盖了 `idea.created` 一类事件。改项目名、删项目这类
     **跨设备同步**是首页最容易被改坏的地方（`applyProjectEvent` 里还带着 `switchProject` 回滚分支）。

### P1 — 用户每天点得到、现在几乎没测（14 条）

8. **批量条「取消选择」按钮**
   - 位置：`web/src/components/BatchBar.vue:131-133`。
   - 为什么算漏掉：它是**唯一**清空勾选的操作（勾错一条只能一条条取消）。`ideas.spec.ts:291`
     只验证了「勾上 → 批量条出现 → 逐个取消 → 条数变」，从没点过这个按钮。

9. **删除想法的「取消」二次确认**
   - 位置：`web/src/components/MessageItem.vue:645-665`（删除按钮与「取消」确认条）。
   - 为什么算漏掉：项目删除（projects.spec.ts:111）与 webhook 删除（webhooks.spec.ts:459）都测了
     「取消」这条破坏性操作的退路，唯独想法删除没测 —— 而想法删除是**唯一**会让内容进回收站的操作。

10. **批量删除的「取消」**
    - 位置：`web/src/components/BatchBar.vue:147-153`。
    - 为什么算漏掉：`ideas.spec.ts:376` 只走通了「确认删除」，没走「取消」这条退路。

11. **合并对话框的「取消」**
    - 位置：`web/src/components/MergeDialog.vue:68`（`@click="emit('cancel')"`）、`HomePage.vue:1332`。
    - 为什么算漏掉：`ideas.spec.ts:407` 只走通了「合并成一条」；取消后对话框应关闭、勾选应保留。

12. **「合并」按钮在只勾一条时禁用**
    - 位置：`web/src/components/BatchBar.vue:106-114`（`:disabled="busy || count < 2"`）。
    - 为什么算漏掉：合并至少要两条（`HomePage.vue:901` 有同样的守卫），禁用态没被钉住。

13. **Composer 的 Enter 提交**
    - 位置：`web/src/components/Composer.vue:48-53`。
    - 为什么算漏掉：输入框 placeholder 明确告诉用户「Enter 提交」（`Composer.vue:63`），
      但 `ideas.spec.ts:84` 只点按钮 —— 用户最常用的提交方式没被测。

14. **Composer 的 Shift+Enter 换行（不提交）**
    - 位置：`web/src/components/Composer.vue:49`。
    - 为什么算漏掉：同上；「换行不提交」是防误提交的约定。

15. **编辑正文：Esc 取消编辑**
    - 位置：`web/src/components/MessageItem.vue:290-301`（`onEditorKeydown` 的 Escape 分支）、`:213-219`。
    - 为什么算漏掉：Ctrl+Enter 测了（ideas.spec.ts:84），Esc 是「放弃这次编辑」的唯一显式入口。

16. **编辑正文：失焦自动保存**
    - 位置：`web/src/components/MessageItem.vue:576`（`@blur="saveEdit"`）。
    - 为什么算漏掉：点条目别处就存盘，用户最容易「以为没保存」的场景。

17. **项目重命名：输入框回车提交**
    - 位置：`web/src/pages/ProjectsPage.vue:279`（`@keydown.enter="onSaveRename(project)"`）。
    - 为什么算漏掉：`projects.spec.ts:71` 只点「保存」按钮。

18. **项目改名为空时给出提示**
    - 位置：`web/src/pages/ProjectsPage.vue:92-96`。
    - 为什么算漏掉：与新建项目的空名提示（projects.spec.ts:43）同源，但改名这条分支没测。

19. **拖拽重排的「往上拖」方向**（源在目标之后 → 落到目标之前）
    - 位置：`web/src/lib/reorder.ts:28-52`（`reorderToTarget`：`from < to ? target + 1 : target`）。
    - 为什么算漏掉：`projects.spec.ts:169` 与 `:204` 只测了「往下拖 → 落到目标之后」；
      文件头注释列的四种组合里，「往上拖」一种没有用例。

20. **批量加标签的「并集式追加」语义**（*只测了一半*）
    - 位置：`web/src/pages/HomePage.vue:844-869`（`replace: false`）、`BatchBar.vue:60-72`（回执后清草稿）。
    - 为什么算漏掉：`ideas.spec.ts:346` 只断言「两条都加上了新标签」，没断言**它们原有的标签还在**
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
      「刚存好就被问还有没有未保存改动」（`MessageItem.vue:153-157`）。

24. **标签输入：上下键移动高亮候选**
    - 位置：`web/src/components/TagEditor.vue:75-81`（`moveHighlight`）、`:108-111`（键盘映射）。
    - 为什么算漏掉：键盘选候选是补全面板的主交互。

25. **标签输入：Esc 清掉半截文字**
    - 位置：`web/src/components/TagEditor.vue:103-107`。
    - 为什么算漏掉：与 TagFilter 的 Esc（收起面板）不同，两处都叫 Esc 但行为不一样，没测就分不清。

26. **补全下拉里点候选项添加标签**（*只测了一半*）
    - 位置：`web/src/components/TagEditor.vue:141-148`（`@click="addTag(option)"`）。
    - 为什么算漏掉：`projects.spec.ts:341` 只断言了候选**内容与顺序**，从没有点过候选项。

27. **收起标签编辑器时的「还有未保存的改动」确认框**
    - 位置：`web/src/components/MessageItem.vue:333-345`（`window.confirm`）。
    - 为什么算漏掉：`ideas.spec.ts:62` 的 `openTagEditor` 辅助函数会**自动接受**对话框，从不断言这个确认流程 ——
     「不静默丢改动」是这段代码存在的理由（`MessageItem.vue:323-330` 的注释）。

28. **标签过滤面板：点组件外部收起 / Esc 收起**
    - 位置：`web/src/components/TagFilter.vue:50-66`。
    - 为什么算漏掉：不下拉会一直挂在那儿，是「点了没反应」的经典来源。

29. **Webhook 订阅事件的完整清单**（8 种）与多选
    - 位置：`web/src/lib/webhook.ts:9-30`（8 种事件与中文标签）、`WebhookForm.vue:317-324`。
    - 为什么算漏掉：用例只勾过「想法新建」一种（webhooks.spec.ts:221 的新建用例、:403 的投递用例）。

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
    - 为什么算漏掉：`trash.spec.ts:129` 只点过「展开」。

34. **编辑正文：内容没改时不发请求、直接收起**
    - 位置：`web/src/components/MessageItem.vue:239-244`。
    - 为什么算漏掉：这条分支省掉一次请求，也是「在飞保护」之外的一道判断。

### P3 — 展示层与边界路径（11 条）

35. **顶栏「⋯」菜单的「项目管理」与「Webhook」两个入口**（「回收站」那一步已补上）
    - 位置：`web/src/components/MoreMenu.vue`（`emit('manageProjects')` / `emit('trash')` / `emit('webhooks')`）。
    - 为什么还算漏掉：`layout.spec.ts:1121` 已经从主页顶栏点进了**回收站**，
      但「项目管理」「Webhook」两个入口 e2e 里还没点过 ——
      窄屏下这一步是管理页的唯一入口。

36. **侧栏上的项目条数与「全部项目」总数**
    - 位置：`web/src/components/ProjectSidebar.vue:35`（总数）、`:122,145`（各项目条数）。
    - 为什么算漏掉：条数是用户判断「这个项目里有什么」的主要依据。

37. **/projects 上的项目条数与最近活动时间**
    - 位置：`web/src/pages/ProjectsPage.vue:282-285`。
    - 为什么算漏掉：同上；`formatRelativeTime` 的输出也没被断言过。

38. **上翻时不抢滚动，出现「↓ 有 N 条新内容」按钮**
    - 位置：`web/src/pages/HomePage.vue:998`、`web/src/composables/useScrollFollow.ts:100-138`。
    - 为什么算漏掉：`useScrollFollow` 的整套逻辑（贴底判定、积攒计数、按钮回到底部）一条用例都没有。

39. **合并结果滚到底部并高亮定位**
    - 位置：`web/src/pages/HomePage.vue:911`（设 `focusIdeaId`）、`web/src/composables/useDeepLink.ts:111-139`。
    - 为什么算漏掉：合并结果出现在列表最下面，用户多半没看见；
      `ideas.spec.ts:407` 只断言了条目出现，没断言它被滚到视野里并高亮。

40. **投递记录的失败态（`lastError`）与重投失败提示**
    - 位置：`web/src/components/DeliveryList.vue:75`、`:34-51`。
    - 为什么算漏掉：投递失败是 webhook 使用中最常见的现场，只测了成功路径。

41. **回收站恢复请求在飞时按钮禁用**
    - 位置：`web/src/pages/TrashPage.vue:154, 172, 200`（`:disabled="busyId !== null"`）。
    - 为什么算漏掉：防重复提交，用户连点「恢复」时不该发两次。

42. **「全部项目」命中数超上限的兜底提示**
    - 位置：`web/src/pages/HomePage.vue:531-549`（`recoverFromTooMany`）、`:558-560`（提示文案）。
    - 为什么算漏掉：这是首页唯一一条「应用替用户做决定」的分支（自动切到最近有活动的项目）。

43. **切换项目失败时回滚到上一个项目**
    - 位置：`web/src/pages/HomePage.vue:397-401`（`switchProject` 的 catch 分支）。
    - 为什么算漏掉：回滚错了就是「列表停在错的数据上」，而且是静默的。

44. **切换项目排序方式后顺序不跳位**
    - 位置：`web/src/pages/ProjectsPage.vue:183-193`（注释描述的语义）、`server/.../ProjectService.SetSortMode`。
    - 为什么算漏掉：`projects.spec.ts:141` 只断言「页面顺序与后端给的一致」，没断言「与切换**前**一致」。

45. **提交失败时输入框内容保留**
    - 位置：`web/src/components/Composer.vue:37`（`ok` 为 false 不清空）、`HomePage.vue:594-613`。
    - 为什么算漏掉：用户刚敲的正文不能因为一次网络失败就没了；失败路径要用接口打桩才能造。

---

## 四、复现方式

```bash
# 现有用例清单（不跑用例，只列出）
cd web && npx playwright test --list

# 跑全量
bash web/e2e/run-e2e.sh
```

> ⚠️ **一轮 = 一个端口 + 一个库**：端口从 5300 起自己找空闲的（`E2E_PORT` 可指定），
> 库落在 `web/e2e/.tmp/run-<端口>/e2e.db`，产物目录也按端口分。
> 两轮**之间**互不干扰（端口、库、产物目录都分开），可以同时跑；
> 但**一轮内部**是 `workers: 1` —— 一轮里的用例共用同一个库，
> 靠每个用例前自动复位来保证互不影响。详见 `web/e2e/README.md` 的「并发」一节。
