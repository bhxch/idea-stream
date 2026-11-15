#!/usr/bin/env bash
# shellcheck disable=SC2034  # 本文件的常量由 source 它的脚本使用，单独看像是没用
#
# E2E 底座的公共 shell 逻辑：端口分配、路径、健康探测、停服。
#
# start-server.sh / run-e2e.sh / rebuild.sh 都 source 这个文件，
# 别把同一段逻辑抄三遍 —— 抄出去的版本迟早会走样。
#
# 这个文件只放函数与常量，不做副作用（除了定义变量）。
#
# ── 「一轮 = 一个端口 + 一个库」 ─────────────────────────────────────────
#
# 端口从 5300 起找空闲的（探测用 python3 —— 仓库对 python3 是硬依赖，
# verify.sh 里缺 python3 直接失败）。挑中之后：
#
#   - 端口：env 里的 E2E_PORT说了算；没设就自己挑一个；
#   - 库：   web/e2e/.tmp/run-<端口>/e2e.db —— 每轮一个目录；
#   - 产物： web/test-results/run-<端口>/ 与 web/playwright-report/run-<端口>/。
#
# 于是两轮并发跑时，两边端口不同、库不同、产物目录也不同 ——
# 一轮的复位（恢复回收站 → 清想法 → 清项目 → 清 webhook）不会踩到另一轮。

