#!/usr/bin/env python3
"""取件器：把服务端分发的 skill 内容同步到本地缓存，再打印 agent 要的那一份。

* 纯标准库（urllib / hashlib / json），无第三方依赖，Python 3.9+ 可跑。
* 连接配置**只**从环境变量读：IDEA_BASE_URL / IDEA_USER / IDEA_PASS / IDEA_TOKEN。
  命令行不接受密码，--help 里也不会出现密码。
* 同步是**整体**的：清单里的每个文件都下载并逐个校验 sha256，
  全部通过才把临时目录改名成版本目录 —— 中途失败时旧版本原样留着，
  不会出现「手册是新的、别的是旧的」。
* 出错时把错误码与细节打到 stderr（**一行** `code: detail`），退出码非 0。

用法：

    fetch.py                    # 索引页（清单里的 entry）
    fetch.py references/x.md    # 分发副本里的某一份文件
    fetch.py /openapi/v1.json   # 服务端某个绝对路径的原文
"""

import argparse
import base64
import hashlib
import http.client
import json
import os
import re
import shutil
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, List, Optional, Tuple

# 单次请求的超时（秒）。后端是本机/内网的一台小实例，30 秒足够。
TIMEOUT_SECONDS = 30

# 清单端点与分发副本的路径前缀（相对服务端的根）。
MANIFEST_PATH = "/skill/idea-manager/version"
CONTENT_PREFIX = "/skill/idea-manager/"

# 缓存根目录的默认名字（放在 XDG_CACHE_HOME 或 ~/.cache 底下）。
CACHE_DIR_NAME = "idea-skill"

# 缓存里保留几个版本目录。留旧版本是为了「同步失败时还有旧版可退」，
# 留太多又白占地方（每份几十 KB），两个刚好。
KEEP_VERSIONS = 2

# 清单快照：存在缓存副本里的一份清单。
#
# 取不到清单时（网络断了）要靠它知道「入口页是哪个、哪些文件在」——
# 没有它，离线降级就不知道该打印什么。
MANIFEST_SNAPSHOT = ".manifest.json"

# 临时目录的前缀（``.tmp-<version>-<pid>``）。
TEMP_PREFIX = ".tmp-"

# 版本串要能当目录名：只收字母数字与 . _ -。
#
# 版本串是**服务端给的**，不是自己算的 —— 但它会直接变成目录名，
# 所以必须挡住路径分隔符与 ..（否则一个恶意的服务端能把缓存写到别处去）。
VERSION_PATTERN = re.compile(r"^[A-Za-z0-9._-]{1,128}$")

# 清单里的哈希：64 位十六进制。
SHA256_PATTERN = re.compile(r"^[0-9a-fA-F]{64}$")

# 脱敏用的占位符。
MASK = "***"

# 地址里的 userinfo（user:password@host）—— 回显地址时抹掉。
#
# 按 URL 结构来（先分出 scheme，再看 authority 里有没有 '@'），
# **不是**按最后一个 '@' 切：按 '@' 切会把路径里的 '@'（http://host/a@b）
# 误当成 userinfo，把一段正常的路径改成 "***@b"。
BASE_URL_USERINFO = re.compile(
    r"^(?P<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)?(?P<userinfo>[^/?#@\s]*)@"
)

# 文本里的 userinfo（user:password@host）——错误信息里抹掉。
#
# 与上面那个的区别：这里要能在**一段文本**里找（异常消息可能只带半个 URL），
# 所以不锚在开头；userinfo 段必须带冒号（user:password），
# 免得把普通的 "someone@example.com" 也当成凭据。
USERINFO_PATTERN = re.compile(
    r"(?P<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)?"
    r"(?P<userinfo>[^/?#@\s]*:[^/?#@\s]*)"
    r"@"
    r"(?P<host>[^/?#@\s]*)"
)


