# Idea Manager

个人自用的 idea 记事本：单用户、单实例，通过 nginx 反代访问。

- **后端**：.NET 10 + SQLite + Native AOT，无 ORM、无反射式序列化。
- **前端**：Vue 3 + TypeScript + Vite，构建产物直接放进后端的 `wwwroot/`。
- **agent 接入**：REST API + SSE，外加一份提交进仓库的 OpenAPI 契约。

设计与取舍见 [`docs/superpowers/specs/2026-11-11-idea-manager-design.md`](docs/superpowers/specs/2026-11-11-idea-manager-design.md)。

---

## 目录结构

```
server/IdeaApp/            后端应用（.NET 10，Native AOT）
  Domain/                  领域模型与 MonotonicGuid（UUIDv7）
  Application/             用例服务层（项目/想法/标签/回收站/webhook/投递）
  Infrastructure/          SQL、事件总线、SSE、webhook 投递、OpenAPI 导出
  Contracts/               DTO、请求体、JSON 源生成上下文
  Endpoints/               各资源的端点映射（MapXxx 扩展方法）
  Program.cs               组合根：注册、端点映射、静态文件与 SPA 兜底
  wwwroot/                 前端构建产物的落点（被 .gitignore 排除，不进仓库）
server/IdeaApp.Tests/      集成测试（WebApplicationFactory + 真实 SQLite）
server/scripts/aot-smoke.sh AOT 产物冒烟脚本
tools/ExportOpenApi/       导出 OpenAPI 文档的小工具
web/                       前端（Vue 3 + TypeScript + Vite）
  src/pages/               路由级页面
  src/components/          可复用组件
  src/composables/         可复用的组合式函数
  src/stores/              全局状态
  src/api/                 后端调用层（REST + SSE）
  src/lib/                 纯函数工具
  src/styles/              全局样式
openapi/idea-v1.json       OpenAPI 3.1 契约快照（提交进仓库）
skills/idea-manager/       agent skill 的内容源（索引页 + references，服务端分发）
skills/idea-manager-client/ agent 侧安装的薄壳（指针 + 取件器）
docs/nginx/idea.conf       nginx 反代配置
docs/superpowers/          规格与计划
```

`web/` 已落地（Vue 3 + TypeScript + Vite，详见 [`web/README.md`](web/README.md)）。
`skills/idea-manager/`（agent skill 的内容源）也已落地：索引页加 `references/` 几份细节，
由**服务端随产物分发**；agent 侧只装一个薄壳（`skills/idea-manager-client/`），
见下面的「Agent skill」一节。

## 构建

### 前端

```bash
cd web
npm ci                 # 首次
npm run build          # 产物输出到 server/IdeaApp/wwwroot/
```

开发时用 `npm run dev`（Vite dev server，`/api` 由 proxy 转发到
`http://127.0.0.1:5080`，**要先起后端**），测试与类型检查分别是
`npm test`（Vitest + jsdom）和 `npm run typecheck`（vue-tsc）。
详见 [`web/README.md`](web/README.md)。

前端用 history 模式（`/projects`、`/trash`、`/webhooks` 等都是真实路径），
后端用 `MapFallbackToFile("index.html")` 兜底未匹配的 GET 请求 ——
这几行接线在 `server/IdeaApp/Program.cs` 里：`UseStaticFiles` +
`MapFallbackToFile`，另外还有**一行 provider 覆盖**
（`WebRootFileProvider`）—— `wwwroot` 被 gitignore 排除，新克隆的仓库里
它不存在，光建目录不够，得显式指一个 `PhysicalFileProvider`，
否则静态文件中间件会拿到查不到任何文件的 provider。

那个 SPA 兜底是 catch-all，会兜住**所有**没匹配到端点的 GET —— 包括 `/api/**`。
所以 `/api/**` 另有一层兜底（`app.Map("/api/{**rest}", …)`），
未匹配的 API 路径回 **404 + ProblemDetails**（`code=api_not_found`），
不是 200 + HTML：这个 API 是给 agent 读的（契约在 `openapi/idea-v1.json`），
agent 只按状态码判断的话，200 + HTML 会被当成成功。

> ⚠️ **`server/IdeaApp/wwwroot/` 被 `.gitignore` 排除**，构建产物**不进仓库**。
> 新克隆的仓库里这个目录不存在 —— 跑一次 `npm run build` 才有产物，
> 否则后端能起、但页面是白屏。

### 后端

