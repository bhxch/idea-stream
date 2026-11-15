# 错误

调 API 失败时看这一页。分两块：**服务端回的错**（业务错误码）与**取件器自己的错**
（说明书没取到）。

## 错误响应长什么样

4xx / 5xx 回的是 RFC 9457 的 ProblemDetails（`Content-Type: application/problem+json`）：

```json
{
  "title": "请求失败",
  "status": 400,
  "detail": "想法正文为空或全是空白",
  "code": "invalid_content"
}
```

- `code` 是**机器可读的稳定错误码**，在**顶层**（不在 `extensions` 里）。
  **按 `code` 分支处理，不要去匹配 `detail` 的文字** —— detail 是给人看的，会改。
- `type` 与 `instance` 是 RFC 9457 的标准字段，本应用不填（是 `null`）。
- 出错时**状态码也有用**：400 多半是请求本身不合法，404 是东西不存在，
  415 是 `Content-Type` 不对，503 是服务暂时接不住。

## 服务端错误码

这张表是**全集**：业务错误码的权威来源是 `server/IdeaApp/Contracts/ApiErrorCodes.cs`，
契约文件里的错误码说明也是从它生成的。改错误码时两处一起改。

| code | 含义 | agent 该怎么办 |
|---|---|---|
| `inbox_not_deletable` | 内置收件箱不能删除 | 换个项目 |
| `invalid_body_template` | Body 模板不是合法的 JSON 文档 | 模板本身必须是一个 JSON 文档 |
| `invalid_content` | 想法正文为空或全是空白 | 补上正文再提交 |
| `invalid_event_type` | 订阅的事件名不在已知事件类型里 | 用 `webhooks.md` 里那组事件名 |
| `invalid_merge_selection` | 合并至少需要两条想法 | 至少选两条 |
| `invalid_pagination` | limit / offset 不是整数，或超出允许范围 | limit 传范围内的整数（想法列表 1..1000，全库搜索 1..100）；offset 传非负整数（想法列表里它必须配 limit 一起给） |
| `invalid_project_name` | 项目名称为空或全是空白 | 补上名字 |
| `invalid_reorder_ids` | 重排请求没有给出任何项目 id | 别发空的 reorder |
| `invalid_request_body` | 请求体不是合法的 JSON（模型绑定阶段的失败） | 检查发出去的 body 是不是一个 JSON 文档 |
| `invalid_search_query` | 全库搜索的 `q` 缺失或全是空白 | 给出非空的搜索词 |
| `invalid_sort_mode` | 排序方式只能是 recent 或 manual | 用这两个值之一 |
| `invalid_status` | status 参数只支持 all / open / done | 用这三个值之一 |
| `invalid_tag_mode` | tagMode 参数只支持 or / and | 用这两个值之一 |
| `invalid_webhook_name` | webhook 名称为空 | 补上名称 |
| `invalid_webhook_project` | 项目级 webhook 必须指定一个存在的项目，全局 webhook 不能绑定项目 | scope 与 projectId 对齐（见 `webhooks.md`） |
| `invalid_webhook_scope` | webhook 的作用域只能是 global 或 project | 用这两个值之一 |
| `invalid_webhook_url` | webhook 的 URL 为空或不是 http(s) 绝对地址 | 换成 http(s) 绝对地址 |
| `merge_cross_project` | 不能跨项目合并想法 | 先把它们移到同一项目 |
| `not_in_trash` | 要恢复的东西不在回收站里 | 先拉 GET /api/trash 确认 |
| `setting_key_not_writable` | 该设置项不可经 API 修改（只有 project.sort_mode 可以） | 只能改 project.sort_mode |
| `too_many_ideas` | 命中数超过单次返回上限，请收窄过滤条件 | 收窄过滤条件（加 projectId / status / tags）或给 `limit` 分页 |
| `api_not_found` | 请求的接口不存在（路径打错或接口已下线） | 核对路径；完整契约见 `/openapi/v1.json` |
| `delivery_not_found` | 投递记录不存在 | 重新拉该 webhook 的投递列表 |
| `idea_not_found` | 想法不存在 | 重新拉列表，可能是别的设备删了 |
| `project_not_found` | 项目不存在 | 同上 |
| `skill_endpoint_not_found` | /skill 下的路径没有对应端点（拼错了 skill 名或路径） | 核对 skill 名与路径 |
| `skill_file_not_found` | 分发副本里没有这个文件（编码形态的路径穿越也走这里） | 核对路径 |
| `skill_not_distributed` | 服务端没有分发 skill 内容（分发副本的路径没配、目录不存在或缺少入口文件） | 部署漏了分发目录 |
| `webhook_not_found` | webhook 不存在 | 重新拉列表 |
| `unsupported_media_type` | 请求的 Content-Type 不是 application/json | 带上 `Content-Type: application/json` 再发 |
| `delivery_queue_full` | 投递队列已满，请稍后重试 | 稍后重试；**只有手动重投**会返回这个码，事件路径（自动投递）不返回 503 —— 它丢掉这次投递并记一条 failed 的投递记录，页面「投递记录」里能看到失败原因 |

