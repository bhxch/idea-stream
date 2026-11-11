# Idea 管理工具 · Agent Skill 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付一个可直接安装的 agent skill，把 idea 记事本的 REST API 包成命令行工具 + 说明书，让 agent 不必手写 HTTP 请求就能管理想法。

**Architecture:** 纯标准库 Python 脚本（`urllib`），无第三方依赖，任何装了 Python 3.9+ 的机器都能跑。命令走 argparse 子命令树，默认输出 JSON，错误把 ProblemDetails 的 `code`/`detail` 打到 stderr 并以非 0 退出。`references/openapi.json` 是 `openapi/idea-v1.json` 的副本，供 agent 阅读完整契约。

**Tech Stack:** Python 3.9+ 标准库（argparse / json / urllib.request / base64 / os / sys）/ unittest

**Spec:** `docs/superpowers/specs/2026-11-11-idea-manager-design.md` §13、§17

**Depends on:** `docs/superpowers/plans/2026-11-11-idea-backend.md` 必须先完成（`openapi/idea-v1.json` 由后端计划 Task 15 产出）。

## Global Constraints

- **只用 Python 标准库**，不引入 requests / click / pydantic。目标解释器 **Python 3.9+**（不得用 3.10+ 语法，如 `match`、PEP 604 的 `X | Y` 类型注解在运行时求值的写法）。
- 脚本必须可直接执行：`chmod +x`，shebang `#!/usr/bin/env python3`。
- 连接配置只从环境变量读，**不读命令行明文密码**：`IDEA_BASE_URL`、`IDEA_USER`、`IDEA_PASS`、`IDEA_TOKEN`。
- 默认输出 JSON（`--format json`，可省略）；`--format text` 给人类看。
- 错误处理：非 2xx 解析 ProblemDetails，stderr 打印 `code: detail`，退出码 1；网络错误 stderr 打印 `network_error: ...`，退出码 2。
- **skill 目录不得包含任何真实凭据**（bot token、密码）。
- 每个 task 结束提交一次，commit message 用 Conventional Commits（中文正文）。

## Review Focus

1. **凭据泄漏** —— 脚本报错时不能把 `IDEA_PASS` 或 basic auth 头打进 stderr/日志；`--help` 输出里不能有密码。
2. **特殊字符** —— 正文含引号、换行、反斜杠、`<b>` 时，`add` / `update` / `merge` 必须原样送达并原样取回。
3. **错误码透传** —— 服务端返回 `merge_cross_project` 等 `code` 时，脚本必须把这个 code 原样打出来，不能笼统报「请求失败」。
4. **退出码** —— 失败必须非 0 退出，否则 agent 会把失败当成功继续往下走。
5. **环境缺失** —— 没设 `IDEA_BASE_URL` 时要给出明确提示，而不是抛 traceback。

---

## 文件结构

```
skills/idea-manager/
  SKILL.md                     # 说明书（frontmatter + 用法）
  scripts/idea.py              # 命令行封装（可执行）
  scripts/run-tests.sh         # 起后端 + 跑端到端测试
  references/openapi.json      # openapi/idea-v1.json 的副本
  tests/test_cli.py            # unittest 端到端测试
```

---

### Task S1: 接口契约副本与 skill 骨架

**Files:**
- Create: `skills/idea-manager/references/openapi.json`
- Create: `skills/idea-manager/SKILL.md`
- Create: `skills/idea-manager/scripts/run-tests.sh`

**Interfaces:**
- Consumes: `openapi/idea-v1.json`（后端计划 Task 15 产出）。
- Produces:
  - `skills/idea-manager/SKILL.md`，frontmatter **必须**是：
    ```yaml
    ---
    name: idea-manager
    description: 管理本地 idea 记事本（项目、想法、标签、完成状态、合并、回收站、webhook 通知）。当用户要记录、查看、整理、批量操作 idea/想法，或要配置 Telegram 通知时使用。
    ---
    ```
    正文含：能力清单表（子命令 → 作用 → 对应 API 路径）、典型场景示例（记一条想法、查未完成、按标签筛、批量标记完成、合并几条、恢复删除的）、环境变量说明、错误码对照表、指向 `references/openapi.json` 的说明。
  - `skills/idea-manager/scripts/run-tests.sh`：可执行脚本，职责为
    1. `dotnet run --project server/IdeaApp` 起后端（后台），`IDEA_Database__Path` 指向临时库；
    2. 轮询 `http://127.0.0.1:5080/api/health` 直到 200（最多 60 秒）；
    3. `IDEA_BASE_URL=http://127.0.0.1:5080 python3 -m unittest discover -s skills/idea-manager/tests -v`；
    4. 无论成败都杀掉后端进程；
    5. 用后端退出码作为脚本退出码。

- [ ] **Step 1: 复制契约**

```bash
mkdir -p skills/idea-manager/references skills/idea-manager/scripts skills/idea-manager/tests
cp openapi/idea-v1.json skills/idea-manager/references/openapi.json
```

