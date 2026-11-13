---
name: idea-manager
description: 管理本地 idea 记事本（项目、想法、标签、完成状态、合并、回收站、webhook 通知）。当用户要记录、查看、整理、批量操作 idea/想法，或要配置 Telegram 通知时使用。
---

# idea-manager

把 idea 记事本的 REST API 包成一条命令行：`scripts/idea.py` 是纯标准库的命令行封装
（`urllib`，无第三方依赖，Python 3.9+ 即可跑），`references/openapi.json` 是后端
OpenAPI 契约的副本。

**用法**：不要手写 HTTP 请求，直接调 `scripts/idea.py`。

```bash
# 记一条想法
scripts/idea.py add "把导出功能改成异步任务" --title "导出" --tags 后端,导出

# 看未完成的想法
scripts/idea.py list --status open

# 按标签筛（同时带 a 和 b）
scripts/idea.py list --tags a,b --tag-mode and
```

## 安装

skill 目录可直接软链到 agent 的 skills 目录（两种格式一致），也可以直接复制：

```bash
ln -s "$PWD/skills/idea-manager" ~/.claude/skills/idea-manager
# 或
ln -s "$PWD/skills/idea-manager" ~/.zcode/skills/idea-manager
# 或
cp -r skills/idea-manager ~/.zcode/skills/
```

自检（打得通就说明目标实例可达、凭证正确）：

```bash
scripts/idea.py list
```

## 连接配置（只读环境变量）

命令行**不接受**密码；凭证一律走环境变量。`--help` 里也不会出现密码。

| 变量 | 必填 | 说明 |
|---|---|---|
| `IDEA_BASE_URL` | 是 | 实例地址，如 `https://ideas.example.com`。命令行 `--base-url` 可临时覆盖它 |
| `IDEA_USER` | 否 | nginx basic auth 用户名；与密码变量同时存在时发 basic auth 头 |
| `IDEA_PASS` | 否 | nginx basic auth 密码（值别写进仓库、别写进命令行） |
| `IDEA_TOKEN` | 否 | 可选。存在时额外发 `X-Api-Key: <token>` 头 |

`IDEA_BASE_URL` 与 `--base-url` 都没给时，脚本打印
`missing_config: IDEA_BASE_URL is required` 到 stderr 并以退出码 1 退出。

## 能力清单

全局选项：`--format json|text`（默认 `json`）、`--base-url <url>`。
两个选项放在子命令前后都认。

| 子命令 | 作用 | 对应 API 路径 |
|---|---|---|
| `projects` | 项目列表 | `GET /api/projects` |
| `project add <name>` | 新建项目 | `POST /api/projects` |
| `project rename <id> <name>` | 改项目名 | `PATCH /api/projects/{id}` |
| `project delete <id>` | 删项目（连带其想法进回收站） | `DELETE /api/projects/{id}` |
| `project restore <id>` | 从回收站恢复项目 | `POST /api/projects/{id}/restore` |
| `list` | 列想法 | `GET /api/ideas` |
| `add <content>` | 记一条想法 | `POST /api/ideas` |
| `update <id>` | 改标题/正文 | `PATCH /api/ideas/{id}` |
| `tags <id> <tags-csv>` | **覆盖式**设置标签 | `PUT /api/ideas/{id}/tags` |
| `done <id> [--undo]` | 标记完成/取消完成 | `POST /api/ideas/{id}/complete` |
| `delete <id>` | 删想法（进回收站） | `DELETE /api/ideas/{id}` |
| `restore <id>` | 从回收站恢复想法 | `POST /api/ideas/{id}/restore` |
| `merge <id> [<id> …]` | 多条想法合并成一条 | `POST /api/ideas/merge` |
| `bulk done <id> [<id> …] [--undo]` | 批量标记完成 | `POST /api/ideas/bulk/complete` |
| `bulk tags <id> [<id> …] <tags-csv>` | 批量加标签（并集追加，不覆盖） | `POST /api/ideas/bulk/tags` |
| `bulk delete <id> [<id> …]` | 批量删除 | `POST /api/ideas/bulk/delete` |
| `trash` | 回收站内容 | `GET /api/trash` |
| `taglist` | 标签字典（含使用次数） | `GET /api/tags` |
| `webhook list [--project <id>]` | webhook 列表 | `GET /api/webhooks` |
| `webhook add --name … --url …` | 新建 webhook | `POST /api/webhooks` |
| `webhook update <id>` | 改 webhook | `PATCH /api/webhooks/{id}` |
| `webhook delete <id>` | 删 webhook | `DELETE /api/webhooks/{id}` |
| `telegram-template` | 打印 Telegram webhook 模板预设 | `GET /api/webhooks/templates/telegram` |