# --------------------------------------------------------------------------
# 错误
# --------------------------------------------------------------------------
#
# 取件器自己的错误码：一行 `code: detail` 到 stderr，退出码非 0。
#
# | 情况 | 码 | 退出码 |
# |---|---|---|
# | 缺 IDEA_BASE_URL | missing_config | 1 |
# | IDEA_BASE_URL 非法（没 scheme / 没 host） | invalid_config | 1 |
# | 服务端拒了鉴权（401 / 403） | auth_error | 1 |
# | 服务端没分发 skill 内容 | skill_not_distributed | 1 |
# | 要取的文件不存在 | not_found | 1 |
# | 取回来的内容与服务端清单对不上 | skill_integrity_error | 1 |
# | 清单不是合法 JSON（多半是代理返回了 HTML） | invalid_response | 1 |
# | 服务端回了别的业务错误码 | 服务端给的 code | 1 |
# | 别的非 2xx | http_<状态码> | 1 |
# | 网络不可达 / 超时 / 传输中断 | network_error | 2 |
# | argparse 的用法错误 | argparse 自己的用法提示 | 2 |
# | 兜底（未预期的异常） | internal_error | 1 |
#
# 降级不是错误：取不到清单但本地有**完好**的缓存时，走缓存并在 stderr 打一行
# ``warning:`` 提示，退出码仍是 0。


def _one_line(value: Any) -> str:
    """把一段文本压成**一行**：换行与连续空白都变成单个空格。

    错误输出占一行是硬约定（agent 侧要的是能读的一行）：
    异常消息里带换行是常事，不压掉就会把「一行」这个约定破掉。
    """
    return " ".join(str(value).split())


class CliError(Exception):
    """取件器自己的错误：一对 (code, detail)，渲染成 stderr 上的一行。"""

    exit_code = 1

    def __init__(self, code: str, detail: str) -> None:
        super().__init__("{}: {}".format(code, detail))
        self.code = code
        self.detail = detail

    def render(self) -> str:
        # 脱敏在这一层做（而不是各错误码各做一遍）：所有错误码都从这里过。
        return "{}: {}".format(self.code, _redact(_one_line(self.detail)))


class ConfigError(CliError):
    """配置缺失或写错了（比如没给 IDEA_BASE_URL）。"""


class AuthError(CliError):
    """服务端拒了鉴权（401 / 403）。"""

    def __init__(self, detail: str = "服务端拒绝了鉴权（用户名/密码或 token 不对）") -> None:
        super().__init__("auth_error", detail)


class NotFoundError(CliError):
    """要取的文件不存在。"""

    def __init__(self, detail: str) -> None:
        super().__init__("not_found", detail)


class NetworkError(CliError):
    """网络层失败：连不上、超时、连接被拒。"""

    exit_code = 2

    def __init__(self, detail: str) -> None:
        super().__init__("network_error", detail)


class InvalidResponse(CliError):
    """服务端的回答不是预期形状（代理返回了 HTML、清单字段不对……）。"""

    def __init__(self, detail: str) -> None:
        super().__init__("invalid_response", detail)


class IntegrityError(CliError):
    """取回来的内容与服务端清单对不上（哈希不匹配）。"""

    def __init__(self, detail: str) -> None:
        super().__init__("skill_integrity_error", detail)


# --------------------------------------------------------------------------
# 脱敏
# --------------------------------------------------------------------------


def _redact(text: str) -> str:
    """把凭据从一段文本里抹掉。

    错误信息是给人（和 agent 的上下文）看的 —— 凭据进去就等于泄了：
    stderr 会进日志、进 agent 的上下文。

    两道一起上：
    * **已知的凭据值**（环境变量里的密码 / token、地址里带的密码）→ 换成 ``***``；
    * **文本里 URL 的 userinfo**（``user:password@host``）→ 换成 ``***@``。

    只做第二道不够：异常消息未必带着 URL（比如 urllib 抱怨端口时只说
    ``nonnumeric port: 'sekret-pw@host'``），密码会**原样**出现在里面。
    """
    for value in _secret_values():
        if value:
            text = text.replace(value, MASK)

    return USERINFO_PATTERN.sub(_redact_userinfo, text)


