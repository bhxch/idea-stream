# E2E 测试底座（Playwright）

Idea Manager 的端到端测试底座。后端（.NET 10 + SQLite）与前端产物**同源**托管，
所以 E2E 只跑一个地址：`http://127.0.0.1:5299`。

本目录只放**底座**：夹具、脚本、约定。用例本身按分区写在 `web/e2e/` 下。

---

## 目录结构

```
web/
  playwright.config.ts     Playwright 配置（testDir、baseURL、webServer、project、报告）
  e2e/
    README.md              本文件：约定
    fixtures.ts            公共夹具：api（接口封装 + 复位）、seed（造数据）
    fixtures.spec.ts       夹具自检：把「复位」的契约钉住
    smoke.spec.ts          冒烟：底座通不通
    start-server.sh        幂等启动（或复用）E2E 服务
    run-e2e.sh             保证服务在跑 → 跑用例 → 退出码透传
    rebuild.sh             重建前端/后端（flock 串行化）
    lib/common.sh          三个脚本共用的 shell 逻辑（路径、健康探测、停服）
    .tmp/                  运行时产物：数据库、PID、日志、锁（已 gitignore）
```

用例文件按分区加在 `web/e2e/` 下即可（`*.spec.ts`），例如
`web/e2e/projects.spec.ts`、`web/e2e/trash.spec.ts`。

## 端口与基址

| 项 | 值 | 说明 |
|---|---|---|
| 端口 | `5299` | E2E 专用。**5080 被沙箱里的别的进程占着，5199 是 `server/scripts/aot-smoke.sh` 的端口，两个都不能抢** |
| baseURL | `http://127.0.0.1:5299` | 配在 `playwright.config.ts` 的 `use.baseURL` |
| 数据库 | `web/e2e/.tmp/e2e.db` | 与开发库、冒烟脚本的库完全分开 |
| 前端产物 | `server/IdeaApp/wwwroot/` | 构建产物，被 `.gitignore` 排除 |

`use.baseURL` 已经设好，用例里直接 `page.goto('/projects')` 就行 —— 不要写绝对地址。

## 怎么跑

```bash
# 全量（推荐：先起服务，跑完停服务）
bash web/e2e/run-e2e.sh

# 全量 + 干净的库（删掉数据库重建）—— 排查「上一轮的数据影响这一轮」时用
bash web/e2e/run-e2e.sh --fresh

# 强制重建（前端 + 后端）并重启服务
bash web/e2e/run-e2e.sh --rebuild

# 跑完不停服务（下一轮接着用，省一次重建）
bash web/e2e/run-e2e.sh --keep

# 单个文件 / 单条用例：参数原样透传给 playwright test
bash web/e2e/run-e2e.sh e2e/smoke.spec.ts
bash web/e2e/run-e2e.sh --grep "回收站"
bash web/e2e/run-e2e.sh --headed e2e/projects.spec.ts

# 只起服务，自己跑（服务会一直在）
bash web/e2e/start-server.sh
cd web && npx playwright test e2e/smoke.spec.ts

# 看报告
cd web && npm run e2e:report        # playwright show-report
```

`run-e2e.sh` 的退出码就是 `playwright test` 的退出码，可以直接进 CI。

### 脚本契约

| 命令 | 作用 |
|---|---|
| `bash web/e2e/start-server.sh [--fresh] [--rebuild]` | 幂等启动服务。端口 5299 上已有健康服务 → 直接退出 0 复用（不重建、不重启）；否则构建前端 → 构建后端 → 以 `IDEA_Urls=http://127.0.0.1:5299`、`IDEA_Database__Path=<仓库根>/web/e2e/.tmp/e2e.db` 启动，轮询 `/api/health` 到 200。`--fresh` 先杀旧服务并删库，`--rebuild` 强制重建并重启 |
| `bash web/e2e/run-e2e.sh [--fresh] [--rebuild] [--keep] [playwright 参数…]` | 保证服务在跑 → 在 `web/` 下跑 `npx playwright test` → 退出码透传。默认跑完停服务，`--keep` 不停 |
| `bash web/e2e/rebuild.sh [frontend\|backend\|all]` | 重建。`backend`（与 `all`）重建后自动重启服务 |

