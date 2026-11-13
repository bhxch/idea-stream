#!/usr/bin/env bash
#
# skill 的端到端测试脚本：起一个真实的后端实例，跑 skills/idea-manager/tests 下的 unittest。
#
# 职责（照 skill 计划 Task S1）：
#   1. 用临时 SQLite 库起后端（后台）；
#   2. 轮询 GET /api/health 直到 200（最多 60 秒）；
#   3. 跑 unittest（IDEA_BASE_URL 指向刚起的实例）；
#   4. 无论成败都杀掉后端进程；
#   5. 用测试的退出码作为脚本退出码。
#
# 用法：bash skills/idea-manager/scripts/run-tests.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
PORT="${IDEA_TEST_PORT:-5080}"
BASE="http://127.0.0.1:${PORT}"

WORK="$(mktemp -d)"
DB="${WORK}/skill-test.db"
LOG="${WORK}/server.log"

SERVER_PID=""

cleanup() {
  if [ -n "${SERVER_PID}" ]; then
    kill "${SERVER_PID}" 2>/dev/null || true
    # dotnet run 是个包装进程：真正的应用是它的子进程，一并收掉，
    # 否则端口一直被占着，下一次跑测试就撞端口了。
    pkill -P "${SERVER_PID}" 2>/dev/null || true
    wait "${SERVER_PID}" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# 后端日志单独落文件：不跟测试输出混在一起，失败时再整段贴出来。
IDEA_Database__Path="${DB}" IDEA_Urls="${BASE}" \
  dotnet run --project "${ROOT}/server/IdeaApp" --no-launch-profile >"${LOG}" 2>&1 &
SERVER_PID=$!

healthy=0
for _ in $(seq 1 60); do
  if curl -sf "${BASE}/api/health" >/dev/null 2>&1; then
    healthy=1
    break
  fi
  # 后端进程中途挂掉就别再空等了，直接看日志。
  if ! kill -0 "${SERVER_PID}" 2>/dev/null; then
    break
  fi
  sleep 1
done

if [ "${healthy}" -ne 1 ]; then
  echo "后端没能在 60 秒内就绪（${BASE}/api/health 无响应），日志如下：" >&2
  cat "${LOG}" >&2
  exit 1
fi

set +e
IDEA_BASE_URL="${BASE}" python3 -m unittest discover -s "${ROOT}/skills/idea-manager/tests" -v
status=$?
set -e

exit "${status}"