def _redact_userinfo(match: "re.Match") -> str:
    """把一段文本里的 userinfo 换成 ``***@``（没有 host 就整个抹掉）。"""
    scheme = match.group("scheme") or ""
    host = match.group("host")

    if not host:
        # 整个 URL 就是凭据段（没有 host）→ 整个抹掉。
        return MASK

    return "{}***@{}".format(scheme, host)


def _secret_values() -> List[str]:
    """本进程里可能出现在输出中的凭据值。

    凭据可能来自两处：
    * 环境变量（``IDEA_USER`` / ``IDEA_PASS`` / ``IDEA_TOKEN``）；
    * **实例地址里的 userinfo** —— 有人会把密码写进 URL，
      那时它不在环境变量里，但也绝不能进 stderr。
    """
    values: List[str] = []

    for name in ("IDEA_USER", "IDEA_PASS", "IDEA_TOKEN"):
        raw = os.environ.get(name, "")
        for value in (raw, raw.strip()):
            if value:
                values.append(value)

    # 实际发出去的那串 basic auth：请求头若被回显，它就是凭据
    # （base64 可逆 —— 只掩裸密码、放过 base64 等于掩了个寂寞）。
    user = os.environ.get("IDEA_USER", "").strip()
    password = os.environ.get("IDEA_PASS", "").strip()
    if user and password:
        raw = "{}:{}".format(user, password).encode("utf-8")
        values.append(base64.b64encode(raw).decode("ascii"))

    url_password = _password_in_base_url()
    if url_password:
        values.append(url_password)

    # 长的先掩：短的可能是长的的一部分（先掩短的会把长的拆成两半）。
    return sorted(set(values), key=len, reverse=True)


def _password_in_base_url() -> str:
    """实例地址的 userinfo 里带的密码（没有就返回空串）。

    userinfo 是 ``user:password``，按**第一个**冒号切（RFC 里的规矩：
    用户名里不能有冒号，密码里可以）。

    地址要先去掉首尾空白 —— 真正发出去的地址也是去空白的那个，
    不然 ``" http://u:p@host"`` 里的密码就漏了（它带前导空格，
    正则锚在开头就匹配不上）。
    """
    raw = os.environ.get("IDEA_BASE_URL", "").strip()
    match = BASE_URL_USERINFO.match(raw)
    if not match:
        return ""

    userinfo = match.group("userinfo")
    if ":" not in userinfo:
        return ""

    return userinfo.split(":", 1)[1]


# --------------------------------------------------------------------------
# 环境与地址
# --------------------------------------------------------------------------


def resolve_base_url() -> str:
    """定下目标地址。

    校验放在这里：缺 scheme 的地址到 urllib 手上会变成 ``ValueError``
    （裸 traceback）。「地址写错」是最常犯的配置错，比「代理返回 HTML」常见得多 ——
    所以本地就说清楚。
    """
    raw = os.environ.get("IDEA_BASE_URL", "").strip()
    if not raw:
        raise ConfigError("missing_config", "IDEA_BASE_URL is required")

    try:
        parts = urllib.parse.urlsplit(raw)
        host = parts.hostname
    except ValueError:
        parts = None
        host = None

    if parts is None or parts.scheme.lower() not in ("http", "https") or not host:
        raise ConfigError(
            "invalid_config",
            "IDEA_BASE_URL 不是合法的实例地址：{}（要带 scheme，如 https://ideas.example.com）".format(
                _redact_base_url(raw)
            ),
        )

    return raw.rstrip("/")


def _redact_base_url(raw: str) -> str:
    """回显实例地址时把 userinfo 抹掉（有人会把密码写在 URL 里）。

    按 URL 结构来（先分出 scheme，再看 authority 里有没有 '@'），
    不是按最后一个 '@' 切 —— 按 '@' 切会把路径里的 '@'（http://host/a@b）
    误当成 userinfo。
    """
    match = BASE_URL_USERINFO.match(raw)
    if not match:
        return raw

    scheme = match.group("scheme") or ""
    return "{}***@{}".format(scheme, raw[match.end():])


