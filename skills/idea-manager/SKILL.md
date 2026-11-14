---
name: idea-manager
description: 管理本地 idea 记事本（项目、想法、标签、完成状态、合并、回收站、webhook 通知）。当用户要记录、查看、整理、批量操作 idea/想法，或要配置 Telegram 通知时使用。
---

# idea-manager

## 这是什么

idea 记事本的 REST API：项目、想法、标签、完成状态、合并、回收站、webhook 通知。
**直接用 curl 调** —— 本文件是索引页：有哪些能力、怎么调、细节去哪找、最容易错在哪。

内容由服务端分发，与服务端**同一版本**：服务端改了 API，这里跟着改。

## 连接配置

| 变量 | 必填 | 说明 |
|---|---|---|
| `IDEA_BASE_URL` | 是 | 实例地址，如 `https://ideas.example.com` |
| `IDEA_USER` | 否 | nginx basic auth 用户名；与密码同时存在时才发 |
| `IDEA_PASS` | 否 | nginx basic auth 密码 |
| `IDEA_TOKEN` | 否 | 可选。存在时额外发 `X-Api-Key: $IDEA_TOKEN` 头 |

有 `IDEA_USER`/`IDEA_PASS` 才加 `-u "$IDEA_USER:$IDEA_PASS"`；`IDEA_TOKEN` 有值才加
`-H "X-Api-Key: $IDEA_TOKEN"`。**别把凭据写进 URL** —— nginx 会把 userinfo 写进访问日志。

## curl 模板

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

带请求体就要带 `Content-Type: application/json`（不带是 415）。
样板里的 `-u` **只在有凭据时保留** —— `IDEA_USER`/`IDEA_PASS` 都没有时删掉它
（带上空用户名密码会白跑一趟拿个 401）。
响应怎么读、特殊字符怎么原样送达，见 `references/calling.md`。

## 能力清单

| 能力 | 方法 + 路径 |
|---|---|
| 健康检查 | GET /api/health |
| 想法列表（可按项目 / 状态 / 标签筛） | GET /api/ideas |
| 记一条想法 | POST /api/ideas |
| 改一条想法（只发要改的字段） | PATCH /api/ideas/{id} |
| 删一条想法（进回收站） | DELETE /api/ideas/{id} |
| 覆盖式设置标签（**不给 `tags` = 清空**） | PUT /api/ideas/{id}/tags |
| 标记完成 / 取消完成（`done` **必填**） | POST /api/ideas/{id}/complete |
| 从回收站恢复想法 | POST /api/ideas/{id}/restore |
| 批量标记完成 / 取消完成（`done` **必填**） | POST /api/ideas/bulk/complete |
| 批量加标签（并集追加） | POST /api/ideas/bulk/tags |
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

## 路由表

| agent 想做的事 | 取哪份 |
|---|---|
| 请求体怎么写、特殊字符怎么办、认证怎么带 | `references/calling.md` |
| 拿到一个 `code`、要判断该怎么处理 | `references/errors.md` |
| 配 webhook、订阅事件、url 与 headers 的遮蔽 | `references/webhooks.md` |
| 某个端点的完整参数与 schema | `references/api.md` |
| 要一个能照抄的完整例子 | `references/scenarios.md` |

取法：`scripts/fetch.py references/calling.md`（不带参数就是本索引页）。

## 最易错的坑

1. **覆盖式设标签**是 `PUT /api/ideas/{id}/tags`；**并集追加**是 `POST /api/ideas/bulk/tags`
   （但它给 `replace: true` 时也是覆盖式 —— `tags` 漏了就把标签清空）。用错了会丢标签。
2. **`PATCH` 只发要改的字段**：`null` = 不改；**空串 / 空数组常常是「真的清空」**
   （想法的 `title` 空串 = 清成无标题；webhook 的 `headers` 给 `{}` = 清空请求头）——
   但**不是所有字段都吃这一套**：想法的 `content` 与项目的 `name` 给空串是 400，
   项目的 `name` 还**必填**（不给就是 400 `invalid_project_name`）。
   逐端点的语义见 `references/api.md`。
3. `POST /api/webhooks` **必须给 body 模板**；`scope=global` **不能**带 `projectId`，
   `scope=project` **必须**带一个存在的项目 id。
4. `POST /api/ideas/merge` 会**硬删**源想法（不在回收站里）。
5. webhook 的 url 与 headers **默认是遮蔽版**，拿到的不是原值 ——
   要原值得显式加 `?unmask=true`，而**除非用户明确要看原始 url，否则不要加这个参数**。
6. **`done` 必填**：`POST /api/ideas/{id}/complete` 与 `POST /api/ideas/bulk/complete`
   不给 `done` = `false` = **取消完成**，服务端**不报错**。要标完成就给 `{"done":true}`。

## 错误的最短约定

- 4xx / 5xx 回 RFC 9457 的 ProblemDetails（`application/problem+json`），
  顶层带机器可读的扩展字段 `code`。
- **按 `code` 分支，不要去匹配 `detail` 的文字**（detail 是给人看的，会改）。
- 错误码全表在 `references/errors.md`。413（请求体太大）不是业务错误，也见那一节。

## 契约位置

取件器的三种用法：

| 用法 | 拿到什么 |
|---|---|
| `scripts/fetch.py` | 索引页（本文件） |
| `scripts/fetch.py references/errors.md` | 分发副本里的某一份细节 |
| `scripts/fetch.py /openapi/v1.json` | 服务端的完整契约 |

完整契约在服务端的 `/openapi/v1.json` —— **实时发**，仓库里没有副本，契约里有全部路径。
也可以直接
`curl -sS "$IDEA_BASE_URL/openapi/v1.json" -u "$IDEA_USER:$IDEA_PASS"`
（这个端点同样要 basic auth，没凭据时删掉 `-u`）。
