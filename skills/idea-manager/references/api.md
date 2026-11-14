# 契约与端点清单

## 契约在哪

完整契约在服务端的 `/openapi/v1.json` —— **服务端实时发**。
**skill 分发副本里没有第二份契约**（手抄进分发副本的那份会漂，所以删掉了）：
仓库根的 `openapi/idea-v1.json` 是同一份的快照，但那是**仓库里的事** ——
分发副本取不到它（`fetch.py openapi/idea-v1.json` 会 `not_found`），服务端也不发它。

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
| POST /api/ideas/{id}/complete | 标记完成 / 取消完成 | 路径参数 `id` | `done`（布尔，**必填**） | 200 |
| POST /api/ideas/{id}/restore | 从回收站恢复 | 路径参数 `id` | 无 | 200 |

### 批量与合并

| 方法 + 路径 | 说明 | 请求体字段 | 成功状态码 |
|---|---|---|---|
| POST /api/ideas/bulk/complete | 批量标记完成 / 取消完成 | `ids`（数组）、`done`（布尔，**必填**） | 200 |
| POST /api/ideas/bulk/tags | 批量加标签（默认**并集追加**；`replace: true` = **覆盖式**） | `ids`（数组）、`tags`（数组）、`replace`（布尔） | 200 |
| POST /api/ideas/bulk/delete | 批量删除（进回收站） | `ids`（数组） | 200 |
| POST /api/ideas/merge | 多条合并成一条（源想法**硬删**） | `ids`（数组）、`content`、`title` | 200 |

`replace` 不给（或给 `false`）是**并集追加**（原有标签还在）；
给 `true` 才是**覆盖式** —— 那时 `tags` 给空集就等于**清空全部标签**。
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

SSE 的帧不止一种，**别只按一种解析**：

| 帧 | 长相 | 含义 |
|---|---|---|
| 业务事件 | `id:` / `event:` / `data:` 三行一组（`data` 是单行 JSON） | 一条想法/项目的变化 |
| 控制帧 | `event: resync` / `data: {}`（**没有 `id` 行**） | 「你漏了事件，重新拉完整列表」—— 收到它要重拉列表，**不能**拿它当 `Last-Event-ID` 续发 |
| 心跳 | 一行 `: ping` 注释 | 空闲时的保活，防止中间代理掐断连接 |

断线重连时把上次收到的 `id` 放进 `?lastEventId=`（或 `Last-Event-ID` 请求头）。
curl 要加 `-N`（不缓冲），否则帧会攒着不出来。

### webhook

| 方法 + 路径 | 说明 | 入参 | 请求体字段 | 成功状态码 |
|---|---|---|---|---|
| GET /api/webhooks | 列表（裸数组，**默认遮蔽**） | `projectId`、`unmask` | 无 | 200 |
| POST /api/webhooks | 新建 | 无 | `name`、`url`、`method`、`headers`、`bodyTemplate`、`events`、`scope`、`projectId`、`secret` | 201 |
| GET /api/webhooks/templates/telegram | Telegram 模板预设（**不遮蔽**） | 无 | 无 | 200 |
| GET /api/webhooks/{id} | 读一个（**默认遮蔽**） | 路径参数 `id`；查询参数 `unmask` | 无 | 200 |
| PATCH /api/webhooks/{id} | 改 | 路径参数 `id` | `name`、`url`、`method`、`headers`、`bodyTemplate`、`events`、`enabled`、`secret` | 200 |
| DELETE /api/webhooks/{id} | 删 | 路径参数 `id` | 无 | 204 |
| GET /api/webhooks/{id}/deliveries | 投递记录（裸数组） | 路径参数 `id` | 无 | 200 |
| POST /api/webhooks/{id}/deliveries/{deliveryId}/retry | 手动重投 | 路径参数 `id`、`deliveryId` | 无 | 200 |

webhook 的字段语义（遮蔽、scope 对齐、body 模板、事件类型）见 `webhooks.md`。

## 请求体字段：不给会怎样

**「字段可以不给」这句话只对一部分端点成立。** 下面是逐端点的语义。
**先记这几条**（错了会写坏数据，或拿到与意图相反的结果）：

1. **`done` 不给 = 把想法标成未完成。**
   `POST /api/ideas/{id}/complete` 与 `POST /api/ideas/bulk/complete` 的 `done`
   是**必填的布尔**：省略它等于给 `false`（= 取消完成），服务端**不报错**。
   要标完成就得给 `{"done":true}`。
2. **`PUT /api/ideas/{id}/tags` 不给 `tags` = 清空全部标签。**
   （它是覆盖式端点，空集就等于「清空」。）
3. **`POST /api/ideas/bulk/tags` 给了 `replace: true` 却漏了 `tags` = 也清空全部标签。**
   默认是**并集追加**（`replace` 不给或 `false`），那时省略 `tags` 只是「不追加」，
   **不丢标签**；但 `replace: true` 是**覆盖式** —— 空集就等于「清空」。
4. **`PATCH /api/projects/{id}` 不给 `name` = 400。**
   项目的改名是必填的，不像想法那样「`null` = 不改」。

