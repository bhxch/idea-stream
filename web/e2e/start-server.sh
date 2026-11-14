#!/usr/bin/env bash
#
# 幂等地启动（或复用）E2E 专用后端。
#
#   bash web/e2e/start-server.sh [--fresh] [--rebuild]
#
# 行为：
#   - 端口 5299 上已有健康服务 → 直接退出 0 复用（不重建、不重启）；
#   - 否则：构建前端 → 构建后端 → 以 setsid 拉起服务（脚本退出后仍活着）
#     → 轮询 /api/health 直到 200。
#
# 标志：
#   --fresh    先杀掉端口上的旧服务、删掉数据库文件，再重建启动（干净的库）。
#   --rebuild  强制重建并重启（保留数据库）。
#
# 并发：整个流程用 flock 串行化。多个阶段会并发调用本脚本，
# 拿到锁之后**重新探测一次**健康状态再决定要不要起 —— 免得两个进程都
# 看到「没在跑」然后各起一个。
#
# 退出码：0 = 服务已在跑（或已成功启动）；非 0 = 启动失败。
# stdout 只输出一行机器可读的状态（E2E_SERVER=reused|started pid=...），
# 进度信息一律走 stderr —— run-e2e.sh 靠这一行判断「这次是不是我起的」。

set -euo pipefail

# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

FRESH=0
REBUILD=0
for arg in "$@"; do
  case "${arg}" in
    --fresh) FRESH=1 ;;
    --rebuild) REBUILD=1 ;;
    -h|--help)
      sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "start-server.sh: 不认识的参数：${arg}" >&2
      exit 2
      ;;
  esac
done

mkdir -p "${E2E_TMP_DIR}"

# flock：整个启动流程串行化。用 fd 9，脚本退出时自动释放。
exec 9>"${E2E_LOCK_FILE}"
if ! flock -w 900 9; then
  echo "start-server.sh: 等不到锁（${E2E_LOCK_FILE}），放弃。" >&2
  exit 1
fi

# ── 拿到锁之后再探测一次 ────────────────────────────────────────────────
# 排队等的这段时间里，前一个进程可能已经把服务起好了。
if (( FRESH == 0 )) && (( REBUILD == 0 )) && e2e_health_ok; then
  pid="$(e2e_listening_pids | head -n 1)"
  # PID 文件可能过期（别人起的服务），顺手更正一下。
  [[ -n "${pid}" ]] && echo "${pid}" >"${E2E_PID_FILE}"
  echo "E2E_SERVER=reused pid=${pid}"
  exit 0
fi

if (( FRESH == 1 )); then
  echo "start-server.sh: --fresh —— 停掉旧服务并删除数据库。" >&2
  e2e_stop_server
  rm -f "${E2E_DB_FILE}" "${E2E_DB_FILE}-wal" "${E2E_DB_FILE}-shm"
elif (( REBUILD == 1 )); then
  echo "start-server.sh: --rebuild —— 停掉旧服务后重建。" >&2
  e2e_stop_server
else
  # 端口上有东西在听但健康检查不过：多半是别的进程占了端口。
  if e2e_port_busy; then
    echo "start-server.sh: 端口 ${E2E_PORT} 被占着，且 /api/health 不通 —— 放弃。" >&2
    e2e_dump_log
    exit 1
  fi
fi

# ── 构建 ────────────────────────────────────────────────────────────────
# 构建输出走 stderr：stdout 只留那一行机器可读的状态。
echo "start-server.sh: 构建前端（产物落到 server/IdeaApp/wwwroot）。" >&2
(cd "${REPO_ROOT}" && npm --prefix web run build) >&2

echo "start-server.sh: 构建后端。" >&2
(cd "${REPO_ROOT}" && dotnet build server/IdeaApp -c Debug) >&2

# ── 启动 ────────────────────────────────────────────────────────────────
# IDEA_Database__Path 给**绝对路径**：应用把相对路径按「应用目录」
# （bin/Debug/net10.0/）解析，而不是按当前工作目录 —— 相对路径会落到
# 意料之外的地方去。绝对路径才是 web/e2e/.tmp/e2e.db。
echo "start-server.sh: 启动服务（${E2E_BASE_URL}）。" >&2
: >"${E2E_LOG_FILE}"
cd "${REPO_ROOT}"
# 9>&- ：把锁用的 fd 9 关掉再拉起服务。
# 不关的话，后台服务会**继承**这个 fd，锁跟着它活到服务退出 ——
# 下一个调本脚本的进程就永远拿不到锁（flock 认的是打开文件描述，不是进程）。
IDEA_Urls="${E2E_BASE_URL}" \
IDEA_Database__Path="${E2E_DB_FILE}" \
  setsid nohup dotnet run --project server/IdeaApp --no-build --no-launch-profile \
  >>"${E2E_LOG_FILE}" 2>&1 9>&- &
wrapper_pid=$!
echo "${wrapper_pid}" >"${E2E_WRAPPER_PID_FILE}"

if ! e2e_wait_for_health; then
  echo "start-server.sh: 服务没能在 ${E2E_START_TIMEOUT} 秒内就绪。" >&2
  e2e_dump_log
  e2e_stop_server
  exit 1
fi

# PID 文件记**真正在监听**的进程（dotnet run 起的是子进程，壳子 PID 没用）。
pid="$(e2e_listening_pids | head -n 1)"
if [[ -n "${pid}" ]]; then
  echo "${pid}" >"${E2E_PID_FILE}"
fi

echo "E2E_SERVER=started pid=${pid}"

# ── 被 Playwright 的 webServer 拉起来时：驻留 ───────────────────────────
# Playwright 的 webServer 要求这个命令的进程**一直活着** ——
# 它一退出，Playwright 就报 "Process from config.webServer exited early"
# （哪怕服务其实已经起来了）。
#
# 锁要先放掉：不然别的阶段调本脚本会被挡住整个测试时长。
if [[ "${E2E_WEB_SERVER:-}" == "1" ]]; then
  flock -u 9 || true
  exec 9>&-
  echo "start-server.sh: webServer 模式，驻留到 Playwright 收工。" >&2
  while true; do
    sleep 3600
  done
fi

exit 0