def resolve_cache_root() -> str:
    """定下缓存根目录。

    ``IDEA_SKILL_CACHE_DIR`` 优先（测试用它把缓存放到临时目录里），
    否则 ``$XDG_CACHE_HOME/idea-skill``，再否则 ``~/.cache/idea-skill``。
    """
    override = os.environ.get("IDEA_SKILL_CACHE_DIR", "").strip()
    if override:
        return os.path.abspath(os.path.expanduser(override))

    xdg = os.environ.get("XDG_CACHE_HOME", "").strip()
    if xdg:
        return os.path.join(os.path.abspath(os.path.expanduser(xdg)), CACHE_DIR_NAME)

    return os.path.join(os.path.expanduser("~"), ".cache", CACHE_DIR_NAME)


# --------------------------------------------------------------------------
# HTTP 层
# --------------------------------------------------------------------------


def build_request(url: str) -> urllib.request.Request:
    """造一个 GET 请求。

    认证：
    * IDEA_USER 与 IDEA_PASS 都非空 → Authorization: Basic base64(user:pass)；
    * IDEA_TOKEN 非空 → X-Api-Key 头。

    凭据只从环境变量读，命令行不接受密码 —— 密码出现在进程参数里
    会被同机上的其它进程看见（ps），也会进 agent 的命令日志。
    """
    headers = {"Accept": "*/*"}

    user = os.environ.get("IDEA_USER", "").strip()
    password = os.environ.get("IDEA_PASS", "").strip()
    if user and password:
        raw = "{}:{}".format(user, password).encode("utf-8")
        headers["Authorization"] = "Basic " + base64.b64encode(raw).decode("ascii")

    token = os.environ.get("IDEA_TOKEN", "").strip()
    if token:
        headers["X-Api-Key"] = token

    return urllib.request.Request(url, headers=headers, method="GET")


def fetch_bytes(url: str) -> bytes:
    """GET 一个 URL，返回响应体字节。

    非 2xx → 抛对应的错误（401/403 → auth_error，404 → not_found，
    其它 → 服务端的 code 或 ``http_<状态码>``）；
    网络层失败 → 抛 :class:`NetworkError`。
    """
    request = build_request(url)

    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS) as resp:
            return resp.read()
    except urllib.error.HTTPError as exc:
        raw = b""
        try:
            raw = exc.read()
        except Exception:  # noqa: BLE001 —— 读不到 body 也要把状态码报出去
            raw = b""
        raise _error_for_status(exc.code, raw)
    except urllib.error.URLError as exc:
        raise NetworkError(_one_line(exc.reason))
    except http.client.HTTPException as exc:
        # 传输中断（响应体没读全、连接被掐断……）：
        # 这是网络层的失败，不是「服务端说了什么」——归 network_error。
        raise NetworkError(_one_line(exc))
    except ValueError as exc:
        # urllib 对畸形地址/端口的抱怨（"nonnumeric port: …"）：
        # 请求层的问题，不是内部 bug —— 归 network_error。
        # （不同 Python 版本抛的异常类型不一样，这里都收着。）
        raise NetworkError(_one_line(exc))
    except (TimeoutError, OSError) as exc:
        raise NetworkError(_one_line(exc))


def _error_for_status(status: int, raw: bytes) -> CliError:
    """把一个非 2xx 响应变成异常。

    404 要分开看：服务端说「没分发 skill 内容」与「文件不存在」是两回事，
    装 skill 的人该知道该去查哪一头 —— 所以认服务端的 ``code``，
    认不出来才退回通用的 ``not_found``。
    """
    code, detail = _problem_fields(raw)

    if status in (401, 403):
        return AuthError(detail or "服务端拒绝了鉴权（用户名/密码或 token 不对）")

    if status == 404:
        if code == "skill_not_distributed":
            return CliError("skill_not_distributed", detail or "服务端没有分发 skill 内容")
        return NotFoundError(detail or "要取的文件不存在")

    if code:
        return CliError(code, detail or "HTTP {}".format(status))

    return CliError("http_{}".format(status), detail or "HTTP {}".format(status))


