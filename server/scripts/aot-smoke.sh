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
#
# 脚本末尾还有一组**跨栈**检查：用仓库里那个真的取件器（skills/idea-manager-client/scripts/fetch.py）
# 去取跑着的这个实例的说明书 —— .NET 造的东西，Python 收不收得下，只有真取一次才知道。
# （两套测试各自绿、各自定义，谁也没对过面，跨栈的 bug 就这么全绿漏过去。）
#
# 这组检查**缺 python3 就失败**，不跳过：它是整个分支唯一的跨栈证据，
# 它不跑而脚本还报 PASS，等于什么都没验。整个闸门用仓库根的 verify.sh 一条命令跑。

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

# json_first_string <响应体文件> <属性名>
#
# 打印响应体**第一个数组元素**里（根是数组时）或根对象里的某个字符串属性。
# 用真的 System.Text.Json 解析，不依赖 jq —— 脚本要能在没装 jq 的机器上跑。
json_first_string() {
    local file="$1"
    local prop="$2"
    local dir="$WORK_DIR/json-first-string"
    mkdir -p "$dir"
    cat >"$dir/first.cs" <<'CS'
using System.Text.Json;

var body = Console.In.ReadToEnd();
using var document = JsonDocument.Parse(body);

var root = document.RootElement;
var element = root.ValueKind == JsonValueKind.Array ? root[0] : root;

var name = Environment.GetEnvironmentVariable("JSON_PROP")!;
Console.WriteLine(element.GetProperty(name).GetString()!);
CS

    (
        export JSON_PROP="$prop"
        cd "$dir" && dotnet run first.cs <"$file"
    )
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
# 只断言「数组里有收件箱」，不守顺序：列表顺序由 project.sort_mode 决定
# （默认 recent 按最近活动时间排），拿「第一个是 inbox」当断言，
# 将来在这条前面加一步建项目就会以「AOT 回归」的面貌失败。
check_endpoint "/api/projects" "200" \
    'root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 1 && root.EnumerateArray().Any(p => p.GetProperty("id").GetString() == "inbox")'

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

echo "==> GET /api/ideas?q=咖啡&limit=2"
# 带关键词与分页的同一个端点：q 走全文索引（idea_fts 的 MATCH 在 AOT 下一样要走真实的
# SQLite 互操作），matches 是 IdeaDto 的嵌套类型（SearchMatch）——
# JsonContext 白名单里少登记它，这个端点在 AOT 产物下就是 500。
check_endpoint "/api/ideas?q=%E5%92%96%E5%95%A1&limit=2" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> GET /api/search?q=咖啡&limit=2"
# 全库搜索：返回静态类型 SearchResponse（**对象**，里面三组 SearchPage<T>）。
# 白名单里少登记 SearchResponse 或三个 SearchPage<T> 中的任何一个，
# 这个端点在 AOT 产物下就是 500 —— 普通 dotnet test 有反射兜底，测不出来。
check_endpoint "/api/search?q=%E5%92%96%E5%95%A1&limit=2" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("query", out var query) && query.GetString() != null && root.TryGetProperty("ideas", out var ideas) && ideas.TryGetProperty("items", out var ideaItems) && ideaItems.ValueKind == JsonValueKind.Array && root.TryGetProperty("projects", out var projects) && projects.TryGetProperty("items", out var projectItems) && projectItems.ValueKind == JsonValueKind.Array && root.TryGetProperty("tags", out var tags) && tags.TryGetProperty("items", out var tagItems) && tagItems.ValueKind == JsonValueKind.Array'

echo "==> GET /api/search?q=（关键词为空）"
# 错误路径：400 + code=invalid_search_query（业务错误码在 AOT 下同样要送得到）。
check_endpoint "/api/search?q=" "400" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.GetString() == "invalid_search_query"'

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

# 记下这个 webhook 的 id：下面两个投递端点要用它。
WEBHOOK_ID="$(json_first_string "$WORK_DIR/body-_api_webhooks.json" id)"
[[ -n "$WEBHOOK_ID" ]] || fail "取不到刚创建的 webhook 的 id"

echo "==> GET /api/webhooks/{id}/deliveries"
# 投递记录列表：返回静态类型 List<DeliveryDto>（裸数组）。
# 白名单里少登记这个集合包装类型时，这个端点在 AOT 产物下就是 500。
check_endpoint "/api/webhooks/$WEBHOOK_ID/deliveries" "200" \
    'root.ValueKind == JsonValueKind.Array'

echo "==> POST /api/webhooks（开一个会真的去投递的 webhook）"
# 指向一个没人监听的端口：投递会失败（连接被拒），但**投递记录会落库** ——
# 手动重投端点要的就是一条真实的投递记录。
check_endpoint "/api/webhooks" "201" \
    'root.ValueKind == JsonValueKind.Object' \
    POST \
    '{"name":"AOT 冒烟 投递","url":"http://127.0.0.1:1/hook","method":"POST","headers":{},"bodyTemplate":"{\"text\":\"{{idea.content}}\"}","events":["idea.created"],"scope":"global"}'

DELIVERY_WEBHOOK_ID="$(json_first_string "$WORK_DIR/body-_api_webhooks.json" id)"
[[ -n "$DELIVERY_WEBHOOK_ID" ]] || fail "取不到第二个 webhook 的 id"

echo "==> PATCH /api/webhooks/{id}（打开投递开关）"
# 全局 webhook 默认禁用；不打开的话事件不会投递，也就没有投递记录。
check_endpoint "/api/webhooks/$DELIVERY_WEBHOOK_ID" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean()' \
    PATCH \
    '{"enabled":true}'

echo "==> POST /api/ideas（触发一次投递）"
check_endpoint "/api/ideas" "201" \
    'root.ValueKind == JsonValueKind.Object' \
    POST \
    '{"content":"AOT 冒烟 投递","title":"投递"}'

echo "==> GET /api/webhooks/{id}/deliveries（等第一条投递记录落库）"
# 投递是异步的：事件发布之后 worker 才去投递，记录也就才落库。轮询等它出现。
DELIVERY_ID=""
for _ in $(seq 1 100); do
    curl -sS -o "$WORK_DIR/deliveries.json" "$BASE_URL/api/webhooks/$DELIVERY_WEBHOOK_ID/deliveries"
    DELIVERY_ID="$(json_first_string "$WORK_DIR/deliveries.json" id 2>/dev/null || true)"
    if [[ -n "$DELIVERY_ID" ]]; then
        break
    fi
    sleep 0.1
done
[[ -n "$DELIVERY_ID" ]] || fail "投递记录一直没落库"

echo "==> POST /api/webhooks/{id}/deliveries/{deliveryId}/retry"
# 手动重投：返回静态类型 DeliveryDto。白名单里少登记它，AOT 产物下就是 500。
# 这个端点没有请求体，所以最后一个参数给空串（给了就变成带 Content-Type 的 POST）。
check_endpoint "/api/webhooks/$DELIVERY_WEBHOOK_ID/deliveries/$DELIVERY_ID/retry" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.GetString() is not null' \
    POST \
    ''

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

echo "==> GET /api/settings"
# 返回静态类型 Dictionary<string,string>：白名单里少登记它，AOT 产物下就是 500。
# 默认值不播种到库里，读不到就回落 recent —— 响应里一定有这一项。
check_endpoint "/api/settings" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("project.sort_mode", out var mode) && mode.GetString() == "recent"'

echo "==> PUT /api/settings/project.sort_mode"
# 写路径：请求体要源生成元数据，返回静态类型 Dictionary<string,string>。
check_endpoint "/api/settings/project.sort_mode" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("project.sort_mode", out var mode) && mode.GetString() == "manual"' \
    PUT \
    '{"value":"manual"}'

echo "==> PUT /api/settings/Trash.RetentionDays"
# 业务错误路径：未定义的键 → 400 + code=setting_key_not_writable。
# 配置文件里的项不可经 API 修改，这条守住它。
check_endpoint "/api/settings/Trash.RetentionDays" "400" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.GetString() == "setting_key_not_writable"' \
    PUT \
    '{"value":"30"}'

echo "==> GET /skill/idea-manager/version"
# 清单端点：返回静态类型 SkillManifestDto。AOT 下少登记它，这里就是 500。
check_endpoint "/skill/idea-manager/version" "200" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) && version.GetString() != null && root.TryGetProperty("entry", out var entry) && entry.GetString() == "SKILL.md" && root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array'

