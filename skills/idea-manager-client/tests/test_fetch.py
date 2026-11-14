"""fetch.py 的测试。

取件器是 agent 与服务端之间那道门：它按服务端的清单把 skill 内容同步到本地缓存，
逐文件校验 sha256 后**整体**切换，再把 agent 要的那一份打到 stdout。

测试用一个假的 HTTP 服务端（``http.server.ThreadingHTTPServer``）打桩：
不依赖真的后端实例，也不依赖内容源目录里的文件形状 ——
内容源以后会重写（Task 4），测试不该跟着它变。

取件器一律以子进程调用：端到端测的是「agent 实际会用的那条命令」，
包括 stdout/stderr 的分工与退出码。
"""

import base64
import hashlib
import importlib.util
import json
import os
import re
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

# 仓库根（HERE 是 skills/idea-manager-client/tests，上三层就是它）。
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))

# 手册的错误码那一页与薄壳：它们各自抄了一份取件器的错误码。
MANUAL_ERRORS = os.path.join(ROOT, "skills", "idea-manager", "references", "errors.md")
SHELL_SKILL = os.path.join(ROOT, "skills", "idea-manager-client", "SKILL.md")

# 取件器的兜底码：main() 里遇到未预期的异常时直接写出去的一行
# （不走 CliError，所以它不在 OWN_ERROR_CODES 里 —— 但它确实会出现在 stderr 上）。
FALLBACK_CODE = "internal_error"

# 手册里那一行「http_<状态码>」：那是**形态**不是码（取件器产出 http_404 这种）。
HTTP_STATUS_ROW = "http_<状态码>"

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


def valid_manifest():
    """一份**合法**的清单（与假服务端平时发的那份等价）。

    用它做底，把某一处改坏 —— 清单校验的每个分支各改一处。
    """
    files = {ENTRY: ENTRY_BODY, REFERENCE: REFERENCE_BODY}

    return {
        "version": compute_version(files),
        "entry": ENTRY,
        "files": [
            {
                "path": path,
                "sha256": sha256_hex(files[path]),
                "size": len(files[path].encode("utf-8")),
            }
            for path in sorted(files)
        ],
    }


class _State(object):
    """假服务端的状态：分发副本的内容、清单的行为、要不要拒鉴权。"""

    def __init__(self):
        self.files = {ENTRY: ENTRY_BODY, REFERENCE: REFERENCE_BODY}
        self.version = compute_version(self.files)
        # 清单里的 version 覆盖（模拟服务端给了个不合法的版本串，比如带换行）。
        self.version_override = None
        # **整份清单覆盖**：非 None 时清单端点原样返回它（不做任何加工）。
        # 用它造「服务端说了什么就是什么」的清单 —— 取件器的清单校验
        # 有六七个分支，光靠 version_override 只够覆盖其中一条。
        self.manifest_override = None
        # 清单端点回 302，指向这个地址（模拟重定向到别处）。
        self.redirect_to = None
        # 清单端点的特殊行为：(状态码, code) 或 (状态码, code, detail)。
        # 非空时清单端点回这个错（problem+json）。
        self.manifest_problem = None
        # 清单里的哈希故意给错 —— 模拟「传输中内容被改了」。
        self.corrupt_hash = False
        # 一律 401：模拟 basic auth / token 不对。
        self.deny = False
        # 清单端点回 HTML：模拟代理把请求截走了（SPA 兜底、错误页……）。
        self.manifest_html = False
        # 取文件时传输中断：模拟「头说有多长、body 只有一点」。
        self.truncate_files = False
        # 把收到的 Authorization 头回显进错误体：模拟代理/服务端反射请求头。
        self.echo_auth = False
        # 服务端的绝对路径（不走分发副本的那一类）。
        self.absolute = {"/openapi/v1.json": OPENAPI_BODY}
        # 记下被请求过的路径，用来断言「绝对路径是直连服务端的」。
        self.requested = []
        # 记下每次请求的头，用来断言认证头发对了。
        self.headers = []


class _Handler(BaseHTTPRequestHandler):
    """分发副本的桩：清单端点 + 取文件端点 + 绝对路径。"""

    def do_GET(self):
        state = self.server.state
        path = urllib.parse.urlsplit(self.path).path
        state.requested.append(path)
        state.headers.append(dict(self.headers))

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
        if state.redirect_to is not None:
            # 302 到别处：跨 origin 的重定向取件器不该跟着走。
            self.send_response(302)
            self.send_header("Location", state.redirect_to)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        if state.manifest_override is not None:
            # 整份清单原样返回：服务端说什么就是什么。
            raw = json.dumps(state.manifest_override).encode("utf-8")
            self._send(200, raw, "application/json")
            return

        if state.echo_auth:
            # 回显请求头：凭据原样进了响应体，看取件器会不会把它抄进 stderr。
            auth = self.headers.get("Authorization", "")
            body = json.dumps(
                {"detail": "服务端把请求头反射回来了：Authorization: {}".format(auth)},
                ensure_ascii=False,
            ).encode("utf-8")
            self._send(500, body, "application/problem+json")
            return

        if state.manifest_html:
            self._send(200, b"<html><body>proxy error</body></html>", "text/html")
            return

        if state.manifest_problem is not None:
            status, code = state.manifest_problem[0], state.manifest_problem[1]
            detail = state.manifest_problem[2] if len(state.manifest_problem) > 2 else "manifest problem"
            body = json.dumps({"code": code, "detail": detail}).encode("utf-8")
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

        version = state.version
        if state.version_override is not None:
            version = state.version_override

        manifest = {"version": version, "entry": ENTRY, "files": files}
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


