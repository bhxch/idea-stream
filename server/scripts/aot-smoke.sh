#!/usr/bin/env bash
#
# AOT 冒烟脚本：发布 Native AOT 产物，启动后打端点，断言返回 200/201 而不是 500。
#
# 为什么必须有这个脚本：普通 `dotnet test` 的宿主带着反射式 JSON 兜底，
# 端点一旦退回 `Results.Ok(new { status = "ok" })`（匿名对象），
# 输出与静态类型 DTO 逐字节相同，集成测试照样全绿。
# 只有真 AOT 产物缺少匿名类型的 JsonTypeInfo 元数据，才会暴露成 500。
# 集合包装类型（List<ProjectDto>）同理：少登记一个，只有真产物才暴露。
#
# 用法：bash server/scripts/aot-smoke.sh
# 退出码：0 = 全部通过；非 0 = 失败（详情见输出）。
#
# 新增端点时在文件末尾加一行 check_endpoint 调用即可。断言走 file-based
# C# 程序，用的是真正的 System.Text.Json 解析路径（与 JsonDocument.Parse 同一套语义）。

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

PUBLISH_DIR="$(mktemp -d "${TMPDIR:-/tmp}/idea-aot-smoke-publish.XXXXXX")"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/idea-aot-smoke-work.XXXXXX")"
APP_PID=""
PORT="${AOT_SMOKE_PORT:-5199}"
BASE_URL="http://127.0.0.1:$PORT"

cleanup() {
    if [[ -n "$APP_PID" ]] && kill -0 "$APP_PID" 2>/dev/null; then
        kill "$APP_PID" 2>/dev/null
        wait "$APP_PID" 2>/dev/null
    fi
    rm -rf "$PUBLISH_DIR" "$WORK_DIR"
}
trap cleanup EXIT

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

# check_endpoint <路径> <期望状态码> <C# 布尔表达式> [HTTP 方法，默认 GET] [请求体 JSON]
#
# 表达式在 JsonDocument 的根元素 root 上求值，root 是 JsonElement。
# 根元素的类型（对象/数组）也由表达式自己断言 —— 集合包装类型的端点
# （如 GET /api/projects 返回 List<ProjectDto>）根是数组，不是对象。
# 端点返回静态类型 DTO 时，AOT 下缺元数据会直接 500，状态码断言先把它拦下来。
#
# 给请求体时按 application/json 发送：带请求体的端点（POST /api/ideas/bulk/* 等）
# 在 AOT 下同样需要源生成的请求体元数据，少登记一个这里就会暴露成 500。
check_endpoint() {
    local path="$1"
    local expected="$2"
    local assertion="$3"
    local method="${4:-GET}"
    local body="${5:-}"

    local body_file="$WORK_DIR/body-$(echo "$path" | tr '/' '_').json"
    local code
    if [[ -n "$body" ]]; then
        code="$(curl -sS -X "$method" -H 'Content-Type: application/json' -d "$body" \
            -o "$body_file" -w '%{http_code}' "$BASE_URL$path")"
    else
        code="$(curl -sS -X "$method" -o "$body_file" -w '%{http_code}' "$BASE_URL$path")"
    fi

    if [[ "$code" != "$expected" ]]; then
        echo "--- 应用日志 ---" >&2
        cat "$WORK_DIR/app.log" >&2
        fail "$path 期望 HTTP $expected，实际 $code"
    fi

    local assert_dir="$WORK_DIR/assert-$(echo "$path" | tr '/' '_')"
    mkdir -p "$assert_dir"
    cat >"$assert_dir/assert.cs" <<CS
using System.Text.Json;

var body = Console.In.ReadToEnd();
using var document = JsonDocument.Parse(body);
var root = document.RootElement;

var ok = $assertion;
if (!ok)
{
    Console.Error.WriteLine("断言不成立");
    Console.Error.WriteLine(\$"响应体：{body}");
    return 1;
}

Console.WriteLine("响应体断言通过");
return 0;
CS

    if ! (cd "$assert_dir" && dotnet run assert.cs <"$body_file"); then
        fail "$path 的响应体断言不成立"
    fi
}

echo "==> 发布 Native AOT 产物（-r linux-x64 -c Release）"
# PublishAot=true 显式写在这里：只依赖 csproj 的话，那一行被删掉时脚本会静默
# 退化成非 AOT 发布，从此再也测不出退化，而且脚本仍然 PASS。
if ! dotnet publish server/IdeaApp/IdeaApp.csproj \
        -c Release -r linux-x64 -o "$PUBLISH_DIR" -p:PublishAot=true --nologo; then
    fail "dotnet publish 失败"
fi

APP_BIN="$PUBLISH_DIR/IdeaApp"
[[ -x "$APP_BIN" ]] || fail "未产出可执行文件：$APP_BIN"

