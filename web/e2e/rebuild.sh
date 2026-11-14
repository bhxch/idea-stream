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
fi

if [[ "${target}" == "backend" || "${target}" == "all" ]]; then
  echo "rebuild.sh: 重建后端。" >&2
  (cd "${REPO_ROOT}" && dotnet build server/IdeaApp -c Debug) >&2
fi

# 放锁：重启服务要走 start-server.sh，它自己会拿锁。
flock -u 9 || true
exec 9>&-

if [[ "${target}" == "backend" || "${target}" == "all" ]]; then
  # 后端换了 → 跑着的进程是旧的 → 停掉重起。
  # start-server.sh 会重新构建前端（产物必须是最新的）并启动服务。
  echo "rebuild.sh: 重启服务。" >&2
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