def dead_pid():
    """找一个确定没有进程在跑的 pid —— 拿来当「崩溃残留」的临时目录名。"""
    for candidate in (999999, 999998, 4194303, 1234567, 424242):
        try:
            os.kill(candidate, 0)
        except ProcessLookupError:
            return candidate
        except OSError:
            continue
    raise RuntimeError("找不到没在跑的 pid")


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

    def test_prunes_the_temporary_directory_left_by_a_crashed_run(self):
        """崩了留下的临时目录 → 下一次取件时清掉（别在缓存根目录堆孤儿）。

        临时目录名里带着 pid：主人那个进程已经不在了，就是残骸。
        """
        leftover = os.path.join(self.cache, ".tmp-deadbeef-{}".format(dead_pid()))
        os.makedirs(leftover)
        with open(os.path.join(leftover, "SKILL.md"), "w", encoding="utf-8") as handle:
            handle.write("半个文件")

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 0, err)
        self.assertFalse(os.path.exists(leftover), "崩溃留下的临时目录该被清掉")

    def test_prunes_the_temporary_directory_when_the_manifest_cannot_be_fetched(self):
        """清单取不到（网络失败）→ 残留的临时目录也要清掉。

        清理要挂在**所有出口**上：成功、失败、提前返回 —— 只挂在成功路径上的话，
        取不到清单那次就把孤儿留下了。
        """
        leftover = os.path.join(self.cache, ".tmp-deadbeef-{}".format(dead_pid()))
        os.makedirs(leftover)
        with open(os.path.join(leftover, "SKILL.md"), "w", encoding="utf-8") as handle:
            handle.write("半个文件")

        code, out, err = self.run_fetch(
            [], base_url="http://127.0.0.1:{}".format(free_port())
        )

        self.assertEqual(code, 2)
        self.assertTrue(err.startswith("network_error: "), err)
        self.assertFalse(os.path.exists(leftover), "取不到清单时残留的临时目录也要清掉")
        # 缓存根目录里不该剩下任何临时目录
        self.assertEqual(
            [name for name in os.listdir(self.cache) if name.startswith(".tmp-")], []
        )

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

    # -- 地址里的密码也不许漏 -----------------------------------------------

    def test_never_echoes_the_password_in_the_base_url_when_the_port_is_broken(self):
        """地址里带密码且端口解析不出来 → 异常消息里不许出现那个密码。

        urllib 抱怨端口时会把整段 authority 说出来（"nonnumeric port: 'pw@host'"），
        密码就跟着进了 stderr —— 这是实测过的泄漏，不是推测。
        """
        code, out, err = self.run_fetch(
            [], base_url="http://user:sekret-pw@nonexistent.invalid"
        )

        self.assertNotIn("sekret-pw", err)
        self.assertNotIn("sekret-pw", out)
        self.assertIn("network_error", err)
        self.assertEqual(code, 2)

    def test_never_echoes_the_password_in_the_base_url_when_the_path_has_a_space(self):
        """地址里带密码且路径含空格 → 异常消息里不许出现那个密码。"""
        code, out, err = self.run_fetch(
            [], base_url="http://user:sekret-pw@127.0.0.1:1/a b"
        )

        self.assertNotIn("sekret-pw", err)
        self.assertNotIn("sekret-pw", out)
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))

    def test_never_echoes_the_password_in_the_base_url_without_a_scheme(self):
        """地址没带 scheme（连 userinfo 带密码）→ 回显地址时也不许出现那个密码。"""
        code, out, err = self.run_fetch([], base_url="user:sekret-pw@example.invalid")

        self.assertNotIn("sekret-pw", err)
        self.assertNotIn("sekret-pw", out)
        self.assertIn("invalid_config", err)
        self.assertEqual(code, 1)

    def test_never_echoes_a_password_containing_a_space(self):
        """地址里的密码带**内部空白** → (stderr 与 stdout 都不许出现它。

        旧做法用 ``[^/?#@\\s]*`` 那个字符类找 userinfo：它整个不含空白，
        ``strip()`` 又只救首尾 —— 密码里的那个空白让两道脱敏**都拦不住**，
        凭据原样进了 stderr（实测形态：``http://user:se kret@host``）。

        现在按 **URL 结构**取：authority 到第一个 '/'、'?'、'#' 为止，
        userinfo 是最后一个 '@' 之前的部分，密码是第一个 ':' 之后的全部。
        """
        code, out, err = self.run_fetch([], base_url="http://user:se kret@host")

        self.assertNotIn("se kret", err)
        self.assertNotIn("se kret", out)
        self.assertEqual(code, 2)

    def test_never_echoes_a_password_containing_an_at_sign(self):
        """地址里的密码带 '@' → 整个密码都得遮住。

        旧做法把 userinfo 的字符类写成不含 '@'：``user:pa@ss@host`` 里
        只认出 ``user:pa``，剩下的 ``ss`` 原样留在 stderr 里。
        """
        code, out, err = self.run_fetch([], base_url="http://user:pa@ss@host")

        self.assertNotIn("pa@ss", err)
        self.assertNotIn("pa@ss", out)
        # 旧做法漏出来的那一截（"ss@host"）也不许留
        self.assertNotIn("ss@host", err)
        self.assertEqual(code, 2)

    def test_never_echoes_the_decoded_fragment_of_a_percent_encoded_password(self):
        """地址里的密码是百分号编码的 → **解码后**的那份（及其片段）也得遮。

        urllib 会把 host 百分号解码，而它把**整个 authority** 当 host 用：
        ``http://user:p%40ss%3Aword@host`` 报的是
        ``nonnumeric port: 'word@host'`` —— 露出来的是解码后的**片段**。

        所以凭据表里要同时有：原文、解码后的原文、以及每个冒号之后的片段
        （连主机名一起 —— http.client 报出来的就是那个形状）。
        """
        code, out, err = self.run_fetch([], base_url="http://user:p%40ss%3Aword@host")

        for secret in ("p%40ss%3Aword", "p@ss:word", "word@host"):
            self.assertNotIn(secret, err, "stderr 里露出了 {}".format(secret))
            self.assertNotIn(secret, out, "stdout 里露出了 {}".format(secret))

        self.assertEqual(code, 2)

    def test_never_echoes_the_password_when_the_base_url_has_leading_space(self):
        """地址带前导空白 → 密码仍然不许漏。

        真正发出去的地址是去空白的那份，脱敏也得按去空白的那份算 ——
        不然正则锚在开头就匹配不上，密码原样进 stderr。
        """
        code, out, err = self.run_fetch(
            [], base_url=" http://user:sekret-pw@nonexistent.invalid"
        )

        self.assertNotIn("sekret-pw", err)
        self.assertNotIn("sekret-pw", out)
        self.assertIn("network_error", err)

    def test_keeps_the_error_code_intact_when_the_password_looks_like_a_code(self):
        """密码与错误码同名时也不能把 code 改写掉（agent 要按 code 分支）。"""
        code, out, err = self.run_fetch(
            [],
            base_url="http://127.0.0.1:{}".format(free_port()),
            env={"IDEA_PASS": "error"},
        )

        self.assertEqual(code, 2)
        self.assertTrue(err.startswith("network_error: "), err)
        self.assertNotIn("network_***", err)
        # code 段逐字是 network_error：既没被值级脱敏改写，也没被换成别的码
        self.assertEqual(err.split(":", 1)[0], "network_error")

    def test_does_not_echo_the_basic_auth_header_the_server_reflects(self):
        """服务端把 Authorization 头回显进错误体 → stderr 不含那串 base64。

        base64 是可逆的 —— 只掩裸密码、放过 base64 等于掩了个寂寞。
        """
        self.server.state.echo_auth = True

        code, out, err = self.run_fetch(
            [], env={"IDEA_USER": "agent", "IDEA_PASS": "pw-1234"}
        )

        self.assertNotIn("YWdlbnQ6cHctMTIzNA==", err)   # base64("agent:pw-1234")
        self.assertNotIn("pw-1234", err)
        self.assertNotIn("agent", err)
        self.assertEqual(code, 1)

    def test_keeps_the_at_sign_in_a_path_intact(self):
        """路径里的 '@' 不是 userinfo：脱敏不许把它当成密码段抹掉。

        旧做法是按最后一个 '@' 切（rpartition），那样 "host/a@b" 会被切成 "***@b"。
        """
        code, out, err = self.run_fetch([], base_url="host.invalid/a@b")

        self.assertNotIn("***", err)
        self.assertIn("host.invalid/a@b", err)

    # -- 服务端给的 code ----------------------------------------------------
    #
    # code 段是 agent 要拿来分支的（network_error / not_found / …），
    # 但它也是**服务端给的字符串**：回显型服务端/代理只要把凭据放进
    # problem+json 的 code 字段（而不是 detail），凭据就跟着 code 进了 stderr
    # 与 agent 的上下文。

    def test_never_echoes_a_credential_the_server_puts_in_the_code(self):
        """服务端把凭据塞进 code → stderr 里不许出现那个凭据。

        密码这里特意取成「形状合法」的那一种（首字母 + 字母数字）：
        这样能过的只有凭据检查那一关 —— 形状检查单独是拦不住它的。
        """
        password = "sekretpw9f3a"
        self.server.state.manifest_problem = (500, password, "boom")

        code, out, err = self.run_fetch([], env={"IDEA_PASS": password})

        self.assertEqual(code, 1)
        self.assertNotIn(password, err)
        self.assertNotIn(password, out)
        # 换成一个可解析的码（agent 还能按 code 分支）：逐字断言，别只看前缀
        self.assertEqual(err, "http_500: boom\n")
        self.assertEqual(out, "")

    def test_keeps_a_valid_server_code_intact(self):
        """服务端给一个合法的 code → 原样保留（agent 要按它分支）。"""
        self.server.state.manifest_problem = (500, "skill_file_not_found", "boom")

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertEqual(err, "skill_file_not_found: boom\n")
        self.assertEqual(out, "")

    def test_checks_a_server_code_even_when_it_looks_like_one_of_our_own(self):
        """服务端回一个叫 network_error 的码 → 照样要过凭据检查。

        「取件器自己的码」与「服务端给的码」的区别在**出处**，不在长相：
        服务端完全可以回一个叫 ``network_error`` 的码 —— 那种码是服务端给的
        字符串，照样不许含凭据值。
        """
        self.server.state.manifest_problem = (500, "network_error", "boom")

        code, out, err = self.run_fetch([], env={"IDEA_PASS": "network_error"})

        self.assertEqual(code, 1)
        self.assertEqual(err, "http_500: boom\n")
        self.assertNotIn("network_error", err)
        self.assertEqual(out, "")

    def test_masks_a_server_code_that_contains_the_password(self):
        """服务端给的 code 里含着密码（形状合法也不行）→ 换成 http_<状态码>。"""
        self.server.state.manifest_problem = (500, "server_error", "boom")

        code, out, err = self.run_fetch([], env={"IDEA_PASS": "error"})

        self.assertEqual(code, 1)
        self.assertTrue(err.startswith("http_500: "), err)
        self.assertNotIn("server_error", err)

    def test_replaces_a_server_code_with_a_trailing_newline(self):
        """服务端给的 code 末尾带一个换行 → 换掉（stderr 恰好一行是硬约定）。

        这一条专门盯「结尾换行」：旧做法是用 ``^...$`` 锚配 ``match()``，
        而 Python 的 ``$`` 匹配「串尾**或**紧邻结尾换行之前」——
        所以 ``"boom\\n"`` 能蒙混过关，stderr 变成两行
        （第一行没有冒号，agent 解析不出来）。

        （换行在中间的 "boom\\nsecond line" 反而是安全的：形状本来就不匹配。
        旧用例测的是那种，测不到真正的洞。）
        """
        self.server.state.manifest_problem = (500, "boom\n", "boom")

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))
        self.assertTrue(err.startswith("http_500: "), err)
        # 逐字断言：code 段仍可解析（agent 按它分支），detail 没被吞掉
        self.assertEqual(err, "http_500: boom\n")
        self.assertEqual(out, "")

    def test_does_not_echo_the_basic_auth_base64_without_padding(self):
        """服务端回 base64 凭据的「去 padding」形态 → stderr 不许出现它。

        形状校验禁 ``=``，所以**带 padding** 的形态被挡住了；
        **去 padding** 的（服务端/代理回显时常用的 base64url 写法）却两关全过。

        base64 可逆 —— 掩了等于没掩，所以两种形态都得堵。
        """
        encoded = base64.b64encode(b"agent:pw-1234").decode("ascii").rstrip("=")
        self.server.state.manifest_problem = (500, encoded, "boom")

        code, out, err = self.run_fetch(
            [], env={"IDEA_USER": "agent", "IDEA_PASS": "pw-1234"}
        )

        self.assertEqual(code, 1)
        self.assertNotIn(encoded, err)
        self.assertNotIn(encoded, out)
        self.assertEqual(err, "http_500: boom\n")
        self.assertEqual(out, "")

    def test_does_not_echo_a_short_basic_auth_base64_without_padding(self):
        """**短**的 basic auth base64（去 padding 的形态）也要遮。

        旧做法有个长度门槛（``MIN_MASKABLE_BASE64 = 8``）：短 base64 可能出现在
        正常文本里，遮了会毁可读性 —— 于是短的「去 padding」形态被放过了。

        但这三个形态（原文 / 带 padding / 去 padding）都是**派生自同一个真实
        凭据**的，不是猜出来的形态 —— 遮它们不会误伤正常文本，
        放过它们就是真的漏（base64 可逆）。
        """
        user = "u"
        password = "zzz"
        encoded = base64.b64encode("{}:{}".format(user, password).encode("utf-8"))
        unpadded = encoded.decode("ascii").rstrip("=")

        # 这条用例的前提：短到会被旧的门槛放过（否则测的还是上面那条），
        # 而且**不含密码原文**（否则旧代码靠密码本身也能蒙对，这条就白测了）。
        self.assertLess(len(unpadded), 8)
        self.assertNotIn(password, unpadded)

        self.server.state.manifest_problem = (500, unpadded, "boom")

        code, out, err = self.run_fetch(
            [], env={"IDEA_USER": user, "IDEA_PASS": password}
        )

        self.assertEqual(code, 1)
        self.assertNotIn(unpadded, err)
        self.assertNotIn(unpadded, out)
        # 含凭据的 code 不许原样输出（换成可解析的 http_<状态码>）
        self.assertEqual(err, "http_500: boom\n")
        self.assertEqual(out, "")

    def test_flattens_control_characters_in_the_error_detail(self):
        """detail 里的回车等控制字符不许留下（stderr 恰好一行是硬约定）。

        换行与回车之外的控制字符（ESC、NUL……）不会让输出多出一行，
        但会让终端乱掉、也可能被下游当成结构 —— 一并换成空格。
        """
        self.server.state.manifest_problem = (500, "boom", "第一行\r第二行\x1b[0m")

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))
        self.assertNotIn("\r", err)
        self.assertNotIn("\x1b", err)

    def test_rejects_a_manifest_with_a_trailing_newline_in_the_version(self):
        """清单的 version 末尾带换行 → 清单不合法，stderr 恰好一行。

        version 会变成缓存目录名：带换行的目录名既难看，
        降级提示行也会变成两行（提示行走的是 ``_write_stderr``，没有压行）。
        """
        self.server.state.version_override = "v1\n"

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1)
        self.assertIn("skill_integrity_error", err)
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))
        self.assertEqual(out, "")

        # 缓存目录名里不许出现换行（目录名是拿 version 拼的）
        names = os.listdir(self.cache) if os.path.isdir(self.cache) else []
        for name in names:
            self.assertNotIn("\n", name)
            self.assertNotIn("\r", name)
        self.assertEqual([name for name in names if name.startswith(".tmp-")], [])

    # -- 清单校验的防御分支 -------------------------------------------------
    #
    # 清单是**服务端给的输入**：版本串会变成缓存目录名，path 会变成缓存里的
    # 文件名，sha256 是「内容对不对」的唯一依据。
    #
    # 这些分支早先几乎一个用例都没有 —— 而「守卫没覆盖到」与「代码没写错」
    # 在测试里长得一模一样：那个 1012 字符的版本串 bug 就是这么全绿漏过去的
    # （两边各自按自己的理解造数据，从没对过面）。
    #
    # 每一条都断言三件事：退出码 1、stderr 是 ``skill_integrity_error: …``、
    # **缓存目录里没有落下任何文件**（校验不过就不许写盘）。

    def assert_rejected_manifest(self, manifest):
        """喂一份坏清单给服务端，断言取件器把它挡在缓存之外。"""
        self.server.state.manifest_override = manifest

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 1, err)
        self.assertTrue(err.startswith("skill_integrity_error: "), err)
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))
        self.assertEqual(out, "")
        self.assertEqual(
            os.listdir(self.cache), [], "清单不合法时，缓存目录里不该落下任何文件"
        )

    def test_rejects_a_manifest_whose_path_escapes_the_cache_directory(self):
        """清单里的 path 含 ``..`` → 清单不合法（那个 path 会变成缓存里的文件名）。"""
        manifest = valid_manifest()
        manifest["files"].append({"path": "../evil.md", "sha256": "0" * 64, "size": 1})

        self.assert_rejected_manifest(manifest)

    def test_rejects_a_manifest_whose_path_is_absolute(self):
        """清单里的 path 是绝对路径 → 清单不合法（会写到缓存目录之外去）。"""
        manifest = valid_manifest()
        manifest["files"].append({"path": "/etc/passwd", "sha256": "0" * 64, "size": 1})

        self.assert_rejected_manifest(manifest)

    def test_rejects_a_manifest_whose_digest_has_the_wrong_shape(self):
        """清单里的 sha256 形状不对 → 清单不合法（形状错了却放行 = 不校验）。"""
        manifest = valid_manifest()
        manifest["files"][0]["sha256"] = "not-a-sha256"

        self.assert_rejected_manifest(manifest)

    def test_rejects_a_manifest_whose_entry_is_not_in_files(self):
        """清单里的 entry 不在 files 里 → 清单不合法（入口页不在副本内）。"""
        manifest = valid_manifest()
        manifest["entry"] = "index.md"

        self.assert_rejected_manifest(manifest)

    def test_rejects_a_manifest_whose_files_is_not_a_list(self):
        """清单里的 files 不是数组 → 清单不合法。"""
        manifest = valid_manifest()
        manifest["files"] = {"SKILL.md": {}}

        self.assert_rejected_manifest(manifest)

    def test_rejects_a_manifest_with_no_files_at_all(self):
        """清单里的 files 是空数组 → 清单不合法（没有可分发的东西）。"""
        manifest = valid_manifest()
        manifest["files"] = []

        self.assert_rejected_manifest(manifest)

    def test_rejects_a_manifest_whose_file_entry_is_not_an_object(self):
        """清单里的 files 项不是对象 → 清单不合法。"""
        manifest = valid_manifest()
        manifest["files"] = [ENTRY]

        self.assert_rejected_manifest(manifest)

    def test_does_not_mask_the_username_in_the_error_detail(self):
        """用户名不是秘密：detail 里出现它时不该被糊成 ***（否则错误没法看）。

        密码该遮还是遮 —— 掩码表里有密码、token 与它们的 base64，没有用户名。
        """
        self.server.state.manifest_problem = (500, "boom", "用户 agent 的密码 pw-1234 不对")

        code, out, err = self.run_fetch(
            [], env={"IDEA_USER": "agent", "IDEA_PASS": "pw-1234"}
        )

        self.assertEqual(code, 1)
        self.assertIn("agent", err)
        self.assertNotIn("pw-1234", err)

    # -- 退出路径上的清理 ---------------------------------------------------

    def test_cleans_up_temporary_directories_after_a_usage_error(self):
        """用法错误退出后，残留的临时目录也要清掉。

        argparse 的用法错误是 SystemExit(2) —— except Exception 抓不到它。
        但 finally 照样会跑，所以这条路径也得清残留。
        """
        leftover = os.path.join(self.cache, ".tmp-deadbeef-{}".format(dead_pid()))
        os.makedirs(leftover)

        code, out, err = self.run_fetch(["a", "b"])

        self.assertEqual(code, 2)
        self.assertFalse(os.path.exists(leftover), "用法错误后残留的临时目录该被清掉")
        self.assertEqual(
            [name for name in os.listdir(self.cache) if name.startswith(".tmp-")], []
        )

    def test_cleans_up_temporary_directories_after_help(self):
        """--help 退出后，残留的临时目录也要清掉（SystemExit(0)）。"""
        leftover = os.path.join(self.cache, ".tmp-deadbeef-{}".format(dead_pid()))
        os.makedirs(leftover)

        code, out, err = self.run_fetch(["--help"])

        self.assertEqual(code, 0)
        self.assertFalse(os.path.exists(leftover), "--help 之后残留的临时目录该被清掉")
        self.assertEqual(
            [name for name in os.listdir(self.cache) if name.startswith(".tmp-")], []
        )

    # -- 认证头与 --help ----------------------------------------------------

    def test_sends_basic_auth_and_api_key_headers(self):
        """IDEA_USER/IDEA_PASS → Basic 头；IDEA_TOKEN → X-Api-Key 头。"""
        code, out, err = self.run_fetch(
            [],
            env={"IDEA_USER": "agent", "IDEA_PASS": "pw-1234", "IDEA_TOKEN": "tok-5678"},
        )

        self.assertEqual(code, 0, err)

        headers = dict((name.lower(), value) for name, value in self.server.state.headers[-1].items())
        expected = base64.b64encode(b"agent:pw-1234").decode("ascii")
        self.assertEqual(headers.get("authorization"), "Basic " + expected)
        self.assertEqual(headers.get("x-api-key"), "tok-5678")

    def test_help_does_not_mention_credentials(self):
        """--help 里不许出现密码相关的词（命令行不接受密码）。"""
        code, out, err = self.run_fetch(["--help"])

        self.assertEqual(code, 0)
        lowered = (out + err).lower()
        for word in ("pass", "password", "token", "secret"):
            self.assertNotIn(word, lowered, "--help 里不该出现 {}".format(word))

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
        """取不到清单但本地有缓存 → 用缓存，stderr 给一行提示（不含凭据）。

        退出码是**专属的 3**：只看退出码的调用方必须能看出「这不是当前版本」——
        上一版手册会让 agent 按旧语义操作，那正是这套改造要消除的东西。
        """
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        password = "PASS-SECRET-9f3a2b"
        dead = "http://127.0.0.1:{}".format(free_port())
        code, out, err = self.run_fetch(
            [], base_url=dead, env={"IDEA_USER": "agent", "IDEA_PASS": password}
        )

        self.assertEqual(code, 3, err)
        self.assertEqual(out, ENTRY_BODY)
        self.assertEqual(len(err.strip().splitlines()), 1, "提示只该有一行：{}".format(err))
        self.assertTrue(err.startswith("degraded_cache: "), err)
        # 降级的那行提示也不能带凭据
        self.assertNotIn(password, err)
        self.assertNotIn(password, out)

    def test_keeps_the_error_code_intact_in_the_degraded_warning(self):
        """降级提示行里的错误码也不能被改写（提示行里的码 agent 也要认）。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        dead = "http://127.0.0.1:{}".format(free_port())
        code, out, err = self.run_fetch([], base_url=dead, env={"IDEA_PASS": "error"})

        self.assertEqual(code, 3, err)
        self.assertIn("（network_error）", err)
        self.assertNotIn("network_***", err)

    def test_does_not_mark_the_stdout_when_degraded(self):
        """降级时 stdout **只有手册正文**：加标记行会污染内容。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        dead = "http://127.0.0.1:{}".format(free_port())
        code, out, err = self.run_fetch([], base_url=dead)

        self.assertEqual(code, 3, err)
        self.assertEqual(out, ENTRY_BODY)
        self.assertNotIn("degraded_cache", out)

    def test_reports_one_line_when_the_degraded_cache_lacks_the_file(self):
        """降级 + 缓存里没有那个文件 → stderr 只该有一行（错误），不再另打提示。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        dead = "http://127.0.0.1:{}".format(free_port())
        code, out, err = self.run_fetch(["references/nope.md"], base_url=dead)

        self.assertEqual(code, 1)
        self.assertIn("not_found", err)
        self.assertEqual(len(err.strip().splitlines()), 1, "错误只该有一行：{}".format(err))
        self.assertNotIn("degraded_cache", err)
        self.assertEqual(out, "")

    def test_falls_back_to_the_old_cache_when_a_download_is_cut_off(self):
        """同步到一半传输断了 → 用旧版本（旧版本留着就是为了这一刻）。"""
        code, out, err = self.run_fetch([])
        self.assertEqual(code, 0, err)

        # 服务端换版本，但取文件时传输中断
        self.server.state.files[ENTRY] = "# 新内容\n"
        self.server.state.version = compute_version(self.server.state.files)
        self.server.state.truncate_files = True

        code, out, err = self.run_fetch([])

        self.assertEqual(code, 3, err)
        self.assertEqual(out, ENTRY_BODY)
        self.assertEqual(len(err.strip().splitlines()), 1, "提示只该有一行：{}".format(err))

        # 同步没成功：新版本目录没建立，临时目录也清掉了
        self.assertFalse(os.path.exists(os.path.join(self.cache, self.server.state.version)))
        self.assertEqual([name for name in os.listdir(self.cache) if name.startswith(".tmp-")], [])

    # -- 管道下游提前关闭 ---------------------------------------------------

    def test_exits_quietly_when_the_pipe_is_closed_early(self):
        """``fetch.py | head``：管道下游先走 → 静默退出，不打那一行错误。

        agent 把取件器的输出管给 head/less 是很自然的动作；
        看到 ``internal_error: [Errno 32] Broken pipe`` 会以为取件失败了。
        """
        # 内容要比管道的缓冲大，写入才会真的撞上 EPIPE。
        big = "x" * (1024 * 1024)
        self.server.state.files[ENTRY] = big
        self.server.state.version = compute_version(self.server.state.files)

        environ = dict(os.environ)
        for name in ("IDEA_BASE_URL", "IDEA_USER", "IDEA_PASS", "IDEA_TOKEN"):
            environ.pop(name, None)
        environ["IDEA_BASE_URL"] = self.base_url
        environ["IDEA_SKILL_CACHE_DIR"] = self.cache

        proc = subprocess.Popen(
            [sys.executable, SCRIPT],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environ,
        )

        # 读一点就把管道关掉：写的那一端随后会撞上 EPIPE。
        proc.stdout.read(1024)
        proc.stdout.close()

        _, err = proc.communicate(timeout=60)

        self.assertEqual(proc.returncode, 0, err.decode("utf-8", "replace"))
        self.assertNotIn("Broken pipe", err.decode("utf-8", "replace"))
        self.assertNotIn("internal_error", err.decode("utf-8", "replace"))
        self.assertEqual(err, b"")

    # -- 重定向 -------------------------------------------------------------

    def test_does_not_follow_a_redirect_to_another_origin(self):
        """302 到别的 origin → 不跟随（凭据不跟着出本机），报 http_302。"""
        other = _RecordingServer()
        other.start()

        try:
            self.server.state.redirect_to = "{}/skill/idea-manager/version".format(other.url)

            code, out, err = self.run_fetch(
                [], env={"IDEA_USER": "agent", "IDEA_PASS": "pw-1234"}
            )

            self.assertEqual(code, 1, err)
            self.assertIn("http_302", err)
            self.assertEqual(out, "")

            # 另一个 origin 一个请求都没收到 —— 凭据没跟着过去。
            self.assertEqual(other.state.requested, [], "跨 origin 的重定向不该被跟随")
        finally:
            other.stop()


class _RecordingServer(object):
    """只记账的 HTTP 服务端：记下收到过哪些请求、哪些头。

    用来验证「凭据有没有跟着重定向跑到别处去」。
    """

    def __init__(self):
        self.state = _State()
        self._server = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
        self._server.state = self.state
        self._thread = None

    @property
    def url(self):
        return "http://127.0.0.1:{}".format(self._server.server_address[1])

    def start(self):
        self._thread = threading.Thread(
            target=self._server.serve_forever, kwargs={"poll_interval": 0.05}
        )
        self._thread.daemon = True
        self._thread.start()

    def stop(self):
        self._server.shutdown()
        self._server.server_close()


def fetcher_module():
    """把取件器当模块导入（只为读它自己的码表，不跑它）。"""
    spec = importlib.util.spec_from_file_location("idea_fetch_module", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    return module


def emittable_codes():
    """取件器真的可能打在 stderr 上的码。

    = 它自己的码表（``OWN_ERROR_CODES``）+ 它的兜底码（``internal_error``）。
    """
    codes = set(fetcher_module().OWN_ERROR_CODES)
    codes.add(FALLBACK_CODE)

    return codes


def read_lines(path):
    """读一个文本文件，按行拆开（行尾统一成 \\n）。"""
    with open(path, encoding="utf-8") as handle:
        return handle.read().replace("\r\n", "\n").split("\n")


def section_body(lines, heading):
    """某一节的正文：从标题的下一行到下一个二级标题之前。"""
    start = lines.index(heading)
    body = []

    for line in lines[start + 1:]:
        if line.startswith("## "):
            break
        body.append(line)

    return body


def manual_fetcher_section():
    """手册「## 取件器的错误码」那一节的全文（表格 + 正文）。"""
    return "\n".join(section_body(read_lines(MANUAL_ERRORS), "## 取件器的错误码"))