```bash
# 开发
dotnet build IdeaApp.sln

# 测试
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj

# Native AOT 发布（自包含单文件）
cd web && npm run build && cd ..          # ← 先构建前端，见下面的警告
dotnet publish server/IdeaApp -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

产物在 `server/IdeaApp/bin/Release/net10.0/linux-x64/publish/`：`IdeaApp` 可执行文件 +
`wwwroot/` + `skill/idea-manager/`（skill 的分发副本，见「部署」一节）。

> ⚠️ **publish 前必须先 `cd web && npm run build`** ——
> `wwwroot/` 被 gitignore 排除，仓库里没有它。忘了这一步就会发布一个
> **空的 wwwroot**：应用照常起、`/api/*` 照常工作，但页面是白屏，
> 而且**没有任何报错**（静态文件中间件找不到文件而已）。
> 部署前用 `ls server/IdeaApp/bin/Release/net10.0/linux-x64/publish/wwwroot`
> 确认 `index.html` 与 `assets/` 都在。

AOT 有自己的坑：JSON 一律走源生成上下文（`IdeaJsonContext`），类型漏登记在开发期能测出来，
在 AOT 产物下才会炸。改完端点记得跑一遍 AOT 冒烟：

```bash
bash server/scripts/aot-smoke.sh
```

## 运行

```bash
IDEA_Urls=http://127.0.0.1:5080 \
IDEA_Trash__RetentionDays=30 \
IDEA_PublicBaseUrl=https://ideas.example.com \
./IdeaApp
```

**首次启动自动建库建表**并写入内置收件箱（`inbox`），不需要额外的迁移步骤。
数据库默认在 `data/idea.db` —— 相对路径以**应用目录**（可执行文件所在目录）为基准，
**与当前工作目录无关**（见下面的「配置项」一节）。

应用同时把前端页面发在 `/` 上（`wwwroot/` 里的构建产物），
`/projects`、`/trash`、`/webhooks` 这类前端路由由 `MapFallbackToFile("index.html")` 兜底，
所以深链直接访问也是 200，不会 404。

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
| `Trash:RetentionDays` | `30` | 回收站保留天数。**页面与 API 都不可改** |
| `PublicBaseUrl` | 空 | 用于生成 `{{idea.url}}`，如 `https://ideas.example.com` |
| `Api:MaxIdeasPerResponse` | `2000` | 单次列表返回的最大条数，超过则 400 |
| `Webhook:TimeoutSeconds` | `10` | 单次投递超时 |
| `Webhook:MaxAttempts` | `4` | 最多投递尝试次数（**含首次尝试**）→ 首次 + 3 次重试 |
| `Webhook:MaxDeliveryRecords` | `500` | 每个 webhook 保留的投递记录条数 |
| `Sse:BufferSize` | `500` | 服务端事件缓冲条数（供 `Last-Event-ID` 补发） |
| `Sse:HeartbeatSeconds` | `25` | SSE 心跳间隔（发 `: ping` 注释帧，防中间代理掐断空闲连接） |
| `Skill:Path` | `skill/idea-manager` | skill 分发副本的根目录。**相对路径以应用目录为基准，与当前工作目录无关**（与 `Database:Path` 同一条约定） |

#### 「应用目录」是哪个目录

`Database:Path` 的相对路径锚在**应用目录**上，也就是可执行文件所在目录
（代码里是 `AppContext.BaseDirectory`）：

| 跑法 | 应用目录 | `data/idea.db` 落在 |
|---|---|---|
| AOT 单文件发布（`/opt/idea/IdeaApp`） | `/opt/idea/` | `/opt/idea/data/idea.db` |
| `dotnet run`（开发） | `server/IdeaApp/bin/Debug/net10.0/` | `server/IdeaApp/bin/Debug/net10.0/data/idea.db` |

**不能按当前工作目录解析**：用 systemd 跑且没设 `WorkingDirectory` 时 CWD 是 `/`，
库文件会落到 `/data/idea.db` —— 不可预期、可能没权限，也可能误伤别的数据。
开发时想让库文件落在项目目录，显式给绝对路径即可：

```bash
IDEA_Database__Path="$PWD/server/IdeaApp/data/idea.db" dotnet run --project server/IdeaApp
```

另有一个只认环境变量的开关：`IDEA_EXPORT_OPENAPI=<路径>` —— 设了它，应用启动后会把
OpenAPI 文档写到该文件（见「OpenAPI 文档」一节）。

## 部署

应用只监听回环地址，对外一律走 nginx（TLS、访问控制都在 nginx 层，
应用本身不做鉴权、也不做 Origin/CSRF 校验）。

```bash
cp docs/nginx/idea.conf /etc/nginx/conf.d/idea.conf
# 按自己的域名改 server_name、证书路径、htpasswd 路径
htpasswd -c /etc/nginx/.htpasswd ideauser
nginx -t && systemctl reload nginx
```

`docs/nginx/idea.conf` 里 SSE（`/api/events`）单独一段：`proxy_buffering off` +
`X-Accel-Buffering: no`，否则实时推送会被 nginx 攒着不发。

发布产物里带着 skill 的分发副本（`skill/idea-manager/`）—— agent 的说明书由它提供，
部署时跟应用一起拷过去。少了它，取说明书的那个端点会回 404 `skill_not_distributed`
（副本在哪儿由 `Skill:Path` 决定）；**路径写错是另外两个码**：skill 名字拼错 →
`skill_endpoint_not_found`，文件名拼错 → `skill_file_not_found`，排查方向不一样。

应用侧接了 `UseForwardedHeaders()`：`X-Forwarded-Proto` / `X-Forwarded-For` /
`X-Forwarded-Host` 会被采信，**但只信回环地址来的代理**。nginx 与应用同机时直接可用；
nginx 在别的机器上要在代码里显式加 `KnownProxies`，否则这些头不生效
（应用会以为自己还在 http 上）。注意 .NET 10 的 `ForwardedHeadersOptions` 默认
`ForwardedHeaders.None` —— 不显式点名要处理哪些头，中间件等于没挂。

## OpenAPI 文档

- **契约文件**：[`openapi/idea-v1.json`](openapi/idea-v1.json)（OpenAPI 3.1，提交进仓库）。
  它是构建产物快照：路径、参数、请求体与响应体 schema、状态码都在里面。
  文件里没有 `servers` 段 —— 地址是运行期的事，对外地址由 nginx 决定，契约只管接口本身。
- **错误响应也在契约里**：4xx / 5xx 一律是 RFC 9457 的 ProblemDetails
  （内容类型 `application/problem+json`），带机器可读的扩展字段 `code`。
  每个错误响应的描述里列了**这个端点**可能出现的 `code` 与含义，
  `components.schemas.ProblemDetails` 的描述里是全部 `code` 的取值全集。
- **运行时文档**：应用启动后可以访问 `/openapi/v1.json`，内容与契约文件一致 ——
  agent 手上拿的是这一份（手册指向的就是它）。
- **重新导出**（改了端点、DTO 或状态码之后要重跑）：

  ```bash
  dotnet run --project tools/ExportOpenApi            # 写到 openapi/idea-v1.json
  dotnet run --project tools/ExportOpenApi -- <路径>    # 写到指定路径
  ```

  工具在进程里起**应用本体**（`WebApplicationFactory`），所以导出的文档与应用实际暴露的端点必然一致。

- **另一条导出路径**：在真实应用进程里导出，产物与上面一致：

  ```bash
  IDEA_EXPORT_OPENAPI=/tmp/idea-v1.json ./IdeaApp
  ```

## Agent skill

agent skill 的**权威内容在服务端**：应用把 `skills/idea-manager/`（索引页 + `references/`）
随产物一起分发出去，agent 取到的手册与服务端**同一版本** ——
服务端改了 API，agent 手上的说明书跟着改：**不用重新装 skill**（薄壳一个文件都不用动），
每次取件都会重新同步一次。

### 交付形态

| 目录 | 是什么 | 谁用 |
|---|---|---|
| `skills/idea-manager/` | **内容源**：索引页 `SKILL.md` + `references/`（各管一件事，路由表在索引页里） | 服务端随产物分发；仓库里改它 |
| `skills/idea-manager-client/` | **薄壳**：`SKILL.md`（指针）+ `scripts/fetch.py`（取件器） | 装到 agent 的 skills 目录 |

服务端这边有只读端点：`/skill/idea-manager/version` 给清单（版本 + 文件清单 + 逐个 sha256），
`/skill/idea-manager/{path}` 给文件内容。路径拼错回 404，不会落到 SPA 兜底变成 200 + HTML。

### 安装

装的是**拷贝**，不是软链 —— 软链 `skills/idea-manager/`（内容源）会把内容源当成薄壳装过去，
agent 手上就握着一份与服务端不同步的说明书：

```bash
bash skills/install-agent-skill.sh                       # 装到 ~/.zcode/skills/idea-manager
bash skills/install-agent-skill.sh ~/.claude/skills/idea-manager
```

脚本是幂等的：目标目录里认得出的旧形状残留会被清掉，不认识的文件只警告、不删。
薄壳的约定是「那个目录里只有两个文件」—— 留着别的东西，agent 可能会直接去读它。

装完之后 agent 读到的就是薄壳那份 `SKILL.md`（指针）—— 它自己会先跑 `scripts/fetch.py`
取说明书，不需要谁去提醒它。

### 连接配置

走环境变量，不进仓库（命令行不接受密码）：

| 变量 | 必填 | 说明 |
|---|---|---|
| `IDEA_BASE_URL` | 是 | 实例地址，如 `https://ideas.example.com` |
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

### 取说明书

取件器是纯标准库脚本，**Python 3.9+** 就能跑（第三方依赖一个都没有）。
下面假设上面那几个变量已经 `export` 过了：

```bash
SKILL=~/.zcode/skills/idea-manager      # 装到哪儿就是哪儿

python3 "$SKILL/scripts/fetch.py"                        # 索引页：有哪些能力、怎么调、细节去哪找
python3 "$SKILL/scripts/fetch.py" references/errors.md   # 按索引页里的路由表取某一份细节
python3 "$SKILL/scripts/fetch.py" /openapi/v1.json       # 完整接口契约
```

取件器把清单里的每个文件都取下来、逐个校验 sha256，全部通过才切到新版本 ——
中途失败就保留上一版，不会出现「手册是新的、别的是旧的」。
服务端暂时连不上、但本地有完好缓存时，它在 stderr 上打一行 `warning:`，
这时拿到的是**上一版**手册。

取件**硬失败**时，它往 stderr 打**一行** `code: detail`、退出码非 0（不吐 traceback）。
每个码是什么意思，见手册的 `references/errors.md`；
薄壳 `SKILL.md` 里也有一份精简的排查提示（缺配置、鉴权不对、没分发、内容对不上……）。

自检（打得通就说明地址对、凭证对、服务端分发了 skill 内容）—— 这一段自成一体，
复制过去就能跑：

```bash
SKILL=~/.zcode/skills/idea-manager
IDEA_BASE_URL=https://ideas.example.com \
IDEA_USER=ideauser IDEA_PASS='<密码>' \
python3 "$SKILL/scripts/fetch.py"
```

**不带凭据跑自检会直接 `auth_error`**（退出码 1）：本项目的标准部署走 nginx basic auth，
只有不带 basic auth 的实例才用得着空着跑。

### 调 API

agent 直接用 curl 调 API，不经别的封装（`$IDEA_BASE_URL` / `$IDEA_USER` / `$IDEA_PASS`
按上面「连接配置」里 `export` 好）：

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

- 样板里的 `-u` **只在有凭据时保留**；`IDEA_TOKEN` 有值时再加 `-H "X-Api-Key: $IDEA_TOKEN"`。
- **别把凭据写进 URL** —— nginx 会把 userinfo 原样写进访问日志。
- 带请求体就要带 `Content-Type: application/json`（不带是 415）。
- 4xx / 5xx 回 RFC 9457 的 ProblemDetails，顶层带机器可读的 `code` ——
  **按 `code` 分支**，别去匹配 `detail` 的文字。
- **webhook 的 url 与 headers 默认是遮蔽版**（url 只留 `scheme://host[:port]`，路径段与
  请求头的值显示成 `***`）—— 那是设计如此，不是 bug。要原值得显式加 `?unmask=true`，
  而**只有 `GET /api/webhooks` 与 `GET /api/webhooks/{id}` 认它**（写接口的响应一律遮蔽，
  加了也没用）；除非用户明确要看原始 url，否则不要加这个参数。
  （细节见手册的 `references/webhooks.md`。）

跑通了会看到响应体：列表是**裸数组**（`[{...}]`），单个资源是一个对象，
`DELETE` 是 204、没有响应体。

**请求体别用 shell 拼字符串** —— 引号、换行、反斜杠会被 shell 吃掉。写进文件，
或用带引号的 heredoc 直接喂给 curl（`<<'JSON'` 的引号不能省，否则 body 里的 `$` 会被展开）：

```bash
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"content":"正文","title":"标题"}
JSON
```

完整契约在服务端的 `/openapi/v1.json`（实时发）。仓库根的 `openapi/idea-v1.json` 是同一份的
快照，**skill 分发副本里没有第二份契约** —— agent 手上这一份是服务端运行时发的。
调用姿势、错误码全表、逐端点参数与可照抄的完整例子都在手册里：
[`skills/idea-manager/SKILL.md`](skills/idea-manager/SKILL.md) 是索引页，
它给的路由表指向 `references/` 下那几份。

### 测试

取件器的端到端测试（每个用例自己起一个假的 HTTP 服务端，不需要真的后端实例）：

```bash
bash skills/idea-manager-client/scripts/run-tests.sh
```