echo "==> 启动产物（临时库 + 端口 $PORT）"
# Database__Path 指到临时目录，避免污染仓库里的 data/idea.db。
# Urls 指到临时端口，避免与开发环境抢 5080。
Urls="$BASE_URL" \
Database__Path="$WORK_DIR/idea.db" \
    "$APP_BIN" >"$WORK_DIR/app.log" 2>&1 &
APP_PID=$!

# 等待端口就绪。
for _ in $(seq 1 60); do
    if curl -fsS -o /dev/null "$BASE_URL/api/health" 2>/dev/null; then
        break
    fi
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        cat "$WORK_DIR/app.log" >&2
        fail "AOT 产物启动即退出"
    fi
    sleep 0.5
done

echo "==> GET /api/health"
# 端点返回静态类型 HealthResponse，断言响应体是 JsonDocument 能解析出的
# 根对象，且 status == "ok"。
check_endpoint "/api/health" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.GetString() == "ok"'

echo "==> GET /api/projects"
# 业务端点：返回静态类型 List<ProjectDto>，根是**数组**。
# 这一条守住「集合包装类型的 JSON 元数据在 AOT 下可用」——
# 白名单里少登记 List<ProjectDto> 时，这个端点在 AOT 产物下就是 500。
check_endpoint "/api/projects" "200" \
    'root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 1 && root[0].GetProperty("id").GetString() == "inbox"'

echo "==> DELETE /api/projects/inbox"
# 业务错误路径：ApiException → ProblemDetails，扩展字段 code 扁平化在顶层。
check_endpoint "/api/projects/inbox" "400" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.GetString() == "inbox_not_deletable"' \
    DELETE

echo "==> GET /api/ideas"
# 返回静态类型 List<IdeaDto>（裸数组）。白名单里少登记它时，这个端点在 AOT 产物下就是 500。
check_endpoint "/api/ideas" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> GET /api/ideas?status=open&tagMode=and"
# 带过滤参数的同一个端点：查询参数绑定与解析在 AOT 下同样要走真实的路由代码。
check_endpoint "/api/ideas?status=open&tagMode=and" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> GET /api/tags"
# 返回静态类型 List<TagDto>（裸数组）。
check_endpoint "/api/tags" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> GET /api/tags/suggest?prefix=a"
# 返回静态类型 List<string>：字符串数组的包装类型也要登记，否则 AOT 产物下 500。
check_endpoint "/api/tags/suggest?prefix=a" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> GET /api/trash"
# 回收站视图：返回静态类型 List<TrashEntryDto>（裸数组）。
# 白名单里少登记这个集合包装类型时，这个端点在 AOT 产物下就是 500。
check_endpoint "/api/trash" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> POST /api/ideas/bulk/complete"
# 批量端点：返回静态类型 List<IdeaDto>（裸数组），请求体也要有源生成元数据。
# ids 给空数组是合法的空操作 —— 这一条只验「端点能通 + 集合类型可用」。
check_endpoint "/api/ideas/bulk/complete" "200" \
    'root.ValueKind == JsonValueKind.Array' \
    POST \
    '{"ids":[],"done":true}'

echo "==> POST /api/ideas/bulk/tags"
check_endpoint "/api/ideas/bulk/tags" "200" \
    'root.ValueKind == JsonValueKind.Array' \
    POST \
    '{"ids":[],"tags":["a"]}'

echo "==> POST /api/ideas/bulk/delete"
# 批量删除也是 POST（动作），返回 200 + 静态类型 List<IdeaDto>（裸数组）。
# spec §13.5 的「删除返回 204」只适用于 HTTP 方法 DELETE，不适用于批量删除。
check_endpoint "/api/ideas/bulk/delete" "200" \
    'root.ValueKind == JsonValueKind.Array' \
    POST \
    '{"ids":[]}'

echo "==> POST /api/ideas/bulk/complete （id 全都不存在）"
# 批量对「陈旧 id」宽容：一个都不存在时报 404 + code=idea_not_found。
# 这条守住批量端点的错误路径在 AOT 下同样走得通。
check_endpoint "/api/ideas/bulk/complete" "404" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.GetString() == "idea_not_found"' \
    POST \
    '{"ids":["0198f0a1b2c3d4e5f60718293a4b5cff"],"done":true}'

echo "==> POST /api/ideas/merge"
# 合并少于两条是业务错误：400 + code=invalid_merge_selection。
# 这一条同时守住合并端点的请求体元数据。
check_endpoint "/api/ideas/merge" "400" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.GetString() == "invalid_merge_selection"' \
    POST \
    '{"ids":["0198f0a1b2c3d4e5f60718293a4b5c01"]}'

echo "==> POST /api/ideas"
# 写路径：新建想法会走一遍事件发布（EventFactory → 单调 UUIDv7 → 事件总线）。
# AOT 下这条链路要是断了（事件类型没登记进源生成上下文、事件构造用到反射），
# 这里就是 500。id 必须是 32 位无连字符的小写 hex。
check_endpoint "/api/ideas" "201" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && id.GetString() is { Length: 32 }' \
    POST \
    '{"content":"AOT 冒烟","title":"标题","tags":["冒烟"]}'