### 逐端点

| 端点 | 字段 | 省略 / `null` | 给空值 | 给非空值 |
|---|---|---|---|---|
| POST /api/ideas | `projectId` | 落到内置收件箱 | 空串 = 同样落收件箱 | 用它 |
| POST /api/ideas | `content` | **400 `invalid_content`**（正文必填） | 空白 = 400 | 用它 |
| POST /api/ideas | `title` | 无标题 | 空串 = 无标题 | 用它 |
| POST /api/ideas | `tags` | 不带标签 | 空数组 = 不带标签 | 用它 |
| PATCH /api/ideas/{id} | `title` | 不改 | 空串 = **真的清成无标题** | 改成它 |
| PATCH /api/ideas/{id} | `content` | 不改 | 空串 / 空白 = **400**（正文不能为空，清不掉） | 改成它 |
| PATCH /api/projects/{id} | `name` | **400 `invalid_project_name`**（必填） | 空串 = 400 | 改成它 |
| POST /api/projects | `name` | **400 `invalid_project_name`** | 空串 = 400 | 用它 |
| POST /api/projects/reorder | `ids` | **400 `invalid_reorder_ids`** | 空数组 = 400 | 用它 |
| PUT /api/ideas/{id}/tags | `tags` | **清空全部标签** | 空数组 = 清空 | 覆盖成它 |
| POST /api/ideas/{id}/complete | `done` | **必填**：省略 = `false` = **取消完成** | `null` = 400 `invalid_request_body` | `true` = 标完成 |
| POST /api/ideas/bulk/complete | `done` | 同上：省略 = **取消完成** | `null` = 400 | `true` = 标完成 |
| POST /api/ideas/bulk/complete | `ids` | 空数组 = 200 + `[]`（**什么都没改**） | 空数组 = 同上 | 用它 |
| POST /api/ideas/bulk/tags | `tags` | 空数组 = 追加空标签。**`replace` 不给或 `false` 时 = 不变**；**`replace: true` 时 = 清空全部标签** | 空数组 = 同上 | 用它 |
| POST /api/ideas/bulk/tags | `replace` | `false` = 并集追加（不变） | — | `true` = **覆盖式**（`tags` 是空集就清空） |
| POST /api/ideas/bulk/delete | `ids` | 空数组 = 200 + `[]`（**什么都没删**） | 空数组 = 同上 | 用它 |
| POST /api/ideas/merge | `ids` | **400 `invalid_merge_selection`**（至少两条） | 空数组 = 400 | 用它 |
| POST /api/ideas/merge | `content` | 服务端拼接（正文之间空行分隔） | 空串 = 400 `invalid_content` | 用它 |
| POST /api/ideas/merge | `title` | 用第一条源想法的标题 | 空串 = 无标题 | 用它 |
| PUT /api/settings/{key} | `value` | **400 `invalid_sort_mode`** | 空串 = 400 | 用它 |
| POST /api/webhooks | `name` / `url` / `scope` / `bodyTemplate` | **400**（各有各的码） | 400 | 用它 |
| POST /api/webhooks | `events` | 不订阅任何事件（合法） | 空数组 = 同上 | 用它 |
| POST /api/webhooks | `headers` | 没有请求头 | 空对象 = 同上 | 用它 |
| POST /api/webhooks | `method` | `POST` | 空串 = `POST` | 用它 |
| POST /api/webhooks | `secret` | 没有密钥 | 空串 = 没有密钥 | 用它 |
| PATCH /api/webhooks/{id} | 全部键 | 不改 | 见 `webhooks.md` 的三态表 | 用它 |

补充：

- **批量端点（`bulk/complete`、`bulk/tags`、`bulk/delete`）的 id 列表**：
  不存在的 id **直接跳过、不报错**（多端同步的常态）；
  已在回收站里的（`bulk/delete`）算**已处理**（幂等）；
  但传的 id **一个都没处理到**时是 404 `idea_not_found`；
  `ids` 给空数组则**什么都不做**（200 + `[]`）。
- **合并不跳过**（`POST /api/ideas/merge`）：语义是「这 N 条合成一条」，
  少一条就合成不了 —— 任一 id 不存在就**整体 404 `idea_not_found`**（一条都不改）；
  少于两条是 400 `invalid_merge_selection`。
- **重排不给 id 是 400**（`POST /api/projects/reorder`）：
  `ids` 省略或给空数组都是 400 `invalid_reorder_ids`。
- 想法的 `tags` 是**数组**（`["后端","导出"]`），不是逗号串；
  只有查询参数 `tags`（筛选用）是逗号分隔的一串。

## 列表的返回形状

- 列表一律是**裸数组**：`[{...}, {...}]`，没有信封对象。
- 想法列表的排序恒为 `updated_at` 升序、`id` 升序（**最老的在顶、最新的在底**）。
- 命中数超过单次返回上限时是 400 `too_many_ideas`（**不静默截断**）——
  加 `projectId` / `status` / `tags` 把范围收窄。