三个脚本都用 `flock`（`web/e2e/.tmp/e2e.lock`）串行化：多个阶段并发调用时，
构建产物不会互相覆盖。`start-server.sh` 拿到锁之后会**重新探测一次**健康状态再决定要不要起。

服务是 `setsid` 拉起来的 —— 脚本退出后它还活着；PID 记在 `web/e2e/.tmp/server.pid`
（另有 `server.wrapper.pid` 记 `dotnet run` 那个壳子）。

## 夹具

用例文件一律从 `./fixtures` 导入 `test` 与 `expect`（**不要**从 `@playwright/test` 导入 `test`，
否则复位不生效）：

```ts
import { test, expect } from './fixtures';

test('收件箱里能看到刚记的想法', async ({ page, seed }) => {
  await seed.idea('今天想到一个点子');
  await page.goto('/');
  await expect(page.getByText('今天想到一个点子')).toBeVisible();
});
```

### `api`：接口封装 + 复位

`api` 是应用 API 的薄封装（`web/e2e/fixtures.ts` 里的 `IdeaApi`），路径都以 `/api/` 开头，
请求走 Playwright 的 `request` 夹具（自带 baseURL）。常用方法：

| 方法 | 说明 |
|---|---|
| `api.health()` | `GET /api/health` |
| `api.listProjects()` / `createProject(name)` / `deleteProject(id)` / `restoreProject(id)` | 项目 |
| `api.listIdeas(params?)` / `createIdea(input)` / `updateIdea(id, patch)` / `setTags(id, tags)` / `setDone(id, done)` / `deleteIdea(id)` / `restoreIdea(id)` / `mergeIdeas(ids, opts?)` | 想法 |
| `api.listTags()` / `suggestTags(projectId, prefix)` | 标签 |
| `api.listWebhooks(projectId?)` / `createWebhook(input)` / `deleteWebhook(id)` | webhook |
| `api.listTrash()` | 回收站 |
| `api.getSettings()` / `setSetting(key, value)` | 设置 |
| `api.reset()` | 复位（下节） |

### `seed`：造数据

```ts
const project = await seed.project();                    // 造项目，默认名「测试项目」
const idea = await seed.idea('正文');                     // 造想法，默认落收件箱
await seed.idea('正文', { projectId: project.id });       // 落到指定项目
await seed.doneIdea('正文');                              // 造一条已完成的
await seed.tag('标签名');                                // 造一条带标签的想法，返回标签名
await seed.webhook({ name: '我的钩子' });                 // 造 webhook（默认全局、创建后禁用，不会真发请求）
```

`seed` 只是省事的封装，底层就是 `api` 的那几个方法 —— 需要更细的控制就直接调 `api`。

### 复位（每个用例前自动发生）

`fixtures.ts` 里注册了一个**自动夹具** `resetBeforeEach`（`{ auto: true }`）：
每个用例开始前都会 `api.reset()`。写用例的人不用记着复位 ——
「忘了复位」是顺序相关用例的常见来源。

> ⚠️ 这里必须用自动夹具，**不能**写成顶层 `test.beforeEach(...)`：
> 顶层钩子会挂到「模块体首次求值时正在加载的那个 spec 文件」的套件上，
> 而 `fixtures.ts` 被 ESM 缓存、每个 worker 只求值一次 —— 于是同一轮里
> 只有**第一个** spec 文件拿得到复位，后面的文件全都没有，脏数据会在用例
> 之间串味（表现是「单独跑这个文件全过、全量跑就红」）。
> 自动夹具挂在夹具定义上，对每一个用到这个 `test` 的用例都生效。

复位走 HTTP API，**不碰数据库文件**，做四件事：

1. 把回收站里的条目**恢复**回来（回收站清空，所有项目与想法回到活动状态）；
2. 清掉所有活动想法：同一个项目里的想法用合并**硬删**（行真的没了），
   只留一条标题为 `e2e-reset` 的合并产物，清空标签后软删；
3. 删掉除收件箱外的项目；
4. 删掉所有 webhook，并把 `project.sort_mode` 复位成默认值 `recent`。

复位之后保证：

