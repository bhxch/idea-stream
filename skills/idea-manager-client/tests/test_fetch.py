"""fetch.py 的测试。

取件器是 agent 与服务端之间那道门：它按服务端的清单把 skill 内容同步到本地缓存，
逐文件校验 sha256 后**整体**切换，再把 agent 要的那一份打到 stdout。

测试用一个假的 HTTP 服务端（``http.server.ThreadingHTTPServer``）打桩：
不依赖真的后端实例，也不依赖内容源目录里的文件形状 ——
内容源以后会重写（Task 4），测试不该跟着它变。

取件器一律以子进程调用：端到端测的是「agent 实际会用的那条命令」，
包括 stdout/stderr 的分工与退出码。
"""

import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.normpath(os.path.join(HERE, os.pardir, "scripts", "fetch.py"))

# 假服务端分发副本里的内容。名字取的是「将来」的样子（Task 4 之后内容源会拆成
# 索引页 + references/*.md），取件器对它们一视同仁。
ENTRY = "SKILL.md"
REFERENCE = "references/errors.md"

ENTRY_BODY = "# 索引页\n\n有哪些能力、怎么调、细节去哪找。\n"
REFERENCE_BODY = "# 错误码\n\n| code | 含义 |\n|---|---|\n"
OPENAPI_BODY = '{"openapi":"3.1.0","paths":{}}'


def sha256_hex(text):
    """一段文本的 sha256（小写十六进制）——与清单里的口径一致。"""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def compute_version(files):
    """按服务端的口径算版本串：文件路径与哈希拼起来再取哈希。

    这里只是让「内容变了 → 版本变了」在测试里自然成立；
    取件器自己**不重算**版本串（直接用清单给的）。
    """
    parts = []
    for path in sorted(files):
        parts.append(path + "\n" + sha256_hex(files[path]))
    return hashlib.sha256("\n".join(parts).encode("utf-8")).hexdigest()


class _State(object):
    """假服务端的状态：分发副本的内容、清单的行为、要不要拒鉴权。"""

    def __init__(self):
        self.files = {ENTRY: ENTRY_BODY, REFERENCE: REFERENCE_BODY}
        self.version = compute_version(self.files)
        # 清单端点的特殊行为：(状态码, code)。非空时清单端点回这个错。
        self.manifest_problem = None
        # 清单里的哈希故意给错 —— 模拟「传输中内容被改了」。
        self.corrupt_hash = False
        # 一律 401：模拟 basic auth / token 不对。
        self.deny = False
        # 清单端点回 HTML：模拟代理把请求截走了（SPA 兜底、错误页……）。
        self.manifest_html = False
        # 取文件时传输中断：模拟「头说有多长、body 只有一点」。
        self.truncate_files = False
        # 服务端的绝对路径（不走分发副本的那一类）。
        self.absolute = {"/openapi/v1.json": OPENAPI_BODY}
        # 记下被请求过的路径，用来断言「绝对路径是直连服务端的」。
        self.requested = []


