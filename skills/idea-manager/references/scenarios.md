# 典型场景

一段一段照抄就能用。每个例子都是完整的 curl（含认证与请求体）。

## 1. 记一条想法（正文带引号、换行、反斜杠）

```bash
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"content":"引号 \" 反斜杠 \\ 换行\n第二行\n<b>标签</b>","tags":["笔记"]}
JSON
```

`projectId` 不给就落到内置收件箱。

## 2. 查未完成的想法

```bash
curl -sS --fail-with-body "$IDEA_BASE_URL/api/ideas?status=open" \
  -u "$IDEA_USER:$IDEA_PASS"
```

`status` 取 `all` / `open` / `done`（缺省是 `all`）。返回的是裸数组。

## 3. 按标签筛

```bash
# 命中任意一个（缺省就是 or）
curl -sS --fail-with-body -G "$IDEA_BASE_URL/api/ideas" \
  --data-urlencode "tags=后端,前端" \
  -u "$IDEA_USER:$IDEA_PASS"

# 必须同时带两个标签
curl -sS --fail-with-body -G "$IDEA_BASE_URL/api/ideas" \
  --data-urlencode "tags=后端,导出" \
  --data-urlencode "tagMode=and" \
  -u "$IDEA_USER:$IDEA_PASS"
```

## 4. 标记完成 / 批量标记完成

> **`done` 必填**：省略它等于给 `false`（= **取消完成**），服务端**不报错**。
> 要标完成就明确给 `{"done":true}`。

```bash
# 一条想法标完成
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/0198f0a1b2c3d4e5f60718293a4b5c6d/complete" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"done":true}
JSON

# 批量标记完成
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/bulk/complete" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"ids":["0198f0a1b2c3d4e5f60718293a4b5c6d","0198f0a1b2c3d4e5f60718293a4b5c6e"],"done":true}
JSON

# 批量取消完成
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/bulk/complete" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"ids":["0198f0a1b2c3d4e5f60718293a4b5c6d"],"done":false}
JSON
```

返回的是被改动的那些想法（裸数组）。`ids` 给空数组则**什么都不改**（200 + `[]`）。

## 5. 批量加标签（并集追加）

```bash
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/bulk/tags" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"ids":["0198f0a1b2c3d4e5f60718293a4b5c6d"],"tags":["导出","待办"]}
JSON
```

这是**并集追加**（原有标签还在）。
⚠️ 但 `POST /api/ideas/bulk/tags` 给了 `replace: true` 就变回**覆盖式** ——
那时 `tags` 漏了（或给空数组）= 把标签全清掉（不报错）。
要**覆盖式**改一条想法的标签，用
`PUT /api/ideas/{id}/tags`：

```bash
curl -sS --fail-with-body -X PUT "$IDEA_BASE_URL/api/ideas/0198f0a1b2c3d4e5f60718293a4b5c6d/tags" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"tags":["导出"]}
JSON
```

⚠️ `PUT` 是**覆盖式**的：`tags` 不给、或给空数组 = **清空全部标签**（不是「不改」）。

## 6. 合并几条想法

```bash
# 让服务端拼接正文
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/merge" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"ids":["0198f0a1b2c3d4e5f60718293a4b5c6d","0198f0a1b2c3d4e5f60718293a4b5c6e"]}
JSON

# 自己指定合并后的正文
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/merge" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"ids":["0198f0a1b2c3d4e5f60718293a4b5c6d","0198f0a1b2c3d4e5f60718293a4b5c6e"],"content":"合并后的正文"}
JSON
```

**源想法会被硬删**（不在回收站里）—— 结果是一条**新**想法。
合并的想法必须属于同一个项目，否则是 `merge_cross_project`。

## 7. 恢复误删的想法

```bash
# 先看回收站
curl -sS --fail-with-body "$IDEA_BASE_URL/api/trash" \
  -u "$IDEA_USER:$IDEA_PASS"

# 再恢复
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas/0198f0a1b2c3d4e5f60718293a4b5c6d/restore" \
  -u "$IDEA_USER:$IDEA_PASS"
```

东西不在回收站里时是 400 `not_in_trash`。

## 8. 改一条想法（只发要改的字段）

```bash
# 只改正文：其它键整个别发
curl -sS --fail-with-body -X PATCH "$IDEA_BASE_URL/api/ideas/0198f0a1b2c3d4e5f60718293a4b5c6d" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"content":"新正文"}
JSON
```

发 `null` = 不改；**发空串是 400 `invalid_content`**（正文不能为空，清不掉）。

## 9. 配一个 Telegram webhook

```bash
# 先拿模板预设（url 里的 <BOT_TOKEN> 要用户自己换）
curl -sS --fail-with-body "$IDEA_BASE_URL/api/webhooks/templates/telegram" \
  -u "$IDEA_USER:$IDEA_PASS"

# 建一个全局 webhook（创建后默认禁用；要启用就 PATCH {"enabled":true}）
cat > webhook.json <<'JSON'
{
  "name": "tg",
  "url": "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage",
  "method": "POST",
  "headers": {},
  "bodyTemplate": "{\"chat_id\":-1003800671233,\"message_thread_id\":89,\"text\":\"{{idea.content}}\"}",
  "events": ["idea.created", "idea.updated"],
  "scope": "global"
}
JSON

curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/webhooks" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @webhook.json

# 看投递记录（裸数组）
curl -sS --fail-with-body "$IDEA_BASE_URL/api/webhooks/<webhook id>/deliveries" \
  -u "$IDEA_USER:$IDEA_PASS"
```

**读回来的 url 与 headers 是遮蔽版** —— 要原值得显式加 `?unmask=true`。