def _problem_fields(raw: bytes) -> Tuple[Optional[str], str]:
    """从响应体里读出 (code, detail)。不是 JSON 就当没有。

    服务端的错误是 RFC 9457 的 ProblemDetails，``code`` 在**顶层**
    （不是嵌在 ``extensions`` 里）。
    """
    if not raw.strip():
        return None, ""

    try:
        payload = json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None, ""

    if not isinstance(payload, dict):
        return None, ""

    code = payload.get("code")
    detail = payload.get("detail") or payload.get("title") or ""
    return (str(code) if code else None), str(detail)


# --------------------------------------------------------------------------
# 清单
# --------------------------------------------------------------------------


class Manifest(object):
    """服务端的清单：版本、入口、逐个文件的哈希。"""

    def __init__(self, version: str, entry: str, files: List[Dict[str, str]]) -> None:
        self.version = version
        self.entry = entry
        self.files = files

    @property
    def paths(self) -> List[str]:
        return [item["path"] for item in self.files]


def fetch_manifest(base_url: str) -> Manifest:
    """取清单并校验它的形状。"""
    url = base_url + MANIFEST_PATH
    raw = fetch_bytes(url)

    try:
        payload = json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        raise InvalidResponse("清单不是 JSON（多半是代理返回了 HTML）")

    if not isinstance(payload, dict):
        raise InvalidResponse("清单不是 JSON 对象")

    return _parse_manifest(payload)


def _parse_manifest(payload: Dict[str, Any]) -> Manifest:
    """校验清单的字段。

    清单是**服务端给的输入**，跟请求头一样不可信：
    版本串会变成目录名、文件路径会变成缓存里的文件名，
    所以这里把不合法的一律挡掉（挡不住就是写到别处去了）。
    """
    version = payload.get("version")
    if not isinstance(version, str) or not VERSION_PATTERN.match(version):
        raise InvalidResponse("清单里的 version 不合法")

    entry = payload.get("entry")
    if not isinstance(entry, str) or not entry:
        raise InvalidResponse("清单里没有 entry")

    files = payload.get("files")
    if not isinstance(files, list) or not files:
        raise InvalidResponse("清单里没有 files")

    parsed: List[Dict[str, str]] = []
    for item in files:
        if not isinstance(item, dict):
            raise InvalidResponse("清单里的 files 项不是对象")

        path = item.get("path")
        digest = item.get("sha256")
        if not isinstance(path, str) or not isinstance(digest, str):
            raise InvalidResponse("清单里的文件项缺 path 或 sha256")

        _check_relative_path(path)

        if not SHA256_PATTERN.match(digest):
            raise InvalidResponse("清单里 {} 的 sha256 不合法".format(path))

        parsed.append({"path": path, "sha256": digest.lower()})

    if entry not in [item["path"] for item in parsed]:
        raise InvalidResponse("清单里的 entry 不在 files 里")

    return Manifest(version, entry, parsed)


def _check_relative_path(path: str) -> None:
    """清单里的路径必须是干净的相对路径。

    绝对路径与含 ``..`` 段的路径一律不认：它们会跑到分发副本之外去。
    """
    if not path or path.startswith("/") or "\\" in path:
        raise InvalidResponse("清单里的路径不是相对路径：{}".format(path))

    for segment in path.split("/"):
        if segment in ("", ".", ".."):
            raise InvalidResponse("清单里的路径不干净：{}".format(path))


# --------------------------------------------------------------------------
# 同步
# --------------------------------------------------------------------------