class _Handler(BaseHTTPRequestHandler):
    """分发副本的桩：清单端点 + 取文件端点 + 绝对路径。"""

    def do_GET(self):
        state = self.server.state
        path = urllib.parse.urlsplit(self.path).path
        state.requested.append(path)

        if state.deny:
            self._send(401, b'{"code":"unauthorized","detail":"no"}', "application/problem+json")
            return

        if path == "/skill/idea-manager/version":
            self._serve_manifest(state)
            return

        prefix = "/skill/idea-manager/"
        if path.startswith(prefix):
            relative = urllib.parse.unquote(path[len(prefix):])
            if relative in state.files:
                if state.truncate_files:
                    self._send_truncated(_content_type_of(relative))
                    return
                raw = state.files[relative].encode("utf-8")
                self._send(200, raw, _content_type_of(relative))
                return
            self._send(
                404,
                b'{"code":"skill_file_not_found","detail":"no such file"}',
                "application/problem+json",
            )
            return

        if path in state.absolute:
            self._send(200, state.absolute[path].encode("utf-8"), "application/json")
            return

        self._send(404, b'{"code":"not_found","detail":"no"}', "application/problem+json")

    def _serve_manifest(self, state):
        if state.manifest_html:
            self._send(200, b"<html><body>proxy error</body></html>", "text/html")
            return

        if state.manifest_problem is not None:
            status, code = state.manifest_problem
            body = json.dumps({"code": code, "detail": "manifest problem"}).encode("utf-8")
            self._send(status, body, "application/problem+json")
            return

        files = []
        for path in sorted(state.files):
            digest = sha256_hex(state.files[path])
            if state.corrupt_hash and path == ENTRY:
                # 清单说有另一个哈希 —— 内容取回来就对不上。
                digest = "0" * 64
            files.append(
                {
                    "path": path,
                    "sha256": digest,
                    "size": len(state.files[path].encode("utf-8")),
                }
            )

        manifest = {"version": state.version, "entry": ENTRY, "files": files}
        self._send(200, json.dumps(manifest).encode("utf-8"), "application/json")

    def _send(self, status, raw, content_type):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _send_truncated(self, content_type):
        """回一个「头说有多长、body 只有一点」的响应：模拟传输中断。"""
        body = b"partial"
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body) + 100))
        self.end_headers()
        self.wfile.write(body)
        self.close_connection = True

    def log_message(self, *args):
        pass


def _content_type_of(path):
    if path.endswith(".md"):
        return "text/markdown; charset=utf-8"
    if path.endswith(".json"):
        return "application/json; charset=utf-8"
    return "text/plain; charset=utf-8"


def free_port():
    """找一个当前没人监听的端口 —— 拿来当「连不上的服务端」。"""
    sock = socket.socket()
    try:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]
    finally:
        sock.close()


# 「用默认的假服务端地址」与「不设 IDEA_BASE_URL」要分得开 —— 用哨兵值区分。
_DEFAULT_BASE_URL = object()