### 各命令的参数

```
idea.py list [--project <id>] [--status all|open|done] [--tags a,b] [--tag-mode or|and]
idea.py add <content> [--project <id>] [--title <title>] [--tags a,b]
idea.py update <id> [--content <content>] [--title <title>]
idea.py merge <id> [<id> ...] [--content <content>] [--title <title>]
idea.py bulk tags <id> [<id> ...] <tags-csv>     # 最后一个参数是标签串，其余是 id
idea.py webhook add --name <name> --url <url> --body <template> [--headers <k=v,...>]
                     [--events <e1,e2>] [--scope global|project] [--project <id>] [--secret <s>]
idea.py webhook update <id> [--name <name>] [--url <url>] [--body <template>]
                        [--headers <k=v,...>] [--events <e1,e2>] [--enable|--disable] [--secret <s>]
```

要点：

- `--tags` / `--events` / `--headers` 都是逗号串；`--headers` 的每一项是 `键=值`。
- `bulk tags` 的参数顺序是 **id 在前、标签串在最后**：`bulk tags id1 id2 a,b`。
  它是**并集追加**（不覆盖原有标签）；要覆盖式改标签用 `tags <id> a,b`。
- `merge` 不给 `--content` 时由服务端拼接（正文之间用空行分隔）；给了就以 `--content` 为准。
  源想法会被**硬删**（不在回收站里），合并结果是一条新想法。
- `update` 与 `webhook update` **只发送用户真正给出的字段**，没给的字段一个都不发
  （后端把 `null` 当「不改」，发空串/空数组反而会真的清空）。
- `webhook add` **必须给 `--body`**：Body 模板不能为空（后端会拒，CLI 也提前拦下）。
- `webhook add` **不发送 `enabled`**：全局作用域的 webhook 创建后默认禁用，
  项目作用域的默认启用，由后端按 scope 决定。
- `--scope` **默认是 `global`**。scope 与 `--project` 必须对齐：
  全局 webhook **不能绑定项目**（给了 `--project` 会拿到 `invalid_webhook_project`），
  项目级 webhook **必须**给一个存在的项目 id（不给或项目不存在也是这个错误码）。
  要绑项目就写全：`--scope project --project <id>`。
- `--secret` 留空就不发这个键（后端的密钥三态是 `null` = 不改、`""` = 清空、非空 = 替换；
  CLI 不给空串，所以「清空」这一档 CLI 做不到）。
- `--status` / `--tag-mode` 的取值由**服务端**校验：写错了会拿到 `invalid_status` /
  `invalid_tag_mode`（退出码 1）。

## 典型场景

```bash
# 1. 记一条想法（正文里的引号/换行照原样送达）
scripts/idea.py add '引号 " 反斜杠 \ 换行
<b>标签</b>' --title "带特殊字符" --tags 笔记

# 2. 查未完成的想法
scripts/idea.py list --status open

# 3. 按标签筛：命中任意一个（默认 or）
scripts/idea.py list --tags 后端,前端
#    必须同时带两个标签
scripts/idea.py list --tags 后端,导出 --tag-mode and

# 4. 批量标记完成
scripts/idea.py bulk done id1 id2 id3
#    批量取消完成
scripts/idea.py bulk done id1 id2 --undo

# 5. 合并几条（源想法被硬删，结果是一条新想法）
scripts/idea.py merge id1 id2
#    自己指定合并后的正文
scripts/idea.py merge id1 id2 --content "合并后的正文"

# 6. 恢复误删的想法
scripts/idea.py trash                 # 先看回收站
scripts/idea.py restore <id>

# 7. 配一个 Telegram 通知（token 换成自己的）
scripts/idea.py telegram-template      # 打印模板：url + Body 模板
scripts/idea.py webhook add --name "tg" \
  --url "https://api.telegram.org/bot<你的 token>/sendMessage" \
  --body '{"chat_id":-1003800671233,"message_thread_id":89,"text":"{{idea.content}}"}' \
  --events idea.created,idea.updated
```

## 输出

- 默认 `--format json`：响应体原样 `json.dumps(…, ensure_ascii=False, indent=2)` 打到 stdout，
  便于 agent 直接解析。列表是**裸数组**，单个资源是对象。
  204（删除成功）没有响应体，这时 stdout 是 `null`。
