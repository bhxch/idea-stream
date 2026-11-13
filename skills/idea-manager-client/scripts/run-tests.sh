#!/usr/bin/env bash
#
# 取件器的测试脚本：跑 skills/idea-manager-client/tests 下的 unittest。
#
# 不需要真的后端实例：每个用例自己起一个假的 HTTP 服务端
# （http.server.ThreadingHTTPServer：端口随机、内容可控、能造各种错）。
# 与旧的 skills/idea-manager/scripts/run-tests.sh 的区别：那份要起一个真的
# .NET 后端（要 dotnet、要编译、慢），这份什么都不依赖。
#
# 顺带做一次冒烟：拿一个**真的** HTTP 服务端（静态文件，python3 -m http.server）
# 打取件器的绝对路径那条路 —— 它验证的是「取件器跟真的 HTTP 服务端能对话」。
#
# 用法：bash skills/idea-manager-client/scripts/run-tests.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORK="$(mktemp -d)"
SERVER_PID=""

cleanup() {
  local status=$?
  if [ -n "${SERVER_PID}" ]; then
    kill "${SERVER_PID}" 2>/dev/null || true
    wait "${SERVER_PID}" 2>/dev/null || true
  fi
  rm -rf "${WORK}"
  exit "${status}"
}
trap cleanup EXIT

# ---- 冒烟：真的 HTTP 服务端 ---------------------------------------------

PORT="$(python3 -c 'import socket
sock = socket.socket()
sock.bind(("127.0.0.1", 0))
print(sock.getsockname()[1])
sock.close()')"

printf 'hello from the fake server\n' >"${WORK}/hello.txt"

python3 -m http.server "${PORT}" --bind 127.0.0.1 --directory "${WORK}" \
  >"${WORK}/server.log" 2>&1 &
SERVER_PID=$!

ready=0
for _ in $(seq 1 50); do
  if curl -sf "http://127.0.0.1:${PORT}/hello.txt" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 0.1
done

if [ "${ready}" -ne 1 ]; then
  echo "冒烟失败：假的 HTTP 服务端没起来，日志如下：" >&2
  cat "${WORK}/server.log" >&2
  exit 1
fi

smoke="$(IDEA_BASE_URL="http://127.0.0.1:${PORT}" \
  python3 "${ROOT}/skills/idea-manager-client/scripts/fetch.py" /hello.txt)"

if [ "${smoke}" != "hello from the fake server" ]; then
  echo "冒烟失败：取件器没取到静态文件，取到的是：${smoke}" >&2
  exit 1
fi

echo "冒烟通过：取件器从真的 HTTP 服务端取到了静态文件。"
echo

# ---- 单元测试 ------------------------------------------------------------

cd "${ROOT}"
python3 -m unittest discover -s skills/idea-manager-client/tests -v