def sync(base_url: str, cache_root: str) -> Tuple[Manifest, str]:
    """把服务端的分发副本同步到本地缓存，返回 (清单, 缓存目录)。

    同步是**整体**的：每个文件都下载并逐个校验 sha256，
    全部通过才把临时目录改名成版本目录。
    中途失败 → 临时目录删掉，旧版本原样留着（不会半新半旧）。
    """
    manifest = fetch_manifest(base_url)
    target = os.path.join(cache_root, manifest.version)

    if _cache_matches(target, manifest):
        # 缓存里已经是这一版了，直接用。
        _prune(cache_root, target)
        return manifest, target

    os.makedirs(cache_root, exist_ok=True)

    # 临时目录带上 pid：两个进程同时取件时，第二个不能删掉第一个正在写的目录。
    # 名字还是「同目录下的临时目录」——改名落位的原子性不受影响。
    temporary = os.path.join(
        cache_root, TEMP_PREFIX + manifest.version + "-" + str(os.getpid())
    )

    # 上一次崩了可能留下半个临时目录：从干净状态重来。
    _remove_path(temporary)
    os.makedirs(temporary)

    try:
        for item in manifest.files:
            relative = item["path"]
            raw = download_file(base_url, relative)
            path = _write_file(temporary, relative, raw)

            # 校验的是**落盘之后**的字节：写了一半的文件、或者传输中被改过的内容，
            # 都会在这里露出来。
            digest = _sha256_of_file(path)
            if digest != item["sha256"]:
                raise IntegrityError(
                    "文件哈希对不上：{}（清单说 {}，取回来的是 {}）".format(
                        relative, item["sha256"], digest
                    )
                )

        # 清单快照随副本一起改名落位：离线降级时靠它知道入口页是哪个。
        _write_file(
            temporary,
            MANIFEST_SNAPSHOT,
            json.dumps(
                {
                    "version": manifest.version,
                    "entry": manifest.entry,
                    "files": manifest.files,
                },
                ensure_ascii=False,
                indent=2,
            ).encode("utf-8"),
        )

        # 同目录下的 rename 是原子的：缓存目录**整体**切换。
        # 目标已存在（上一版缓存损坏了要重来）→ 先删掉再改名。
        _remove_path(target)
        os.replace(temporary, target)
    except BaseException:
        _remove_path(temporary)
        raise

    _prune(cache_root, target)
    return manifest, target


def download_file(base_url: str, relative: str) -> bytes:
    """取分发副本里的一个文件。"""
    url = base_url + CONTENT_PREFIX + urllib.parse.quote(relative, safe="/")
    return fetch_bytes(url)


def _write_file(root: str, relative: str, raw: bytes) -> str:
    """把内容写进缓存目录，返回落盘后的路径。"""
    path = os.path.join(root, *relative.split("/"))
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(raw)
    return path


def _sha256_of_file(path: str) -> str:
    """文件字节的 sha256（小写十六进制）——与清单里的口径一致。"""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _cache_matches(target: str, manifest: Manifest) -> bool:
    """缓存目录里是不是已经有这一版，而且完好。

    「完好」= 清单里的每个文件都在，哈希也对得上。
    哈希要重算一遍：本地缓存也可能被别的东西改过（磁盘故障、别的进程、
    手滑）—— 与其打印一份坏内容，不如重新同步一次。
    """
    if not os.path.isdir(target):
        return False

    for item in manifest.files:
        path = os.path.join(target, *item["path"].split("/"))
        if not os.path.isfile(path):
            return False
        try:
            if _sha256_of_file(path) != item["sha256"]:
                return False
        except OSError:
            return False

    return True


def _prune(cache_root: str, keep: str) -> None:
    """旧版本目录按 mtime 保留最近 :data:`KEEP_VERSIONS` 个，其余删掉。

    留着旧版本是为了「同步失败时还有旧版可退」；留太多又白占地方。
    当前版本一定留着 —— 它是刚同步好的那份。
    """
    if not os.path.isdir(cache_root):
        return

    versions = []
    for name in os.listdir(cache_root):
        path = os.path.join(cache_root, name)
        # 以点开头的不是版本目录（临时目录与快照），不管它。
        if name.startswith(".") or not os.path.isdir(path):
            continue
        versions.append(path)

    try:
        versions.sort(key=os.path.getmtime, reverse=True)
    except OSError:
        return

    survivors = {keep}
    for path in versions:
        if path == keep:
            continue
        survivors.add(path)
        if len(survivors) >= KEEP_VERSIONS:
            break

    for path in versions:
        if path not in survivors:
            _remove_path(path)

    _prune_temporaries(cache_root)


