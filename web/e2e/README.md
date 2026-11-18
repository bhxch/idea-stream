# E2E 测试底座（Playwright）

Idea Manager 的端到端测试底座。后端（.NET 10 + SQLite）与前端产物**同源**托管，
所以 E2E 只跑一个地址 —— 但**每一轮**跑自己的那个地址：
端口从 5300 起找空闲的，库也是每轮一个（`web/e2e/.tmp/run-<端口>/e2e.db`）。

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
    lib/common.sh          三个脚本共用的 shell 逻辑（端口分配、路径、健康探测、停服）
    .tmp/                  运行时产物：数据库、PID、日志、锁、端口预约（已 gitignore）
    .tmp/run-<端口>/       一轮的产物：e2e.db、server.log、server.pid
    .tmp/ports/            端口预约：<端口>.owner（占用者的 PID）
```

用例文件按分区加在 `web/e2e/` 下即可（`*.spec.ts`），例如
`web/e2e/projects.spec.ts`、`web/e2e/trash.spec.ts`。

## 端口与基址

**一轮 = 一个端口 + 一个库**。端口怎么来：

| 项 | 值 | 说明 |
|---|---|---|
| 端口 | 每轮一个，5300 起 | `E2E_PORT` 指定了就用它；没指定就自己找一个**空闲**的（探测用 python3）。**5080 被沙箱里的别的进程占着，5199 是 `server/scripts/aot-smoke.sh` 的端口，两个都不能抢** |
| baseURL | `http://127.0.0.1:<本轮端口>` | 配在 `playwright.config.ts` 的 `use.baseURL`，端口由配置自己挑好再写进 `process.env.E2E_PORT` |
| 数据库 | `web/e2e/.tmp/run-<端口>/e2e.db` | 每轮一个目录。与开发库、冒烟脚本的库完全分开，也与别的轮次分开 |
| 产物目录 | `web/test-results/run-<端口>/`、`web/playwright-report/run-<端口>/` | 每轮一份，两轮并发跑不会互相删产物 |
| 前端产物 | `server/IdeaApp/wwwroot/` | ⚠️ **共享的**，不是每轮一份。见「并发」一节 |

端口是「探测空闲」再「起服务」，中间隔着一次构建（几十秒）——
所以挑中的端口会**预约**一下（`web/e2e/.tmp/ports/<端口>.owner` 记着占用者的 PID），
两个并发轮次不会挑到同一个端口。

`use.baseURL` 已经设好，用例里直接 `page.goto('/projects')` 就行 —— 不要写绝对地址。

## 怎么跑

```bash
# 全量（推荐：先起服务，跑完停服务）
bash web/e2e/run-e2e.sh

# 全量 + 干净的库（删掉数据库重建）—— 排查「上一轮的数据影响这一轮」时用
bash web/e2e/run-e2e.sh --fresh

# 强制重建（前端 + 后端）并重启服务
bash web/e2e/run-e2e.sh --rebuild

# 跑完不停服务（留着给下一轮/手工排查用）
bash web/e2e/run-e2e.sh --keep

# 单个文件 / 单条用例：参数原样透传给 playwright test
bash web/e2e/run-e2e.sh e2e/smoke.spec.ts
bash web/e2e/run-e2e.sh --grep "回收站"
bash web/e2e/run-e2e.sh --headed e2e/projects.spec.ts

# 指定端口（默认自己挑一个空闲的）
E2E_PORT=5398 bash web/e2e/run-e2e.sh

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
| `bash web/e2e/start-server.sh [--fresh] [--rebuild] [--stop]` | 幂等启动服务。本轮端口上已有健康服务 → 直接退出 0 复用（不重建、不重启）；否则构建前端 → 构建后端 → 以 `IDEA_Urls=http://127.0.0.1:<端口>`、`IDEA_Database__Path=<仓库根>/web/e2e/.tmp/run-<端口>/e2e.db` 启动，轮询 `/api/health` 到 200。`--fresh` 先杀旧服务并删库，`--rebuild` 强制重建并重启，`--stop` 只停服务 |
| `bash web/e2e/run-e2e.sh [--fresh] [--rebuild] [--keep] [playwright 参数…]` | 保证服务在跑 → 在 `web/` 下跑 `npx playwright test` → 退出码透传。默认跑完停服务，`--keep` 不停 |
| `bash web/e2e/rebuild.sh [frontend\|backend\|all]` | 重建。`backend`（与 `all`）重建后自动重启服务（端口不变，库保留） |
| `bash web/e2e/lib/common.sh --reserve-port` | 挑一个空闲端口并预约，打印端口。`playwright.config.ts` 与 `run-e2e.sh` 走的是同一份逻辑 |

三个脚本都用 `flock`（`web/e2e/.tmp/e2e.lock`）串行化**构建**：多个阶段并发调用时，
构建产物不会互相覆盖。`start-server.sh` 拿到锁之后会**重新探测一次**健康状态再决定要不要起。

