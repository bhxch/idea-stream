#!/usr/bin/env python3
"""idea 记事本的命令行封装。

把 REST API 包成一条命令，agent 不必手写 HTTP 请求。

* 纯标准库（argparse / json / urllib / base64），无第三方依赖，Python 3.9+ 可跑。
* 连接配置**只**从环境变量读：IDEA_BASE_URL / IDEA_USER / IDEA_PASS / IDEA_TOKEN。
  命令行不接受密码，--help 里也不会出现密码。
* 默认输出 JSON（便于 agent 直接解析）；--format text 给人类看。
* 出错时把 ProblemDetails 的 code 与 detail 打到 stderr，退出码非 0。

命令树见 SKILL.md。
"""

import argparse
import base64
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, List, Optional

# 单次请求的超时（秒）。后端是本机/内网的一台小实例，30 秒足够。
TIMEOUT_SECONDS = 30

# 脱敏用的占位符。
MASK = "***"

# Telegram 模板里的 bot token 占位符：它不是秘密（是给用户替换的提示），要原样保留。
BOT_PLACEHOLDER = "<BOT_TOKEN>"

# 路径段白名单：明确是结构/协议词的段才原样保留，其余一律遮。
#
# 白名单只收「形状词」：协议前缀、版本号、资源的复数形式、动作名。
# 凡是可能装凭据的东西（id、token、随机串）都不在这里 —— 宁可遮错，
# 也不能因为「看着像词」就放过去。
SAFE_PATH_SEGMENTS = frozenset(
    [
        "api",
        "apis",
        "webhook",
        "webhooks",
        "hook",
        "hooks",
        "service",
        "services",
        "endpoint",
        "endpoints",
        "trigger",
        "triggers",
        "incoming",
        "outgoing",
        "sendmessage",
        "bot",
    ]
)

# 版本号段：结构性的，放行。
#
# **裸数字版本号最多两位**（v1、v2、v10……）：位数不限的话，
# 「v + 长数字」（v1234567890123，纯数字 id 的形状）会被当成版本号原样放行 ——
# 那就是漏网。
# 带后缀的版本段只认两种形状：
#   * 分隔符 + 短的字母数字段（v1.0、v1-beta1）；
#   * 常见的版本后缀词 + 至多两位数字（v1beta1、v1rc2）。
# 后缀词用白名单而不是「任意字母」：任意字母会让「v1 + 一串字母」这种
# 短 token 的形状蒙混过关。
VERSION_SEGMENT = re.compile(r"^v\d{1,2}(([._-][a-z0-9]{1,8})|(beta|alpha|rc|preview)\d{0,2})?$")

# 目标实例的地址。由 main() 设置，build_request() 读它
# （build_request 的签名是固定的，不收 base_url 参数）。
BASE_URL = ""


# --------------------------------------------------------------------------
# 错误
# --------------------------------------------------------------------------


def _one_line(value: Any) -> str:
    """把一段文本压成**一行**：换行与连续空白都变成单个空格。

    错误输出占一行是硬约定（agent 侧要的是能读的一行）：
    异常消息里带换行是常事，不压掉就会把「一行」这个约定破掉。
    """
    return " ".join(str(value).split())


class CliError(Exception):
    """CLI 自己的错误：一对 (code, detail)，渲染成 stderr 上的一行。"""

    exit_code = 1

    def __init__(self, code: str, detail: str) -> None:
        super().__init__("{}: {}".format(code, detail))
        self.code = code
        self.detail = detail

    def render(self) -> str:
        # 一行是硬约定（agent 侧要的是能读的一行）：detail 里带了换行就压掉。
        return "{}: {}".format(self.code, _one_line(self.detail))


class ApiError(CliError):
    """服务端返回的业务错误（RFC 9457 ProblemDetails + 扩展字段 code）。"""

    def __init__(self, code: str, detail: str, status: Optional[int] = None) -> None:
        super().__init__(code, detail)
        self.status = status


class ConfigError(CliError):
    """配置缺失（比如没给 IDEA_BASE_URL）。"""


class NetworkError(CliError):
    """网络层失败：连不上、超时、连接被拒。"""

    exit_code = 2

    def __init__(self, detail: str) -> None:
        super().__init__("network_error", detail)


