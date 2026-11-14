# 契约与端点清单

## 契约在哪

完整契约在服务端的 `/openapi/v1.json` —— **服务端实时发**，仓库里没有副本
（副本会漂，所以删掉了）。

```bash
# 用取件器取（它会同步说明书，再打印这一份）
scripts/fetch.py /openapi/v1.json

# 或直接用 curl 取
curl -sS "$IDEA_BASE_URL/openapi/v1.json" -u "$IDEA_USER:$IDEA_PASS"
```

契约（OpenAPI 3.1）里有**全部路径**：每个端点的参数、请求体 schema、响应体 schema、
状态码与可能出现的错误码都在里面。**要精确的字段与类型就去读它**，下面只是速查表。

## 端点速查

「入参」列里没写「路径参数」的，就是查询参数；「请求体」列里没写的，就是没有请求体。

### 想法

| 方法 + 路径 | 说明 | 入参 | 请求体字段 | 成功状态码 |
|---|---|---|---|---|
| GET /api/ideas | 列表（裸数组） | `projectId`、`status`、`tags`、`tagMode` | 无 | 200 |
| POST /api/ideas | 新建一条想法 | 无 | `projectId`、`content`、`title`、`tags` | 201 |
| PATCH /api/ideas/{id} | 改标题/正文 | 路径参数 `id` | `title`、`content` | 200 |
| DELETE /api/ideas/{id} | 删（进回收站） | 路径参数 `id` | 无 | 204 |
| PUT /api/ideas/{id}/tags | **覆盖式**设标签 | 路径参数 `id` | `tags`（数组） | 200 |
| POST /api/ideas/{id}/complete | 标记完成 / 取消完成 | 路径参数 `id` | `done`（布尔） | 200 |
| POST /api/ideas/{id}/restore | 从回收站恢复 | 路径参数 `id` | 无 | 200 |

### 批量与合并

| 方法 + 路径 | 说明 | 请求体字段 | 成功状态码 |
|---|---|---|---|
| POST /api/ideas/bulk/complete | 批量标记完成 / 取消完成 | `ids`（数组）、`done`（布尔） | 200 |
| POST /api/ideas/bulk/tags | 批量加标签（**并集追加**） | `ids`（数组）、`tags`（数组）、`replace`（布尔） | 200 |
| POST /api/ideas/bulk/delete | 批量删除（进回收站） | `ids`（数组） | 200 |
| POST /api/ideas/merge | 多条合并成一条（源想法**硬删**） | `ids`（数组）、`content`、`title` | 200 |

`replace` 不给（或给 `false`）是**并集追加**；给 `true` 才是覆盖式。
`merge` 不给 `content` 时由服务端拼接（正文之间空行分隔），给了就以 `content` 为准。

### 项目与设置

| 方法 + 路径 | 说明 | 入参 | 请求体字段 | 成功状态码 |
|---|---|---|---|---|
| GET /api/projects | 项目列表（裸数组） | 无 | 无 | 200 |
| POST /api/projects | 新建项目 | 无 | `name` | 201 |
| PATCH /api/projects/{id} | 改项目名 | 路径参数 `id` | `name` | 200 |
| DELETE /api/projects/{id} | 删项目（连带想法进回收站） | 路径参数 `id` | 无 | 204 |
| POST /api/projects/{id}/restore | 从回收站恢复项目 | 路径参数 `id` | 无 | 200 |
| POST /api/projects/reorder | 重排项目顺序 | 无 | `ids`（数组） | 200 |
| GET /api/settings | 读设置（**扁平键值字典**） | 无 | 无 | 200 |
| PUT /api/settings/{key} | 改设置项 | 路径参数 `key` | `value` | 200 |

只有 `project.sort_mode` 能改（其它键是 `setting_key_not_writable`），取值 `recent` 或 `manual`。

### 标签、回收站、事件

| 方法 + 路径 | 说明 | 入参 | 成功状态码 |
|---|---|---|---|
| GET /api/tags | 标签字典（含使用次数，裸数组） | 无 | 200 |
| GET /api/tags/suggest | 标签补全建议（裸数组） | `projectId`、`prefix` | 200 |
| GET /api/trash | 回收站内容（裸数组） | 无 | 200 |
| GET /api/health | 健康检查 | 无 | 200 |
| GET /api/events | **SSE 事件流**（长连接） | `lastEventId` | 200 |

SSE 的帧是 `id:` / `event:` / `data:` 三行一组，`data` 是单行 JSON；
空闲时服务端发一行 `: ping` 注释防连接被掐断。
断线重连时把上次收到的 `id` 放进 `?lastEventId=`（或 `Last-Event-ID` 请求头）。
curl 要加 `-N`（不缓冲），否则帧会攒着不出来。

### webhook

| 方法 + 路径 | 说明 | 入参 | 请求体字段 | 成功状态码 |
|---|---|---|---|---|
| GET /api/webhooks | 列表（裸数组，**默认遮蔽**） | `projectId`、`unmask` | 无 | 200 |
| POST /api/webhooks | 新建 | 无 | `name`、`url`、`method`、`headers`、`bodyTemplate`、`events`、`scope`、`projectId`、`secret` | 201 |
| GET /api/webhooks/templates/telegram | Telegram 模板预设（**不遮蔽**） | 无 | 无 | 200 |
| GET /api/webhooks/{id} | 读一个（**默认遮蔽**） | 路径参数 `id`、`unmask` | 无 | 200 |
| PATCH /api/webhooks/{id} | 改 | 路径参数 `id` | `name`、`url`、`method`、`headers`、`bodyTemplate`、`events`、`enabled`、`secret` | 200 |
| DELETE /api/webhooks/{id} | 删 | 路径参数 `id` | 无 | 204 |
| GET /api/webhooks/{id}/deliveries | 投递记录（裸数组） | 路径参数 `id` | 无 | 200 |
| POST /api/webhooks/{id}/deliveries/{deliveryId}/retry | 手动重投 | 路径参数 `id`、`deliveryId` | 无 | 200 |

webhook 的字段语义（遮蔽、scope 对齐、body 模板、事件类型）见 `webhooks.md`。

## 请求体字段的通用约定

- 请求体字段**都可以是 `null`**：`null` = 不改（PATCH）或用默认值（新建）。
- 发空串 / 空数组**会真的清空** —— 那是「我要清掉它」，不是「没给」。
- 想法的 `tags` 是**数组**（`["后端","导出"]`），不是逗号串；
  只有查询参数 `tags`（筛选用）是逗号分隔的一串。

## 列表的返回形状

- 列表一律是**裸数组**：`[{...}, {...}]`，没有信封对象。
- 想法列表的排序恒为 `updated_at` 升序、`id` 升序（**最老的在顶、最新的在底**）。
- 命中数超过单次返回上限时是 400 `too_many_ideas`（**不静默截断**）——
  加 `projectId` / `status` / `tags` 把范围收窄。