`start-server.sh` 默认**源码没变就不重建前端**（构建戳在 `web/e2e/.tmp/frontend-build.stamp`）：
两轮并发跑时，第二个轮次不会去清空第一个轮次正在用的 `wwwroot`（见「并发」一节）。
`--fresh` 与 `--rebuild` 一定重建 —— 这两个标志的语义就是「重建」。

`start-server.sh` 的 stdout **只有一行**：`E2E_SERVER=reused|started pid=...`
（`run-e2e.sh` 靠它判断「这次是不是我起的」）。别的信息一律走 stderr。

服务是 `setsid` 拉起来的 —— 脚本退出后它还活着；PID 记在
`web/e2e/.tmp/run-<端口>/server.pid`（另有 `server.wrapper.pid` 记 `dotnet run` 那个壳子）。

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

复位走 HTTP API，**不碰数据库文件**，做五件事（**顺序有讲究**）：

1. 删掉所有 webhook（全局的 + 项目级的）；
2. 把回收站里的条目**恢复**回来（回收站清空，所有项目与想法回到活动状态）；
3. 清掉所有活动想法：同一个项目里的想法用合并**硬删**（行真的没了），
   只留一条正文为 `e2e-reset` 的合并产物，清空标签后软删；
4. 删掉除收件箱外的项目；
5. `project.sort_mode` 复位成默认值 `recent`。

**webhook 一定要排在删项目前面**：项目一软删就不再出现在 `GET /api/projects` 里，
只顺着活动项目列表去查就漏掉了它们 —— 那些钩子一直启用着、越积越多，
每次复位都给死地址投递，把投递队列塞满（还会拖慢别的用例）。
所以删 webhook 时，活动项目与**回收站里的项目**都要查一遍
（`GET /api/webhooks?projectId=<已删项目id>` 仍然列得出来，
回收站里的项目从 `GET /api/trash` 拿，`kind` 是 `project`）。
把 webhook 放在最前面还有一层好处：后面的恢复/合并/删除都会发事件，
钩子先没了，那些事件就没有投递对象。

复位之后保证：

| 状态 | 结果 |
|---|---|
| 活动想法 | 0 条 ✅ |
| 活动项目 | 只有「收件箱」 ✅ |
| webhook | 0 个 ✅ |
| 设置 | `project.sort_mode = recent` ✅ |
| **回收站** | ⚠️ **有残渣**：每个有想法的项目留一条正文为 `e2e-reset` 的想法（项目被删时它挂在容器条目下） |
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
   - `aria-label="选择 <正文首行>"`（想法条目的复选框）、`aria-label="删除标签 <标签名>"`
   - `role="status"`（全局提示条 ToastHost）
3. **可见文字**：`page.getByText('想法二')` —— 用户看得见的文案。
4. **BEM class**（`page.locator('.bar__title')`）：**最后手段**。
   class 是实现细节，改样式就会红；只在没有别的办法时用，并在用例里写一句注释说明为什么。

不要写的东西：

- 绝对 URL（`page.goto('http://127.0.0.1:5300/')`）—— 用 `page.goto('/')`；
  端口每轮都可能是不同的，写死就错了；
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
- **不要自己起服务**：每轮的端口是底座挑的，`start-server.sh` 会复用。
  用例里要重启服务只能用 `freshDatabase()`。
- **不要往用例里塞 `web/e2e/.tmp/` 里的东西**：那是运行时产物，已 gitignore。
- **不要把凭据、token 写进仓库**（本项目是单用户本地实例，没有凭据；将来有了也一样）。
- **不要提交 `web/playwright-report/`、`web/test-results/`**（已 gitignore）。
- **不要在用例之间共享状态**（全局变量、`test.describe.serial` 串行依赖）：
  复位保证了顺序无关，共享状态会把这一点毁掉。

## 并发

**两轮之间已经隔离了**：一轮 = 一个端口 + 一个库 + 一份产物目录。

| 隔离的东西 | 落在哪 |
|---|---|
| 端口 | 每轮一个（`E2E_PORT` 可指定），5300 起 |
| 数据库 | `web/e2e/.tmp/run-<端口>/e2e.db` |
| 日志 / PID | `web/e2e/.tmp/run-<端口>/server.log` 等 |
| Playwright 产物 | `web/test-results/run-<端口>/`、`web/playwright-report/run-<端口>/` |

所以两个人（或者两个 agent）**同时**跑 E2E 是安全的：两轮的复位不会互相踩，
产物也不会互相删（以前两轮写同一个 `test-results/`，会删掉对方的 trace，
失败时留下的现场变成 `ENOENT ... traces/*.network`）。

```bash
# 两轮同时跑，各跑各的 spec —— 互不干扰
bash web/e2e/run-e2e.sh --keep e2e/ideas.spec.ts &
bash web/e2e/run-e2e.sh --keep e2e/projects.spec.ts &
wait
```

