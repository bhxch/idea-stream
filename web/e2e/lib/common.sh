#!/usr/bin/env bash
# shellcheck disable=SC2034  # 本文件的常量由 source 它的脚本使用，单独看像是没用
#
# E2E 底座的公共 shell 逻辑：路径、健康探测、端口占用、停服。
#
# start-server.sh / run-e2e.sh / rebuild.sh 都 source 这个文件，
# 别把同一段逻辑抄三遍 —— 抄出去的版本迟早会走样。
#
# 这个文件只放函数与常量，不做副作用（除了定义变量）。

# 端口：E2E 专用。5080 被沙箱里的别的进程占着，5199 是 AOT 冒烟脚本的端口，
# 两个都不能抢。
E2E_PORT=5299
E2E_BASE_URL="http://127.0.0.1:${E2E_PORT}"
E2E_HEALTH_URL="${E2E_BASE_URL}/api/health"

# 目录：e2e/lib/common.sh → e2e → web → 仓库根
E2E_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_DIR="$(cd "${E2E_LIB_DIR}/.." && pwd)"
WEB_DIR="$(cd "${E2E_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${WEB_DIR}/.." && pwd)"

# 运行时产物：数据库、PID、日志、锁。全部落在 .tmp/ 下（已 gitignore）。
E2E_TMP_DIR="${E2E_DIR}/.tmp"
E2E_LOCK_FILE="${E2E_TMP_DIR}/e2e.lock"
E2E_PID_FILE="${E2E_TMP_DIR}/server.pid"
E2E_WRAPPER_PID_FILE="${E2E_TMP_DIR}/server.wrapper.pid"
E2E_LOG_FILE="${E2E_TMP_DIR}/server.log"
E2E_DB_FILE="${E2E_TMP_DIR}/e2e.db"

# 启动后等健康检查通过的最长秒数。
E2E_START_TIMEOUT=180

# 健康检查：/api/health 回 200 且 body 里有 "ok"。
# 只看 HTTP 状态码不够 —— 端口上有别的进程时也可能回 200。
e2e_health_ok() {
  local body
  body="$(curl -fsS --max-time 3 "${E2E_HEALTH_URL}" 2>/dev/null)" || return 1
  [[ "${body}" == *'"ok"'* ]]
}

# 监听 E2E_PORT 的进程 PID（每行一个）。没有就是空输出。
# ss -p 只列出当前用户能见的进程 —— 我们自己起的服务，看得到。
#
# 末尾的 `|| true` 是必须的：grep 没匹配到就返回 1，
# 而调用方多半是 `pids="$(e2e_listening_pids)"` —— 命令替换的退出码
# 会传染给赋值语句，set -e + pipefail 下直接把脚本打断。
e2e_listening_pids() {
  { ss -ltnp "sport = :${E2E_PORT}" 2>/dev/null || true; } \
    | { grep -oP 'pid=\K[0-9]+' || true; } \
    | sort -u || true
}

# 端口上还有没有人监听。
e2e_port_busy() {
  [[ -n "$(e2e_listening_pids)" ]]
}

# 停掉 E2E 服务：先按 PID 文件停（连同进程组），再按端口补刀。
#
# 两个 PID 都要记：
#   server.wrapper.pid —— setsid 起的 dotnet run，它是**会话/进程组**的组长，
#                          负号 kill 能连它带应用一起收掉；
#   server.pid         —— 真正在监听端口的应用进程（dotnet run 的子进程）。
#
# 按端口补刀是必须的：PID 文件可能过期（机器重启、别的进程占过端口），
# 只按 PID 停会漏。
e2e_stop_server() {
  local pid pids
  if [[ -f "${E2E_WRAPPER_PID_FILE}" ]]; then
    pid="$(cat "${E2E_WRAPPER_PID_FILE}" 2>/dev/null || true)"
    if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
      # 负号 = 整个进程组（setsid 让服务自成一个会话）。
      kill -TERM -- "-${pid}" 2>/dev/null || kill -TERM "${pid}" 2>/dev/null || true
    fi
    rm -f "${E2E_WRAPPER_PID_FILE}"
  fi

  if [[ -f "${E2E_PID_FILE}" ]]; then
    pid="$(cat "${E2E_PID_FILE}" 2>/dev/null || true)"
    if [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null; then
      kill -TERM "${pid}" 2>/dev/null || true
    fi
    rm -f "${E2E_PID_FILE}"
  fi

  pids="$(e2e_listening_pids)"
  for pid in ${pids}; do
    kill -TERM "${pid}" 2>/dev/null || true
  done

  # 等端口释放，最多 10 秒。
  for _ in $(seq 1 50); do
    e2e_port_busy || return 0
    sleep 0.2
  done

  # 还不走就上 SIGKILL。
  for pid in $(e2e_listening_pids); do
    kill -KILL "${pid}" 2>/dev/null || true
  done
  sleep 0.5
  return 0
}

# 轮询 /api/health 直到通过（或超时）。
e2e_wait_for_health() {
  local deadline=$((SECONDS + E2E_START_TIMEOUT))
  while (( SECONDS < deadline )); do
    if e2e_health_ok; then
      return 0
    fi
    sleep 0.5
  done
  return 1
}

# 打印日志尾部（启动失败时用来定位）。
e2e_dump_log() {
  if [[ -f "${E2E_LOG_FILE}" ]]; then
    echo "---- ${E2E_LOG_FILE} 尾部 ----" >&2
    tail -n 40 "${E2E_LOG_FILE}" >&2
  fi
}