class InvalidResponse(CliError):
    """响应体不是 JSON（代理返回 HTML、路径写错拿到 SPA 的 index.html……）。"""

    def __init__(self, status: int, content_type: str) -> None:
        super().__init__(
            "invalid_response",
            "响应不是 JSON（HTTP {}，Content-Type: {}）；"
            "可能是代理返回了 HTML，或请求的路径不存在".format(status, content_type or "未知"),
        )
        self.status = status


# --------------------------------------------------------------------------
# HTTP 层
# --------------------------------------------------------------------------


def env_or_die(name: str) -> str:
    """读环境变量；没有（或只有空白）就报配置错误，不抛 traceback。"""
    value = os.environ.get(name, "").strip()
    if not value:
        raise ConfigError("missing_config", "{} is required".format(name))
    return value


def build_request(method: str, path: str, body: Optional[dict] = None) -> urllib.request.Request:
    """造一个请求。

    认证：
    * IDEA_USER / IDEA_PASS → Authorization: Basic base64(user:pass)；
    * IDEA_TOKEN → X-Api-Key 头。
    """
    url = BASE_URL.rstrip("/") + _encode_path(path)
    headers = {"Accept": "application/json"}
    data = None

    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    elif method in ("POST", "PUT", "PATCH"):
        # 没有请求体的 POST/PUT/PATCH 也显式给一个空 body：
        # 免得有些代理对「没有 Content-Length 的 POST」有意见。
        data = b""

    if data is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"

    user = os.environ.get("IDEA_USER", "")
    password = os.environ.get("IDEA_PASS", "")
    if user or password:
        raw = "{}:{}".format(user, password).encode("utf-8")
        headers["Authorization"] = "Basic " + base64.b64encode(raw).decode("ascii")

    token = os.environ.get("IDEA_TOKEN", "")
    if token:
        headers["X-Api-Key"] = token

    return urllib.request.Request(url, data=data, headers=headers, method=method)


def _encode_path(path: str) -> str:
    """把路径里的非 ASCII 字符百分号编码。

    urllib 只肯发 ASCII 的 URL：路径里带中文（agent 拿一个手抄的 id 来试）
    会直接抛 UnicodeEncodeError —— 那是裸 traceback，不是错误提示。
    查询串已经由 urlencode 编过了，不重复编码。
    """
    head, sep, query = path.partition("?")
    encoded = urllib.parse.quote(head, safe="/")
    return encoded + sep + query


def call(req: urllib.request.Request) -> Any:
    """发请求。

    2xx → 解析后的 JSON（204 或空 body → None）；
    非 2xx → 抛 :class:`ApiError`（带 ProblemDetails 的 code 与 detail）；
    响应体不是 JSON → 抛 :class:`InvalidResponse`（带状态码）；
    网络层失败 → 抛 :class:`NetworkError`。
    """
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_SECONDS) as resp:
            status = resp.getcode()
            content_type = resp.headers.get("Content-Type", "")
            raw = resp.read()
    except urllib.error.HTTPError as exc:
        try:
            raw = exc.read()
        except Exception:  # noqa: BLE001 —— 读不到 body 也要把状态码报出去
            raw = b""
        content_type = exc.headers.get("Content-Type", "") if exc.headers else ""
        raise _error_for(exc.code, raw, content_type)
    except urllib.error.URLError as exc:
        raise NetworkError(str(exc.reason))
    except (TimeoutError, OSError) as exc:
        raise NetworkError(str(exc))

    if status == 204 or not raw.strip():
        return None

    try:
        return json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        raise InvalidResponse(status, content_type)


def _error_for(status: int, raw: bytes, content_type: str) -> CliError:
    """把一个非 2xx 响应变成异常。"""
    payload = None
    if raw.strip():
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            payload = None

    if payload is None:
        if raw.strip():
            # 不是 JSON：说清楚「不是 JSON」并带上状态码，别抛裸 traceback。
            return InvalidResponse(status, content_type)
        return ApiError("http_{}".format(status), "HTTP {}".format(status), status)

    if isinstance(payload, dict):
        code = payload.get("code")
        detail = payload.get("detail") or payload.get("title") or ""
        if code:
            return ApiError(str(code), str(detail), status)
        return ApiError("http_{}".format(status), str(detail or "HTTP {}".format(status)), status)

    return ApiError("http_{}".format(status), "HTTP {}".format(status), status)