class FetchTest(unittest.TestCase):
    """取件器的端到端行为。"""

    def setUp(self):
        self.cache = tempfile.mkdtemp(prefix="idea-fetch-cache-")
        self.addCleanup(shutil.rmtree, self.cache, True)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
        self.server.state = _State()
        # poll_interval 调小：shutdown() 最多等一个轮询周期，
        # 默认 0.5 秒会让整套测试白等好几秒。
        thread = threading.Thread(
            target=self.server.serve_forever, kwargs={"poll_interval": 0.05}
        )
        thread.daemon = True
        thread.start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)

        self.base_url = "http://127.0.0.1:{}".format(self.server.server_address[1])

    # -- 工具 ---------------------------------------------------------------

    def run_fetch(self, args, base_url=_DEFAULT_BASE_URL, env=None):
        """跑一次取件器，返回 (退出码, stdout, stderr)。

        环境从**干净**的环境变量起步：调用方（也就是这台机器）的
        IDEA_PASS 之类不该渗进测试。

        ``base_url`` 默认指向假服务端；显式给 ``None`` 表示**不设**
        （用来测「没设 IDEA_BASE_URL」那条）。
        """
        environ = dict(os.environ)
        for name in ("IDEA_BASE_URL", "IDEA_USER", "IDEA_PASS", "IDEA_TOKEN", "IDEA_SKILL_CACHE_DIR"):
            environ.pop(name, None)
        environ["IDEA_SKILL_CACHE_DIR"] = self.cache
        if base_url is _DEFAULT_BASE_URL:
            base_url = self.base_url
        if base_url is not None:
            environ["IDEA_BASE_URL"] = base_url
        if env:
            environ.update(env)

        proc = subprocess.run(
            [sys.executable, SCRIPT] + args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environ,
            encoding="utf-8",
        )
        return proc.returncode, proc.stdout, proc.stderr

    def cached_entry(self, version):
        """缓存里某个版本目录里的索引页内容。"""
        path = os.path.join(self.cache, version, ENTRY)
        with open(path, encoding="utf-8") as handle:
            return handle.read()

    # -- 三种用法 -----------------------------------------------------------

    def test_prints_the_entry_document(self):
        """不给参数 → 打印清单里 entry 指向的索引页。"""
        code, out, err = self.run_fetch([])

        self.assertEqual(code, 0, err)
        self.assertEqual(out, ENTRY_BODY)

    def test_prints_a_reference_file(self):
        """给相对路径 → 打印分发副本里的那一份文件。"""
        code, out, err = self.run_fetch(["references/errors.md"])

        self.assertEqual(code, 0, err)
        self.assertEqual(out, REFERENCE_BODY)

    def test_prints_an_absolute_server_path(self):
        """给绝对路径 → 直连服务端取那个路径（不走分发副本）。"""
        code, out, err = self.run_fetch(["/openapi/v1.json"])

        self.assertEqual(code, 0, err)
        self.assertEqual(out, OPENAPI_BODY)
        self.assertIn("/openapi/v1.json", self.server.state.requested)

    def test_accepts_a_relative_path_with_a_dot_slash_prefix(self):
        """./references/x.md 与 references/x.md 是同一个文件。"""
        code, out, err = self.run_fetch(["./" + REFERENCE])

        self.assertEqual(code, 0, err)
        self.assertEqual(out, REFERENCE_BODY)

    # -- 同步 ---------------------------------------------------------------

    def test_syncs_a_new_version_and_replaces_the_cache(self):
        """服务端换版本 → 缓存跟着换，当前版本目录里是新版内容。

        旧版本目录**留着**（同步失败时还要靠它退），所以不断言
        「缓存里只有一个版本目录」。
        """
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        old_version = self.server.state.version
        self.assertTrue(os.path.isdir(os.path.join(self.cache, old_version)))

        # 内容变了 → 版本跟着变
        self.server.state.files[ENTRY] = ENTRY_BODY + "\n新增的一行。\n"
        self.server.state.version = compute_version(self.server.state.files)

        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)
        self.assertEqual(out, self.server.state.files[ENTRY])

        new_version = self.server.state.version
        self.assertNotEqual(new_version, old_version)
        # 当前版本目录里是新版内容
        self.assertEqual(self.cached_entry(new_version), self.server.state.files[ENTRY])
        # 旧版本目录还留着
        self.assertTrue(os.path.isdir(os.path.join(self.cache, old_version)))
        self.assertEqual(self.cached_entry(old_version), ENTRY_BODY)

    def test_keeps_the_old_version_when_a_hash_does_not_match(self):
        """哈希对不上 → 整次同步作废，旧版本原样留着。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        old_version = self.server.state.version

        # 服务端内容变了，但清单里的哈希是错的
        self.server.state.files[ENTRY] = "# 被改过的内容\n"
        self.server.state.version = compute_version(self.server.state.files)
        self.server.state.corrupt_hash = True

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertIn("skill_integrity_error", err)
        self.assertEqual(out, "")

        # 旧版本目录还在，内容没被覆盖
        self.assertTrue(os.path.isdir(os.path.join(self.cache, old_version)))
        self.assertEqual(self.cached_entry(old_version), ENTRY_BODY)

        # 新版本目录没被建立，临时目录也清掉了
        self.assertFalse(os.path.exists(os.path.join(self.cache, self.server.state.version)))
        self.assertEqual([name for name in os.listdir(self.cache) if name.startswith(".tmp-")], [])

    # -- 错误 ---------------------------------------------------------------

    def test_reports_missing_config_without_the_base_url(self):
        """没设 IDEA_BASE_URL → 一行 missing_config，退出码 1。"""
        code, out, err = self.run_fetch([], base_url=None)

        self.assertEqual(code, 1)
        self.assertTrue(err.startswith("missing_config: "), err)
        self.assertEqual(out, "")

    def test_reports_skill_not_distributed_on_404(self):
        """清单端点 404 + skill_not_distributed → 同名错误码。"""
        self.server.state.manifest_problem = (404, "skill_not_distributed")

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertIn("skill_not_distributed", err)
        self.assertEqual(out, "")

    def test_rejects_an_invalid_base_url(self):
        """地址没带 scheme → invalid_config，退出码 1。"""
        code, out, err = self.run_fetch([], base_url="ideas.example.com")

        self.assertEqual(code, 1)
        self.assertTrue(err.startswith("invalid_config: "), err)
        self.assertEqual(out, "")

    def test_reports_not_found_for_an_unknown_relative_path(self):
        """清单里没有这个文件 → not_found，退出码 1。"""
        code, out, err = self.run_fetch(["references/nope.md"])

        self.assertEqual(code, 1)
        self.assertIn("not_found", err)
        self.assertEqual(out, "")

    def test_reports_network_error_when_the_server_is_unreachable(self):
        """连不上 → network_error，退出码 2。"""
        code, out, err = self.run_fetch([], base_url="http://127.0.0.1:{}".format(free_port()))

        self.assertEqual(code, 2)
        self.assertIn("network_error", err)
        self.assertEqual(out, "")

    def test_reports_a_non_json_manifest_as_invalid_response(self):
        """清单端点回的不是 JSON（代理返回了 HTML）→ invalid_response，不是 traceback。"""
        self.server.state.manifest_html = True

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertIn("invalid_response", err)
        self.assertEqual(out, "")
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))

    def test_never_echoes_credentials(self):
        """401 时 stdout 与 stderr 都不含密码。"""
        password = "PASS-SECRET-9f3a2b"
        self.server.state.deny = True

        code, out, err = self.run_fetch(
            [], env={"IDEA_USER": "agent", "IDEA_PASS": password}
        )

        self.assertEqual(code, 1)
        self.assertIn("auth_error", err)
        self.assertNotIn(password, err)
        self.assertNotIn(password, out)
        self.assertEqual(out, "")

    # -- 缓存目录 -----------------------------------------------------------

    def test_uses_the_xdg_cache_home_by_default(self):
        """没设 IDEA_SKILL_CACHE_DIR → 用 XDG_CACHE_HOME 下的 idea-skill。"""
        xdg = tempfile.mkdtemp(prefix="idea-fetch-xdg-")
        self.addCleanup(shutil.rmtree, xdg, True)

        code, out, err = self.run_fetch(
            [], env={"IDEA_SKILL_CACHE_DIR": "", "XDG_CACHE_HOME": xdg}
        )

        self.assertEqual(code, 0, err)
        self.assertTrue(
            os.path.isdir(os.path.join(xdg, "idea-skill", self.server.state.version)),
            "缓存目录该落在 XDG_CACHE_HOME/idea-skill/<version>",
        )

    def test_falls_back_to_the_cache_when_the_manifest_cannot_be_fetched(self):
        """取不到清单但本地有缓存 → 用缓存，stderr 给一行提示。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        dead = "http://127.0.0.1:{}".format(free_port())
        code, out, err = self.run_fetch([], base_url=dead)

        self.assertEqual(code, 0, err)
        self.assertEqual(out, ENTRY_BODY)
        self.assertEqual(len(err.strip().splitlines()), 1, "提示只该有一行：{}".format(err))

    def test_falls_back_to_the_old_cache_when_a_download_is_cut_off(self):
        """同步到一半传输断了 → 用旧版本（旧版本留着就是为了这一刻）。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        # 服务端换版本，但取文件时传输中断
        self.server.state.files[ENTRY] = "# 新内容\n"
        self.server.state.version = compute_version(self.server.state.files)
        self.server.state.truncate_files = True

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 0, err)
        self.assertEqual(out, ENTRY_BODY)
        self.assertEqual(len(err.strip().splitlines()), 1, "提示只该有一行：{}".format(err))

        # 同步没成功：新版本目录没建立，临时目录也清掉了
        self.assertFalse(os.path.exists(os.path.join(self.cache, self.server.state.version)))
        self.assertEqual([name for name in os.listdir(self.cache) if name.startswith(".tmp-")], [])


if __name__ == "__main__":
    unittest.main()
