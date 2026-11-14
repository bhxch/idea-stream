# 怎么调（curl）

没有命令行封装了：agent 直接用 curl 调 API。姿势就三样 —— **方法 + 地址 + 请求体**。
这一页讲清三件事：认证怎么带、请求体怎么写、响应怎么读。

## 认证

| 情况 | 加什么 |
|---|---|
| `IDEA_USER` 与 `IDEA_PASS` 都有值 | `-u "$IDEA_USER:$IDEA_PASS"`（nginx basic auth） |
| `IDEA_TOKEN` 有值 | `-H "X-Api-Key: $IDEA_TOKEN"` |
| 都没有 | 什么都不加 |

两样都没有却带上 `-u`，curl 会拿空用户名密码去认证 —— 多半白跑一趟拿个 401。

**别把凭据写进 URL**（`https://user:pass@host/...` 这种）：nginx 会把 userinfo 原样写进
访问日志，凭据就跟着日志走了。写进 `-u` 的值不进日志（nginx 只记请求行）。

## 三个选项

- `-sS`：安静模式，但出错时仍在 stderr 上说话（不加 `-s` 会把进度条混进响应体）。
- `--fail-with-body`：4xx / 5xx 时退出码非 0，**响应体照样留在 stdout** ——
  错误响应里就有 `code`（见 `errors.md`）。curl 版本太老（< 7.76）没有这个选项，
  那就**不加**，自己看状态码。
- **不要加 `-f` / `--fail`**：老 curl 的 `--fail` 会把错误响应体丢掉，
  那样就拿不到 `code` 了。

## 请求体

带请求体的方法（POST / PUT / PATCH）要带 `Content-Type: application/json`，
不带就是 415（`unsupported_media_type`）。GET 与 DELETE 没有请求体，不用带。

**请求体不要用 shell 拼字符串** —— 引号、换行、反斜杠会被 shell 吃掉。
两种照抄就能用的写法：

### 写法一：写进文件（推荐）

```bash
cat > body.json <<'JSON'
{"content":"引号 \" 反斜杠 \\ 换行\n第二行","title":"带特殊字符","tags":["笔记"]}
JSON

curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @body.json
```

### 写法二：heredoc 直接喂给 curl

```bash
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"content":"引号 \" 反斜杠 \\ 换行\n第二行","title":"带特殊字符","tags":["笔记"]}
JSON
```

`<<'JSON'`（**引号包住 JSON**）是重点：不加引号时 shell 会展开 body 里的 `$` 与反引号，
正文里有个美元符号就坏了。`--data-binary @-` 表示「从标准输入读」。

### 特殊字符怎么写

| 想送的东西 | JSON 里怎么写 |
|---|---|
| 双引号 | `\"` |
| 反斜杠 | `\\` |
| 换行 | `\n`（**字面的两个字符**，不是真换行） |
| 制表符 | `\t` |
| HTML 标签 | 原样写 `<b>标签</b>`（JSON 不必转义 `<`） |

请求体里的换行用 JSON 转义（`\n`），不要用字面换行。

## 各方法的样板

样板里的 `-u` **只在有凭据时保留** —— `IDEA_USER`/`IDEA_PASS` 都没有时删掉那一段
（带上空用户名密码会白跑一趟拿个 401）。

```bash
# GET：读
curl -sS --fail-with-body "$IDEA_BASE_URL/api/ideas?status=open" \
  -u "$IDEA_USER:$IDEA_PASS"

# POST：新建
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/projects" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @body.json

# PUT：覆盖式（设标签、改设置项）
curl -sS --fail-with-body -X PUT "$IDEA_BASE_URL/api/ideas/$ID/tags" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @body.json

# PATCH：只发要改的字段
curl -sS --fail-with-body -X PATCH "$IDEA_BASE_URL/api/ideas/$ID" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @patch.json

# DELETE：删（204，没有响应体）
curl -sS --fail-with-body -X DELETE "$IDEA_BASE_URL/api/ideas/$ID" \
  -u "$IDEA_USER:$IDEA_PASS"

# 事件流：长连接，-N 不缓冲
curl -sS -N "$IDEA_BASE_URL/api/events" -u "$IDEA_USER:$IDEA_PASS"
```

## 查询参数里的中文与特殊字符

查询参数里的非 ASCII 字符与 `&`、`=` 要**URL 编码**。手写容易错，让 curl 来编：

```bash
curl -sS --fail-with-body -G "$IDEA_BASE_URL/api/ideas" \
  --data-urlencode "tags=后端,前端" \
  --data-urlencode "tagMode=and" \
  -u "$IDEA_USER:$IDEA_PASS"
```

`-G` 表示「把 `--data-urlencode` 变成查询参数而不是请求体」。
`tags` 是**逗号分隔**的一串标签，`tagMode` 取 `or`（缺省）或 `and`；
`status` 取 `all` / `open` / `done`。

## 怎么读响应

- **列表是裸数组**：`[{...}, {...}]`，没有 `{"items": [...]}` 那种信封。
- **单个资源是对象**：`{...}`。
- **`DELETE` 返回 204，没有响应体**：curl 的 stdout 是空的（不是 `null`）。
- **状态码**：`-w '%{http_code}\n'` 单独打出来，或 `-i` 连响应头一起看：

  ```bash
  curl -sS -o body.json -w '%{http_code}\n' \
    "$IDEA_BASE_URL/api/ideas" -u "$IDEA_USER:$IDEA_PASS"
  ```

- **错误**：4xx / 5xx 的响应体是 ProblemDetails JSON，顶层带 `code`（见 `errors.md`）。
