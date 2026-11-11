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

# check_endpoint <路径> <期望状态码> <C# 布尔表达式> [HTTP 方法，默认 GET]
#
# 表达式在 JsonDocument 的根元素 root 上求值，root 是 JsonElement。
# 根元素的类型（对象/数组）也由表达式自己断言 —— 集合包装类型的端点
# （如 GET /api/projects 返回 List<ProjectDto>）根是数组，不是对象。
# 端点返回静态类型 DTO 时，AOT 下缺元数据会直接 500，状态码断言先把它拦下来。
check_endpoint() {
    local path="$1"
    local expected="$2"
    local assertion="$3"
    local method="${4:-GET}"

    local body_file="$WORK_DIR/body-$(echo "$path" | tr '/' '_').json"
    local code
    code="$(curl -sS -X "$method" -o "$body_file" -w '%{http_code}' "$BASE_URL$path")"

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

echo "PASS: AOT 产物下所有被检查的端点都返回预期状态码与响应体"
exit 0