def api(method: str, path: str, body: Optional[dict] = None) -> Any:
    """一步到位：造请求 + 发请求。"""
    return call(build_request(method, path, body))


# --------------------------------------------------------------------------
# 脱敏（R165）
# --------------------------------------------------------------------------


def mask_url(url: str) -> str:
    """把 URL 变成「host + 路径形状」，可能含凭据的段替换成 ``***``。

    * scheme 与 host 保留（不是秘密）；query 与 fragment 一律丢弃
      （access_token 之类的凭据常在 query 里）；userinfo 也不保留。
    * **路径段默认全遮**，只有明确是结构词的段才原样保留（见
      :data:`SAFE_PATH_SEGMENTS`）—— 方向是「默认遮、白名单例外」：
      「长得像随机串才遮」那种启发式会漏掉纯字母的 token、纯数字的 id
      和短 token，而漏掉的就是凭据本身。
    * ``<BOT_TOKEN>`` 这个占位符原样保留：它不是秘密，是给用户替换的提示。
      但**只保留占位符本身**：同段里夹带的其它内容照样遮
      （见 :func:`_mask_placeholder_segment`）。
    """
    try:
        parts = urllib.parse.urlsplit(url)
        host = parts.hostname or ""
        port = parts.port
    except ValueError:
        return MASK

    netloc = host
    if port:
        netloc = "{}:{}".format(host, port)

    segments = parts.path.split("/")
    masked: List[str] = [_mask_segment(segment) for segment in segments]
    path = "/".join(masked)

    if not netloc and not path:
        return MASK
    if parts.scheme:
        return "{}://{}{}".format(parts.scheme, netloc, path)
    return "{}{}".format(netloc, path)


def _mask_segment(segment: str) -> str:
    """遮一个路径段：默认遮，白名单例外。"""
    if not segment:
        return segment
    if BOT_PLACEHOLDER in segment:
        return _mask_placeholder_segment(segment)
    lowered = segment.lower()
    if lowered in SAFE_PATH_SEGMENTS or VERSION_SEGMENT.match(lowered):
        return segment
    if lowered.startswith("bot") and len(segment) > 3:
        # Telegram 的形状：token 粘在 bot 后面（/bot<token>）。
        # 例子里不写真实的数字串 —— 仓库有一条 grep 盯着「像真 token」的形状。
        return "bot" + MASK
    return MASK


def _mask_placeholder_segment(segment: str) -> str:
    """带占位符的段：**只保留占位符本身**，同段里别的东西一律遮。

    占位符不是秘密（是给用户替换的提示），但**同段里夹带的东西是**：
    ``/bot<BOT_TOKEN>123456789:AAHfakeTOKEN`` 这种「占位符后面粘着真 token」
    的段，整段保留等于把真 token 带出去了。

    规则：
    * 占位符之外没有别的东西 → 整段保留（``<BOT_TOKEN>``、``bot<BOT_TOKEN>``）；
    * 占位符之外还有东西 → 那些东西遮成 ``***``，占位符原样留着；
    * 只有紧挨在占位符前面的结构词 ``bot`` 保留（Telegram 的形状是 ``/bot<token>``）。
    """
    prefix, _, suffix = segment.partition(BOT_PLACEHOLDER)
    head = prefix if prefix.lower() == "bot" else (MASK if prefix else "")
    tail = MASK if suffix else ""
    return head + BOT_PLACEHOLDER + tail


def redact(value: Any) -> Any:
    """把响应里的 webhook ``url`` 与 ``headers`` 脱敏。

    两种输出格式共用这一份脱敏后的数据 —— text 格式不可能绕过它。
    """
    if isinstance(value, dict):
        result: Dict[str, Any] = {}
        for key, item in value.items():
            if key == "url" and isinstance(item, str):
                result[key] = mask_url(item)
            elif key == "headers" and isinstance(item, dict):
                # 只留键名，值一律换成 ***（Authorization: Bearer … 这类就在这儿）。
                result[key] = dict((header, MASK) for header in item.keys())
            else:
                result[key] = redact(item)
        return result
    if isinstance(value, list):
        return [redact(item) for item in value]
    return value