另有 500 的 `internal_error`：服务端兜底码，含义是「未预期的异常，看服务端日志」，
agent 侧只需当成「重试或报错」，不必分支处理。

## 分发副本里的单个文件有大小上限

服务端分发的 skill 内容目录（分发副本）里，**每个文件**的上限是
**4 MiB（4194304 字节）**。超限的那一份**不在分发副本里**：

- 清单（`/skill/idea-manager/version`）里**不列**它；
- 取它也**取不到** → 404 `skill_file_not_found`（详情里会写明「大小上限」）。

所以拿到 `skill_file_not_found` 而路径看着没问题时，先看一眼那个文件多大：
超限的那一份要么改小，要么从分发副本里拿掉。
手册里每份都是几十 KB 的文档，离这个上限远得很。

## 路径里带 `..` 会发生什么

服务端**会**把路径里的点号段归一化掉（RFC 3986），所以同一个「穿越」请求有三种结果：

| 请求 | 结果 |
|---|---|
| `/skill/idea-manager/../../etc/passwd` | 归一化成 `/etc/passwd` → 逃出 `/skill/` 前缀 → 落到**前端兜底** → **200 + HTML**（不是 404，也读不到任何文件） |
| `/skill/idea-manager/..%2f..%2fetc%2fpasswd` | `%2f` 不是路径分隔符，归一化不认它 → 路径原样进端点 → **404 + `skill_file_not_found`** |
| `/skill/idea-manager/a/../SKILL.md` | 归一化成 `/skill/idea-manager/SKILL.md` → **200 + 手册原文** |

**读到 HTML 就说明路径写错了**：200 只表示「服务端有东西给」，不表示「给的是你要的那一份」。
取件器在同步时会逐个文件校验 sha256 —— 服务端给回来的内容与清单对不上
（多半是被前端兜底截了）时，它报 `skill_integrity_error` 而不是把 HTML 当手册用。

## 取件器的错误码

取件器（`scripts/fetch.py`）自己失败时，往 stderr 打**一行** `code: detail`，退出码非 0，
**不会**吐 Python traceback。

