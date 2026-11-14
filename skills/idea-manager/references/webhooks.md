# webhook

webhook 是「想法变了就往外部发通知」。这一页讲它的三条特殊语义：
**凭据默认遮蔽**、**作用域与项目要对齐**、**body 模板必须给**。

## 凭据默认遮蔽

webhook 的 `url` 里可能有凭据（Telegram 的 bot token 就在 URL 的路径段里），
`headers` 里可能有鉴权头。**读接口默认回传遮蔽版**：

| 字段 | 遮蔽后长什么样 |
|---|---|
| `url` | 只留 `scheme://host[:port]`，路径的**每一段**换成 `***`（段数不变）；query、fragment、userinfo 一律丢弃 |
| `headers` | 只保留键名，值一律换成 `***` |

例：真实地址 `https://api.telegram.org/bot123456:ABCDEF/sendMessage` →
遮蔽版 `https://api.telegram.org/***/***`。

- 要完整值得显式加 `?unmask=true`（`GET /api/webhooks` 与 `GET /api/webhooks/{id}` 都认）。
  **除非用户明确要看原始 url，否则不要加这个参数** —— 加了它，凭据就进 agent 的上下文与日志了。
- **写接口（`POST` / `PATCH`）的响应一律是遮蔽版**，加不加 `unmask` 都一样。
- `GET /api/webhooks/templates/telegram` **不遮蔽**：那是个预设，url 里是 `<BOT_TOKEN>` 占位符，
  不是真凭据。

**改 webhook 的其它字段时，别把读到的遮蔽值写回去。** `url` 与 `headers` 这两个键
在 `PATCH` 里**整个别发** —— 发了遮蔽值就是把真凭据改成 `***` 了。

## PATCH 的每一键怎么算

| 键 | 不发这个键 | 发 `null` | 发空值 | 发非空值 |
|---|---|---|---|---|
| `name` | 不改 | 不改 | 报错（空名） | 改名 |
| `url` | 不改 | 不改 | 报错（空地址） | 换地址 |
| `method` | 不改 | 不改 | 改成 `POST` | 换方法 |
| `headers` | 不改 | 不改 | **清空所有请求头** | 整体替换 |
| `bodyTemplate` | 不改 | 不改 | 报错（模板不能空） | 换模板 |
| `events` | 不改 | 不改 | 不订阅任何事件 | 换订阅 |
| `enabled` | 不改 | 不改 | —（布尔没有「空值」这一档） | `false` = **禁用**、`true` = 启用 |
| `secret` | 不改 | 不改 | **清空密钥** | 换密钥 |

一句话：**`url` 与 `headers` 只在你真的要改它们时才发**（而且要发真值），
其余的键按上表的三态来。

## 作用域与项目要对齐

`scope` 只有两个值：`global`（全局）与 `project`（项目级）。

- `scope=global` **不能**带 `projectId`（带了是 `invalid_webhook_project`）。
- `scope=project` **必须**带一个**存在的**项目 id（不给或项目不存在也是这个错误码）。
- `scope` 在新建时**必填**（不写会拿到 `invalid_webhook_scope`）。
- 作用域**不可改**：想换作用域就删了重建。
- 启用状态由作用域决定：**全局 webhook 创建后默认禁用**，**项目级默认启用**。
  新建请求里没有 `enabled` 这个键；要用就建成项目级，或事后 `PATCH` 改。

## body 模板必须给

`POST /api/webhooks` **必须给 `bodyTemplate`**：空模板或不是合法 JSON 都是
`invalid_body_template`。

模板是一个 **JSON 对象**（不是数组、不是裸字符串），占位符 `{{...}}` 只能出现在
**字符串值**里（写在对象的键里会被拒）。可用的占位符：

| 占位符 | 内容 |
|---|---|
| `{{event}}` | 事件类型（如 `idea.created`） |
| `{{timestamp}}` | 事件时间（ISO 8601） |
| `{{project}}` / `{{project.id}}` | 项目名 / 项目 id |
| `{{idea.id}}` / `{{idea.title}}` / `{{idea.content}}` | 想法的 id / 标题 / 正文 |
| `{{idea.tags}}` | 标签（逗号分隔） |
| `{{idea.done}}` | 是否完成（`true` / `false`） |
| `{{changed}}` | 这次改了哪些字段（逗号分隔） |
| `{{idea.url}}` | 想法的深链（没配公开地址时是空串） |

认不出来的占位符**原样保留**（不会让投递炸掉）。事件里没有的字段渲染成空串。

值是在 **JSON 字符串上下文**里转义后注入的 —— 正文里的引号、换行、反斜杠不会破坏 JSON。

## 订阅的事件类型

只有这 8 种（写别的名字是 `invalid_event_type`）：

`idea.created`、`idea.updated`、`idea.deleted`、`idea.restored`、`idea.merged`、
`project.created`、`project.updated`、`project.deleted`。

`events` 给空数组是合法的（= 不订阅任何事件）。注意 `idea.updated` 覆盖
改正文/标题、换标签、标记完成；`idea.merged` 是合并（源想法被硬删）。

## 其它字段

- `headers`：**只在创建/更新时给**（读回来的值是遮蔽版）。字典，键是请求头名。
- `secret`：只进不出 —— 响应里只有 `hasSecret` 这个布尔值。
  三态：`null` = 不改、`""` = 清空、非空 = 替换（首尾空白会被裁掉）。
- `method`：投递用的 HTTP 方法，缺省是 `POST`。
- `name`：不能为空。

## 投递记录与手动重投

- `GET /api/webhooks/{id}/deliveries` 返回**裸数组**（最近的在前），每条是
  `{id, webhookId, eventId, eventType, payload, status, attempts, lastError, createdAt, updatedAt}`。
  `payload` 是**已经渲染好**的正文。
- `POST /api/webhooks/{id}/deliveries/{deliveryId}/retry` 重投一条记录。
  它**重发记录里已渲染的 payload**，不按当前模板重新渲染 —— 改了模板也不会影响重投的内容。
  队列满时是 503 `delivery_queue_full`（这次没排上，稍后再试）。

## 一个完整的例子

```bash
# 1. 看模板预设：url 里的 <BOT_TOKEN> 要用户自己换，bodyTemplate 可以直接抄
curl -sS --fail-with-body "$IDEA_BASE_URL/api/webhooks/templates/telegram" \
  -u "$IDEA_USER:$IDEA_PASS"

# 2. 建一个全局 webhook（创建后默认禁用）
cat > webhook.json <<'JSON'
{
  "name": "tg",
  "url": "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage",
  "method": "POST",
  "headers": {},
  "bodyTemplate": "{\"chat_id\":-1003800671233,\"message_thread_id\":89,\"text\":\"{{idea.title}}: {{idea.content}}\"}",
  "events": ["idea.created", "idea.updated"],
  "scope": "global"
}
JSON

curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/webhooks" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @webhook.json
```

> 用户要自己把 `<BOT_TOKEN>` 换成真 token；**别把真 token 写进仓库或日志**。