一轮**内部**仍然是 `workers: 1`、`fullyParallel: false` —— 这是**故意**的：
一轮里所有用例共用**同一个数据库**（E2E 服务只有一个实例），
而每个用例前都会复位一次。并行跑的话，A 的复位会把 B 刚造的数据清掉，
表现为「用例之间互相干扰」，而且是偶发的，极难定位。
要提速就加用例内的等待优化，或者给每个 worker 配一套独立的服务与库
（目前没做）。

### 端口是怎么挑的（以及为什么起服务失败不换端口）

端口是「探测空闲」再「起服务」，中间隔着一次构建（几十秒）——
不预约的话两个并发轮次会挑到同一个端口，后起的那轮绑定失败。
所以挑中的端口会**预约**一下：`web/e2e/.tmp/ports/<端口>.owner` 里记着占用者的 PID，
占用者进程一死，预约自动失效（崩了也不会留下永久占位）。

探测用 python3（仓库对 python3 是硬依赖，`verify.sh` 里缺了就直接失败）。

起服务失败时**不会自动换端口** —— 换了，配置里的 `baseURL` 就跟服务对不上了，
报错只会更难懂。而是把「端口被别人抢走了」写进日志（`server.log` 与 stderr），
让人知道该换个端口重跑（`E2E_PORT=<别的端口>`）。

### 仍然会互相影响的：前端产物

服务与数据库是每轮一份，但**前端产物是共享的**（`server/IdeaApp/wwwroot` ——
后端同源托管它）。`npm run build` 会先把这个目录**清空**再写新产物，
那个窗口里（实测约 30~100ms）页面会 404 / 白屏。

所以底座做了两件事：

- **同一时刻只跑一个 build**（脚本用 flock 把构建串行化了）；
- **源码没变就不重建**（构建戳在 `web/e2e/.tmp/frontend-build.stamp`：
  比它新的源码文件一个都没有就跳过）—— 于是并发的第二个轮次不会去清空
  第一个轮次正在用的 `wwwroot`。

要是**构建期间源码又变了**，那个白屏窗口还是会撞上（正在跑的那一轮可能白屏几秒）。
E2E 用例里有兜底重载（`navigation.spec.ts` 的 `loadUntilMounted`），
但不是 100% 兜得住；真撞上了就重跑那一轮。

### 报错签名对照表（同库并发才会出现）

新默认下不该再出现这些报错了 —— 留着这张表是因为：**一旦看到它，
就知道自己还在同库并发上跑**（比如手工起了两个实例打同一个库）。

| 报错 | 真实原因 |
|---|---|
| `POST /api/ideas/merge` → 404 `idea_not_found` | 合并源想法在「列出来」之后被对方**软删**了（`RequireExisting` 把回收站里的想法也算「不存在」，见 `server/IdeaApp/Application/IdeaService.cs`） |
| `POST /api/ideas/{id}/restore` → 404 `idea_not_found` | 回收站清单里的想法被对方**恢复后合并硬删**了，行真的没了 |
| `POST /api/projects/{id}/restore` → 400 `not_in_trash` | 回收站清单里的项目被对方**先恢复了** |
| `POST /api/ideas` → 404 `project_not_found` | 项目被对方软删了 |
| 断言「列表是空的」却看到一堆数据 | 对方正在造的脏数据 |
| `ENOENT ... traces/*.network` | 两轮写同一个 `test-results/`，互相删产物 |

## 常见问题

| 现象 | 原因与处理 |
|---|---|
| `start-server.sh: 端口 <端口> 被占着，且 /api/health 不通` | 端口上有别的进程（多半是手工 `E2E_PORT=...` 起的另一个实例）。`ss -ltnp "sport = :<端口>"` 看是谁；不是我们的服务就换个端口（`E2E_PORT=<别的端口>` 重跑） |
| `start-server.sh: 端口 <端口> 起不来 —— 很可能被别人抢走了` | 端口是「探测空闲」之后才起服务的，中间隔着一次构建，那段时间里被别人抢了。看 `web/e2e/.tmp/run-<端口>/server.log` 里的绑定错误；换个端口重跑 |
| `e2e: 100 个端口（5300 起）都被占了` | 之前的轮次留下的服务还在跑。`ss -ltnp | grep 5[3-9][0-9][0-9]` 找出来停掉（`bash web/e2e/start-server.sh --stop`），或者清掉 `web/e2e/.tmp/ports/` 里的过期预约 |
| 页面白屏 | 前端产物没构建。`bash web/e2e/rebuild.sh frontend` |
| `browserType.launch: Executable doesn't exist` | 浏览器没装。本机 chromium-1248 已就位（`~/.cache/ms-playwright`）；装 `@playwright/test@1.64.1` 就是为了匹配它，别随意升版本 |
| 用例偶发失败、重跑就好 | 多半是断言没等状态。改成等状态（`toBeVisible` / `toHaveCount`），别加固定等待 |
| 回收站/标签断言对不上 | 复位清不掉这两处（见上）。用 `freshDatabase()` 或 `--fresh` 重跑 |