# --------------------------------------------------------------------------
# 文本输出
# --------------------------------------------------------------------------


def render_text(payload: Any) -> str:
    """把响应渲染成给人看的几行。"""
    if payload is None:
        return "ok"
    if isinstance(payload, list):
        lines = [_render_line(item) for item in payload]
        return "\n".join(lines)
    return "\n".join(_render_fields(payload))


def _render_line(item: Any) -> str:
    if not isinstance(item, dict):
        return str(item)

    # 想法
    if "id" in item and "content" in item:
        label = item.get("title") or _first_line(item.get("content", ""))
        line = "{}  {}".format(item.get("id", ""), label)
        if item.get("done"):
            line += "  [done]"
        tags = item.get("tags") or []
        if tags:
            line += "  #{}".format(",".join(str(tag) for tag in tags))
        return line

    # 项目
    if "id" in item and "ideaCount" in item:
        return "{}  {}  ({} 条想法)".format(
            item.get("id", ""), item.get("name", ""), item.get("ideaCount", 0)
        )

    # 标签字典
    if "name" in item and "count" in item and "id" not in item:
        return "{}  {}".format(item.get("name", ""), item.get("count", 0))

    # webhook
    if "id" in item and "url" in item:
        return "{}  {}  {}  {}  scope={}  {}  events={}".format(
            item.get("id", ""),
            item.get("name", ""),
            item.get("method", ""),
            item.get("url", ""),
            item.get("scope", ""),
            "enabled" if item.get("enabled") else "disabled",
            ",".join(str(event) for event in (item.get("events") or [])),
        )

    # 回收站条目
    if "kind" in item:
        if item.get("kind") == "project" and item.get("project"):
            project = item["project"]
            return "project  {}  {}  ({} 条想法)".format(
                project.get("id", ""), project.get("name", ""), len(item.get("ideas") or [])
            )
        idea = item.get("idea")
        if idea:
            return "idea  {}  {}".format(idea.get("id", ""), _first_line(idea.get("content", "")))
        return str(item.get("kind"))

    # 投递记录
    if "id" in item and "eventType" in item:
        return "{}  {}  {}  attempts={}".format(
            item.get("id", ""),
            item.get("status", ""),
            item.get("eventType", ""),
            item.get("attempts", 0),
        )

    return "\n".join(_render_fields(item))


def _render_fields(item: Any) -> List[str]:
    if not isinstance(item, dict):
        return [str(item)]
    lines = []
    for key, value in item.items():
        if isinstance(value, (dict, list)):
            lines.append("{}: {}".format(key, json.dumps(value, ensure_ascii=False)))
        else:
            lines.append("{}: {}".format(key, value))
    return lines


def _first_line(text: str) -> str:
    for line in (text or "").splitlines():
        stripped = line.strip()
        if stripped:
            return stripped
    return ""


# --------------------------------------------------------------------------
# 参数
# --------------------------------------------------------------------------


def _split_csv(value: Optional[str]) -> List[str]:
    """逗号串 → 列表（去首尾空白、丢空项）。"""
    if not value:
        return []
    return [part.strip() for part in value.split(",") if part.strip()]


def _parse_headers(value: Optional[str]) -> Dict[str, str]:
    """``k=v,k2=v2`` → 字典。

    用法错误时**不回显那一项**：--headers 的内容是凭据（``Authorization: Bearer …``），
    回显进 stderr 就等于把它打进 agent 的上下文与日志 ——
    规矩是「skill 不得把 webhook 的 url 或 headers 打进日志或错误信息」。
    只报「第几项」与「哪儿不对」。
    """
    headers: Dict[str, str] = {}
    for index, item in enumerate(_split_csv(value), start=1):
        if "=" not in item:
            raise CliError(
                "usage_error",
                "--headers 的第 {} 项不是 键=值 的形式（内容不回显：它可能是凭据）".format(index),
            )
        key, _, val = item.partition("=")
        key = key.strip()
        if not key:
            raise CliError("usage_error", "--headers 的第 {} 项的键为空".format(index))
        headers[key] = val.strip()
    return headers