echo "==> GET /skill/idea-manager/SKILL.md"
# 内容端点返回的是 markdown（不是 JSON），用 curl 直接断言状态码与内容类型。
SKILL_CODE="$(curl -sS -o "$WORK_DIR/skill.md" -w '%{http_code}' "$BASE_URL/skill/idea-manager/SKILL.md")"
[[ "$SKILL_CODE" == "200" ]] || fail "/skill/idea-manager/SKILL.md 期望 HTTP 200，实际 $SKILL_CODE"
grep -q '^---' "$WORK_DIR/skill.md" || fail "取到的 SKILL.md 不像 markdown（没有 frontmatter）"

echo "==> 取件器取一次说明书（跨栈：真产物 + 仓库里的真取件器）"
# 这一条把 .NET 与 Python 两侧第一次放到同一个进程边界上：
# **.NET 造出来的东西，Python 收不收得下，只有真取一次才知道。**
#
# 为什么必须真取：两套测试各绿、各自定义，谁也没对过面 ——
# .NET 侧只断言「version 非空」，Python 侧的假服务端按规格自己造版本。
# 于是「version 只做十六进制编码、从不哈希」这种 bug 全绿漏过，
# 真跑起来时取件器第一步就把清单拒了（版本串 1012 字符，超出形状上限）。
#
# 假服务端那套（skills/idea-manager-client/tests/test_fetch.py）按规格造数据，
# **不构成跨栈验证** —— 它验的是「取件器能处理符合规格的清单」。
#
# 缺 python3 是**失败**不是跳过：这条检查是整个分支唯一的跨栈证据，
# 它不跑、脚本却报 PASS，等于什么都没验 —— 而 agent 侧本来就需要 python3，
# 缺了说明环境不对（规格里也把 python3 列成 agent 侧的硬依赖）。
command -v python3 >/dev/null 2>&1 \
    || fail "这台机器上没有 python3，取件器跑不起来（跨栈这条没法验，不许跳过）"