echo "==> GET /api/webhooks"
# 返回静态类型 List<WebhookDto>（裸数组）。白名单里少登记这个集合包装类型时，
# 这个端点在 AOT 产物下就是 500。
check_endpoint "/api/webhooks" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> GET /api/webhooks/templates/telegram"
# 模板预设：静态类型 WebhookTemplateDto。少登记它，AOT 产物下这里就是 500。
check_endpoint "/api/webhooks/templates/telegram" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("url", out var url) && url.GetString() == "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage" && root.TryGetProperty("bodyTemplate", out var bodyTemplate) && bodyTemplate.GetString()!.Contains("{{idea.content}}")'

echo "==> POST /api/webhooks"
# 写路径：请求体要源生成元数据，返回静态类型 WebhookDto。
# 全局作用域的 webhook 创建时**默认禁用**（enabled == false）——
# 这一条同时守住「默认启用规则」与「secret 不回传」（hasSecret == false）。
check_endpoint "/api/webhooks" "201" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean() && root.TryGetProperty("hasSecret", out var hasSecret) && !hasSecret.GetBoolean()' \
    POST \
    '{"name":"AOT 冒烟","url":"https://example.com/hook","method":"POST","headers":{},"bodyTemplate":"{\"text\":\"{{idea.content}}\"}","events":["idea.created"],"scope":"global"}'

echo "==> GET /api/events (SSE)"
# SSE 是长连接：不能像别的端点那样把响应体读完（读完会一直等到超时）。
# 这里断言三件事：连得上（200）、头是 text/event-stream、
# 写操作产生的事件帧真的推过来了。
# 最后一件事守住的是「事件帧的 JSON 走源生成的上下文」：
# AOT 下少了 IdeaEvent 的元数据就推不出帧（普通 dotnet test 测不出来）。
SSE_HEADERS="$WORK_DIR/sse-headers.txt"
SSE_BODY="$WORK_DIR/sse-body.txt"
# curl 的存活期（--max-time）必须**长于**下面两段等待预算（各 10 秒）：
# 否则「等不到 200 响应头」的失败分支只能在 curl 已经死了之后才触发
# （报错文案与真实原因不符），而落在窗口里的 POST 会打向已断开的连接，
# 随后事件帧的断言跟着失败。
curl -sS -N -D "$SSE_HEADERS" --max-time 30 "$BASE_URL/api/events" >"$SSE_BODY" 2>/dev/null &
SSE_PID=$!

# 轮询等 SSE 连上（响应头里有 200 行）再 POST。
# 固定 sleep 是抢时间：慢机器上 curl 还没连上就 POST，那条事件就丢了 ——
# 冒烟脚本偶发失败。flaky 的冒烟脚本比没有脚本更糟。
for _ in $(seq 1 100); do
    if grep -qi '^HTTP/1.1 200' "$SSE_HEADERS" 2>/dev/null; then
        break
    fi
    sleep 0.1
done

if ! grep -qi '^HTTP/1.1 200' "$SSE_HEADERS" 2>/dev/null; then
    kill "$SSE_PID" 2>/dev/null || true
    echo "--- 应用日志 ---" >&2
    cat "$WORK_DIR/app.log" >&2
    fail "/api/events 没有连上（等不到 200 响应头）"
fi

# 写一条想法：它的事件应当被推给 SSE 订阅端。
check_endpoint "/api/ideas" "201" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && id.GetString() is { Length: 32 }' \
    POST \
    '{"content":"AOT 冒烟 SSE","title":"SSE"}'

# 轮询等事件帧到达：curl 还活着（--max-time 30 > 这段预算），
# 帧到了就收工 —— 不用干等到 max-time。
for _ in $(seq 1 100); do
    if grep -q '^event: idea.created' "$SSE_BODY" 2>/dev/null; then
        break
    fi
    sleep 0.1
done

kill "$SSE_PID" 2>/dev/null || true
wait "$SSE_PID" 2>/dev/null || true

if ! grep -qi '^HTTP/1.1 200' "$SSE_HEADERS"; then
    echo "--- 应用日志 ---" >&2
    cat "$WORK_DIR/app.log" >&2
    fail "/api/events 没有返回 200"
fi

if ! grep -qi '^content-type: text/event-stream' "$SSE_HEADERS"; then
    echo "--- 应用日志 ---" >&2
    cat "$WORK_DIR/app.log" >&2
    fail "/api/events 的 Content-Type 不是 text/event-stream"
fi

if ! grep -q '^event: idea.created' "$SSE_BODY"; then
    echo "--- 应用日志 ---" >&2
    cat "$WORK_DIR/app.log" >&2
    echo "--- 收到的内容 ---" >&2
    cat "$SSE_BODY" >&2
    fail "/api/events 没有推送到 idea.created 事件帧"
fi

echo "PASS: AOT 产物下所有被检查的端点都返回预期状态码与响应体"
exit 0