- [ ] **Step 2: 写 SKILL.md**

按上面的 frontmatter 与正文结构写。**能力清单必须覆盖这些子命令**（与 Task S2 的 argparse 定义逐字一致）：

`projects`、`project add|rename|delete|restore`、`list`、`add`、`update`、`tags`、`done`、`delete`、`restore`、`merge`、`bulk done|tags|delete`、`trash`、`taglist`、`webhook list|add|update|delete`、`telegram-template`

- [ ] **Step 3: 写 run-tests.sh**

```bash
#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
DB="$(mktemp -d)/skill-test.db"
IDEA_Database__Path="$DB" IDEA_Urls=http://127.0.0.1:5080 \
  dotnet run --project "$ROOT/server/IdeaApp" --no-launch-profile &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do
  curl -sf http://127.0.0.1:5080/api/health >/dev/null && break
  sleep 1
done
IDEA_BASE_URL=http://127.0.0.1:5080 python3 -m unittest discover -s "$ROOT/skills/idea-manager/tests" -v
```

- [ ] **Step 4: 提交**

```bash
git add skills/
git commit -m "feat(skill): 搭建 agent skill 骨架与接口契约副本"
```

---

### Task S2: 命令行封装 scripts/idea.py

**Files:**
- Create: `skills/idea-manager/scripts/idea.py`
- Create: `skills/idea-manager/tests/test_cli.py`

**Interfaces:**
- Consumes: 后端计划 Task 13/14/15 的端点契约。
- Produces: `skills/idea-manager/scripts/idea.py`，可执行。argparse 子命令树固定为：

```
idea.py projects
idea.py project add <name>
idea.py project rename <id> <name>
idea.py project delete <id>
idea.py project restore <id>
idea.py list [--project <id>] [--status all|open|done] [--tags a,b] [--tag-mode or|and]
idea.py add <content> [--project <id>] [--title <title>] [--tags a,b]
idea.py update <id> [--content <content>] [--title <title>]
idea.py tags <id> <tags-csv>
idea.py done <id> [--undo]
idea.py delete <id>
idea.py restore <id>
idea.py merge <id> [<id> ...] [--content <content>] [--title <title>]
idea.py bulk done <id> [<id> ...] [--undo]
idea.py bulk tags <id> [<id> ...] <tags-csv>
idea.py bulk delete <id> [<id> ...]
idea.py trash
idea.py taglist
idea.py webhook list [--project <id>]
idea.py webhook add --name <name> --url <url> [--body <template>] [--headers <k=v,...>]
                     [--events <e1,e2>] [--scope global|project] [--project <id>] [--secret <s>]
idea.py webhook update <id> [--name <name>] [--url <url>] [--body <template>]
                        [--headers <k=v,...>] [--events <e1,e2>] [--enable|--disable] [--secret <s>]
idea.py webhook delete <id>
idea.py telegram-template
```

全局选项：`--format json|text`（默认 json）、`--base-url <url>`（覆盖 `IDEA_BASE_URL`）。

内部函数签名固定：

```python
def build_request(method: str, path: str, body: Optional[dict] = None) -> Request
def call(req: Request) -> Any            # 2xx 返回解析后的 JSON 或 None；非 2xx 抛 ApiError
def env_or_die(name: str) -> str
```

- [ ] **Step 1: 写失败测试**

`skills/idea-manager/tests/test_cli.py`（unittest，`IDEA_BASE_URL` 指向的实例不可达时整体 `skipTest`）：

```python
class IdeaCliTest(unittest.TestCase):
    def test_health_via_list(self):        # list 返回数组且可解析
    def test_add_and_get_back(self):       # add 后 list 里能看到，content 逐字相同
    def test_special_characters_roundtrip(self):
        # content = '引号 " 反斜杠 \\ 换行\n<b>标签</b>' → add 后取回逐字相同
    def test_done_marks_completed(self):   # done 后 list --status done 能查到
    def test_tags_filter(self):            # 打标签后按标签筛能查到
    def test_merge_creates_new_idea(self): # merge 两条 → 结果 content == "A\n\nB"，源 id 不再出现
    def test_merge_cross_project_error_code(self):
        # 跨项目合并 → 退出码 1，stderr 含 "merge_cross_project"
    def test_delete_then_restore(self):    # delete → trash 里有 → restore → trash 里没有
    def test_telegram_template_contains_thread_id(self):
        # telegram-template 输出含 message_thread_id 与 {{idea.content}}
    def test_missing_base_url_exits_with_hint(self):
        # 清掉 IDEA_BASE_URL 且不传 --base-url → 退出码 1，stderr 含 "IDEA_BASE_URL"
    def test_error_prints_code_and_detail(self):
        # 对不存在的 id 操作 → 退出码 1，stderr 含 code 与 detail
```

- [ ] **Step 2: 运行测试确认失败**