def build_parser() -> argparse.ArgumentParser:
    """命令树。

    全局选项（--format / --base-url）用 ``parents`` 挂到每个子命令上，
    子命令上的默认值是 SUPPRESS：这样「子命令前给」与「子命令后给」都认，
    而且谁都没给时不会被默认值互相覆盖。
    """
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument(
        "--format",
        choices=["json", "text"],
        default=argparse.SUPPRESS,
        help="输出格式：json（默认，给 agent 解析）或 text（给人看）",
    )
    common.add_argument(
        "--base-url",
        dest="base_url",
        default=argparse.SUPPRESS,
        help="覆盖 IDEA_BASE_URL（临时换个实例用）",
    )

    parser = argparse.ArgumentParser(
        prog="idea.py",
        description="idea 记事本的命令行封装（凭证走环境变量，不走命令行）",
        parents=[common],
    )
    sub = parser.add_subparsers(dest="command", metavar="<命令>")

    sub.add_parser("projects", parents=[common], help="项目列表")

    project = sub.add_parser("project", parents=[common], help="项目管理")
    project_sub = project.add_subparsers(dest="subcommand", metavar="<子命令>")

    add_project = project_sub.add_parser("add", parents=[common], help="新建项目")
    add_project.add_argument("name", help="项目名")

    rename_project = project_sub.add_parser("rename", parents=[common], help="改项目名")
    rename_project.add_argument("id")
    rename_project.add_argument("name", help="新名字")

    delete_project = project_sub.add_parser("delete", parents=[common], help="删项目（进回收站）")
    delete_project.add_argument("id")

    restore_project = project_sub.add_parser("restore", parents=[common], help="从回收站恢复项目")
    restore_project.add_argument("id")

    listing = sub.add_parser("list", parents=[common], help="列想法")
    listing.add_argument("--project", dest="project", help="只看某个项目")
    listing.add_argument("--status", dest="status", help="all / open / done")
    listing.add_argument("--tags", dest="tags", help="按标签筛，逗号分隔")
    listing.add_argument("--tag-mode", dest="tag_mode", help="or（默认）/ and")

    add_idea = sub.add_parser("add", parents=[common], help="记一条想法")
    add_idea.add_argument("content", help="正文")
    add_idea.add_argument("--project", dest="project", help="所属项目（默认落进内置收件箱）")
    add_idea.add_argument("--title", dest="title", help="标题")
    add_idea.add_argument("--tags", dest="tags", help="标签，逗号分隔")

    update_idea = sub.add_parser("update", parents=[common], help="改标题/正文")
    update_idea.add_argument("id")
    update_idea.add_argument("--content", dest="content", help="新正文")
    update_idea.add_argument("--title", dest="title", help="新标题")

    set_tags = sub.add_parser("tags", parents=[common], help="覆盖式设置标签")
    set_tags.add_argument("id")
    set_tags.add_argument("tags", help="标签，逗号分隔（覆盖原有的）")

    done = sub.add_parser("done", parents=[common], help="标记完成/取消完成")
    done.add_argument("id")
    done.add_argument("--undo", action="store_true", help="取消完成")

    delete_idea = sub.add_parser("delete", parents=[common], help="删想法（进回收站）")
    delete_idea.add_argument("id")

    restore_idea = sub.add_parser("restore", parents=[common], help="从回收站恢复想法")
    restore_idea.add_argument("id")

    merge = sub.add_parser("merge", parents=[common], help="多条想法合并成一条")
    merge.add_argument("ids", nargs="+", help="要合并的想法 id（至少两条）")
    merge.add_argument("--content", dest="content", help="合并后的正文（不给就由服务端拼接）")
    merge.add_argument("--title", dest="title", help="合并后的标题")

    bulk = sub.add_parser("bulk", parents=[common], help="批量操作")
    bulk_sub = bulk.add_subparsers(dest="subcommand", metavar="<子命令>")

    bulk_done = bulk_sub.add_parser("done", parents=[common], help="批量标记完成")
    bulk_done.add_argument("ids", nargs="+", help="想法 id")
    bulk_done.add_argument("--undo", action="store_true", help="取消完成")

    bulk_tags = bulk_sub.add_parser("tags", parents=[common], help="批量加标签（并集追加）")
    bulk_tags.add_argument("args", nargs="+", metavar="id... 标签串", help="想法 id，最后一个参数是标签串")

    bulk_delete = bulk_sub.add_parser("delete", parents=[common], help="批量删除")
    bulk_delete.add_argument("ids", nargs="+", help="想法 id")

    sub.add_parser("trash", parents=[common], help="回收站内容")
    sub.add_parser("taglist", parents=[common], help="标签字典（含使用次数）")

    webhook = sub.add_parser("webhook", parents=[common], help="webhook 配置")
    webhook_sub = webhook.add_subparsers(dest="subcommand", metavar="<子命令>")

    webhook_list = webhook_sub.add_parser("list", parents=[common], help="webhook 列表")
    webhook_list.add_argument("--project", dest="project", help="只看绑定到某个项目的")

    webhook_add = webhook_sub.add_parser("add", parents=[common], help="新建 webhook")
    webhook_add.add_argument("--name", dest="name", required=True, help="名称")
    webhook_add.add_argument("--url", dest="url", required=True, help="投递地址（http/https 绝对地址）")
    webhook_add.add_argument("--body", dest="body", help="Body 模板（一个 JSON 文档）")
    webhook_add.add_argument("--headers", dest="headers", help="请求头，k=v 逗号分隔")
    webhook_add.add_argument("--events", dest="events", help="订阅的事件，逗号分隔")
    webhook_add.add_argument(
        "--scope",
        dest="scope",
        default="global",
        help="global（默认，创建后禁用）/ project（创建后启用）",
    )
    webhook_add.add_argument("--project", dest="project", help="scope=project 时的所属项目 id")
    webhook_add.add_argument("--secret", dest="secret", help="HMAC 签名密钥（留空则不设置）")

    webhook_update = webhook_sub.add_parser("update", parents=[common], help="改 webhook")
    webhook_update.add_argument("id")
    webhook_update.add_argument("--name", dest="name", help="新名称")
    webhook_update.add_argument("--url", dest="url", help="新投递地址")
    webhook_update.add_argument("--body", dest="body", help="新 Body 模板")
    webhook_update.add_argument("--headers", dest="headers", help="新请求头，k=v 逗号分隔")
    webhook_update.add_argument("--events", dest="events", help="新的订阅事件，逗号分隔")
    webhook_update.add_argument("--enable", dest="enable", action="store_true", help="启用")
    webhook_update.add_argument("--disable", dest="disable", action="store_true", help="禁用")
    webhook_update.add_argument("--secret", dest="secret", help="新密钥（留空则不设置）")

    webhook_delete = webhook_sub.add_parser("delete", parents=[common], help="删 webhook")
    webhook_delete.add_argument("id")

    sub.add_parser("telegram-template", parents=[common], help="打印 Telegram webhook 模板预设")

    return parser


