"""idea.py 的端到端测试。

两类测试：

* :class:`IdeaCliTest` —— 打真实后端（``IDEA_BASE_URL`` 指向的实例）。
  实例不可达时整个类 skip（不报错），这样在没起后端的环境里也能跑。
* :class:`LocalServerTest` —— 用一个本地的一次性 HTTP 服务打桩，
  测那些跟后端无关的行为（脱敏、响应不是 JSON、缺配置）。

CLI 一律以子进程调用：端到端测的是「agent 实际会用的那条命令」，
包括 stdout/stderr 的分工与退出码。
"""

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
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

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

    def test_webhook_url_and_headers_are_redacted(self):
        webhook = {
            "id": "wh-1",
            "name": "telegram",
            "url": "https://api.telegram.org/bot123456789:AAFakeTokenSecret/sendMessage",
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
        self.assertNotIn("123456789", out)
        self.assertNotIn("AAFakeTokenSecret", out)
        self.assertNotIn("SUPER-SECRET-HEADER", out)
        self.assertIn("api.telegram.org", out)
        self.assertIn("***", out)

        payload = json.loads(out)
        self.assertEqual(payload[0]["url"], "https://api.telegram.org/bot***/sendMessage")
        self.assertEqual(payload[0]["headers"], {"Authorization": "***"})

        # text 输出同样要脱敏。
        code, out, err = run_cli(["--format", "text", "webhook", "list"], base_url=url)
        self.assertEqual(code, 0, err)
        self.assertNotIn("AAFakeTokenSecret", out)
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


if __name__ == "__main__":
    unittest.main()
