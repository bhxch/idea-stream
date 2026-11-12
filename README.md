# Idea Manager

个人自用的 idea 记事本：单用户、单实例，通过 nginx 反代访问。

- **后端**：.NET 10 + SQLite + Native AOT，无 ORM、无反射式序列化。
- **前端**：原生 JS + CSS，构建产物直接放进后端的 `wwwroot/`。
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
  Program.cs               组合根：注册、端点映射
server/IdeaApp.Tests/      集成测试（WebApplicationFactory + 真实 SQLite）
server/scripts/aot-smoke.sh AOT 产物冒烟脚本
tools/ExportOpenApi/       导出 OpenAPI 文档的小工具
openapi/idea-v1.json       OpenAPI 3.1 契约快照（提交进仓库，给 agent 用）
docs/nginx/idea.conf       nginx 反代配置
docs/superpowers/          规格与计划
```

`web/`（前端）与 `skills/idea-manager/`（agent skill）**目前还不存在** ——
它们分别由前端计划与 skill 计划产出。下面提到这两处的命令，都要等对应的计划落地后才能跑。

## 构建

### 前端

前端计划落地后才有 `web/` 目录，届时构建命令是：

```bash
cd web && npm ci && npm run build    # 产物输出到 server/IdeaApp/wwwroot/
```

前端用 history 模式（`/projects`、`/trash`、`/webhooks` 等都是真实路径），
服务端用 `MapFallbackToFile("index.html")` 兜底未匹配的 GET 请求 ——
**这两行接线由前端计划负责**（计划里的 F12），当前后端还没挂静态文件与 SPA 兜底。

### 后端

```bash
# 开发
dotnet build IdeaApp.sln

# 测试
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj

# Native AOT 发布（自包含单文件）
dotnet publish server/IdeaApp -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

产物在 `server/IdeaApp/bin/Release/net10.0/linux-x64/publish/`：`IdeaApp` 可执行文件 + `wwwroot/`。

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
数据库默认在 `data/idea.db`（相对应用目录）。

### 配置项

配置写在 `server/IdeaApp/appsettings.json`，用环境变量覆盖：**前缀 `IDEA_`，双下划线表示层级**
（`IDEA_Trash__RetentionDays` 对应 `Trash:RetentionDays`）。

不带前缀的环境变量也认（`Urls=`、`Trash__RetentionDays=` 仍然生效）；两套并存，
**带 `IDEA_` 前缀的优先**（后注册的 provider 赢）。

| 键 | 默认 | 说明 |
|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | Kestrel 监听地址（只绑回环，对外走 nginx） |
| `Database:Path` | `data/idea.db` | SQLite 文件路径，相对路径以应用目录为基准 |
| `Trash:RetentionDays` | `30` | 回收站保留天数。**页面与 API 都不可改** |
| `PublicBaseUrl` | 空 | 用于生成 `{{idea.url}}`，如 `https://ideas.example.com` |
| `Api:MaxIdeasPerResponse` | `2000` | 单次列表返回的最大条数，超过则 400 |
| `Webhook:TimeoutSeconds` | `10` | 单次投递超时 |
| `Webhook:MaxAttempts` | `4` | 最多投递尝试次数（**含首次尝试**）→ 首次 + 3 次重试 |
| `Webhook:MaxDeliveryRecords` | `500` | 每个 webhook 保留的投递记录条数 |
| `Sse:BufferSize` | `500` | 服务端事件缓冲条数（供 `Last-Event-ID` 补发） |
| `Sse:HeartbeatSeconds` | `25` | SSE 心跳间隔（发 `: ping` 注释帧，防中间代理掐断空闲连接） |

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

应用侧接了 `UseForwardedHeaders()`：`X-Forwarded-Proto` / `X-Forwarded-For` /
`X-Forwarded-Host` 会被采信，**但只信回环地址来的代理**。nginx 与应用同机时直接可用；
nginx 在别的机器上要在代码里显式加 `KnownProxies`，否则这些头不生效
（应用会以为自己还在 http 上）。注意 .NET 10 的 `ForwardedHeadersOptions` 默认
`ForwardedHeaders.None` —— 不显式点名要处理哪些头，中间件等于没挂。

## OpenAPI 文档

- **契约文件**：[`openapi/idea-v1.json`](openapi/idea-v1.json)（OpenAPI 3.1，提交进仓库）。
  它是构建产物快照，**给 agent 读**用：路径、参数、请求体与响应体 schema、状态码都在里面。
  文件里没有 `servers` 段 —— 地址是运行期的事，对外地址由 nginx 决定，契约只管接口本身。
- **错误响应也在契约里**：4xx / 5xx 一律是 RFC 9457 的 ProblemDetails
  （内容类型 `application/problem+json`），带机器可读的扩展字段 `code`。
  每个错误响应的描述里列了**这个端点**可能出现的 `code` 与含义，
  `components.schemas.ProblemDetails` 的描述里是全部 `code` 的取值全集。
- **运行时文档**：应用启动后可以访问 `/openapi/v1.json`，内容与契约文件一致。
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

`skills/idea-manager/` 是随项目交付的 agent skill：`SKILL.md` 是说明书，
`scripts/idea.py` 是纯标准库的命令行封装，`references/openapi.json` 是上面那份契约的副本。
**该目录由 skill 计划产出，目前还不存在** —— 下面的安装命令等它落地后再用。

安装（软链即可，两种 agent 目录格式一致）：

```bash
ln -s "$PWD/skills/idea-manager" ~/.claude/skills/idea-manager
# 或
ln -s "$PWD/skills/idea-manager" ~/.zcode/skills/idea-manager
```

连接配置走环境变量，不进仓库：

| 变量 | 说明 |
|---|---|
| `IDEA_BASE_URL` | 如 `https://ideas.example.com` |
| `IDEA_USER` | nginx basic auth 用户名 |
| `IDEA_PASS` | nginx basic auth 密码 |
| `IDEA_TOKEN` | 可选。若 nginx 侧另外校验 `X-Api-Key` 头则填 |

自检：`idea.py list` —— 打得通就说明目标实例可达、凭证正确。