# --------------------------------------------------------------------------
# 各命令
# --------------------------------------------------------------------------


def dispatch(args: argparse.Namespace) -> Any:
    command = getattr(args, "command", None)

    if command == "projects":
        return api("GET", "/api/projects")

    if command == "project":
        return _dispatch_project(args)

    if command == "list":
        return _dispatch_list(args)

    if command == "add":
        return _dispatch_add(args)

    if command == "update":
        return _dispatch_update(args)

    if command == "tags":
        return api("PUT", "/api/ideas/{}/tags".format(args.id), {"tags": _split_csv(args.tags)})

    if command == "done":
        return api("POST", "/api/ideas/{}/complete".format(args.id), {"done": not args.undo})

    if command == "delete":
        return api("DELETE", "/api/ideas/{}".format(args.id))

    if command == "restore":
        return api("POST", "/api/ideas/{}/restore".format(args.id))

    if command == "merge":
        return _dispatch_merge(args)

    if command == "bulk":
        return _dispatch_bulk(args)

    if command == "trash":
        return api("GET", "/api/trash")

    if command == "taglist":
        return api("GET", "/api/tags")

    if command == "webhook":
        return _dispatch_webhook(args)

    if command == "telegram-template":
        return api("GET", "/api/webhooks/templates/telegram")

    raise CliError("usage_error", "不知道的命令：{}".format(command))


def _dispatch_project(args: argparse.Namespace) -> Any:
    subcommand = getattr(args, "subcommand", None)

    if subcommand == "add":
        return api("POST", "/api/projects", {"name": args.name})

    if subcommand == "rename":
        return api("PATCH", "/api/projects/{}".format(args.id), {"name": args.name})

    if subcommand == "delete":
        return api("DELETE", "/api/projects/{}".format(args.id))

    if subcommand == "restore":
        return api("POST", "/api/projects/{}/restore".format(args.id))

    raise CliError("usage_error", "project 下面要用 add / rename / delete / restore")