# 连的是**跑着的这个实例**；缓存目录给个临时目录（跑完随 WORK_DIR 一起没）。
# 这个冒烟环境没有 basic auth，所以不用带凭据。
if ! IDEA_BASE_URL="$BASE_URL" IDEA_SKILL_CACHE_DIR="$WORK_DIR/skill-cache" \
    python3 skills/idea-manager-client/scripts/fetch.py \
    >"$WORK_DIR/fetched.md" 2>"$WORK_DIR/fetch.err"; then
    echo "--- 取件器的 stderr ---" >&2
    cat "$WORK_DIR/fetch.err" >&2
    fail "取件器取说明书失败（跨栈检查没过：服务端给的清单它收不下）"
fi

# 取到的必须是索引页（清单里的 entry），不是空文件也不是错误页。
grep -q '^name: idea-manager' "$WORK_DIR/fetched.md" \
    || fail "取件器取到的不是索引页（没有 name: idea-manager）"

# 分发副本的完整性（设计文档 §14 的「分发副本完整」那条守卫）：
# **产物里带的副本要跟内容源逐个文件对上，而且要逐字节对上。**
#
# 三个地方各自定义「发了什么」：内容源目录、csproj 的 glob、服务端枚举副本的规则 ——
# 中间没人对过面。少拷一份文件（agent 照索引页的路由表取不到）或者
# 带进一份开发残留，都只有真取一次、逐个文件比一遍才发现得了。
#
# 比的是**取回来的缓存**（= 清单说的那份副本），不是 publish 目录 ——
# 前者多经过了服务端这一跳，才是 agent 真正拿到的东西。
#
# 比 **sha256** 而不是比文件名：文件名对上不代表内容对上（服务端可能发的是
# 另一份同名文件）。这条链在**字节层面**钉死：内容源 → 产物 → 服务端 → agent。
expected_sums="$WORK_DIR/expected-sums.txt"
actual_sums="$WORK_DIR/actual-sums.txt"

# 期望侧 = 产物里该有的那一份：csproj 显式列的两组文件
#（SKILL.md 与 references/ 下的 *.md），再按服务端口径去掉 __pycache__ 段
#（SkillCatalog 只排除这一种段）。
#
# **不能把点目录排掉**：csproj 的 glob 收点目录里的 .md，服务端也会分发它们 ——
# 期望侧排掉的话，内容源里一出现 .cache/x.md，这条检查就假红。
(
    cd skills/idea-manager
    {
        find . -maxdepth 1 -type f -name 'SKILL.md'
        find ./references -type f -name '*.md'
    } 2>/dev/null \
        | sed 's|^\./||' \
        | grep -v '__pycache__' \
        | sort \
        | while read -r file; do
            printf '%s  %s\n' "$(sha256sum "$file" | cut -d' ' -f1)" "$file"
        done
) >"$expected_sums"

# 实际侧 = 取回来的缓存（去掉清单快照那个文件）。
cache_version="$(find "$WORK_DIR/skill-cache" -mindepth 1 -maxdepth 1 -type d \
    ! -name '.tmp-*' | head -n 1)"