- `--format text`：给人看的摘要。列表每行 `id  标题或正文首行`（后面跟 `#标签` 与 `[done]` 标记），
  单个资源按 `字段: 值` 逐行列。
- **webhook 的 `url` 与 `headers` 在两种格式下都脱敏**（见下）。

### 脱敏规则

webhook 的 `url` 里可能带凭据（Telegram 的 bot token 就在 URL 路径里，
Slack/Discord 的凭据在路径段里），`headers` 里可能带鉴权头（Bearer 令牌之类）。
CLI 的 stdout 会进 agent 的上下文与日志，那就是日志 —— 所以两份输出里都脱敏。

- `url` → 保留 scheme 与 host；query 与 fragment 一律丢弃（凭据常在 query 里），
  userinfo 也不保留。
- **路径段默认全遮**（`***`）—— 只有明确是结构词的段才原样保留。
  白名单（结构/协议词，不含任何凭据形态）：
  `api`、`apis`、`webhook(s)`、`hook(s)`、`service(s)`、`endpoint(s)`、`trigger(s)`、
  `incoming`、`outgoing`、`sendMessage`、`bot`，以及版本号段
  （`v1`、`v2`、`v10`……：**裸数字版本号最多两位**；带后缀的 `v1.0`、`v1-beta1`、
  `v1beta1` 也放行。`v` + 长数字不在白名单里 —— 那是纯数字 id 的形状）。
  两条例外：
  - `<BOT_TOKEN>` 占位符**原样保留** —— 它不是秘密，是给用户替换的提示。
    但**只保留占位符本身**：同段里夹带的别的东西照样遮
    （`bot<BOT_TOKEN>123456789:FAKEfake` → `bot<BOT_TOKEN>***`）；
  - `/bot<token>`（Telegram 就是这个形状，token 粘在 `bot` 后面）→ `bot***`。
- `headers` → 只保留键名，值一律换成 `***`。
- **错误信息里也不回显 headers 的内容**：`--headers` 的用法错误只报「第几项不对」
  （`usage_error: --headers 的第 1 项不是 键=值 的形式（内容不回显：它可能是凭据）`）。
  stderr 同样会进 agent 的上下文与日志 —— 回显那一项等于把凭据抄进日志。
  连键名也不回显：漏了 `=` 的那一项，键名那一半也可能就是凭据。

> 为什么是「默认全遮」而不是「看着像随机串才遮」：后者会漏掉纯字母的 token、
> 纯数字的 id 和短 token —— 而漏掉的就是凭据本身。遮错只是不好看，漏掉是泄密。

例子：

| 原地址 | 脱敏后 |
|---|---|
| `https://api.telegram.org/botXXXXXX:ABCDEF/sendMessage` | `https://api.telegram.org/bot***/sendMessage` |
| `https://api.telegram.org/bot<BOT_TOKEN>/sendMessage` | `https://api.telegram.org/bot<BOT_TOKEN>/sendMessage` |
| `https://api.telegram.org/bot<BOT_TOKEN>123456789:FAKEfake/sendMessage` | `https://api.telegram.org/bot<BOT_TOKEN>***/sendMessage` |
| `https://api.example.com/v1234567890123/hook/sk_live_abc123` | `https://api.example.com/***/hook/***` |
| `https://hooks.slack.com/services/T00000000/B00000000/abcdefghijklmnopqrstuvwx` | `https://hooks.slack.com/services/***/***/***` |
| `https://discord.com/api/webhooks/123456789012/AbCdEfGhIjKlMnOpQrStUvWxYz` | `https://discord.com/api/webhooks/***/***` |
| `https://api.example.com/v1/hook/sk_live_abc123` | `https://api.example.com/v1/hook/***` |

## 错误处理

**退出码非 0 就意味着操作没成**，不要把它当成功继续往下走。