def _dispatch_list(args: argparse.Namespace) -> Any:
    params: Dict[str, str] = {}
    if args.project:
        params["projectId"] = args.project
    if args.status:
        params["status"] = args.status
    # 逗号串统一走 _split_csv：跟同一文件里其它地方（--tags/--events/--headers）
    # 用同一套语义（去空白、丢空项），免得两套逗号语义以后分叉。
    tags = _split_csv(args.tags)
    if tags:
        params["tags"] = ",".join(tags)
    if args.tag_mode:
        params["tagMode"] = args.tag_mode

    path = "/api/ideas"
    if params:
        path += "?" + urllib.parse.urlencode(params)
    return api("GET", path)


def _dispatch_add(args: argparse.Namespace) -> Any:
    body: Dict[str, Any] = {"content": args.content}
    if args.project:
        body["projectId"] = args.project
    if args.title is not None:
        body["title"] = args.title
    if args.tags is not None:
        body["tags"] = _split_csv(args.tags)
    return api("POST", "/api/ideas", body)


def _dispatch_update(args: argparse.Namespace) -> Any:
    # 只发用户真正给了的字段：后端把 null 当「不改」，
    # 但发空串会真的把标题/正文清空 —— 那是用户的意图之外的事。
    body: Dict[str, Any] = {}
    if args.content is not None:
        body["content"] = args.content
    if args.title is not None:
        body["title"] = args.title
    if not body:
        raise CliError("usage_error", "update 至少要给出 --content 或 --title 之一")
    return api("PATCH", "/api/ideas/{}".format(args.id), body)


def _dispatch_merge(args: argparse.Namespace) -> Any:
    body: Dict[str, Any] = {"ids": list(args.ids)}
    if args.content is not None:
        body["content"] = args.content
    if args.title is not None:
        body["title"] = args.title
    return api("POST", "/api/ideas/merge", body)


def _dispatch_bulk(args: argparse.Namespace) -> Any:
    subcommand = getattr(args, "subcommand", None)

    if subcommand == "done":
        return api(
            "POST",
            "/api/ideas/bulk/complete",
            {"ids": list(args.ids), "done": not args.undo},
        )

    if subcommand == "tags":
        # 参数顺序是「id 在前、标签串在最后」：bulk tags id1 id2 a,b
        if len(args.args) < 2:
            raise CliError("usage_error", "bulk tags 至少要一个想法 id 和一个标签串")
        tags = _split_csv(args.args[-1])
        ids = list(args.args[:-1])
        # 不发 replace：默认就是并集追加（不覆盖原有标签）。
        return api("POST", "/api/ideas/bulk/tags", {"ids": ids, "tags": tags})

    if subcommand == "delete":
        return api("POST", "/api/ideas/bulk/delete", {"ids": list(args.ids)})

    raise CliError("usage_error", "bulk 下面要用 done / tags / delete")