| code | 什么时候 | 退出码 | agent 该怎么办 |
|---|---|---|---|
| `missing_config` | 缺 `IDEA_BASE_URL` | 1 | 设上它 |
| `invalid_config` | `IDEA_BASE_URL` 非法（没 scheme / 没 host） | 1 | 写成 `https://ideas.example.com` 这样带 scheme 的 |
| `network_error` | 网络不可达 / 超时 / 连接被拒 | 2 | 检查地址与网络 |
| `auth_error` | 服务端拒了鉴权（401 / 403） | 1 | 检查 `IDEA_USER` / `IDEA_PASS` / `IDEA_TOKEN` |
| `not_found` | 要取的文件不在分发副本里 | 1 | 核对路径 |
| `skill_integrity_error` | 取回来的内容与服务端清单对不上（哈希不匹配），或清单本身的字段不合法 | 1 | 重试一次；一直这样就是服务端那边在改内容 |
| `invalid_response` | 清单不是合法 JSON（多半是代理返回了 HTML） | 1 | 检查是不是被代理劫持了 |
| `skill_not_distributed` | 服务端没有分发 skill 内容 | 1 | 部署漏了分发目录 |
| `degraded_cache` | 服务端连不上，用的是本地缓存里的**上一版**手册 | 3 | 内容可能已过时：重试一次，或等服务端恢复 |
| `http_<状态码>` | 服务端回了非 2xx，但响应体是空的、或没有 `code` 字段（代理/网关层，或经 nginx 的 413） | 1 | **按状态码分支**（`http_502` = 网关或上游挂了，`http_413` = 请求体太大）；**错误码对照表里没有它**，别去表一找 |
| `internal_error` | 服务端 500 的兜底码（透传下来），或取件器自己的兜底（未预期的异常） | 1 | 看服务端日志；重试一次 |

取件器把**任何 404**（除了 `skill_not_distributed`）都折叠成 `not_found` ——
`skill_file_not_found` 与 `skill_endpoint_not_found` 是**服务端**的码（见上面表一），
取件器不输出它们，agent 收到的是 `not_found`。

`internal_error` **两边同名**：服务端的 500 兜底码会被原样透传下来，取件器自己的兜底
也用它。两种情况都当成「重试，或去看服务端日志」。

**降级**（`degraded_cache`，退出码 **3**）：服务端暂时连不上、但本地有**完好**的缓存时，
取件器不报错 —— 它在 stderr 上打一行 `degraded_cache:`，然后把缓存里那一份打到 stdout。
这时拿到的是**上一版**手册（不是服务端的当前版本）。
**退出码 3 是专属的**：只看退出码、不看 stderr 的调用方（stdout 被重定向到文件、
stderr 被丢掉）靠的就是它 —— 上一版手册会让 agent 按旧语义操作，那正是这套改造要消除的东西。

**重定向**：取件器只跟随**同源**的重定向。跨到别的 origin 时它不跟随
（凭据不跟着出本机），而是当成一个 302 报出来 —— 状态码那一档归 `http_<状态码>`。

## HTTP 413（请求体太大）

**HTTP 413（请求体太大）不是 API 的业务错误**：错误码对照表里没有它，服务端也来不及生成
ProblemDetails —— 请求体在**读 body 阶段**就被拒掉了。

**两条部署路径给 agent 的是同一个信号**（HTTP 状态码 413），**不必知道走了哪条路**：
- **经 nginx（仓库自带的 `docs/nginx/idea.conf`）** → nginx 先撞上自己的
  `client_max_body_size`，回的是**结构化 JSON**（ProblemDetails 形状，但**刻意没有 `code` 字段**）→
  响应体里有 `detail`，但**没有 `code` 字段**可分支；
- **直连 Kestrel** → 框架在读 body 阶段直接拒掉，回的是**空响应体** →
  curl 只看得到状态码 413，响应体一个字节都没有。

两种形态的 **detail 文字不一样**，但**信号一样**：都表示「请求体太大」，
处理办法也一样 —— **把 body 调小再试**，别去错误码对照表里找它。

上限分两种部署形态（两个上限都不是本项目配的）：
- **经 nginx（仓库自带的 `docs/nginx/idea.conf`）** → `client_max_body_size 4m`，
  上限是 **4 MiB（4194304 字节）**（nginx 的 `m` 是 1024 进位）。
  那个上限在 `docs/nginx/idea.conf` 里可改。
- **直连 Kestrel** → 框架默认上限 **30,000,000 字节**（项目没配 `MaxRequestBodySize`），
  响应体**是空的**。

改了 nginx 的 `client_max_body_size` 或 Kestrel 的请求体上限，这里也要跟着改 ——
`server/IdeaApp.Tests` 里有守卫盯着这几个数字，对不上时测试会红。