| 情况 | stderr | 退出码 |
|---|---|---|
| 服务端返回 4xx/5xx（ProblemDetails） | `code: detail`，如 `merge_cross_project: 不能跨项目合并想法：源想法分属不同项目` | 1 |
| 响应体不是 JSON（代理返回 HTML、路径写错拿到 SPA 的 index.html） | `invalid_response: 响应不是 JSON（HTTP 502，Content-Type: text/html）；…` | 1 |
| 请求体超过服务端上限（HTTP 413；经 nginx 4 MiB，直连 Kestrel 30,000,000 字节） | 两条路径**同一个码** `http_413`：经 nginx → `http_413: 请求体超过上限（nginx 的 client_max_body_size）。`；直连 Kestrel → `http_413: HTTP 413` | 1 |
| 缺 `IDEA_BASE_URL` | `missing_config: IDEA_BASE_URL is required（也可以用 --base-url <地址> 临时覆盖）` | 1 |
| `IDEA_BASE_URL` 写错了（缺 scheme、畸形 IPv6……） | `invalid_config: IDEA_BASE_URL 不是合法的实例地址：…（要带 scheme，如 https://ideas.example.com）` | 1 |
| 命令行用错（缺 `--body`、没给要改的字段、`--headers` 不是 `键=值`） | `usage_error: …` | 1 |
| 网络不可达 / 超时 / 连接被拒 | `network_error: …` | 2 |
| argparse 的用法错误（未知选项、缺必填参数、没给子命令） | argparse 的用法提示 | 2 |
| 未预期的其它异常（兜底） | `internal_error: <消息>` | 1 |

表里的 `missing_config` / `invalid_config` / `usage_error` / `invalid_response` /
`network_error` / `internal_error` / `http_<状态码>` 是 **CLI 自己的**错误码；错误码对照表里的其余 `code`
都来自服务端。**CLI 自己的错误**只往 stderr 打**一行** `code: detail`，
不会吐 Python traceback（真实原因靠服务端日志与报告定位）。
「一行」是硬约定：detail 里的换行会被压成空格（异常消息常带换行）。
argparse 的用法错误不在此列：它打的是多行 usage 提示、退出码 2（见上表）。

其中 `http_<状态码>` 是 CLI **自己造**的码（服务端的 `code` 里没有它）：服务端回了非 2xx，
但响应体是空的、或者是个没有 `code` 字段的 JSON —— CLI 拿不到业务错误码，
就按状态码造一个 `http_413` / `http_502` 这样的码。
**要按状态码分支时看这个码**，别去错误码对照表里找（那儿没有它）。
这个码里最常遇到的两个：**413 = 请求体太大**（不是业务错误，见下）、**502 = 网关/上游挂了**。

**HTTP 413（请求体太大）不是 API 的业务错误**：错误码对照表里没有它，服务端也来不及生成
ProblemDetails —— 请求体在**读 body 阶段**就被拒掉了。

**两条部署路径给 agent 的是同一个码 `http_413`**，按码分支即可，**不必知道走了哪条路**：
- **经 nginx（仓库自带的 `docs/nginx/idea.conf`）** → nginx 先撞上自己的
  `client_max_body_size`，回的是**结构化 JSON**（ProblemDetails 形状，但**刻意没有 `code` 字段**）→
  CLI 报 `http_413: 请求体超过上限（nginx 的 client_max_body_size）。`；
- **直连 Kestrel** → 框架在读 body 阶段直接拒掉，回的是**空响应体** →
  CLI 报 `http_413: HTTP 413`。

两种形态的 **detail 文字不一样**，但**码一样**：都表示「请求体太大」，
处理办法也一样 —— **把 body 调小再试**，别去错误码对照表里找它。

上限分两种部署形态（两个上限都不是本项目配的）：
- **经 nginx（仓库自带的 `docs/nginx/idea.conf`）** → `client_max_body_size 4m`，
  上限是 **4 MiB（4194304 字节）**（nginx 的 `m` 是 1024 进位）。
  那个上限在 `docs/nginx/idea.conf` 里可改。
- **直连 Kestrel** → 框架默认上限 **30,000,000 字节**（项目没配 `MaxRequestBodySize`），
  响应体**是空的**。

改了 nginx 的 `client_max_body_size` 或 Kestrel 的请求体上限，这里也要跟着改 ——
`server/IdeaApp.Tests` 里有守卫盯着这几个数字，对不上时测试会红。

### 错误码对照表

`code` 是后端给的稳定机器可读错误码（在 ProblemDetails 的扩展字段里）。
**拿 `code` 分支处理，不要去匹配 `detail` 的文字**（detail 是给人看的，会改）。