| 状态 | 结果 |
|---|---|
| 活动想法 | 0 条 ✅ |
| 活动项目 | 只有「收件箱」 ✅ |
| webhook | 0 个 ✅ |
| 设置 | `project.sort_mode = recent` ✅ |
| **回收站** | ⚠️ **有残渣**：每个有想法的项目留一条标题为 `e2e-reset` 的想法（项目被删时它挂在容器条目下） |
| **标签字典** | ⚠️ **有残渣**：用过的标签名还在，计数为 0 |

这两处残渣**清不掉是应用的设计，不是夹具偷懒**：回收站只允许恢复、不允许彻底删除
（见 `server/IdeaApp/Application/TrashService.cs`），标签字典的行也不会随想法删除而删
（见 `server/IdeaApp/Infrastructure/TagRepository.cs`）。

所以：

- 断言**活动**状态（想法列表、项目列表、webhook 列表）时可以直接断言「空的 / 只有收件箱」；
- 断言**回收站**时按内容定位（`getByText('要被删的想法')`），
  **不要**断言条目总数为 0 —— 残渣会让它变成 1 或更多；
- 断言**标签下拉**同理：别断言「没有标签」，断言「某个标签在」；
- 确实需要完全干净的库（要断言回收站为空、标签字典为空）时用
  `freshDatabase()`（见下），或者整轮用 `run-e2e.sh --fresh` 重跑。

### `freshDatabase()`：完全干净的库

```ts
import { freshDatabase } from './fixtures';

test('回收站一开始是空的', async ({ page }) => {
  freshDatabase();          // 删掉数据库文件、重建、重启服务（约 5 秒）
  await page.goto('/trash');
  await expect(page.locator('.trash__entry')).toHaveCount(0);
});
```

它会**重启 E2E 服务**，所以慢，而且只能在自己独占服务时用
（配置里 `workers` 固定为 1，同一时刻只有一个用例在跑）。

## 选择器策略

从粗到细，按这个顺序选：

1. **角色 + 可访问名**：`page.getByRole('heading', { name: '想法' })`、
   `page.getByRole('button', { name: '项目' })`、`page.getByRole('dialog', { name: '合并想法' })`。
   最稳，改文案时才会红。
2. **已有的无障碍/测试钩子**（前端已经有的，直接用）：
   - `nav[aria-label="管理"]`（页面导航）、`nav[aria-label="项目"]`（项目侧栏）
   - `[data-project-id="<项目 id>"]`（项目条目；`[data-project-id="all"]` 是「全部」）
   - `role="group" aria-label="状态过滤"`、`aria-label="新项目名称"`、`aria-label="项目名称"`
   - `aria-label="选择 <标题>"`（想法条目的复选框）、`aria-label="删除标签 <标签名>"`
   - `role="status"`（全局提示条 ToastHost）
3. **可见文字**：`page.getByText('想法二')` —— 用户看得见的文案。
4. **BEM class**（`page.locator('.bar__title')`）：**最后手段**。
   class 是实现细节，改样式就会红；只在没有别的办法时用，并在用例里写一句注释说明为什么。

不要写的东西：

- 绝对 URL（`page.goto('http://127.0.0.1:5299/')`）—— 用 `page.goto('/')`；
- `waitForTimeout(1000)` 这种硬等 —— 等**状态**（`expect(...).toBeVisible()` /
  `await expect(locator).toHaveCount(n)`），Playwright 会自己重试；
- 依赖用例执行顺序的断言 —— 每个用例都从复位后的状态开始；
- 依赖上一次跑残留的 id / 文本。

## 断言风格

- 断言用户看得见的东西：文字、可见性、数量、位置。**不要**断言内部状态、接口返回的
  私有字段，或者 CSS 样式。
- 用 `expect(locator).toBeVisible()` / `toHaveText()` / `toHaveCount()` / `toContainText()`。
- 数量类断言用 `toHaveCount(n)`，不要 `count()` + `expect()`（后者不会自动重试）。
- 断言「不该在」用 `not.toBeVisible()` 或 `toHaveCount(0)`。
- 错误提示条用 `page.getByRole('status')`（ToastHost 是 `role="status"` 的 `aria-live` 区域）。
- 需要断言接口行为时走 `api`（例如 `await expect(api.health()).resolves.toEqual({ status: 'ok' })`），
  不要在 `page.evaluate` 里手搓 `fetch`。

## 禁止事项

- **不要改 `web/src/**` 与 `server/**` 的业务代码**来迁就用例。
  底座阶段不改业务代码；写用例时发现是 bug，记下来交给修复阶段，别在用例里绕。