def manual_table_codes():
    """手册「## 取件器的错误码」那张表的 code 列。"""
    codes = []
    in_table = False
    first_row = True

    for line in section_body(read_lines(MANUAL_ERRORS), "## 取件器的错误码"):
        stripped = line.strip()

        if not stripped.startswith("|"):
            # 表到这儿就结束了（后面是散文）。
            if in_table:
                break
            continue

        in_table = True
        cells = stripped.split("|")

        if len(cells) < 2:
            continue

        cell = cells[1].strip()

        # 表头行与分隔行（`|---|---|`）不是数据行。
        if first_row:
            first_row = False
            continue

        if not cell or set(cell) <= set("-: "):
            continue

        codes.append(cell.strip("`"))

    return codes


def shell_codes():
    """薄壳「取件失败怎么查」那一节里提到的码（反引号里的小写词）。"""
    body = "\n".join(section_body(read_lines(SHELL_SKILL), "## 取件失败怎么查"))

    # 只认「小写开头的标识符」：`IDEA_BASE_URL`（大写）、`skill/`（带斜杠）、
    # `warning:`（带冒号）都不是码。
    return re.findall(r"`([a-z][a-z0-9_]*)`", body)


class ErrorCodeDocGuardTests(unittest.TestCase):
    """手册与薄壳里说的取件器错误码，取件器真的产得出吗？

    取件器的错误码有**三份**定义，而它们从没对过面：

    * 取件器代码里的 ``OWN_ERROR_CODES``（真正的权威）；
    * 手册 ``references/errors.md`` 的「## 取件器的错误码」那张表；
    * 薄壳 ``SKILL.md`` 的「取件失败怎么查」那份精简清单。

    任何一份改了而另两份没跟上，agent 拿到的就是「手册里查不到的码」，
    或者「手册说有、其实永远不出现的码」—— 而两侧的测试各自绿，
    这种漂移不会自己暴露（与「版本串的算法」是同一类病：
    两边各自定义，从没对过面）。
    """

    def test_manual_table_only_lists_codes_the_fetcher_can_emit(self):
        """手册那张表里的码，取件器真的产得出。"""
        emittable = emittable_codes()

        for code in manual_table_codes():
            if code == HTTP_STATUS_ROW:
                continue

            self.assertIn(
                code,
                emittable,
                "手册「取件器的错误码」表里的「{}」，取件器产不出来 —— "
                "agent 按它分支的那条路永远不会发生。".format(code),
            )

    def test_every_code_the_fetcher_can_emit_is_documented(self):
        """取件器产得出的码，手册里都写了（表格或正文）。"""
        section = manual_fetcher_section()

        for code in sorted(emittable_codes()):
            self.assertIn(
                code,
                section,
                "取件器会打出「{}」，手册的「取件器的错误码」一节里却没有它 —— "
                "agent 拿到这个码时无处可查。".format(code),
            )

    def test_shell_hints_only_list_codes_the_fetcher_can_emit(self):
        """薄壳的排查清单里说的码，取件器真的产得出。"""
        emittable = emittable_codes()

        for code in shell_codes():
            self.assertIn(
                code,
                emittable,
                "薄壳提到「{}」，但取件器产不出这个码 —— "
                "那份排查清单是照着另一份码表抄的。".format(code),
            )

    def test_the_http_status_code_form_is_produced_by_the_fetcher(self):
        """手册里「http_<状态码>」那一行，取件器真的产得出这种形态的码。"""
        module = fetcher_module()

        self.assertRegex("http_404", module.HTTP_CODE_PATTERN.pattern)


if __name__ == "__main__":
    unittest.main()