| code | 含义 | agent 该怎么办 |
|---|---|---|
| `idea_not_found` | 想法不存在 | 重新拉列表，可能是别的设备删了 |
| `project_not_found` | 项目不存在 | 同上 |
| `inbox_not_deletable` | 内置收件箱不能删 | 换个项目 |
| `invalid_content` | 正文为空或全空白 | 补上正文再提交 |
| `invalid_request_body` | 请求体不是合法 JSON（模型绑定阶段就失败了） | 检查发出去的 body 是不是一个 JSON 文档 |
| `unsupported_media_type` | Content-Type 不是 application/json（415） | 带上 `Content-Type: application/json` 再发 |
| `invalid_project_name` | 项目名为空或全空白 | 补上名字 |
| `invalid_reorder_ids` | 重排的 ids 为空 | 别发空的 reorder |
| `invalid_sort_mode` | 排序方式不是 `recent` / `manual` | 用这两个值之一 |
| `setting_key_not_writable` | 该设置项不可经 API 修改 | 只能改 `project.sort_mode` |
| `invalid_status` | `status` 参数不是 all/open/done | 用这三个值之一 |
| `invalid_tag_mode` | `tagMode` 不是 or/and | 用这两个值之一 |
| `invalid_merge_selection` | 合并时少于两条 | 至少选两条 |
| `merge_cross_project` | 合并的想法不属于同一项目 | 先把它们移到同一项目 |
| `not_in_trash` | 要恢复的东西不在回收站里 | 先拉 `GET /api/trash` 确认 |
| `too_many_ideas` | 命中数超过 `Api:MaxIdeasPerResponse`（默认 2000） | 收窄过滤条件（加 projectId / status / tags） |
| `invalid_webhook_name` | webhook 名称为空 | 补上名称 |
| `invalid_webhook_url` | webhook 的 URL 为空或不是合法绝对地址 | 换成 http(s) 绝对地址 |
| `invalid_webhook_scope` | scope 不是 `global` / `project` | 用这两个值之一 |
| `invalid_webhook_project` | scope 与 project_id 不一致：**scope=project** 却没给 project_id（或该项目不存在）；**scope=global** 却带了 project_id（全局 webhook 不能绑定项目） | scope 与 `--project` 对齐：global 就别给 `--project`；project 就给一个存在的项目 id |
| `invalid_body_template` | Body 模板不是合法 JSON | 模板本身必须是一个 JSON 文档 |
| `invalid_event_type` | 订阅的事件名不在 8 种事件类型里 | 用下面那组事件名 |
| `webhook_not_found` | webhook 不存在 | 重新拉列表，可能是别的设备删了 |
| `api_not_found` | 请求的接口不存在（路径写错了）—— **任何未匹配的 `/api/**` 路径都回这个码**（不只是「路径写错了」）；「路径不存在」有稳定的机器码可分支，不必去猜 404 的含义 | 核对命令名对应的 API 路径；完整契约见 `references/openapi.json` |
| `delivery_not_found` | 投递记录不存在 | 重新拉该 webhook 的投递列表 |
| `delivery_queue_full` | 投递队列已满（503），**这次手动重投没排上** | 稍后重试；事件路径的队列满是静默丢弃 + 日志，不会返回 503 |

另有 500 的 `internal_error`：服务端兜底码，含义是「未预期的异常，看服务端日志」，
agent 侧只需当成「重试或报错」，不必分支处理。

这张表的权威来源是后端的 `server/IdeaApp/Contracts/ApiErrorCodes.cs` ——
OpenAPI 契约里的错误码说明也是从它生成的。改错误码时三处要一起改：
`ApiErrorCodes.cs`、本表、以及后端实际抛错的地方。

### webhook 订阅的 8 种事件类型

`idea.created`、`idea.updated`、`idea.deleted`、`idea.restored`、`idea.merged`、
`project.created`、`project.updated`、`project.deleted`。

## 完整接口契约

`references/openapi.json` 是 `openapi/idea-v1.json` 的逐字副本（OpenAPI 3.1，25 条路径）：
每个端点的参数、请求体、响应体 schema、状态码与错误码都在里面。

**CLI 没覆盖的能力**（项目重排 `POST /api/projects/reorder`、设置项 `PUT /api/settings/{key}`、
webhook 详情与投递记录 `GET /api/webhooks/{id}`、`GET /api/webhooks/{id}/deliveries`、
手动重投 `POST /api/webhooks/{id}/deliveries/{deliveryId}/retry`、SSE `GET /api/events`）
直接照契约调 API。

## 端到端测试

```bash
bash skills/idea-manager/scripts/run-tests.sh
```

脚本会起一个真实的后端实例（临时 SQLite 库）、跑 `tests/` 下的 unittest，再把后端收掉。