Run: `bash skills/idea-manager/scripts/run-tests.sh` → Expected: FAIL（`idea.py` 不存在）

- [ ] **Step 3: 实现**

要点：

- 用 `urllib.request` 构造请求；basic auth 用 `base64.b64encode(f"{user}:{pass}".encode())` 加 `Authorization: Basic ...` 头。
- `--base-url` 优先于 `IDEA_BASE_URL`；两者都缺 → 打印 `missing_config: IDEA_BASE_URL is required` 到 stderr，退出码 1。
- `IDEA_TOKEN` 存在时加 `X-Api-Key` 头。
- `tags` / `events` 的逗号串解析成列表并去空；`--tags a,b` 传数组。
- `bulk tags` 的参数顺序：`bulk tags <id...> <tags-csv>` —— 用 `nargs='+'` 收 ids 后，把**最后一个**参数当标签串，其余当 ids。
- `merge` 用 `nargs='+'` 收 ids；`--content` 省略则不传该字段，让服务端拼接。
- 输出：成功时把响应 JSON `json.dumps(..., ensure_ascii=False, indent=2)` 打到 stdout。`--format text` 时对列表输出简短摘要（每行 `id  标题或正文首行`），对单条输出字段摘要。
- 错误：`ApiError` 携带 ProblemDetails；stderr 打 `f"{code}: {detail}"`，退出码 1。网络异常退出码 2。
- **不要**在任何输出里打印 `IDEA_PASS` 或 Authorization 头。

- [ ] **Step 4: 运行测试确认通过**

Run: `bash skills/idea-manager/scripts/run-tests.sh` → Expected: 全部 PASS

- [ ] **Step 5: 提交**

```bash
git add skills/
git commit -m "feat(skill): 实现 idea 命令行封装与端到端测试"
```

---

### Task S3: 说明书完善、安装说明与整体核对

**Files:**
- Modify: `skills/idea-manager/SKILL.md`
- Modify: `README.md`（根）

**Interfaces:**
- Produces: 安装说明 —— skill 目录可直接软链到 `~/.claude/skills/idea-manager` 或 `~/.zcode/skills/idea-manager`。

- [ ] **Step 1: 补齐 SKILL.md 的错误码对照表**

必须包含这些 `code`（**这是后端代码里实际用到的全集**，`grep -rhoP 'ApiException\(\s*\d+,\s*"\K[a-z_]+' server/IdeaApp/ | sort -u` 可以核对）：

| code | 含义 | agent 该怎么办 |
|---|---|---|
| `idea_not_found` | 想法不存在 | 重新拉列表，可能是别的设备删了 |
| `project_not_found` | 项目不存在 | 同上 |
| `inbox_not_deletable` | 内置收件箱不能删 | 换个项目 |
| `invalid_content` | 正文为空或全空白 | 补上正文再提交 |
| `invalid_project_name` | 项目名为空或全空白 | 补上名字 |
| `invalid_reorder_ids` | 重排的 ids 为空 | 别发空的 reorder |
| `invalid_status` | `status` 参数不是 all/open/done | 用这三个值之一 |
| `invalid_tag_mode` | `tagMode` 不是 or/and | 用这两个值之一 |
| `invalid_merge_selection` | 合并时少于两条 | 至少选两条 |
| `merge_cross_project` | 合并的想法不属于同一项目 | 先把它们移到同一项目 |
| `not_in_trash` | 要恢复的东西不在回收站里 | 先拉 `GET /api/trash` 确认 |
| `too_many_ideas` | 命中数超过 `Api:MaxIdeasPerResponse`（默认 2000） | 收窄过滤条件（加 projectId / status / tags）|

Task 11 会新增 webhook 相关的 `code`，届时补进这张表。

每个 code 一行：`code` → 含义 → agent 该怎么办。

- [ ] **Step 2: 补齐根 README 的 skill 安装段落**

内容：`cp -r skills/idea-manager ~/.zcode/skills/`（或 `~/.claude/skills/`）、需要设置的三个环境变量、一次自检命令 `python3 skills/idea-manager/scripts/idea.py list`。

- [ ] **Step 3: 端到端核对**

Run: `bash skills/idea-manager/scripts/run-tests.sh` → Expected: PASS
Run: `IDEA_BASE_URL=http://127.0.0.1:5080 python3 skills/idea-manager/scripts/idea.py telegram-template` → Expected: 输出含 `message_thread_id`
Run: `grep -rn "IDEA_PASS\|Authorization" skills/idea-manager/SKILL.md` → Expected: 无输出（说明书里不出现密码字段的示例值）
Run: `grep -rniE "bot[0-9]{6,}:" skills/ openapi/ README.md` → Expected: 无输出（仓库里没有真实 bot token）

- [ ] **Step 4: 提交**

```bash
git add skills/ README.md
git commit -m "docs(skill): 完善说明书、错误码对照与安装说明"
```