def _dispatch_webhook(args: argparse.Namespace) -> Any:
    subcommand = getattr(args, "subcommand", None)

    if subcommand == "list":
        path = "/api/webhooks"
        if args.project:
            path += "?" + urllib.parse.urlencode({"projectId": args.project})
        return api("GET", path)

    if subcommand == "add":
        # 只发用户真正给了的字段。
        # 特别地：**不发 enabled** —— 后端按 scope 决定（global 默认禁用、project 默认启用）。
        if args.body is None:
            # Body 模板不能为空（后端会拒，页面也会先说清楚）：
            # 与其白等一次请求，不如本地就说清楚。
            raise CliError("usage_error", "webhook add 需要 --body（Body 模板不能为空）")
        body: Dict[str, Any] = {"name": args.name, "url": args.url}
        body["scope"] = args.scope
        body["bodyTemplate"] = args.body
        if args.project:
            body["projectId"] = args.project
        if args.headers is not None:
            body["headers"] = _parse_headers(args.headers)
        if args.events is not None:
            body["events"] = _split_csv(args.events)
        if args.secret:
            # 密钥三态：null = 不改、"" = 清空、非空 = 替换。
            # CLI 的 --secret 留空就不发这个键。
            body["secret"] = args.secret
        return api("POST", "/api/webhooks", body)

    if subcommand == "update":
        # 后端把 null 当「不改」，所以只发用户真正给了的字段 ——
        # 没给的字段一个都不发（发空串会真的清空）。
        body: Dict[str, Any] = {}
        if args.name is not None:
            body["name"] = args.name
        if args.url is not None:
            body["url"] = args.url
        if args.body is not None:
            body["bodyTemplate"] = args.body
        if args.headers is not None:
            body["headers"] = _parse_headers(args.headers)
        if args.events is not None:
            body["events"] = _split_csv(args.events)
        if args.enable and args.disable:
            raise CliError("usage_error", "--enable 与 --disable 不能同时给")
        if args.enable:
            body["enabled"] = True
        elif args.disable:
            body["enabled"] = False
        if args.secret:
            body["secret"] = args.secret
        if not body:
            raise CliError("usage_error", "webhook update 至少要给出要改的字段")
        return api("PATCH", "/api/webhooks/{}".format(args.id), body)

    if subcommand == "delete":
        return api("DELETE", "/api/webhooks/{}".format(args.id))

    raise CliError("usage_error", "webhook 下面要用 list / add / update / delete")


# --------------------------------------------------------------------------
# 入口
# --------------------------------------------------------------------------


def resolve_base_url(args: argparse.Namespace) -> str:
    """定下目标地址：``--base-url`` 优先，否则读 ``IDEA_BASE_URL``。

    校验放在这里：缺 scheme、畸形 IPv6 之类的地址错误，
    到 urllib 手上会变成 ``ValueError``（裸 traceback）。
    「地址写错」是 agent/用户最常犯的配置错，比「代理返回 HTML」常见得多 ——
    所以本地就说清楚。
    """
    override = getattr(args, "base_url", None)
    if override and override.strip():
        raw = override.strip()
        source = "--base-url"
    else:
        try:
            raw = env_or_die("IDEA_BASE_URL")
        except ConfigError as exc:
            # 提示语拼在这里：env_or_die 只管「有没有」，怎么补是调用方的事。
            raise ConfigError(
                exc.code,
                "{}（也可以用 --base-url <地址> 临时覆盖）".format(exc.detail),
            )
        source = "IDEA_BASE_URL"

    try:
        parts = urllib.parse.urlsplit(raw)
        host = parts.hostname
    except ValueError:
        parts = None
        host = None

    if parts is None or parts.scheme.lower() not in ("http", "https") or not host:
        raise ConfigError(
            "invalid_config",
            "{} 不是合法的实例地址：{}（要带 scheme，如 https://ideas.example.com）".format(
                source, _redact_userinfo(raw)
            ),
        )

    return raw


def _redact_userinfo(raw: str) -> str:
    """错误信息里回显地址时去掉 userinfo（有人会把密码写在 URL 里）。"""
    head, sep, tail = raw.rpartition("@")
    if not sep:
        return raw
    return "***@" + tail


def main(argv: Optional[List[str]] = None) -> int:
    global BASE_URL

    parser = build_parser()
    args = parser.parse_args(argv)

    if not getattr(args, "command", None):
        parser.print_help(sys.stderr)
        return 2

    try:
        BASE_URL = resolve_base_url(args)
        payload = dispatch(args)
        emit(payload, getattr(args, "format", "json"))
    except CliError as exc:
        print(exc.render(), file=sys.stderr)
        return exc.exit_code
    except Exception as exc:  # noqa: BLE001 —— 兜底：干净地失败，别吐 traceback
        # 未预期的异常也要守住「stderr 一行 + 退出码非 0」的契约：
        # agent 侧要的是能读的一行，真实原因靠服务端日志与报告定位。
        # 异常信息里可能有换行（多行异常消息），压成一行。
        print("internal_error: {}".format(_one_line(exc)), file=sys.stderr)
        return 1

    return 0


def emit(payload: Any, output_format: str) -> None:
    """输出。两种格式共用同一份脱敏后的数据。"""
    safe = redact(payload)
    if output_format == "text":
        print(render_text(safe))
    else:
        print(json.dumps(safe, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    sys.exit(main())