# ── 目录 ────────────────────────────────────────────────────────────────
# e2e/lib/common.sh → e2e → web → 仓库根
E2E_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_DIR="$(cd "${E2E_LIB_DIR}/.." && pwd)"
WEB_DIR="$(cd "${E2E_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${WEB_DIR}/.." && pwd)"

# 运行时产物：数据库、PID、日志、锁、端口预约。全部落在 .tmp/ 下（已 gitignore）。
E2E_TMP_DIR="${E2E_DIR}/.tmp"
# 构建锁：**所有轮次共用一把**。构建产物（server/IdeaApp/wwwroot 与 bin/）是共享的，
# 两个进程同时构建会互相覆盖 —— 跑起来的那份可能是半成品。
E2E_LOCK_FILE="${E2E_TMP_DIR}/e2e.lock"
# 端口预约目录：<port>.owner 记着占用者的 PID，<port>.alloc 是挑端口时的互斥锁。
E2E_PORT_DIR="${E2E_TMP_DIR}/ports"
# 「上一轮跑在哪个端口」—— rebuild.sh 靠它知道该重启哪一个服务。
E2E_LAST_RUN_FILE="${E2E_TMP_DIR}/last-run.port"
# 前端产物的「构建戳」：源码没变就不重建（见 e2e_frontend_build_needed）。
E2E_FRONTEND_STAMP="${E2E_TMP_DIR}/frontend-build.stamp"
# 前端产物目录（**共享的**，不是每轮一份）。
E2E_WWWROOT="${REPO_ROOT}/server/IdeaApp/wwwroot"

# 端口范围：从 5300 起往后找。
# 5080 被沙箱里的别的进程占着，5199 是 server/scripts/aot-smoke.sh 的端口，
# 两个都不能抢。5299 是旧版底座的固定端口 —— 现在也让给「手工指定端口」的人用。
E2E_PORT_BASE="${E2E_PORT_BASE:-5300}"
E2E_PORT_RANGE="${E2E_PORT_RANGE:-100}"

# 本轮端口。环境变量说了算；没设的话由 e2e_resolve_port 挑一个。
E2E_PORT="${E2E_PORT:-}"

# 启动后等健康检查通过的最长秒数。
E2E_START_TIMEOUT=180

# ── 按端口派生的路径 ────────────────────────────────────────────────────
# 端口定了，库、PID、日志就都跟着它走 —— 一轮一个目录，互不干扰。
e2e_set_port() {
  E2E_PORT="$1"
  E2E_BASE_URL="http://127.0.0.1:${E2E_PORT}"
  E2E_HEALTH_URL="${E2E_BASE_URL}/api/health"
  E2E_RUN_DIR="${E2E_TMP_DIR}/run-${E2E_PORT}"
  E2E_DB_FILE="${E2E_RUN_DIR}/e2e.db"
  E2E_PID_FILE="${E2E_RUN_DIR}/server.pid"
  E2E_WRAPPER_PID_FILE="${E2E_RUN_DIR}/server.wrapper.pid"
  E2E_LOG_FILE="${E2E_RUN_DIR}/server.log"
  E2E_PORT_MARKER="${E2E_PORT_DIR}/${E2E_PORT}.owner"
}

# 环境里已经给了端口 → 直接把路径算好（source 之后就可用）。
if [[ -n "${E2E_PORT}" ]]; then
  e2e_set_port "${E2E_PORT}"
fi

# ── 端口空闲吗 ──────────────────────────────────────────────────────────
# 用 python3 探（仓库对 python3 是硬依赖，verify.sh 里缺了就失败）。
#
# 不用 ss / bash 的 /dev/tcp：那些判的是「有没有人连着」，
# 而我们要判的是「能不能 bind」—— 半连接、TIME_WAIT 都会骗过前者。
e2e_port_free() {
  python3 - "$1" <<'PY'
import socket, sys

port = int(sys.argv[1])
sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
try:
    sock.bind(("127.0.0.1", port))
except OSError:
    sys.exit(1)
finally:
    sock.close()
sys.exit(0)
PY
}

# python3 是硬依赖：缺了就直说，别让「探测失败」被误读成「端口被占」。
e2e_require_python() {
  if ! command -v python3 >/dev/null 2>&1; then
    echo "e2e: 没有 python3 —— 端口空闲探测靠它（这个项目对 python3 是硬依赖）。" >&2
    return 1
  fi
  return 0
}

# ── 端口预约 ────────────────────────────────────────────────────────────
#
# 端口是「探测出来」再「起服务」的，中间隔着一次前端构建（几十秒）。
# 不预约的话，两个并发轮次会挑到同一个端口 —— 后起的那轮绑定失败，
# 而且失败得莫名其妙（服务起不来，像是应用坏了）。
#
# 预约的载体是 <port>.owner 文件（里面记着占用者的 PID）：
# 占用者进程一死，预约自动失效 —— 崩了也不会留下永久占位。
#
# 用法：
#   e2e_port_reserved <port>   有人预约着吗？
#   e2e_mark_port <port> [pid] 记下占用者（默认当前进程）
e2e_port_reserved() {
  local port="$1" pid
  local marker="${E2E_PORT_DIR}/${port}.owner"
  [[ -f "${marker}" ]] || return 1
  pid="$(cat "${marker}" 2>/dev/null || true)"
  [[ -n "${pid}" ]] || return 1
  kill -0 "${pid}" 2>/dev/null
}

e2e_mark_port() {
  local port="$1" pid="${2:-$$}"
  mkdir -p "${E2E_PORT_DIR}"
  # 先写临时文件再改名：读的人不会读到写了一半的内容。
  printf '%s\n' "${pid}" >"${E2E_PORT_DIR}/${port}.owner.tmp.$$"
  mv -f "${E2E_PORT_DIR}/${port}.owner.tmp.$$" "${E2E_PORT_DIR}/${port}.owner"
}

# ── 记「上一轮跑在哪个端口」 ────────────────────────────────────────────
# rebuild.sh 重启服务时要知道自己该重启哪一个。
e2e_record_run() {
  printf '%s\n' "$1" >"${E2E_LAST_RUN_FILE}.tmp.$$"
  mv -f "${E2E_LAST_RUN_FILE}.tmp.$$" "${E2E_LAST_RUN_FILE}"
}

e2e_last_run_port() {
  [[ -f "${E2E_LAST_RUN_FILE}" ]] || return 1
  cat "${E2E_LAST_RUN_FILE}" 2>/dev/null || true
}

# ── 挑一个空闲端口并预约它 ──────────────────────────────────────────────
# 打印挑到的端口。失败返回 1。
#
# owner_pid = 预约的持有者。默认是当前进程：它活着，预约就有效。
e2e_reserve_port() {
  local owner_pid="${1:-$$}"
  local port lock_fd
  e2e_require_python || return 1
  mkdir -p "${E2E_PORT_DIR}"
  for (( port = E2E_PORT_BASE; port < E2E_PORT_BASE + E2E_PORT_RANGE; port++ )); do
    # 已经有人预约了 → 换下一个。
    if e2e_port_reserved "${port}"; then
      continue
    fi
    # 用一把 per-port 的锁把「挑端口」这一步做成原子的：
    # 两个进程同时挑时，一个拿到锁，另一个立刻换下一个端口。
    lock_fd=""
    exec {lock_fd}>"${E2E_PORT_DIR}/${port}.alloc" 2>/dev/null || continue
    if flock -n "${lock_fd}"; then
      # 拿到锁之后再看一遍预约与占用（等锁的那几秒里状态可能变了）。
      if ! e2e_port_reserved "${port}" && e2e_port_free "${port}"; then
        e2e_mark_port "${port}" "${owner_pid}"
        printf '%s\n' "${port}"
        return 0
      fi
    fi
    exec {lock_fd}>&- 2>/dev/null || true
  done
  echo "e2e: ${E2E_PORT_RANGE} 个端口（${E2E_PORT_BASE} 起）都被占了 ——" \
       "先停掉不用的 E2E 服务（ss -ltnp | grep 5[3-9][0-9][0-9]）再试。" >&2
  return 1
}

# ── 定本轮的端口 ────────────────────────────────────────────────────────
# 用法：e2e_resolve_port
#   - 环境里有 E2E_PORT → 用它（谁指定谁负责，端口被抢了也是它的事）；
#   - 没有 → 挑一个空闲的并预约（占用者是当前进程）。
e2e_resolve_port() {
  if [[ -n "${E2E_PORT:-}" ]]; then
    e2e_set_port "${E2E_PORT}"
    return 0
  fi
  local port
  port="$(e2e_reserve_port "$$")" || return 1
  e2e_set_port "${port}"
  return 0
}

# ── 前端产物要不要重建 ──────────────────────────────────────────────────
#
# 前端产物是**共享的**（server/IdeaApp/wwwroot —— 后端同源托管它），
# 而 `npm run build` 会先把这个目录**清空**再写新产物：
# 那个窗口里（实测约 30~100ms）页面会 404 / 白屏。
#
# 两轮并发跑时，第二个轮次的构建会把第一个轮次正在跑的页面打成白屏 ——
# 所以源码没变就不重建（产物已经是新的，重建出来也一样）。
#
# 返回 0 = 要重建；返回 1 = 产物已是最新。
e2e_frontend_build_needed() {
  local stamp="${E2E_FRONTEND_STAMP}"
  local src
  # 没戳 → 没建过 → 要建。
  [[ -f "${stamp}" ]] || return 0
  # 有戳但产物没了（有人删了 wwwroot）→ 要建。
  [[ -f "${E2E_WWWROOT}/index.html" ]] || return 0
  for src in "${WEB_DIR}/src" "${WEB_DIR}/index.html" "${WEB_DIR}/vite.config.ts" \
             "${WEB_DIR}/package.json" "${WEB_DIR}/package-lock.json" \
             "${WEB_DIR}/public" "${WEB_DIR}/tsconfig.json" "${WEB_DIR}/tsconfig.node.json"; do
    [[ -e "${src}" ]] || continue
    # 比戳新 → 要建。
    [[ "${src}" -nt "${stamp}" ]] && return 0
    if [[ -d "${src}" ]] && find "${src}" -newer "${stamp}" -type f -print -quit | grep -q .; then
      return 0
    fi
  done
  return 1
}

# 记下「前端产物是新的」（构建成功后调）。
e2e_mark_frontend_built() {
  touch "${E2E_FRONTEND_STAMP}"
}

# ── 健康探测 ────────────────────────────────────────────────────────────
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

# ── 停服 ────────────────────────────────────────────────────────────────
# 停掉本轮的 E2E 服务：先按 PID 文件停（连同进程组），再按端口补刀。
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

# 日志里有没有「端口绑不上」的迹象（Kestrel 绑定失败时的措辞）。
e2e_log_has_bind_error() {
  [[ -f "${E2E_LOG_FILE}" ]] || return 1
  grep -qiE "address already in use|address in use|Failed to bind|AddressInUse" "${E2E_LOG_FILE}"
}

# ── 直接执行时的入口 ────────────────────────────────────────────────────
# playwright.config.ts 与 run-e2e.sh 都走这里挑端口 ——
# 「挑端口」的逻辑只有一份，两边不会走样（走样了就是端口对不上）。
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  case "${1:-}" in
    --reserve-port)
      # 预约者默认是调用者的父进程 —— 真正跑那一轮的进程。
      e2e_reserve_port "${2:-${PPID:-$$}}"
      exit $?
      ;;
    -h|--help)
      sed -n '3,29p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "common.sh: 直接执行时只支持 --reserve-port [owner-pid]" >&2
      exit 2
      ;;
  esac
fi