def _prune_temporaries(cache_root: str) -> None:
    """清掉崩溃留下的临时目录（``.tmp-<version>-<pid>``）。

    临时目录名里带着 pid：两个进程并发取件时不会互删对方的目录。
    但进程崩了就会留下一个没人管的目录 —— 长期跑会在缓存根目录堆积孤儿。

    判据是「主人还在不在」而不是「新旧」：主人没了的目录就是残骸
    （刚崩的残骸也是新的，但没人要它）；主人还活着的目录有人在写，
    不能动。pid 认不出来的（旧版固定名 ``.tmp-<version>``）一律当残骸。
    """
    for name in os.listdir(cache_root):
        if not name.startswith(TEMP_PREFIX):
            continue

        path = os.path.join(cache_root, name)
        if not os.path.isdir(path) or os.path.islink(path):
            continue

        pid = _pid_of(path)
        if pid is not None and _process_alive(pid):
            continue

        _remove_path(path)


def _pid_of(path: str) -> Optional[int]:
    """从临时目录名里取出 pid（取不出就返回 None）。"""
    name = os.path.basename(path)
    tail = name[len(TEMP_PREFIX):]
    _, _, pid_text = tail.rpartition("-")
    try:
        return int(pid_text)
    except ValueError:
        return None


def _process_alive(pid: int) -> bool:
    """那个进程还在吗（0 号信号只探活，不发信号）。"""
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except OSError:
        # 别的用户的进程：探不到，但多半还活着 → 当活着处理。
        return True
    return True


def _remove_path(path: str) -> None:
    """删掉一个路径（目录或文件），出错也不抛。

    清理缓存是「尽力而为」：删不掉就让它留在那儿，
    不能因为一个删不掉的文件就让整次同步失败。
    """
    try:
        if os.path.isdir(path) and not os.path.islink(path):
            shutil.rmtree(path, ignore_errors=True)
        elif os.path.exists(path) or os.path.islink(path):
            os.remove(path)
    except OSError:
        pass


def load_cached_manifest(cache_root: str) -> Optional[Tuple[Manifest, str]]:
    """离线降级用：找到最近的那个**完好**的缓存副本。

    取不到清单时（网络断了），本地有缓存就还能继续干活 ——
    这比「整个用不了」强。返回 None 表示没有可用的缓存。
    """
    if not os.path.isdir(cache_root):
        return None

    candidates = []
    for name in os.listdir(cache_root):
        path = os.path.join(cache_root, name)
        if name.startswith(".") or not os.path.isdir(path):
            continue
        candidates.append(path)

    try:
        candidates.sort(key=os.path.getmtime, reverse=True)
    except OSError:
        return None

    for path in candidates:
        snapshot = os.path.join(path, MANIFEST_SNAPSHOT)
        if not os.path.isfile(snapshot):
            continue

        try:
            with open(snapshot, "rb") as handle:
                payload = json.loads(handle.read().decode("utf-8"))
            manifest = _parse_manifest(payload)
        except (CliError, ValueError, UnicodeDecodeError, OSError):
            continue

        if _cache_matches(path, manifest):
            return manifest, path

    return None


# --------------------------------------------------------------------------
# 打印
# --------------------------------------------------------------------------


def resolve_target(manifest: Manifest, relative: Optional[str]) -> str:
    """定下要取哪一份文件。

    不给参数就是入口页（清单里的 ``entry``）；给了相对路径，
    那它必须**在清单里**：清单之外的文件不在分发副本内，
    也不该被当成说明书的一部分。
    """
    target = relative if relative else manifest.entry

    # "./references/x.md" 与 "references/x.md" 指的是同一个文件：
    # 归一化掉前缀，别让一个手滑的点号变成 not_found。
    if target.startswith("./"):
        target = target[2:]

    if target not in manifest.paths:
        raise NotFoundError("分发副本里没有这个文件：{}".format(target))

    return target


def print_from_cache(cache_dir: str, manifest: Manifest, relative: Optional[str]) -> None:
    """打印缓存里的一份文件。"""
    print_cached_file(cache_dir, resolve_target(manifest, relative))


