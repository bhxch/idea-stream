"""idea.py 的端到端测试。

两类测试：

* :class:`IdeaCliTest` —— 打真实后端（``IDEA_BASE_URL`` 指向的实例）。
  实例不可达时整个类 skip（不报错），这样在没起后端的环境里也能跑。
* :class:`LocalServerTest` —— 用一个本地的一次性 HTTP 服务打桩，
  测那些跟后端无关的行为（脱敏、响应不是 JSON、缺配置）。

CLI 一律以子进程调用：端到端测的是「agent 实际会用的那条命令」，
包括 stdout/stderr 的分工与退出码。
"""

import importlib.util
import json
import os
import subprocess
import sys
import threading
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, os.pardir, "scripts", "idea.py")
SCRIPT = os.path.normpath(SCRIPT)

BASE_URL = os.environ.get("IDEA_BASE_URL", "").strip()


def _load_idea_module():
    """按路径加载 idea.py（测它的内部函数时用）。"""
    spec = importlib.util.spec_from_file_location("idea_under_test", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_cli(args, env=None, base_url=None):
    """跑一次 CLI，返回 (退出码, stdout, stderr)。"""
    environ = dict(os.environ)
    if base_url is not None:
        environ["IDEA_BASE_URL"] = base_url
    else:
        environ.pop("IDEA_BASE_URL", None)
    if env:
        environ.update(env)
    proc = subprocess.run(
        [sys.executable, SCRIPT] + args,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=environ,
        universal_newlines=True,
    )
    return proc.returncode, proc.stdout, proc.stderr


def run_json(args, env=None, base_url=None):
    """跑一次 CLI 并解析 stdout 的 JSON。"""
    code, out, err = run_cli(args, env=env, base_url=base_url)
    if code != 0:
        raise AssertionError(
            "命令失败：{}\n退出码：{}\nstdout：{}\nstderr：{}".format(args, code, out, err)
        )
    return json.loads(out)


class IdeaCliTest(unittest.TestCase):
    """打真实后端的端到端测试。"""

    @classmethod
    def setUpClass(cls):
        if not BASE_URL:
            raise unittest.SkipTest("没设 IDEA_BASE_URL，跳过端到端测试")
        try:
            with urllib.request.urlopen(BASE_URL.rstrip("/") + "/api/health", timeout=5) as resp:
                if resp.getcode() != 200:
                    raise unittest.SkipTest("后端健康检查返回 {}".format(resp.getcode()))
        except urllib.error.URLError as exc:
            raise unittest.SkipTest("后端不可达（{}），跳过端到端测试".format(exc))

    def test_health_via_list(self):
        payload = run_json(["list"], base_url=BASE_URL)
        self.assertIsInstance(payload, list)
        # 先锚一下非空：逐项断言只在列表非空时才生效，
        # 空列表会让这个用例「什么都没检查」地通过。
        self.assertTrue(payload, "列表不该是空的（库里至少要有内置收件箱）")
        for item in payload:
            self.assertIn("id", item)
            self.assertIn("content", item)

    def test_add_and_get_back(self):
        content = "端到端测试：加一条想法"
        created = run_json(["add", content], base_url=BASE_URL)
        self.assertEqual(created["content"], content)

        listed = run_json(["list"], base_url=BASE_URL)
        ids = [item["id"] for item in listed]
        self.assertIn(created["id"], ids)

        fetched = [item for item in listed if item["id"] == created["id"]][0]
        self.assertEqual(fetched["content"], content)

    def test_special_characters_roundtrip(self):
        content = '引号 " 反斜杠 \\ 换行\n<b>标签</b>'
        created = run_json(["add", content], base_url=BASE_URL)
        self.assertEqual(created["content"], content)

        listed = run_json(["list"], base_url=BASE_URL)
        fetched = [item for item in listed if item["id"] == created["id"]][0]
        self.assertEqual(fetched["content"], content)

    def test_done_marks_completed(self):
        created = run_json(["add", "端到端测试：标记完成"], base_url=BASE_URL)
        done = run_json(["done", created["id"]], base_url=BASE_URL)
        self.assertTrue(done["done"])

        listed = run_json(["list", "--status", "done"], base_url=BASE_URL)
        self.assertIn(created["id"], [item["id"] for item in listed])

    def test_tags_filter(self):
        tag = "e2e-标签-" + os.urandom(3).hex()
        created = run_json(["add", "端到端测试：打标签", "--tags", tag], base_url=BASE_URL)
        self.assertEqual(created["tags"], [tag])

        listed = run_json(["list", "--tags", tag], base_url=BASE_URL)
        self.assertIn(created["id"], [item["id"] for item in listed])

        tagged = run_json(["tags", created["id"], "另一个标签"], base_url=BASE_URL)
        self.assertEqual(tagged["tags"], ["另一个标签"])

    def test_merge_creates_new_idea(self):
        first = run_json(["add", "A"], base_url=BASE_URL)
        second = run_json(["add", "B"], base_url=BASE_URL)

        merged = run_json(["merge", first["id"], second["id"]], base_url=BASE_URL)
        self.assertEqual(merged["content"], "A\n\nB")
        self.assertNotEqual(merged["id"], first["id"])
        self.assertNotEqual(merged["id"], second["id"])

        listed = run_json(["list"], base_url=BASE_URL)
        ids = [item["id"] for item in listed]
        self.assertNotIn(first["id"], ids)
        self.assertNotIn(second["id"], ids)
        self.assertIn(merged["id"], ids)

    def test_merge_cross_project_error_code(self):
        project = run_json(["project", "add", "端到端测试项目"], base_url=BASE_URL)
        try:
            first = run_json(["add", "项目一里的想法", "--project", project["id"]], base_url=BASE_URL)
            second = run_json(["add", "收件箱里的想法"], base_url=BASE_URL)

            code, out, err = run_cli(["merge", first["id"], second["id"]], base_url=BASE_URL)
            self.assertEqual(code, 1)
            self.assertIn("merge_cross_project", err)
            self.assertEqual(out, "")
        finally:
            run_cli(["project", "delete", project["id"]], base_url=BASE_URL)

    def test_delete_then_restore(self):
        created = run_json(["add", "端到端测试：删除再恢复"], base_url=BASE_URL)
        deleted = run_json(["delete", created["id"]], base_url=BASE_URL)
        self.assertIsNone(deleted)

        trash = run_json(["trash"], base_url=BASE_URL)
        in_trash = [entry for entry in trash if entry.get("idea") and entry["idea"]["id"] == created["id"]]
        self.assertTrue(in_trash, "删掉的想法应该出现在回收站里")

        restored = run_json(["restore", created["id"]], base_url=BASE_URL)
        self.assertEqual(restored["id"], created["id"])

        trash = run_json(["trash"], base_url=BASE_URL)
        in_trash = [entry for entry in trash if entry.get("idea") and entry["idea"]["id"] == created["id"]]
        self.assertFalse(in_trash, "恢复之后不该还在回收站里")

    def test_telegram_template_contains_thread_id(self):
        payload = run_json(["telegram-template"], base_url=BASE_URL)
        self.assertIn("message_thread_id", payload["bodyTemplate"])
        self.assertIn("{{idea.content}}", payload["bodyTemplate"])
        # 模板里的 bot token 是占位符，不是秘密：占位符要原样保留。
        self.assertIn("<BOT_TOKEN>", payload["url"])

    def test_error_prints_code_and_detail(self):
        code, out, err = run_cli(["done", "不存在的想法id"], base_url=BASE_URL)
        self.assertEqual(code, 1)
        self.assertIn("idea_not_found", err)
        self.assertIn("不存在的想法id", err)
        self.assertEqual(out, "")

    def test_webhook_update_only_changes_given_fields(self):
        # 后端把 null 当「不改」：CLI 只发用户真正给了的字段，
        # 别的字段（这里的 url、events、enabled）必须原样不动。
        created = run_json(
            [
                "webhook",
                "add",
                "--name",
                "端到端测试 webhook",
                "--url",
                "https://example.com/hook",
                "--body",
                '{"text":"{{idea.content}}"}',
                "--events",
                "idea.created",
            ],
            base_url=BASE_URL,
        )
        try:
            updated = run_json(
                ["webhook", "update", created["id"], "--name", "改过的名字"], base_url=BASE_URL
            )
            self.assertEqual(updated["name"], "改过的名字")
            self.assertEqual(updated["url"], created["url"])
            self.assertEqual(updated["events"], created["events"])
            self.assertEqual(updated["enabled"], created["enabled"])
        finally:
            run_cli(["webhook", "delete", created["id"]], base_url=BASE_URL)


class LocalServerTest(unittest.TestCase):
    """用本地打桩 HTTP 服务测与后端无关的行为。"""

    def _serve(self, handler_factory):
        server = HTTPServer(("127.0.0.1", 0), handler_factory)
        thread = threading.Thread(target=server.serve_forever)
        thread.daemon = True
        thread.start()
        self.addCleanup(server.shutdown)
        self.addCleanup(server.server_close)
        return "http://127.0.0.1:{}".format(server.server_address[1])

    def _static_handler(self, status, content_type, body):
        """打一个「固定状态码 + 固定响应体」的桩。

        ``content_type`` 传 None 表示**不发 Content-Type 头** ——
        Kestrel 直接拒收超大请求体时回的就是这种 413：
        没有 Content-Type、Content-Length: 0、响应体为空。
        """

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(status)
                if content_type is not None:
                    self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            # POST 也回同一个固定响应：413 本来就是「请求体太大」时服务端回的，
            # 那种场景 CLI 发的是 POST。
            do_POST = do_GET

            def log_message(self, *args):
                pass

        return Handler

    def test_missing_base_url_exits_with_hint(self):
        code, out, err = run_cli(["list"], base_url="")
        self.assertEqual(code, 1)
        self.assertIn("IDEA_BASE_URL", err)
        self.assertEqual(out, "")

    def test_non_json_response_reports_status(self):
        body = "<html><body>proxy error</body></html>".encode("utf-8")
        url = self._serve(self._static_handler(502, "text/html", body))

        code, out, err = run_cli(["list"], base_url=url)
        self.assertEqual(code, 1)
        self.assertIn("响应不是 JSON", err)
        self.assertIn("502", err)
        self.assertEqual(out, "")

    def test_non_json_2xx_response_reports_status(self):
        body = b"<!doctype html><html><body>index</body></html>"
        url = self._serve(self._static_handler(200, "text/html", body))

        code, out, err = run_cli(["list"], base_url=url)
        self.assertEqual(code, 1)
        self.assertIn("响应不是 JSON", err)
        self.assertIn("200", err)

    def test_empty_body_413_reports_http_413_code(self):
        """钉住「非 2xx + **空响应体**」这一条 CLI 行为。

        SKILL.md 的「CLI 自己的错误码」清单里写着 ``http_<状态码>``，
        413 的说明里也写着「响应体是空的 → ``http_413: HTTP 413``」——
        但这条此前**没有用例钉着**：文档说一套、代码做另一套也发现不了。

        场景：Kestrel 在读 body 阶段就拒收超大请求体，回的是
        413 + **空的响应体**（没有 Content-Type、没有 ProblemDetails、没有 code），
        见 ``server/IdeaApp.Tests/PayloadTooLargeTests.cs`` 的实测锚点。
        CLI 拿不到业务错误码，只能按状态码自己造一个 ``http_413``。

        关键：**不能**报成 ``invalid_response`` —— 那是「响应体不是 JSON」
        的另一条分支（body 非空但解析不了），两条分支互斥。
        """
        # 没有 Content-Type 头（Kestrel 的 413 就是这样），响应体为空。
        url = self._serve(self._static_handler(413, None, b""))

        # 用「POST + 请求体」的命令：413 本来就是「请求体太大」时服务端回的。
        code, out, err = run_cli(["add", "一条会撑爆上限的想法"], base_url=url)
        self.assertEqual(code, 1)
        self.assertIn("http_413: HTTP 413", err)
        self.assertNotIn("invalid_response", err)
        self.assertNotIn("响应不是 JSON", err)
        self.assertEqual(out, "")

    def test_empty_body_502_reports_http_502_code(self):
        """``http_<状态码>`` 是通用形态，不是只对 413 成立。

        上一条钉的是 413；这一条换个状态码，确认 CLI 是**按状态码机械地造码**
        （502 → ``http_502: HTTP 502``），而不是把 413 写死在一个分支里。
        """
        url = self._serve(self._static_handler(502, None, b""))

        code, out, err = run_cli(["list"], base_url=url)
        self.assertEqual(code, 1)
        self.assertIn("http_502: HTTP 502", err)
        self.assertNotIn("invalid_response", err)
        self.assertNotIn("响应不是 JSON", err)
        self.assertEqual(out, "")

    def test_webhook_url_and_headers_are_redacted(self):
        webhook = {
            "id": "wh-1",
            "name": "telegram",
            # 假 token：刻意不用「bot + 一串数字」的形状 ——
            # 仓库里有一条 grep（bot[0-9]{6,}:）用来确认没有真实 bot token 混进来，
            # 假 token 也不该让它响。
            "url": "https://api.telegram.org/botXXXXXX:FAKE-TOKEN/sendMessage",
            "method": "POST",
            "headers": {"Authorization": "Bearer SUPER-SECRET-HEADER"},
            "bodyTemplate": "{}",
            "events": ["idea.created"],
            "scope": "global",
            "projectId": None,
            "enabled": False,
            "hasSecret": True,
            "createdAt": "2026-01-01T00:00:00Z",
            "updatedAt": "2026-01-01T00:00:00Z",
        }
        body = json.dumps([webhook]).encode("utf-8")
        url = self._serve(self._static_handler(200, "application/json", body))

        code, out, err = run_cli(["webhook", "list"], base_url=url)
        self.assertEqual(code, 0, err)

        # JSON 输出：token 与 header 的值都不能出现。
        self.assertNotIn("XXXXXX", out)
        self.assertNotIn("FAKE-TOKEN", out)
        self.assertNotIn("SUPER-SECRET-HEADER", out)
        self.assertIn("api.telegram.org", out)
        self.assertIn("***", out)

        payload = json.loads(out)
        self.assertEqual(payload[0]["url"], "https://api.telegram.org/bot***/sendMessage")
        self.assertEqual(payload[0]["headers"], {"Authorization": "***"})

        # text 输出同样要脱敏。
        code, out, err = run_cli(["--format", "text", "webhook", "list"], base_url=url)
        self.assertEqual(code, 0, err)
        self.assertNotIn("FAKE-TOKEN", out)
        self.assertNotIn("SUPER-SECRET-HEADER", out)
        self.assertIn("***", out)

    def test_bot_token_placeholder_is_kept(self):
        webhook = dict(
            {
                "id": "wh-2",
                "name": "telegram",
                "url": "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage",
                "method": "POST",
                "headers": {},
                "bodyTemplate": "{}",
                "events": ["idea.created"],
                "scope": "global",
                "projectId": None,
                "enabled": False,
                "hasSecret": False,
                "createdAt": "2026-01-01T00:00:00Z",
                "updatedAt": "2026-01-01T00:00:00Z",
            }
        )
        body = json.dumps([webhook]).encode("utf-8")
        url = self._serve(self._static_handler(200, "application/json", body))

        code, out, err = run_cli(["webhook", "list"], base_url=url)
        self.assertEqual(code, 0, err)
        payload = json.loads(out)
        self.assertEqual(payload[0]["url"], "https://api.telegram.org/bot<BOT_TOKEN>/sendMessage")

    def test_webhook_add_without_body_is_rejected_locally(self):
        # Body 模板不能为空（后端会拒）：本地就说清楚，别白等一次请求。
        url = self._serve(self._static_handler(200, "application/json", b"[]"))

        code, out, err = run_cli(
            ["webhook", "add", "--name", "x", "--url", "https://example.com/hook"], base_url=url
        )
        self.assertEqual(code, 1)
        self.assertIn("--body", err)
        self.assertEqual(out, "")

    def test_headers_usage_error_does_not_echo_the_credential(self):
        """--headers 的用法错误不许把那一项打进 stderr。

        stderr 会进 agent 的上下文与日志，而 headers 里装的就是凭据
        （``Authorization: Bearer …``）：回显出来等于泄密。
        规矩：skill 不得把 webhook 的 url 或 headers 打进日志或错误信息。
        """
        credential = "Authorization Bearer eyJhbGciOiJIUzI1NiJ9.SUPER-SECRET"

        code, out, err = run_cli(
            [
                "webhook",
                "add",
                "--name",
                "带鉴权头",
                "--url",
                "https://example.com/hook",
                "--body",
                "{}",
                "--headers",
                credential,
            ],
            base_url="http://127.0.0.1:1",
        )

        self.assertEqual(code, 1)
        self.assertNotIn("Traceback", err)
        # 凭据一个字符都不许出现。
        self.assertNotIn("eyJhbGciOiJIUzI1NiJ9", err)
        self.assertNotIn("SUPER-SECRET", err)
        self.assertNotIn(credential, err)

        # 但要说清是哪儿错了：报第几项，不报内容。
        self.assertIn("usage_error", err)
        self.assertIn("第 1 项", err)

        # 错误输出仍然只占一行。
        self.assertEqual(1, len(err.strip().splitlines()))
        self.assertEqual(out, "")

    def test_headers_usage_error_with_empty_key_does_not_echo_the_value(self):
        """键为空的那一项同样不回显 —— 值也可能是凭据。"""
        code, out, err = run_cli(
            [
                "webhook",
                "add",
                "--name",
                "带鉴权头",
                "--url",
                "https://example.com/hook",
                "--body",
                "{}",
                "--headers",
                "=SUPER-SECRET",
            ],
            base_url="http://127.0.0.1:1",
        )

        self.assertEqual(code, 1)
        self.assertNotIn("SUPER-SECRET", err)
        self.assertIn("usage_error", err)
        self.assertIn("第 1 项", err)
        self.assertEqual(out, "")

    def test_error_output_is_always_a_single_line(self):
        """错误输出只占一行是硬约定：detail 里的换行要压掉。"""
        module = _load_idea_module()

        self.assertEqual("a b c", module._one_line("a\nb  c"))

        # CliError 的渲染同样只占一行。
        self.assertEqual("usage_error: a b", module.CliError("usage_error", "a\nb").render())

    def test_non_telegram_webhook_tokens_are_redacted(self):
        """非 Telegram 的 webhook token 也不能原样进 stdout。

        三种曾经漏网的形态：
        * Slack：末段是纯字母的长串（不含数字，「像随机串」的启发式放行了它）；
        * Discord：id 段纯数字、token 段纯字母；
        * 短 token：一段不长的下划线串。
        """
        cases = [
            (
                "https://hooks.slack.com/services/T00000000/B00000000/abcdefghijklmnopqrstuvwx",
                "https://hooks.slack.com/services/***/***/***",
                ["T00000000", "B00000000", "abcdefghijklmnopqrstuvwx"],
            ),
            (
                "https://discord.com/api/webhooks/123456789012/AbCdEfGhIjKlMnOpQrStUvWxYz",
                "https://discord.com/api/webhooks/***/***",
                ["123456789012", "AbCdEfGhIjKlMnOpQrStUvWxYz"],
            ),
            (
                "https://api.example.com/v1/hook/sk_live_abc123",
                "https://api.example.com/v1/hook/***",
                ["sk_live_abc123"],
            ),
        ]

        for raw_url, expected, secrets in cases:
            with self.subTest(url=raw_url):
                webhook = {
                    "id": "wh-3",
                    "name": "其它服务",
                    "url": raw_url,
                    "method": "POST",
                    "headers": {},
                    "bodyTemplate": "{}",
                    "events": ["idea.created"],
                    "scope": "global",
                    "projectId": None,
                    "enabled": False,
                    "hasSecret": False,
                    "createdAt": "2026-01-01T00:00:00Z",
                    "updatedAt": "2026-01-01T00:00:00Z",
                }
                body = json.dumps([webhook]).encode("utf-8")
                url = self._serve(self._static_handler(200, "application/json", body))

                code, out, err = run_cli(["webhook", "list"], base_url=url)
                self.assertEqual(code, 0, err)
                self.assertNotIn(raw_url, out)
                for secret in secrets:
                    self.assertNotIn(secret, out)
                self.assertEqual(json.loads(out)[0]["url"], expected)

    def test_bot_token_placeholder_does_not_leak_neighbours(self):
        """占位符只保留它自己：**同段夹带的真 token 必须遮掉**。

        「含 ``<BOT_TOKEN>`` 的整段原样保留」是个泄漏路径：
        ``bot<BOT_TOKEN>123456789:FAKEfakeTOKEN`` 整段进 stdout，
        等于把占位符后面粘着的真 token 一起带出去了。
        """
        raw_url = "https://api.telegram.org/bot<BOT_TOKEN>123456789:FAKEfakeTOKEN/sendMessage"
        webhook = {
            "id": "wh-4",
            "name": "telegram",
            "url": raw_url,
            "method": "POST",
            "headers": {},
            "bodyTemplate": "{}",
            "events": ["idea.created"],
            "scope": "global",
            "projectId": None,
            "enabled": False,
            "hasSecret": False,
            "createdAt": "2026-01-01T00:00:00Z",
            "updatedAt": "2026-01-01T00:00:00Z",
        }
        body = json.dumps([webhook]).encode("utf-8")
        url = self._serve(self._static_handler(200, "application/json", body))

        code, out, err = run_cli(["webhook", "list"], base_url=url)
        self.assertEqual(code, 0, err)

        # 夹带的两截都不许出现在输出里。
        self.assertNotIn("123456789", out)
        self.assertNotIn("FAKEfakeTOKEN", out)
        # 占位符本身留着（agent 要拿它去填自己的 token），夹带的部分遮掉。
        self.assertEqual(
            json.loads(out)[0]["url"],
            "https://api.telegram.org/bot<BOT_TOKEN>***/sendMessage",
        )

    def test_long_numeric_version_segment_is_masked(self):
        """「v + 长数字」是纯数字 id 的形状，不能当成版本号放行。

        版本段白名单只放行**最多两位**的裸数字版本（v1、v2、v10……）；
        带后缀的版本段（v1.0、v1-beta1、v1beta1）仍然放行。
        """
        cases = [
            (
                "https://api.example.com/v1234567890123/hook/sk_live_abc123",
                "https://api.example.com/***/hook/***",
                ["v1234567890123"],
            ),
            (
                "https://api.example.com/v123/hook/sk_live_abc123",
                "https://api.example.com/***/hook/***",
                ["v123"],
            ),
            (
                "https://api.example.com/v1/hook/sk_live_abc123",
                "https://api.example.com/v1/hook/***",
                [],
            ),
            (
                "https://api.example.com/v1beta1/hook/sk_live_abc123",
                "https://api.example.com/v1beta1/hook/***",
                [],
            ),
        ]

        for raw_url, expected, secrets in cases:
            with self.subTest(url=raw_url):
                webhook = {
                    "id": "wh-5",
                    "name": "其它服务",
                    "url": raw_url,
                    "method": "POST",
                    "headers": {},
                    "bodyTemplate": "{}",
                    "events": ["idea.created"],
                    "scope": "global",
                    "projectId": None,
                    "enabled": False,
                    "hasSecret": False,
                    "createdAt": "2026-01-01T00:00:00Z",
                    "updatedAt": "2026-01-01T00:00:00Z",
                }
                body = json.dumps([webhook]).encode("utf-8")
                url = self._serve(self._static_handler(200, "application/json", body))

                code, out, err = run_cli(["webhook", "list"], base_url=url)
                self.assertEqual(code, 0, err)
                for secret in secrets:
                    self.assertNotIn(secret, out)
                self.assertEqual(json.loads(out)[0]["url"], expected)

    def test_base_url_without_scheme_exits_cleanly(self):
        # 「地址忘了写 scheme」是最常见的配置错：不能吐裸 traceback。
        code, out, err = run_cli(["list"], base_url="ideas.example.com")
        self.assertEqual(code, 1)
        self.assertNotIn("Traceback", err)
        self.assertIn("invalid_config", err)
        self.assertIn("scheme", err)
        self.assertEqual(out, "")

    def test_malformed_base_url_exits_cleanly(self):
        # 畸形 IPv6：urlsplit 自己会抛 ValueError（Invalid IPv6 URL）。
        code, out, err = run_cli(["list"], base_url="http://[::1")
        self.assertEqual(code, 1)
        self.assertNotIn("Traceback", err)
        self.assertIn("invalid_config", err)
        self.assertEqual(out, "")

    def test_unexpected_error_does_not_leak_traceback(self):
        # 兜底：未预期的异常也要是 stderr 的一行 + 非 0 退出，不能是 traceback。
        # 这里的触发点：服务端回了 content 不是字符串的畸形载荷，
        # text 渲染器会在上面炸掉（AttributeError）。
        body = json.dumps([{"id": "x", "content": 123}]).encode("utf-8")
        url = self._serve(self._static_handler(200, "application/json", body))

        code, out, err = run_cli(["--format", "text", "list"], base_url=url)
        self.assertEqual(code, 1)
        self.assertNotIn("Traceback", err)
        self.assertTrue(err.startswith("internal_error: "), err)
        self.assertEqual(out, "")


if __name__ == "__main__":
    unittest.main()