- **不要在用例里手写 `fetch` 调后端**：走 `api`（统一 baseURL、统一错误处理）。
- **不要自己起服务**：端口 5299 是共用的，`start-server.sh` 会复用。
  用例里要重启服务只能用 `freshDatabase()`。
- **不要往用例里塞 `web/e2e/.tmp/` 里的东西**：那是运行时产物，已 gitignore。
- **不要把凭据、token 写进仓库**（本项目是单用户本地实例，没有凭据；将来有了也一样）。
- **不要提交 `web/playwright-report/`、`web/test-results/`**（已 gitignore）。
- **不要在用例之间共享状态**（全局变量、`test.describe.serial` 串行依赖）：
  复位保证了顺序无关，共享状态会把这一点毁掉。

## 并发

`playwright.config.ts` 里 `workers: 1`、`fullyParallel: false` —— 这是**故意**的：

所有用例共用**同一个数据库**（E2E 服务只有一个实例），而每个用例前都会复位一次。
并行跑的话，A 的复位会把 B 刚造的数据清掉，表现为「用例之间互相干扰」，而且是偶发的，
极难定位。要提速就加用例内的等待优化，或者给每个 worker 配一套独立的服务与库
（目前没做）。

### 不只是同一轮里：两轮同时跑也互相拆台

`workers: 1` 只管住**一轮**内部。**两轮跑**（两个人、两个 agent，或者一个 agent
同时跑两个 spec 文件）打同一个 5299 实例，同样会互相拆台 —— 复位是**全局破坏性**的
（恢复回收站 → 合并删光所有想法 → 删光所有项目 → 删光所有 webhook），
所以 A 的复位会把 B 正在造的数据整个抹掉。

这种干扰的报错长得很像应用坏了，其实不是：

| 报错 | 真实原因 |
|---|---|
| `POST /api/ideas/merge` → 404 `idea_not_found` | 合并源想法在「列出来」之后被对方**软删**了（`RequireExisting` 把回收站里的想法也算「不存在」，见 `server/IdeaApp/Application/IdeaService.cs`） |
| `POST /api/ideas/{id}/restore` → 404 `idea_not_found` | 回收站清单里的想法被对方**恢复后合并硬删**了，行真的没了 |
| `POST /api/projects/{id}/restore` → 400 `not_in_trash` | 回收站清单里的项目被对方**先恢复了** |
| 断言「列表是空的」却看到一堆数据 | 对方正在造的脏数据 |

**同一时刻只跑一轮**（`run-e2e.sh` 会复用 5299 上的服务，跑之前先看一眼有没有别人在跑）。
非要并行，就给每一轮配一套**独立的端口 + 独立的库**：

```bash
# 另一个端口 + 另一个库起一份实例（注意 IDEA_Database__Path 给绝对路径）
IDEA_Urls=http://127.0.0.1:5398 \
IDEA_Database__Path=/tmp/kanban-e2e-mine/e2e.db \
  dotnet run --project server/IdeaApp --no-build --no-launch-profile

# 再配一份 playwright 配置：baseURL 指自己的端口，testDir 指仓库里的 e2e 目录
# （use.baseURL / workers: 1 / fullyParallel: false 与仓库里那份一致）
```

这样两个 agent 各跑各的，互不干扰。

## 常见问题

| 现象 | 原因与处理 |
|---|---|
| `start-server.sh: 端口 5299 被占着，且 /api/health 不通` | 端口上有别的进程。`ss -ltnp "sport = :5299"` 看是谁；不是我们的服务就换一个端口（配置与脚本里的 `E2E_PORT` 一起改） |
| 页面白屏 | 前端产物没构建。`bash web/e2e/rebuild.sh frontend` |
| `browserType.launch: Executable doesn't exist` | 浏览器没装。本机 chromium-1248 已就位（`~/.cache/ms-playwright`）；装 `@playwright/test@1.64.1` 就是为了匹配它，别随意升版本 |
| 用例偶发失败、重跑就好 | 多半是断言没等状态。改成等状态（`toBeVisible` / `toHaveCount`），别加固定等待 |
| 回收站/标签断言对不上 | 复位清不掉这两处（见上）。用 `freshDatabase()` 或 `--fresh` 重跑 |
