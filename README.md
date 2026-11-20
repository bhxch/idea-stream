# Idea Manager

个人自用的 idea 记事本：单用户、单实例。浏览器与 agent 共用同一套 REST API，
应用只监听回环地址，对外一律走 nginx 反代（TLS 与访问控制都在 nginx 层）。

- **后端**：.NET 10 + SQLite + Native AOT —— 手写 SQL、无 ORM、无反射式序列化、无模板引擎。
- **前端**：Vue 3 + TypeScript + Vite，构建产物直接落进后端的 `wwwroot/`，由后端同源托管。
- **agent 接入**：REST API + SSE，接口契约是提交进仓库的 OpenAPI 3.1；
  agent skill 的说明书**由服务端分发**，永远与接口同一版本。

[English](README_EN.md)

本文档一律中文。

**按目的找**

| 你要做什么 | 看哪 |
|---|---|
| 先把它跑起来 | [快速开始](#快速开始) |
| 知道这系统能做什么 | [能力全景](#能力全景) |
| 改后端 | [构建](#构建)、[配置项](#配置项)、[测试](#测试) |
| 改前端 | [`web/README.md`](web/README.md)、[`web/e2e/README.md`](web/e2e/README.md) |
| 用 API / 写 agent | [OpenAPI 与 API 用法](#openapi-与-api-用法)、[Agent skill](#agent-skill) |
| 上线部署 | [部署](#部署) |
| 提交前过闸门 | [CI 与闸门](#ci-与闸门) |
| 知道哪里有坑 | [已知限制与坑](#已知限制与坑) |
| 知道为什么这么做 | [设计取舍](#设计取舍) |

---

## 快速开始

从零到能用，四步。**每一步在哪个目录跑都写清楚了**，照抄即可。

```bash
# ① 构建前端（产物落 server/IdeaApp/wwwroot/）—— 仓库根
cd web && npm ci && npm run build && cd ..
```

```bash
# ② 发布 Native AOT 产物 —— 仓库根
dotnet publish server/IdeaApp/IdeaApp.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

```bash
# ③ 起服务 —— 仓库根（产物目录）
cd server/IdeaApp/bin/Release/net10.0/linux-x64/publish && ./IdeaApp
```

```bash
# ④ 装 agent skill —— 仓库根（默认装到 ~/.zcode/skills/idea-manager）
bash skills/install-agent-skill.sh
```

起服务后：网页在 <http://127.0.0.1:5080>，接口在 `/api/**`，契约在 `/openapi/v1.json`。
首次启动自动建库建表，不需要迁移步骤。

> ⚠️ **第 ① 步不能省**：`server/IdeaApp/wwwroot/` 被 `.gitignore` 排除，仓库里没有它。
> 忘了构建就发布，拿到的是一个**空 wwwroot** —— 应用照常起、`/api/*` 照常工作、
> 页面全白，而且**没有任何报错**（静态文件中间件只是找不到文件）。
> 发布后用 `ls server/IdeaApp/bin/Release/net10.0/linux-x64/publish/wwwroot`
> 确认 `index.html` 与 `assets/` 都在。

> ⚠️ **第 ② 步依赖 .NET SDK 10.0.113 与 clang**：换成别的 SDK 版本，
> `restore` 可能直接失败（见[已知限制与坑](#已知限制与坑)）。Linux 上发布 Native AOT
> 还需要构建依赖 `clang` 与 `zlib1g-dev`。

---

## 能力全景

### 后端能力

单一 SQLite 库，手写 SQL，服务端一套 REST + 一条 SSE 流。下面按资源分组，
每条写清「能做什么 + 怎么用」；逐端点的参数与 schema 在
[OpenAPI 与 API 用法](#openapi-与-api-用法) 与手册的 `references/api.md` 里。

**想法**

| 能做什么 | 怎么用 |
|---|---|
| 列想法（按项目 / 状态 / 标签 / 关键词筛，可分页） | `GET /api/ideas?projectId=&status=all\|open\|done&tags=a,b&tagMode=or\|and&q=&limit=&offset=` |
| 记一条想法（可带标签） | `POST /api/ideas`，体 `{"projectId":"","content":"…","tags":["a"]}` → 201 |
| 改正文（只发要改的字段） | `PATCH /api/ideas/{id}`，体 `{"content":"新正文"}` |
| 覆盖式设置标签（**不给 `tags` = 清空**） | `PUT /api/ideas/{id}/tags`，体 `{"tags":["咖啡"]}` |
| 标记完成 / 取消完成（`done` **必填**） | `POST /api/ideas/{id}/complete`，体 `{"done":true}` |
| 删一条想法（**软删**，进回收站） | `DELETE /api/ideas/{id}` → 204 |
| 从回收站恢复想法 | `POST /api/ideas/{id}/restore` |
| 批量标记完成 | `POST /api/ideas/bulk/complete`，体 `{"ids":[…],"done":true}` |
| 批量加标签（并集追加；`replace:true` 才是覆盖式） | `POST /api/ideas/bulk/tags`，体 `{"ids":[…],"tags":["a"]}` |
| 批量删除（**软删**） | `POST /api/ideas/bulk/delete`，体 `{"ids":[…]}` → 200 + 数组 |
| 合并多条想法成一条（**源想法硬删**） | `POST /api/ideas/merge`，体 `{"ids":[…, …],"content":"可选"}` |
| 全库搜索（想法 / 项目 / 标签三组，各带分页） | `GET /api/search?q=关键词&limit=&offset=&includeTrash=` |

**项目与设置**

| 能做什么 | 怎么用 |
|---|---|
| 列项目 | `GET /api/projects` |
| 新建项目 | `POST /api/projects`，体 `{"name":"咖啡"}` |
| 改项目名（`name` **必填**） | `PATCH /api/projects/{id}` |
| 删项目（**连带项目下未删想法一起进回收站**） | `DELETE /api/projects/{id}` → 204 |
| 从回收站恢复项目（连带恢复「随项目一起删」的想法） | `POST /api/projects/{id}/restore` |
| 重排项目顺序（未列出的接在后面） | `POST /api/projects/reorder`，体 `{"ids":["id1","id2"]}` |
| 读设置 | `GET /api/settings` → `{"project.sort_mode":"recent"}` |
| 改设置（**只有 `project.sort_mode` 可写**） | `PUT /api/settings/project.sort_mode`，体 `{"value":"recent\|manual"}` |
| 标签字典（含使用次数） | `GET /api/tags` |
| 标签补全建议 | `GET /api/tags/suggest?projectId=&prefix=` |

**回收站**

| 能做什么 | 怎么用 |
|---|---|
| 回收站内容（已删项目 = 容器，顶层是单独删的想法） | `GET /api/trash` |
| 到期自动硬删 | 只从配置读 `Trash:RetentionDays`（默认 30 天），改完重启生效；**没有手动彻底删除入口** |

**实时事件（SSE）**

| 能做什么 | 怎么用 |
|---|---|
| 订阅全量事件流 | `GET /api/events`（`Accept: text/event-stream`） |
| 断线补发 | 重连时带 `Last-Event-ID` 头（或 `?lastEventId=`） |
| 补不到时让客户端重拉 | 服务端发 `event: resync` + `data: {}`（控制帧，不带 id） |

事件类型共 8 种：`idea.created` / `idea.updated` / `idea.deleted` / `idea.restored` / `idea.merged` /
`project.created` / `project.updated` / `project.deleted`。

**Webhook（事件外发）**

| 能做什么 | 怎么用 |
|---|---|
| 增删改查 webhook（目标地址、方法、请求头、body 模板、订阅事件、作用域、密钥） | `GET/POST /api/webhooks`、`GET/PATCH/DELETE /api/webhooks/{id}` |
| Telegram 模板预设 | `GET /api/webhooks/templates/telegram` |
| 投递记录 | `GET /api/webhooks/{id}/deliveries` |
| 手动重投一条投递 | `POST /api/webhooks/{id}/deliveries/{deliveryId}/retry` |

新建时：`scope` 只能是 `global` 或 `project`（`global` 不能带 `projectId`，
`project` 必须带一个存在的项目 id）；**`bodyTemplate` 必填且必须是 JSON 对象**
（占位符只能写在字符串值里）；全局 webhook **创建时默认禁用**、项目级默认启用。
投递是异步的：失败按 1s / 5s / 30s 退避重试，总尝试次数含首次（默认 4 次）。

**运维**

| 能做什么 | 怎么用 |
|---|---|
| 健康检查 | `GET /api/health` → `{"status":"ok"}` |
| 取接口契约 | `GET /openapi/v1.json` |
| 取 agent 说明书 | `GET /skill/idea-manager/version`、`GET /skill/idea-manager/{path}` |

### 前端页面与交互

Vue 3 + TypeScript + Vite，无 UI 框架、无状态库（模块级 `ref` 单例，不是 Pinia）。
详细说明见 [`web/README.md`](web/README.md)，功能点 × 用例对照见
[`web/e2e/COVERAGE.md`](web/e2e/COVERAGE.md)。

**路由表**（history 模式，未知地址在前端层回首页）

| 路径 | 页面 | 能做什么 |
|---|---|---|
| `/` | HomePage | 记想法、编辑、标完成、删、加标签、过滤、批量操作、合并、SSE 实时同步 |
| `/projects` | ProjectsPage | 新建 / 改名 / 删项目，改排序方式（最近活动 / 手动排序），进项目级 Webhook |
| `/trash` | TrashPage | 看回收站（容器可展开）、恢复项目或想法 |
| `/webhooks` | WebhooksPage | 全局 webhook 的新建 / 编辑 / 启停 / 订阅事件 / 投递记录与重投 |
| `/projects/:id/webhooks` | ProjectWebhooksPage | 项目级 webhook，同上但作用域限该项目 |

**主页形态**（断点 1024px / 768px 两套并存）

| 宽度 | 形态 |
|---|---|
| ≥1024px | 左侧 220px 项目侧栏 + 右侧铺满的想法流（两列交错）；顶栏 / 列表 / 输入框三段，只有列表滚动 |
| <1024px | 侧栏收成顶栏项目下拉，过滤行随内容滚走，顶栏多一个 🔍 聚焦搜索框 |
| <768px | 列表单列 |

**主页交互**

| 能做什么 | 怎么用 |
|---|---|
| 记一条想法 | 底部输入框 Enter 提交、Shift+Enter 换行；**成功才清空并把焦点放回，失败保留草稿** |
| 改正文 | 点正文进编辑态 → Ctrl+Enter 保存、Esc 取消、失焦自动保存；内容没改不发请求 |
| 完成 / 删除 | 点卡片 meta 行的 ○/● 切换完成；🗑 删除有二次确认，删后进回收站 |
| 编辑标签 | 点卡片右下 🏷 → 回车 / 空格 / 逗号加标签，上下键选候选，× 删；每次改动立刻落库 |
| 过滤 | 顶部过滤条：状态三态、关键词、标签多选 + AND/OR 切换；**过滤在服务端做**，一次请求拿最终结果 |
| 批量操作 | 勾选卡片左侧复选框 → 操作条：标记完成 / 撤销完成 / 加标签（并集）/ 合并 / 删除 / 取消选择 |
| 合并 | 批量条「合并」→ 对话框预览各条正文，默认空行拼接、可改；确认后源条目硬删、生成新条目 |
| 深链定位 | `/?idea=<想法 id>` 打开主页会滚到那条并闪一下高亮 |
| 往回翻历史 | 向上滚，距顶部 400px 自动预加载更旧的一页，prepend 后视口钉回原处；**底部才是「当下」** |
| 实时同步 | 页面自动订阅 SSE；断线指数退避重连（2s → 30s）；收到 `resync` 就整表重拉 |
| 新内容提示 | 贴底时自动跟随；用户上翻时不抢滚动，浮出「↓ 有 N 条新内容」按钮 |

其它页面：项目管理页可新建 / 改名 / 删除（二次确认），回收站页可恢复，
Webhook 页可管理全局与项目级两套。所有失败统一变成右下角提示条（4 秒后消失）。

### Agent skill 接入

agent 侧的说明书**由服务端分发** —— 服务端改了 API，agent 取到的说明书跟着改，
**不用重新装 skill**（薄壳一个文件都不用动）。

三步链路：

```
装薄壳（2 个文件） → 取说明书（fetch.py） → 按手册调 API（curl）
```

- **薄壳**：`skills/idea-manager-client/` —— 一个指针 `SKILL.md` + 一个取件器 `scripts/fetch.py`。
- **内容源**：`skills/idea-manager/` —— 索引页 + `references/` 五份细节，随产物一起分发。
- **分发端点**：`GET /skill/idea-manager/version`（清单：版本 + 文件清单 + 逐个 sha256）、
  `GET /skill/idea-manager/{path}`（文件正文）。

装薄壳、取说明书、调 API 的完整步骤见 [Agent skill](#agent-skill)。

---

## 目录结构

```
IdeaApp.sln                 解决方案
Directory.Build.props       全局编译属性（TreatWarningsAsErrors 等）
verify.sh                   一条命令跑完七道闸门（见「CI 与闸门」）
openapi/idea-v1.json        OpenAPI 3.1 契约快照（提交进仓库）
docs/
  nginx/idea.conf           nginx 反代配置
  superpowers/
    specs/                  四份设计规格（见「设计取舍」）
    plans/                  实施计划
    reviews/                界面复核结论
.github/workflows/ci.yml    GitHub Actions（5 个 job）
server/
  IdeaApp/                  后端应用（.NET 10，Native AOT）
    Domain/                 领域模型、MonotonicGuid（UUIDv7）、分词器
    Application/            用例服务层（项目/想法/标签/回收站/webhook/投递/搜索）
    Infrastructure/         SQL、事件总线、SSE、webhook 投递、OpenAPI 导出、schema.sql
    Contracts/              DTO、请求体、JSON 源生成上下文、错误码清单
    Endpoints/              各资源的端点映射（MapXxx 扩展方法）
    Program.cs              组合根：注册、端点映射、静态文件与 SPA 兜底
    appsettings.json        默认配置
    wwwroot/                前端构建产物的落点（被 .gitignore 排除，不进仓库）
  IdeaApp.Tests/            集成测试（WebApplicationFactory + 真实 SQLite）
  scripts/aot-smoke.sh      AOT 产物冒烟脚本（含跨栈检查）
tools/ExportOpenApi/        导出 OpenAPI 文档的小工具
web/                        前端（Vue 3 + TypeScript + Vite）
  src/pages/                路由级页面
  src/components/           可复用组件
  src/composables/          可复用的组合式函数
  src/stores/               全局状态
  src/api/                  后端调用层（REST + SSE）
  src/lib/                  纯函数工具
  src/styles/               全局样式
  e2e/                      Playwright 端到端用例与底座
  playwright.config.ts      Playwright 配置（端口、baseURL、webServer）
skills/
  idea-manager/             skill 内容源（索引页 + references/，服务端分发）
  idea-manager-client/      agent 侧安装的薄壳（指针 + 取件器）
  install-agent-skill.sh    把薄壳装到 agent 的 skills 目录
```

---

## 构建

### 前端

```bash
cd web
npm ci                 # 首次
npm run build          # 产物输出到 server/IdeaApp/wwwroot/
```

其余脚本（都在 `web/` 目录跑）：

| 命令 | 作用 |
|---|---|
| `npm run dev` | Vite dev server（`/api` 由 proxy 转发到 `http://127.0.0.1:5080`，**要先起后端**） |
| `npm run preview` | 用 Vite 的静态服务器预览构建产物 |
| `npm run typecheck` | `vue-tsc --noEmit`，跑**两份** tsconfig：应用 + e2e |
| `npm test` | Vitest（jsdom 环境） |
| `npm run e2e` | Playwright 端到端（`playwright test`；服务由 `playwright.config.ts` 的 `webServer` 拉起，跑完收摊） |
| `npm run e2e:report` | 打开 Playwright 报告 |

前端用 history 模式（`/`、`/projects`、`/trash`、`/webhooks`、`/projects/:id/webhooks`
都是真实路径），后端用 `MapFallbackToFile("index.html")` 兜底未匹配的 GET ——
这几行接线在 `server/IdeaApp/Program.cs` 里：`UseStaticFiles` + `MapFallbackToFile`，
另外还有**一行 provider 覆盖**（`WebRootFileProvider`）——
`wwwroot` 被 gitignore 排除，新克隆的仓库里它不存在，光建目录不够，
得显式指一个 `PhysicalFileProvider`，否则静态文件中间件会拿到查不到任何文件的 provider。

> ⚠️ **SPA 兜底是 catch-all，会兜住所有没匹配到端点的 GET** ——
> 所以 `/api/**` 必须由**更早**的一层兜底先接走（`app.Map("/api/{**rest}", …)`，
> 路由的字面量段 `api` 优先于 catch-all 模板），否则路径打错会变成
> **200 + HTML**：这个 API 是给 agent 读的（契约在 `openapi/idea-v1.json`），
> agent 只按状态码判断的话会把「路径打错」当成成功。

### 后端

```bash
# 开发（仓库根）
dotnet build IdeaApp.sln

# 测试（仓库根）
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj

# Native AOT 发布（自包含单文件）—— 仓库根
dotnet publish server/IdeaApp/IdeaApp.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

产物在 `server/IdeaApp/bin/Release/net10.0/linux-x64/publish/`：

| 文件 | 必须一起拷？ | 说明 |
|---|---|---|
| `IdeaApp` | 是 | 可执行文件（原生 ELF） |
| `appsettings.json` | 是 | 配置（默认 Urls / 库路径 / 日志级别）—— **漏了会回落成默认端口与默认库路径** |
| `libe_sqlite3.so` | 是 | SQLite 原生库（动态链接）—— **漏了 SQLite 加载失败** |
| `wwwroot/` | 是 | 前端产物 —— **漏了页面全白且不报错** |
| `skill/idea-manager/` | 想用 agent skill 才必须 | skill 分发副本 —— **漏了取说明书回 404 `skill_not_distributed`** |
| `IdeaApp.dbg` | 否 | 调试符号 |

> ⚠️ **publish 前必须先 `cd web && npm run build`** ——
> `wwwroot/` 被 gitignore 排除，仓库里没有它。忘了这一步就会发布一个
> **空的 wwwroot**：应用照常起、`/api/*` 照常工作，但页面是白屏，
> 而且**没有任何报错**。

> ⚠️ **必须显式给 RID**（`-r linux-x64`）：不带 RID 的发布在 csproj 的
> `RuntimeFrameworkVersion` 条件下会 restore 失败（NU1101）。

AOT 有自己的坑：JSON 一律走源生成上下文（`IdeaJsonContext`）。
加 DTO 必须在 `IdeaJsonContext` 里登记 —— **漏登记在开发期就抛
`NoMetadataForType`**（源生成上下文是直接赋值给 `TypeInfoResolver` 的，
不是挂进解析链），测试阶段就能发现。
但 AOT 还有另一类退化（反射、裁剪）只有真产物才暴露，所以改完端点要跑一遍 AOT 冒烟：

```bash
bash server/scripts/aot-smoke.sh
```

它发布一份真 AOT 产物、打一遍端点，再用仓库里的**真取件器**跟它对一次面
（跨栈检查：逐文件核对 sha256）。缺 python3 是**失败**不是跳过。

### 前置条件

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | **10.0.113** | csproj 把 ILCompiler 显式提到 10.0.14，避开这个 SDK 内置的 10.0.13 命中的 advisory（GHSA-p3jp-9v83-76w9）；`TreatWarningsAsErrors` 会把 NU1903 升级成错误 → restore 直接失败。换 SDK 版本的结论要重验 |
| Node / npm | Node **24** | 与 CI 一致 |
| Python | **3.9+** | 取件器只用标准库，无第三方依赖 |
| clang + zlib1g-dev | 任意近期版本 | Linux 上发布 Native AOT 的构建依赖（`apt-get install -y clang zlib1g-dev`） |

---

## 运行

### 开发

两件事：**先后端，后前端**。

```bash
# ① 后端 —— 仓库根
IDEA_Database__Path="$PWD/server/IdeaApp/data/idea.db" \
  dotnet run --project server/IdeaApp
```

```bash
# ② 前端 —— web/ 目录
cd web
npm run dev
```

后端默认监听 `http://127.0.0.1:5080`（`appsettings.json` 的 `Urls`），
前端 dev server 在 `http://127.0.0.1:5173`，
`/api` 由 Vite proxy 转发到 `http://127.0.0.1:5080` ——
**后端没起，前端所有数据请求都会失败**。

### 生产（AOT 产物）

```bash
cd server/IdeaApp/bin/Release/net10.0/linux-x64/publish

IDEA_Urls=http://127.0.0.1:5080 \
IDEA_Trash__RetentionDays=30 \
IDEA_PublicBaseUrl=https://ideas.example.com \
./IdeaApp
```

**首次启动自动建库建表**并写入内置收件箱（`inbox`），不需要额外的迁移步骤。
数据库默认在 `data/idea.db` —— 相对路径以**应用目录**（可执行文件所在目录）为基准，
**与当前工作目录无关**（见下面的[应用目录是哪个目录](#应用目录是哪个目录)）。

应用同时把前端页面发在 `/` 上，`/projects`、`/trash`、`/webhooks` 这类前端路由
由 `MapFallbackToFile("index.html")` 兜底，所以深链直接访问也是 200，不会 404。

`/api/**` 不走那个兜底：未匹配的 API 路径回 **404 + ProblemDetails**
（`code=api_not_found`），不是 200 + HTML —— 这个 API 是给 agent 读的。

### 配置项

配置写在 `server/IdeaApp/appsettings.json`，用环境变量覆盖：**前缀 `IDEA_`，双下划线表示层级**
（`IDEA_Trash__RetentionDays` 对应 `Trash:RetentionDays`）。

不带前缀的环境变量也认（`Urls=`、`Trash__RetentionDays=` 仍然生效）；两套并存，
**带 `IDEA_` 前缀的优先**（后注册的 provider 赢）。

> ⚠️ **`IDEA_` 前缀的优先级比命令行参数高** —— 与 ASP.NET Core 的常规约定**相反**
> （常规是命令行参数压过环境变量）。原因是 `IDEA_` 这套 provider 注册得**更晚**，
> 而配置源按注册顺序决胜，后注册的赢。
> 实测：`IDEA_Urls=http://127.0.0.1:5192 ./IdeaApp --urls http://127.0.0.1:5193`
> 监听的是 **5192**（`IDEA_` 的那个），5193 不通。

| 键 | 默认 | 说明 |
|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | Kestrel 监听地址（只绑回环，对外走 nginx） |
| `Database:Path` | `data/idea.db` | SQLite 文件路径。**相对路径以应用目录为基准，与当前工作目录无关** |
| `Trash:RetentionDays` | `30` | 回收站保留天数。**页面与 API 都不可改**；这是唯一会导致不可逆删除的开关，配成 ≤0 按 1 天处理并告警 |
| `PublicBaseUrl` | 空 | 用于生成 `{{idea.url}}`，如 `https://ideas.example.com` |
| `Api:MaxIdeasPerResponse` | `2000` | **不给 `limit` 时**的命中数上限，超了报 400 `too_many_ideas`；给了 `limit` 时上限是 **1000**（写死在服务层），且 `offset` 必须与 `limit` 同给 |
| `Webhook:TimeoutSeconds` | `10` | 单次投递超时 |
| `Webhook:MaxAttempts` | `4` | 最多投递尝试次数（**含首次尝试**）→ 首次 + 3 次重试 |
| `Webhook:MaxDeliveryRecords` | `500` | 每个 webhook 保留的投递记录条数 |
| `Sse:BufferSize` | `500` | 服务端事件缓冲条数（供 `Last-Event-ID` 补发）。**只在内存，重启必然补不到** |
| `Sse:HeartbeatSeconds` | `25` | SSE 心跳间隔（发 `: ping` 注释帧，防中间代理掐断空闲连接） |
| `Skill:Path` | `skill/idea-manager` | skill 分发副本的根目录。**相对路径以应用目录为基准，与当前工作目录无关**（与 `Database:Path` 同一条约定） |

另有一个只认环境变量的开关：`IDEA_EXPORT_OPENAPI=<路径>` —— 设了它，应用启动后会把
OpenAPI 文档写到该文件（见[OpenAPI 与 API 用法](#openapi-与-api-用法)）。

#### 应用目录是哪个目录

`Database:Path` 与 `Skill:Path` 的相对路径锚在**应用目录**上，也就是可执行文件所在目录
（代码里是 `AppContext.BaseDirectory`）：

| 跑法 | 应用目录 | `data/idea.db` 落在 |
|---|---|---|
| AOT 单文件发布（`/opt/idea/IdeaApp`） | `/opt/idea/` | `/opt/idea/data/idea.db` |
| `dotnet run`（开发） | `server/IdeaApp/bin/Debug/net10.0/` | `server/IdeaApp/bin/Debug/net10.0/data/idea.db` |

**不能按当前工作目录解析**：用 systemd 跑且没设 `WorkingDirectory` 时 CWD 是 `/`，
库文件会落到 `/data/idea.db` —— 不可预期、可能没权限，也可能误伤别的数据。
开发时想让库文件落在项目目录，显式给绝对路径即可（见[运行 · 开发](#开发)）。

### 数据与存储

- **库**：单个 SQLite 文件，手写 SQL。schema 内嵌成资源
  （`server/IdeaApp/Infrastructure/schema.sql`），首次启动建库建表并写入内置收件箱 `inbox`。
- **全文索引**：与业务写在**同一个事务**里，强一致。老库按 `search_meta` 版本号在启动时整体回填一次
  —— **回填失败直接让启动失败**：索引与数据不一致时搜索给的是**错结果**，那比起不来危险得多。
- **id**：自实现的 RFC 9562 UUIDv7（32 位无连字符小写 hex）。.NET 内置的
  `Guid.CreateVersion7()` 同毫秒内不单调，所以自己实现了计数器法。
- **没有「标题」**：想法就是一段文字。库里无该列、接口无该字段、模板无 `{{idea.title}}`。
  老客户端仍发 `title` 会被 System.Text.Json **静默忽略**（不报错）。
- **排序**：列表恒 `updated_at ASC, id ASC`（最老的在顶）；同刻插入靠 id 定序。

---

## 部署

应用只监听回环地址，对外一律走 nginx（TLS、访问控制都在 nginx 层，
应用本身不做鉴权、也不做 Origin/CSRF 校验）。

```bash
cp docs/nginx/idea.conf /etc/nginx/conf.d/idea.conf
# 按自己的域名改 server_name、证书路径、htpasswd 路径
htpasswd -c /etc/nginx/.htpasswd ideauser
nginx -t && systemctl reload nginx
```

> ⚠️ **配置里的 `http2 on;` 需要 nginx ≥ 1.25.1** —— 旧版 nginx 在 `nginx -t`
> 阶段就报错，照抄配置会直接卡住。

`docs/nginx/idea.conf` 里两处要留意：

- **SSE（`/api/events`）单独一段**：`proxy_buffering off` + `X-Accel-Buffering: no`，
  否则实时推送会被 nginx 攒着不发。
- **请求体上限 `client_max_body_size 4m`**（4 MiB = 4 194 304 字节，nginx 的 `m` 是 1024 进位），
  比 Kestrel 的默认上限（30 000 000 字节）先撞上 —— 走 nginx 的请求根本到不了 Kestrel。
  撞上时 413 是 **nginx 自己回的**，响应体是结构化 JSON（ProblemDetails 形状），
  但**刻意不带 `code`**（直连 Kestrel 时是 413 + 空响应体）。
  **改这个数字要同步改手册的 `references/errors.md`**，两边说的是同一件事。

发布产物里带着 skill 的分发副本（`skill/idea-manager/`）—— agent 的说明书由它提供，
部署时跟应用一起拷过去。少了它，取说明书的那个端点会回 404 `skill_not_distributed`
（副本在哪儿由 `Skill:Path` 决定）；**路径写错是另外两个码**：skill 名字拼错 →
`skill_endpoint_not_found`，文件名拼错 → `skill_file_not_found`，排查方向不一样。

应用侧接了 `UseForwardedHeaders()`：`X-Forwarded-Proto` / `X-Forwarded-For` /
`X-Forwarded-Host` 会被采信，**但只信回环地址来的代理**。nginx 与应用同机时直接可用；
nginx 在别的机器上要在代码里显式加 `KnownProxies`，否则这些头不生效
（应用会以为自己还在 http 上）。注意 .NET 10 的 `ForwardedHeadersOptions` 默认
`ForwardedHeaders.None` —— 不显式点名要处理哪些头，中间件等于没挂。

---

## CI 与闸门

### `verify.sh` —— 一条命令跑完七道闸门

仓库根：

```bash
bash verify.sh
```

脚本自己 cd 到所在目录。**七步全部跑完**才汇总，不是首错即停；任一步失败整体退出码非 0。

| 步 | 内容 |
|---|---|
| 1 | `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj` —— 服务端（含 skill 分发端点、契约快照、文档漂移守卫） |
| 2 | `python3 -m unittest discover -s skills/idea-manager-client/tests` —— 取件器单测 |
| 3 | `bash server/scripts/aot-smoke.sh` —— 真 AOT 产物 + **跨栈**检查（真产物 ↔ 真取件器） |
| 4 | `bash skills/idea-manager-client/scripts/run-tests.sh` —— 取件器测试脚本（带一条真 HTTP 服务端的冒烟） |
| 5 | `npm --prefix web run typecheck` —— 前端类型检查（应用 + e2e 两份 tsconfig） |
| 6 | `npm --prefix web test` —— 前端单测（vitest） |
| 7 | `bash web/e2e/run-e2e.sh` —— 端到端用例（Playwright） |

> ⚠️ **缺前置条件是「失败」，不是「跳过」**：脚本会先检查 python3、node、npm 在不在 PATH，
> 以及 `web/node_modules` 在不在，缺任一项直接退出 1。
> 跳过等于没跑 —— 一道没跑的闸门比没有闸门更危险（它看起来是绿的）。
> 缺前端依赖就先 `cd web && npm ci`。

### GitHub Actions

`.github/workflows/ci.yml` 在 push 与 pull_request 上触发，拆成 **5 个独立 job**：

| job | 跑什么 |
|---|---|
| backend | `dotnet test` |
| frontend | `npm ci` → `typecheck` → `vitest` → `build` |
| e2e | 装 Playwright 的 chromium → 预热后端构建 → `npm run e2e` |
| client | 取件器的 python 单测 + 取件器测试脚本 |
| aot | 装 `clang`/`zlib1g-dev` → 构建前端 → `bash server/scripts/aot-smoke.sh` |

版本钉死：.NET SDK **10.0.113**、Node **24**（与本地一致）。
同 ref 的旧运行自动取消；`permissions: contents: read`，CI 不写回仓库。

> ⚠️ **CI 不跑 `verify.sh`** —— 它是把那几条命令**逐条对应**到 5 个 job 上，
> 这样后端挂了不用等前端跑完，required check 也能各自标红。
> 改了 `verify.sh` 的步骤要同步改 CI，反之亦然。

---

## OpenAPI 与 API 用法

### 契约在哪

- **契约文件**：[`openapi/idea-v1.json`](openapi/idea-v1.json)（OpenAPI 3.1，提交进仓库）。
  它是构建产物快照：路径、参数、请求体与响应体 schema、状态码都在里面。
  文件里没有 `servers` 段 —— 地址是运行期的事，对外地址由 nginx 决定，契约只管接口本身。
- **运行时文档**：应用启动后访问 `/openapi/v1.json`，内容与契约文件一致 ——
  agent 手上拿的是这一份（手册指向的就是它）。
- **skill 分发副本里没有第二份契约**：要契约就取 `/openapi/v1.json`。

**重新导出**（改了端点、DTO 或状态码之后要重跑）：

```bash
# 仓库根
dotnet run --project tools/ExportOpenApi            # 写到 openapi/idea-v1.json
dotnet run --project tools/ExportOpenApi -- <路径>    # 写到指定路径
```

工具在进程里起**应用本体**（`WebApplicationFactory`），所以导出的文档与应用实际暴露的端点必然一致。
它默认写的是**仓库里已跟踪的文件**，重导后要一并提交。

**另一条导出路径**：在真实应用进程里导出，产物与上面一致：

```bash
IDEA_EXPORT_OPENAPI=/tmp/idea-v1.json ./IdeaApp
```

### 错误怎么读

**业务错误**的 4xx / 5xx 一律是 RFC 9457 的 ProblemDetails
（内容类型 `application/problem+json`），带机器可读的扩展字段 `code`：

```json
{ "type": "about:blank", "title": "…", "status": 400,
  "detail": "…", "code": "invalid_pagination" }
```

- **按 `code` 分支，不要去匹配 `detail` 的文字**（detail 是给人看的，会改）。
- 每个错误响应的描述里列了**这个端点**可能出现的 `code`；
  `components.schemas.ProblemDetails` 的描述里是全部 `code` 的取值全集。
- 权威清单在 `server/IdeaApp/Contracts/ApiErrorCodes.cs`，
  守卫测试会核对源码里实际抛出的码（漏登记就测试失败）。
- 业务错误码分四类：400（请求不合法）、404（东西不存在）、415（媒体类型不对）、503（服务暂时接不住），
  另有 500 `internal_error`。

> ⚠️ **两类例外没有 `code`，别按 `code` 分支去读**：
>
> | 情形 | 响应 |
> |---|---|
> | 查询串里的 `bool` 参数非法（如 `?includeTrash=xyz`） | 400，**空 body、没有 `code`** —— 绑定失败发生在路由层，绕过了只处理 `BadHttpRequestException` 的统一出口 |
> | 413 请求体太大 | 状态码 413，**没有 `code`**（经 nginx 是结构化 JSON 但刻意不带 `code`；直连 Kestrel 是空 body） |
>
> 这两种按**状态码**分支。手册的 `references/errors.md` 里写的就是这个口径。

### 调用姿势

agent 直接用 curl 调 API，不经别的封装：

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

- 用 `--fail-with-body`，**不要加 `-f`**（`-f` 会把错误 body 吞掉）。
- 样板里的 `-u` **只在有凭据时保留**；`IDEA_TOKEN` 有值时再加 `-H "X-Api-Key: $IDEA_TOKEN"`。
- **别把凭据写进 URL** —— nginx 会把 userinfo 原样写进访问日志。
- 带请求体就要带 `Content-Type: application/json`（不带是 415）。
- **请求体别用 shell 拼字符串** —— 引号、换行、反斜杠会被 shell 吃掉。写进文件，
  或用带引号的 heredoc 直接喂给 curl（`<<'JSON'` 的引号不能省，否则 body 里的 `$` 会被展开）：

```bash
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"content":"正文"}
JSON
```

跑通了会看到响应体：列表是**裸数组**（`[{...}]`），单个资源是一个对象，
`DELETE` 是 204、没有响应体。

**最容易丢数据的三处**：

1. **覆盖式 vs 并集式标签**：`PUT /api/ideas/{id}/tags` 是**整体替换**
   （不给 `tags` 就是清空）；`POST /api/ideas/bulk/tags` 默认**并集追加**
   （给 `replace: true` 才是覆盖式）。用错了会丢标签。
2. **`POST /api/ideas/merge` 会硬删源想法**（不进回收站）。批量删除
   （`POST /api/ideas/bulk/delete`）才是软删。
3. **webhook 的 url 与 headers 默认是遮蔽版**（url 只留 `scheme://host[:port]`，
   路径段与请求头的值显示成 `***`）。要原值得显式加 `?unmask=true`，
   而**只有 `GET /api/webhooks` 与 `GET /api/webhooks/{id}` 认它**（写接口的响应一律遮蔽，
   加了也没用）。改其它字段时**别把遮蔽值写回去** —— 那两个键整个别发。

**分页的坑**：`/api/ideas` 的 `offset` 从**最新**一端数（往回翻历史），
`/api/search` 的 `offset` 从**最相关**一端数 —— 两者语义相反，别混用。
`/api/ideas` 的 `limit` 上限是 1000；`/api/search` 的 `limit` 上限是 100、默认 20。
`/api/search` 的 `q` **必填**（空串 → 400 `invalid_search_query`），
但契约里它标成了可选。

### 端点清单

服务端的完整契约在 `/openapi/v1.json`；手册的索引页
[`skills/idea-manager/SKILL.md`](skills/idea-manager/SKILL.md) 里有一份同样的
「能力 → 方法 + 路径」表（33 个 API 端点），下表与它保持同步。

| 能力 | 方法 + 路径 |
|---|---|
| 健康检查 | GET /api/health |
| 想法列表（可按项目 / 状态 / 标签 / 关键词筛，可分页） | GET /api/ideas |
| 全库搜索（想法 / 项目 / 标签三组，各带分页） | GET /api/search |
| 记一条想法 | POST /api/ideas |
| 改一条想法（只发要改的字段） | PATCH /api/ideas/{id} |
| 删一条想法（进回收站） | DELETE /api/ideas/{id} |
| 覆盖式设置标签（**不给 `tags` = 清空**） | PUT /api/ideas/{id}/tags |
| 标记完成 / 取消完成（`done` **必填**） | POST /api/ideas/{id}/complete |
| 从回收站恢复想法 | POST /api/ideas/{id}/restore |
| 批量标记完成 / 取消完成（`done` **必填**） | POST /api/ideas/bulk/complete |
| 批量加标签（并集追加；给 `replace: true` 就是**覆盖式**） | POST /api/ideas/bulk/tags |
| 批量删除想法（进回收站） | POST /api/ideas/bulk/delete |
| 合并多条想法成一条 | POST /api/ideas/merge |
| 项目列表 | GET /api/projects |
| 新建项目 | POST /api/projects |
| 改项目名（`name` **必填**） | PATCH /api/projects/{id} |
| 删项目（连带想法进回收站） | DELETE /api/projects/{id} |
| 从回收站恢复项目 | POST /api/projects/{id}/restore |
| 重排项目顺序 | POST /api/projects/reorder |
| 读设置（键值字典） | GET /api/settings |
| 改设置（目前只有 `project.sort_mode`） | PUT /api/settings/{key} |
| 标签字典（含使用次数） | GET /api/tags |
| 标签补全建议 | GET /api/tags/suggest |
| 事件流（SSE） | GET /api/events |
| 回收站内容 | GET /api/trash |
| webhook 列表 | GET /api/webhooks |
| 新建 webhook | POST /api/webhooks |
| Telegram webhook 模板预设 | GET /api/webhooks/templates/telegram |
| 读一个 webhook | GET /api/webhooks/{id} |
| 改 webhook | PATCH /api/webhooks/{id} |
| 删 webhook | DELETE /api/webhooks/{id} |
| webhook 的投递记录 | GET /api/webhooks/{id}/deliveries |
| 手动重投一条投递 | POST /api/webhooks/{id}/deliveries/{deliveryId}/retry |
| skill 清单（版本 + 文件清单 + 逐个 sha256） | GET /skill/idea-manager/version |
| 取 skill 文件正文 | GET /skill/idea-manager/{path} |

---

## Agent skill

agent skill 的**权威内容在服务端**：应用把 `skills/idea-manager/`（索引页 + `references/`）
随产物一起分发出去，agent 取到的手册与服务端**同一版本** ——
服务端改了 API，agent 手上的说明书跟着改：**不用重新装 skill**（薄壳一个文件都不用动），
每次取件都会重新同步一次。

> ⚠️ **前提是产物已经重新发布**：改了 `skills/idea-manager/` 里的内容源，
> 还要重新 publish 并替换部署产物，agent 下次取件才会拿到新手册。
> 内容源是 csproj 在 publish 时显式列文件拷进产物的（不整目录拷，
> 免得把开发侧的残留一起发出去）。

### 交付形态

| 目录 | 是什么 | 谁用 |
|---|---|---|
| `skills/idea-manager/` | **内容源**：索引页 `SKILL.md` + `references/`（各管一件事，路由表在索引页里） | 服务端随产物分发；仓库里改它 |
| `skills/idea-manager-client/` | **薄壳**：`SKILL.md`（指针）+ `scripts/fetch.py`（取件器） | 装到 agent 的 skills 目录 |

服务端这边有只读端点：`/skill/idea-manager/version` 给清单（版本 + 文件清单 + 逐个 sha256），
`/skill/idea-manager/{path}` 给文件内容。**路径拼错**回 404，不会落到 SPA 兜底变成 200 + HTML。

但**带 `..` 逃出 `/skill/` 前缀**的形态是例外：服务端会按 RFC 3986 归一化路径，
`/skill/idea-manager/../../etc/passwd` 被归一化成 `/etc/passwd` → 逃出 `/skill/` 前缀 →
落到**前端兜底** → **200 + HTML**（读不到任何文件）。
三种形态（拼错 / `..` 穿越 / 编码过的穿越）各是什么结果，见手册的 `references/errors.md`。

### 安装

**薄壳是从这个仓库装的** —— 服务端只分发手册正文，薄壳那两个文件（指针与取件器）
不在分发副本里：手上没有这个仓库就装不了。
（前提是服务端跑着、发布产物里带着分发副本 —— 见[部署](#部署)。）

装的是**拷贝**，不是软链 —— 软链 `skills/idea-manager/`（内容源）会把内容源当成薄壳装过去，
agent 手上就握着一份与服务端不同步的说明书：

```bash
bash skills/install-agent-skill.sh                       # 装到 ~/.zcode/skills/idea-manager
bash skills/install-agent-skill.sh ~/.claude/skills/idea-manager
```

脚本是幂等的：目标目录里**旧 skill 留下的那几个已知文件**会被清掉
（`scripts/idea.py`、`scripts/run-tests.sh`、`references/openapi.json`、
`tests/test_cli.py`、`__pycache__` 里的 `.pyc`），
目录只在**空了之后**才删（`rmdir`）—— 里面还有你自己的文件就留着，
不认识的条目只警告、不删。
薄壳的约定是「那个目录里只有两个文件」—— 留着别的东西，agent 可能会直接去读它。

装完之后 agent 需要**重新扫一遍本地 skill 目录**才会发现它；
当前会话里还看不到这个 skill 的话，新开一个会话试试
（不同 agent 运行时刷新本地目录的时机不一样）。

装完之后 agent 读到的就是薄壳那份 `SKILL.md`（指针）—— 它自己会先跑 `scripts/fetch.py`
取说明书，不需要谁去提醒它。

### 连接配置

走环境变量，不进仓库（命令行不接受密码）：

| 变量 | 必填 | 说明 |
|---|---|---|
| `IDEA_BASE_URL` | 是 | 实例地址，如 `https://ideas.example.com`（要带 scheme） |
| `IDEA_USER` | 否 | nginx basic auth 用户名；与密码同时存在时才发 |
| `IDEA_PASS` | 否 | nginx basic auth 密码 |
| `IDEA_TOKEN` | 否 | 可选。若 nginx 侧另外校验 `X-Api-Key` 头则填 |
| `IDEA_SKILL_CACHE_DIR` | 否 | 取件器的缓存根目录（默认 `${XDG_CACHE_HOME:-~/.cache}/idea-skill`） |

设一次，后面每个命令都能用（`export` 只对当前 shell 生效）：

```bash
export IDEA_BASE_URL=https://ideas.example.com
export IDEA_USER=ideauser
export IDEA_PASS='<密码>'
# export IDEA_TOKEN='<令牌>'    # nginx 侧另外校验 X-Api-Key 时才需要
```

**这些变量得在 agent 跑命令的那个环境里也有** —— 你在另一个终端里 `export` 的，agent 看不见。
启动 agent 之前先 `export`，或者写进 agent 的启动配置（**别把密码写进仓库**）。

### 取说明书

取件器是纯标准库脚本，**Python 3.9+** 就能跑（第三方依赖一个都没有）。
下面假设上面那几个变量已经 `export` 过了：

```bash
SKILL=~/.zcode/skills/idea-manager      # 装到哪儿就是哪儿（与上面安装时的目标目录一致）

python3 "$SKILL/scripts/fetch.py"                        # 索引页：有哪些能力、怎么调、细节去哪找
python3 "$SKILL/scripts/fetch.py" references/errors.md   # 按索引页里的路由表取某一份细节
python3 "$SKILL/scripts/fetch.py" /openapi/v1.json       # 完整接口契约
```

取件器把清单里的每个文件都取下来、逐个校验 sha256，全部通过才切到新版本 ——
中途失败就保留上一版，不会出现「手册是新的、别的是旧的」。
缓存保留两版（`~/.cache/idea-skill/<版本>/`），命中缓存也要重算哈希。

服务端暂时连不上、但本地有**完好**缓存时，它在 stderr 上打一行 `degraded_cache:`，
并以**退出码 3** 退出 —— 这时 stdout 上是**上一版**手册（不是服务端的当前版本）。
**退出码 3 是专属的**：只表示「这次拿到的是上一版」，与其它失败区分得开。
agent 看到它该**重试一次**，或提醒用户「服务端暂时不可达」——
上一版手册会让 agent 按旧语义操作，那正是这套改造要消除的东西。

取件**硬失败**时（不是上面那种降级），它往 stderr 打**一行** `code: detail`、
退出码非 0（不吐 traceback）。
每个码是什么意思，见手册的 `references/errors.md`；
薄壳 `SKILL.md` 里也有一份精简的排查提示（缺配置、鉴权不对、没分发、内容对不上……）。

自检（打得通就说明地址对、凭证对、服务端分发了 skill 内容）—— 这一段自成一体，
复制过去就能跑：

```bash
# 路径要跟实际安装位置一致（装到 ~/.claude/skills/idea-manager 就改这一行）
SKILL=~/.zcode/skills/idea-manager
IDEA_BASE_URL=https://ideas.example.com \
IDEA_USER=ideauser IDEA_PASS='<密码>' \
python3 "$SKILL/scripts/fetch.py"
```

**不带凭据跑自检会直接 `auth_error`**（退出码 1）：本项目的标准部署走 nginx basic auth，
只有不带 basic auth 的实例才用得着空着跑。

### 调 API

按手册的姿势直接用 curl 调，不经别的封装 —— 样板与避坑见
[调用姿势](#调用姿势)。完整契约在服务端的 `/openapi/v1.json`（实时发）。
仓库根的 `openapi/idea-v1.json` 是同一份的快照，但那是**仓库里的事** ——
分发副本里**没有第二份契约**，agent 手上这一份是服务端运行时发的。

手册的索引页 [`skills/idea-manager/SKILL.md`](skills/idea-manager/SKILL.md) 里有
能力清单、curl 模板、最易错的坑，以及一张路由表指向 `references/` 下那几份：

| agent 想做的事 | 取哪份 |
|---|---|
| 请求体怎么写、特殊字符怎么办、认证怎么带 | `references/calling.md` |
| 拿到一个 `code`、要判断该怎么处理 | `references/errors.md` |
| 配 webhook、订阅事件、url 与 headers 的遮蔽 | `references/webhooks.md` |
| 某个端点的完整参数与 schema | `references/api.md` |
| 要一个能照抄的完整例子 | `references/scenarios.md` |

---

## 测试

四套闸门，加一条 AOT 冒烟。仓库根 `bash verify.sh` 一次跑完（见[CI 与闸门](#ci-与闸门)）。
单独跑时：

| 命令 | 目录 | 覆盖什么 |
|---|---|---|
| `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj` | 仓库根 | 服务端集成/单元测试（含 skill 分发端点、契约快照、文档漂移守卫） |
| `python3 -m unittest discover -s skills/idea-manager-client/tests` | 仓库根 | 取件器单测（清单校验、降级、管道、重定向……） |
| `bash skills/idea-manager-client/scripts/run-tests.sh` | 仓库根 | 取件器单测 **+ 一条真 HTTP 服务端的冒烟** |
| `bash server/scripts/aot-smoke.sh` | 仓库根 | 真 AOT 产物打端点 + **跨栈**检查（真产物 ↔ 真取件器） |
| `npm --prefix web run typecheck` | 仓库根 | 前端类型检查（应用 + e2e 两份 tsconfig） |
| `npm --prefix web test` | 仓库根 | 前端单测（Vitest，jsdom） |
| `bash web/e2e/run-e2e.sh` | 仓库根 | 端到端用例（Playwright） |

### 端到端（Playwright）

端到端底座在 `web/e2e/`，说明见 [`web/e2e/README.md`](web/e2e/README.md)，
功能点 × 用例对照见 [`web/e2e/COVERAGE.md`](web/e2e/COVERAGE.md)。

```bash
# 全量（先起服务，跑完停服务）
bash web/e2e/run-e2e.sh

# 全量 + 干净的库（排查「上一轮的数据影响这一轮」时用）
bash web/e2e/run-e2e.sh --fresh

# 强制重建（前端 + 后端）并重启服务
bash web/e2e/run-e2e.sh --rebuild

# 跑完不停服务（留给下一轮或手工排查）
bash web/e2e/run-e2e.sh --keep

# 单个文件 / 单条用例：参数原样透传给 playwright test
bash web/e2e/run-e2e.sh e2e/smoke.spec.ts
bash web/e2e/run-e2e.sh --grep "回收站"

# 指定端口（默认自己挑一个空闲的）
E2E_PORT=5398 bash web/e2e/run-e2e.sh
```

**一轮 = 一个端口 + 一个库**：端口 5300 起（`E2E_PORT` 可指定，没指定就自己找一个空闲的），
库在 `web/e2e/.tmp/run-<端口>/e2e.db`，与开发库、冒烟脚本的库完全分开。
Playwright 的 workers 固定 1（共用一个库），并行会互相复位。

> ⚠️ **前端产物 `server/IdeaApp/wwwroot/` 是共享的，不是每轮一份** ——
> 两轮并发跑的话，后一轮的重建会把前一轮的页面换掉。

---

## 已知限制与坑

### API 语义

- **没有手动彻底删除**：删除只有一条路 —— 软删进回收站，到期由后台自动硬删
  （每小时清一次，启动时先清一次）。**保留期是唯一会导致不可逆删除的开关**，
  页面与 API 都没有设置入口，改它只能改配置 + 重启。
  配成 ≤0 按 1 天处理并告警。Testing 环境不注册清理 worker。
- **恢复项目会连带恢复「随项目一起删」的想法**（靠 `deleted_at` 相等判定）；
  之前单独删过的不复活。
- **任何写操作都刷新 `updated_at`** —— 编辑一条老想法会让它下方条目整体上移一格
  （两列布局下下半墙卡片左右互换）。这是**已批准的取舍**，别当 bug 修。
- **合并与批量删除不是一回事**：`merge` 硬删源想法（不进回收站），
  `bulk/delete` 软删。合并**不跳过**不存在的 id（少一条就整体 404，一条都不改），
  不能跨项目，少于两条报 400 `invalid_merge_selection`。
- **批量删除是幂等的**：已在回收站里的 id 算「已处理」，不重置保留期。
- **批量标记完成会刷新每一条的 `updated_at`** —— 处理过的条目会挪到列表底部。
- **`done` 必填**：`POST /api/ideas/{id}/complete` 与 `POST /api/ideas/bulk/complete`
  不给 `done` = `false` = **取消完成**，服务端**不报错**。
- **内置收件箱 `inbox` 不能删**（400 `inbox_not_deletable`），可以改名。
- **只有 `project.sort_mode` 可写**，别的设置键一律 400 `setting_key_not_writable`。
- **`PATCH` 只发要改的字段**：`null` = 不改；但空串 / 空数组常常是「真的清空」
  （webhook 的 `headers` 给 `{}` = 清空请求头）—— 想法的 `content` 与项目的 `name`
  给空串是 400，项目的 `name` 还**必填**。
- **搜索语义比旧的前端子串匹配宽**：中文按词元相邻（标点与空白不挡路），
  英文必须**整词**（搜 `hel` 搜不到 `hello`），多个词之间是 **AND**。
  搜「咖啡水温」命中「咖啡，水温」但不命中「咖啡的水温」。
- **`matches` 高亮区间可能重复或重叠**（"哈哈哈哈" 搜 "哈哈"），偏移单位是 UTF-16
  code unit（与前端 `String.slice` 对齐），调用方要自己合并。
- **SSE 恒定全量推送**，**没有任何过滤参数**。事件 id 是单调 UUIDv7，**不是连续序号** ——
  客户端发现不了中间的静默空洞。
- **`resync` 是控制帧**，不是业务事件，不能被 webhook 订阅；它不带 id，
  客户端不能拿它继续补发。
- **事件所属项目在回收站里，整条不投递**（全局与项目级一律跳过）——
  唯一例外是 `project.deleted` 照投，否则删一次项目会灌出 N+1 条通知。
- **手动重投重发的是「已渲染的 payload」**，不按当前模板重新渲染
  （投递记录表里没存事件全量数据，重新渲染的输入不存在）。
- **webhook 的密钥只进不出**（响应里只有 `hasSecret` 布尔）；secret 是三态：
  不传 = 不改、`""` = 清空、非空 = 替换。

### 数据与一致性

- **全文索引回填失败 = 启动失败**：索引与业务数据不一致时搜索给的是**错结果**，
  那比起不来危险得多。代价是索引写失败会连累业务写（同一事务）。
- **老库的 FTS 列数错位是静默的**：`bm25` 权重个数不对**不会报错**，只是相关度悄悄错位。
  验收必须直接断言 `PRAGMA table_info(idea_fts)` 的列数。
- **硬删必须同步删索引行**：`idea` 表没有显式整数主键，rowid 会被复用；
  回收站到期清理是**裸 SQL、绕过仓储层**，最容易漏。孤儿索引行占着 rowid，新行会串位。
- **事件在事务提交后才投递**（通道无法回滚，事务内投递会产生幽灵事件）。
  通道容量 1000，满了**丢弃新事件并记日志**，绝不阻塞写路径。

### 前端

- **想法列表不虚拟化**：用 `content-visibility` 会改卡片高度，与往回翻历史的视口补偿打架。
- **过滤条件不进 URL**，**相对时间手写不依赖 ICU**，**拖拽排序只有鼠标手势**，
  **底部输入框的草稿离开不提醒** —— 这四条是界面复核时明确接受的偏差
  （见 `docs/superpowers/reviews/2026-11-20-web-design-guidelines.md`）。
- **卡片编号是「位置」不是身份**：往上翻一批，编号整体 +P。
- **往回翻历史必须净增偶数条**：请求 101 条（多一条是探针），满页时丢掉**最老**的那条，
  净增 100 条 —— 丢错成最新的那条会在已加载集合中间留洞。
- **AND/OR 是每台设备自己的偏好**，存 localStorage，不跨设备。
- **过滤条件变化与 SSE 事件用两个防抖计时器**（各 250ms）—— 共用一个会互相顶掉。
- **SSE 推来的增量条目由前端近似判断显不显示**，随后防抖 250ms 重拉服务端结果覆盖 ——
  英文下个别条目会「先出现再被收回」。

### 部署与运维

- **应用层不做鉴权，也不做 Origin/CSRF 校验** —— 浏览器与 agent 共用 nginx 的 basic auth。
  API 必须**始终**挂在访问控制之后。
- **`IDEA_` 前缀压过命令行参数**（与 ASP.NET Core 常规约定相反），见[配置项](#配置项)。
- **`wwwroot/` 被 gitignore**：新克隆的仓库里没有它；忘了构建前端就发布，
  页面全白且**不报错**。
- **发布产物要拷全**：`appsettings.json` 与 `libe_sqlite3.so` 漏了会回落成默认配置 / SQLite 加载失败。
- **AOT 的 JSON 源生成上下文**：加 DTO 必须在 `IdeaJsonContext` 登记，
  **漏登记在开发期就抛 `NoMetadataForType`**；但反射 / 裁剪类退化只有真产物才暴露，
  两条都得跑（`dotnet test` 测不出 AOT 退化）。
- **换 .NET SDK 版本要重验 ILCompiler 那段**：csproj 把 ILCompiler 显式提到 10.0.14，
  避开 SDK 10.0.113 内置的 10.0.13 命中的 advisory；`TreatWarningsAsErrors` 会把
  NU1903 升级成错误 → restore 直接失败。
- **nginx 的请求体上限改了要连手册一起改**：413 没有 `code`，
  手册的 `references/errors.md` 里写的是同一个口径。
- **`Skill:Path` 指向的副本里缺入口 `SKILL.md`**，与「路径没配」「目录不存在」
  是**同一个 404 码**（`skill_not_distributed`），排查方向不同：
  skill 名字拼错 → `skill_endpoint_not_found`，文件名拼错 → `skill_file_not_found`。

### 刻意不做的事

不做多人协作 / 账号 / 分享、不接 LLM、不做看板 / 富文本 / 附件、不做移动原生 App、
不做多实例与水平扩展。

> **项目名里的 kanban 是历史遗留** —— 本项目是消息流，不是看板。
> 别按看板写文档、别按看板提需求。

---

## 设计取舍

设计与取舍的完整推导在 `docs/superpowers/specs/` 下四份规格里：

| 规格 | 解决什么问题 |
|---|---|
| [`2026-11-11-idea-manager-design.md`](docs/superpowers/specs/2026-11-11-idea-manager-design.md) | 主设计：数据模型、端点、部署形态、非目标与反目标 |
| [`2026-11-13-skill-distribution-design.md`](docs/superpowers/specs/2026-11-13-skill-distribution-design.md) | skill 随服务分发：为什么命令层要删掉、说明书为什么放服务端 |
| [`2026-11-15-backend-full-text-search-design.md`](docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md) | 全文检索：**推翻了主规格的一条非目标**（「不做服务端全文检索」）——分词器、索引、事务边界 |
| [`2026-11-17-idea-manager-ui-layout-redesign.md`](docs/superpowers/specs/2026-11-17-idea-manager-ui-layout-redesign.md) | 前端布局重排；**「标题」这个概念被整体去掉** |

界面侧的复核结论（修掉 12 条、明确接受 12 条偏差）在
[`docs/superpowers/reviews/2026-11-20-web-design-guidelines.md`](docs/superpowers/reviews/2026-11-20-web-design-guidelines.md)。

几条贯穿全栈的取舍：

- **AOT 约束贯穿全栈**：不引 ORM / 反射 DI / 模板引擎 —— 手写 SQL、
  手写 Mustache 式渲染器、JSON 走源生成上下文。
- **应用层不鉴权**：访问控制全部放在 nginx 层，应用只监听回环地址。
- **skill 的权威内容在服务端**：命令层重复实现契约，是最大的一块漂移面 ——
  所以把说明书放服务端分发，agent 侧只装一个薄壳。
