#!/usr/bin/env bash
#
# 重建 E2E 用到的东西。
#
#   bash web/e2e/rebuild.sh [frontend|backend|all]
#
#   frontend  只重建前端（产物落 server/IdeaApp/wwwroot）。
#   backend   重建后端，并**重启服务**（复用 start-server.sh 的逻辑）。
#   all       两个都重建（默认）。
#
# 为什么要有这个脚本：多个阶段会并发地改代码、重建、跑用例。
# 构建产物是共享的（server/IdeaApp/wwwroot 与 bin/），
# 两个进程同时构建会互相覆盖 —— 跑起来的那份可能是**半成品**。
# 所以重建一律用 flock 串行化。
#
# 重启的是**哪一个**服务：端口按 E2E_PORT → 「上一轮跑在哪个端口」→
# 新挑一个的顺序定（见 lib/common.sh）。库跟着端口走，所以重启不会丢数据。
#
# 退出码：0 = 重建成功（backend/all 时服务也已重新起来）。

set -uo pipefail

# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

target="${1:-all}"
case "${target}" in
  frontend|backend|all) ;;
  *)
    echo "rebuild.sh: 参数只能是 frontend|backend|all（收到：${target}）" >&2
    exit 2
    ;;
esac

# ── 定端口 ──────────────────────────────────────────────────────────────
# E2E_PORT 指定了就用它；否则接着用「上一轮」的端口（重启那个服务）；
# 都没有就新挑一个（等于新起一个实例）。
port="${E2E_PORT:-}"
if [[ -z "${port}" ]]; then
  port="$(e2e_last_run_port || true)"
fi
if [[ -n "${port}" ]]; then
  e2e_set_port "${port}"
  # 预约它：别让别的并发轮次挑走这个端口。
  e2e_port_reserved "${port}" || e2e_mark_port "${port}" "$$"
else
  e2e_resolve_port || exit 1
fi
export E2E_PORT
mkdir -p "${E2E_TMP_DIR}"

# flock：与 start-server.sh 用同一把锁 —— 重建与启动不能交叉。
# 这里的锁只护住「构建」这一段；重启服务时锁已经放掉了
# （start-server.sh 会自己再拿一次锁）。
exec 9>"${E2E_LOCK_FILE}"
if ! flock -w 900 9; then
  echo "rebuild.sh: 等不到锁（${E2E_LOCK_FILE}），放弃。" >&2
  exit 1
fi

if [[ "${target}" == "frontend" || "${target}" == "all" ]]; then
  echo "rebuild.sh: 重建前端。" >&2
  (cd "${REPO_ROOT}" && npm --prefix web run build) >&2
  # 记下「产物是新的」：start-server.sh 就不用再重建一遍了。
  e2e_mark_frontend_built
fi

if [[ "${target}" == "backend" || "${target}" == "all" ]]; then
  echo "rebuild.sh: 重建后端。" >&2
  (cd "${REPO_ROOT}" && dotnet build server/IdeaApp -c Debug) >&2
fi

# 放锁：重启服务要走 start-server.sh，它自己会拿锁。
flock -u 9 || true
exec 9>&-

if [[ "${target}" == "backend" || "${target}" == "all" ]]; then
  # 后端换了 → 跑着的进程是旧的 → 停掉重起（端口不变，库保留）。
  # start-server.sh 会保证前端产物是最新的（源码没变就复用刚才构建的产物）
  # 并启动服务。
  echo "rebuild.sh: 重启服务（端口 ${E2E_PORT}）。" >&2
  e2e_stop_server
  out="$(bash "${E2E_DIR}/start-server.sh")"
  rc=$?
  printf '%s\n' "${out}"
  if (( rc != 0 )); then
    echo "rebuild.sh: 服务没起来。" >&2
    exit "${rc}"
  fi
fi

echo "rebuild.sh: 完成（${target}）。" >&2
exit 0