def print_cached_file(cache_dir: str, target: str) -> None:
    """把缓存里的那一份文件打到 stdout。"""
    path = os.path.join(cache_dir, *target.split("/"))
    try:
        with open(path, "rb") as handle:
            raw = handle.read()
    except OSError as exc:
        raise NotFoundError("缓存里没有这个文件：{}（{}）".format(target, _one_line(exc)))

    _write_stdout(raw)


def print_from_server(base_url: str, path: str) -> None:
    """打印服务端某个绝对路径的原文（如 ``/openapi/v1.json``）。"""
    url = base_url + urllib.parse.quote(path, safe="/")
    _write_stdout(fetch_bytes(url))


def _write_stdout(raw: bytes) -> None:
    """把字节原样写到 stdout。

    走 ``buffer``：内容是服务端的原文，不要让 Python 的编码设置改它
    （换行转换也不能有）。
    """
    sys.stdout.buffer.write(raw)
    sys.stdout.buffer.flush()


def _write_stderr(line: str) -> None:
    """往 stderr 写一行（UTF-8）。

    同样走 ``buffer``：错误信息里有中文，不能让 locale 决定它能不能写出去。

    **这里不再脱敏**：传进来的整行里带着错误码，而错误码是 agent 要拿来
    分支的（``network_error: …`` 里的 network_error 不能被改成 network_***）。
    脱敏只在 detail 段做 —— 见 :meth:`CliError.render` 与各调用点。
    """
    sys.stderr.buffer.write((line + "\n").encode("utf-8"))
    sys.stderr.buffer.flush()


# --------------------------------------------------------------------------
# 参数与入口
# --------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="fetch.py",
        description="取 idea skill 的说明书（内容在服务端，本地只有缓存）",
    )
    parser.add_argument(
        "path",
        nargs="?",
        help=(
            "要取的内容：不给则取索引页；相对路径取分发副本里的那一份；"
            "以 / 开头的绝对路径直接取服务端的那个路径"
        ),
    )
    return parser


def main(argv: Optional[List[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        base_url = resolve_base_url()

        # 绝对路径：直连服务端要那一份原文，不走分发副本。
        if args.path and args.path.startswith("/"):
            print_from_server(base_url, args.path)
            return 0

        cache_root = resolve_cache_root()
        degraded = ""

        try:
            manifest, cache_dir = sync(base_url, cache_root)
        except NetworkError as exc:
            # 网络不通（取不到清单，或同步到一半断了）：
            # 本地有**完好**的缓存就还能干活（降级），没有就报错退出。
            # 缓存里留着旧版本就是为了这一刻 —— 它是完整的，不会半新半旧。
            cached = load_cached_manifest(cache_root)
            if cached is None:
                raise
            manifest, cache_dir = cached
            degraded = exc.code

        if not degraded:
            print_from_cache(cache_dir, manifest, args.path)
            return 0

        # 降级：先确认缓存里有没有要取的东西。
        # 取不到 → 只打那一行**错误**（把「本次是离线降级」写进 detail），
        # 不再另打一行 warning —— 错误输出只该有一行。
        try:
            target = resolve_target(manifest, args.path)
        except CliError as exc:
            raise CliError(exc.code, "（本次是离线降级，用的是本地缓存）{}".format(exc.detail))

        _write_stderr(
            "warning: 同步失败（{}），改用本地缓存 {}".format(
                degraded, _redact(os.path.basename(cache_dir))
            )
        )
        print_cached_file(cache_dir, target)
    except CliError as exc:
        _write_stderr(exc.render())
        return exc.exit_code
    except Exception as exc:  # noqa: BLE001 —— 兜底：干净地失败，别吐 traceback
        # 未预期的异常也要守住「stderr 一行 + 退出码非 0」的契约：
        # agent 侧要的是能读的一行，真实原因靠报告与服务端日志定位。
        # 脱敏只作用在 detail 段：code 段（internal_error）不能被改写。
        _write_stderr("internal_error: {}".format(_redact(_one_line(exc))))
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