[[ -n "$cache_version" ]] || fail "缓存里没有版本目录"

(
    cd "$cache_version"
    find . -type f ! -name '.manifest.json' \
        | sed 's|^\./||' \
        | sort \
        | while read -r file; do
            printf '%s  %s\n' "$(sha256sum "$file" | cut -d' ' -f1)" "$file"
        done
) >"$actual_sums"

if ! diff -u "$expected_sums" "$actual_sums" >"$WORK_DIR/sums.diff"; then
    echo "--- 内容源与分发副本的差异（sha256 + 路径；- 内容源里有而副本里没有，+ 反之）---" >&2
    cat "$WORK_DIR/sums.diff" >&2
    fail "分发副本与内容源对不上（发布产物少拷、多拷或拷错了文件）"
fi

echo "==> 取件器取一份子文档（跨栈：references/ 下的那一份）"
# 相对路径的拼接（references/ 前缀 + 文件名）是取件器自己做的 ——
# 拼错了这里就会 404。取回来的内容还要与内容源**逐字节**相同。
if ! IDEA_BASE_URL="$BASE_URL" IDEA_SKILL_CACHE_DIR="$WORK_DIR/skill-cache" \
    python3 skills/idea-manager-client/scripts/fetch.py references/errors.md \
    >"$WORK_DIR/fetched-errors.md" 2>"$WORK_DIR/fetch-errors.err"; then
    echo "--- 取件器的 stderr ---" >&2
    cat "$WORK_DIR/fetch-errors.err" >&2
    fail "取件器取子文档失败（references/ 前缀的拼接可能不对）"
fi

expected_errors_sum="$(sha256sum skills/idea-manager/references/errors.md | cut -d' ' -f1)"
actual_errors_sum="$(sha256sum "$WORK_DIR/fetched-errors.md" | cut -d' ' -f1)"
[[ "$expected_errors_sum" == "$actual_errors_sum" ]] \
    || fail "取回来的子文档与内容源不是同一份内容"

echo "==> 404 的错误响应体：ProblemDetails 的 code 必须在顶层"
# 这条**直接打服务端**，不靠取件器 ——
# 取件器把任何 404（除 skill_not_distributed）一律折叠成 not_found，
# **根本不看**响应体里 code 在不在顶层：拿取件器的输出去断言 code 的位置，
# 验的是「折叠」而不是「code 在顶层」（上一版就是这么写的，恒绿：
# 服务端把 code 挪进 extensions、甚至干脆不返回，它照样绿）。
#
# 这里的断言用真的 JsonDocument 解析响应体：`root.TryGetProperty("code", ...)`
# 只在 **code 是顶层属性** 时才成立 —— 挪进 extensions 里它就不成立。
check_endpoint "/skill/idea-manager/nope.md" "404" \
    'root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() == "skill_file_not_found"'

echo "==> 取件器取一个不存在的文件（跨栈：404 折叠成 not_found）"
# 错误路径也要跨栈对过面：取件器按状态码分支（404 → not_found），退出码非 0。
# 注意这条**只**验「折叠」这一件事；code 在不在顶层由上面那条守着。
if IDEA_BASE_URL="$BASE_URL" IDEA_SKILL_CACHE_DIR="$WORK_DIR/skill-cache" \
    python3 skills/idea-manager-client/scripts/fetch.py /skill/idea-manager/nope.md \
    >"$WORK_DIR/fetch-nope.out" 2>"$WORK_DIR/fetch-nope.err"; then
    fail "取一个不存在的文件应当失败（退出码非 0），实际却成功了"
fi

if ! grep -q '^not_found: ' "$WORK_DIR/fetch-nope.err"; then
    echo "--- 取件器的 stderr ---" >&2
    cat "$WORK_DIR/fetch-nope.err" >&2
    fail "取件器没有把服务端的 404 折叠成 not_found"
fi

echo
echo "PASS: 跑过的检查 ——"
echo "  * AOT 产物下每个被检查的端点都返回预期的状态码与响应体（含 skill 分发端点）"
echo "  * 404 的 ProblemDetails：code 在**顶层**（服务端直连断言，不靠取件器）"
echo "  * 跨栈：仓库里的真取件器收下了真产物的清单"
echo "  * 跨栈：分发副本与内容源逐文件、逐字节（sha256）对上"
echo "  * 跨栈：子文档（references/ 下的那一份）能取到，且与内容源是同一份"
echo "  * 跨栈：不存在的文件回 not_found、退出码非 0"
exit 0
