#!/usr/bin/env bash
#
# 跑 E2E 用例。
#
#   bash web/e2e/run-e2e.sh [--fresh] [--rebuild] [--keep] [playwright 参数…]
#
#   --fresh    先停掉旧服务、删掉数据库、重建后启动（干净的库）。
#   --rebuild  强制重建（前端 + 后端）并重启服务，保留数据库。
#   --keep     跑完**不**停服务（留着给下一轮/手工排查用）。
#
# 其余参数原样交给 playwright test（例如 --grep、--headed、e2e/foo.spec.ts）。
#
# 端口与库：**一轮 = 一个端口 + 一个库**。
#   端口从 5300 起找空闲的（E2E_PORT 可以指定），库落在
#   web/e2e/.tmp/run-<端口>/e2e.db。端口同时传给 playwright（baseURL 与
#   webServer.url 都用它），所以两轮并发跑时两边端口不同、库不同、
#   产物目录（test-results/run-<端口>、playwright-report/run-<端口>）也不同 ——
#   一轮的复位不会踩到另一轮。
#
# 退出码：playwright test 的退出码原样透传。

set -uo pipefail

# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

FRESH=0
REBUILD=0
KEEP=0
PLAYWRIGHT_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh) FRESH=1; shift ;;
    --rebuild) REBUILD=1; shift ;;
    --keep) KEEP=1; shift ;;
    *) PLAYWRIGHT_ARGS+=("$1"); shift ;;
  esac
done

# ── 定本轮的端口 ────────────────────────────────────────────────────────
# E2E_PORT 指定了就用它；没指定就挑一个空闲的并预约（占用者是本脚本）。
# 挑端口的逻辑在 lib/common.sh 里 —— playwright.config.ts 走的是同一份，
# 两边不会挑出不一样的端口。
e2e_resolve_port || exit 1
export E2E_PORT
echo "run-e2e.sh: 本轮端口 ${E2E_PORT}（${E2E_BASE_URL}），库 ${E2E_DB_FILE}。" >&2

# ── 收摊（被打断时也要做）────────────────────────────────────────────────
# 服务是 setsid 起的：脚本一死它还活着 —— 于是端口被一个**孤儿进程**占着
# （下一轮虽然会换端口，但那个孤儿还在跑，机器上越攒越多）。
# 所以收摊挂在 EXIT 上：正常跑完、Ctrl-C、被 TERM，都会走到。
#
# 装在起服务**之前**：起服务与跑用例之间那几秒也是能被打断的窗口。
#
# SIGKILL 收不了（内核直接杀进程，trap 不跑）—— 那种情况只能靠下一轮换端口。
PLAYWRIGHT_PID=""

cleanup() {
  local rc=$?
  trap - EXIT INT TERM

  # playwright 还在跑就先把它停掉（它是自己起的子进程）。
  if [[ -n "${PLAYWRIGHT_PID}" ]] && kill -0 "${PLAYWRIGHT_PID}" 2>/dev/null; then
    kill -TERM "${PLAYWRIGHT_PID}" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "${PLAYWRIGHT_PID}" 2>/dev/null || break
      sleep 0.25
    done
    kill -KILL "${PLAYWRIGHT_PID}" 2>/dev/null || true
  fi

  if (( KEEP == 0 )); then
    # 默认把 E2E 服务停掉：它是这个脚本自己起的（本轮专用的端口）。
    # 加 --keep 可以留着给下一轮/手工排查用。
    e2e_stop_server
    echo "run-e2e.sh: 服务已停（加 --keep 可以留着）。" >&2
  fi

  exit "${rc}"
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# ── 保证服务在跑 ────────────────────────────────────────────────────────
start_args=()
if (( FRESH == 1 )); then
  start_args+=(--fresh)
fi
if (( REBUILD == 1 )); then
  start_args+=(--rebuild)
fi

start_out="$(bash "${E2E_DIR}/start-server.sh" "${start_args[@]+"${start_args[@]}"}")"
start_rc=$?
printf '%s\n' "${start_out}"
if (( start_rc != 0 )); then
  echo "run-e2e.sh: 服务没起来，放弃。" >&2
  exit "${start_rc}"
fi

# ── 跑用例 ──────────────────────────────────────────────────────────────
# E2E_PORT 已经导出：playwright.config.ts 读到它就用同一个端口
# （baseURL 与 webServer.url 都指向它）。
#
# E2E_NO_AUTOSTOP=1：告诉配置「服务归本脚本管」——
# 否则配置会在 playwright 退出时把服务停掉，--keep 就白给了。
export E2E_NO_AUTOSTOP=1
cd "${WEB_DIR}" || exit 1

# 放后台跑：这样脚本被 TERM 时，收摊能立刻把 playwright 停掉，
# 而不是等它自己跑完（用例可能还要好几分钟）。
npx playwright test "${PLAYWRIGHT_ARGS[@]+"${PLAYWRIGHT_ARGS[@]}"}" &
PLAYWRIGHT_PID=$!
wait "${PLAYWRIGHT_PID}"
status=$?
PLAYWRIGHT_PID=""

exit "${status}"
