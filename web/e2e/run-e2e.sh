#!/usr/bin/env bash
#
# 跑 E2E 用例。
#
#   bash web/e2e/run-e2e.sh [--fresh] [--rebuild] [--keep] [playwright 参数…]
#
#   --fresh    先停掉旧服务、删掉数据库、重建后启动（干净的库）。
#   --rebuild  强制重建（前端 + 后端）并重启服务，保留数据库。
#   --keep     跑完**不**停服务（下一轮跑可以接着用，省一次重建）。
#
# 其余参数原样交给 playwright test（例如 --grep、--headed、e2e/foo.spec.ts）。
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
cd "${WEB_DIR}" || exit 1
npx playwright test "${PLAYWRIGHT_ARGS[@]+"${PLAYWRIGHT_ARGS[@]}"}"
status=$?

# ── 收尾 ────────────────────────────────────────────────────────────────
if (( KEEP == 0 )); then
  # 默认把 E2E 服务停掉：它是这个脚本自己起的（端口 5299，专用）。
  # 加 --keep 可以留着给下一轮用（下一轮就不用重建了）。
  e2e_stop_server
  echo "run-e2e.sh: 服务已停（加 --keep 可以留着）。" >&2
fi

exit "${status}"
