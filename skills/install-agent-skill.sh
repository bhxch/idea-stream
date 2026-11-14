#!/usr/bin/env bash
#
# 把 agent 侧的 skill（薄壳 + 取件器）装到 agent 的 skills 目录。
#
# 装的是**拷贝**，不是软链：薄壳（skills/idea-manager-client/）与内容源
# （skills/idea-manager/）是两个东西 —— 软链内容源会把里面的索引页
# 当成薄壳装过去，agent 手上就又握着一份会过期的副本。
#
# **目标目录必须是一个 skill 目录**：里面有 SKILL.md，或者目录名就叫
# idea-manager。脚本要删的是「旧形状 skill 的确定残留」
# （references/、tests/、scripts/idea.py……），而 references 与 tests
# 是极常见的目录名 —— 对着家目录跑一遍脚本就会把它们删掉。
# 所以：**先确认目标像 skill 目录，再删**；不像就只警告、不删。
#
# 旧的安装说明教的是 `cp -r skills/idea-manager ~/.zcode/skills/`，
# 装出来正是同一个目录 —— 里面会剩下旧的 references/ 与旧的命令层脚本。
# 留着它们，薄壳说的「本目录只有两个文件」当场不成立，agent 也可能直接
# 去读那份过期手册（正是这套改造要消除的东西）。所以在 skill 目录里
# 把**旧形状的确定残留**删掉；其它不认识的条目只警告、不删。
#
# 脚本自己不读也不写凭据：连接配置由 agent 运行时从环境变量读
# （取件器的命令行不接受密码）。
#
# 用法：bash skills/install-agent-skill.sh [目标目录]
#       bash skills/install-agent-skill.sh --help
#       默认目标：~/.zcode/skills/idea-manager
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="${ROOT}/skills/idea-manager-client"

usage() {
  cat <<'USAGE'
用法：bash skills/install-agent-skill.sh [目标目录]

  把 agent 侧的薄壳（SKILL.md + scripts/fetch.py）装到目标目录。

  目标目录必须是一个 skill 目录：里面有 SKILL.md，或者目录名就叫 idea-manager。
  不像 skill 目录时，脚本只警告，不会删掉里面的任何东西。

  默认目标：~/.zcode/skills/idea-manager

例子：
  bash skills/install-agent-skill.sh
  bash skills/install-agent-skill.sh ~/.claude/skills/idea-manager
USAGE
}

case "${1:-}" in
  -h | --help)
    usage
    exit 0
    ;;
esac

if [ "$#" -ge 1 ]; then
  TARGET="$1"
elif [ -n "${HOME:-}" ]; then
  TARGET="${HOME}/.zcode/skills/idea-manager"
else
  # HOME 没设（set -u 下 ${HOME} 会直接 unbound variable 退出）：
  # 与其那样崩在一句看不懂的错上，不如说清楚要什么。
  echo "错误：HOME 没设，给一个目标目录：bash skills/install-agent-skill.sh <目标目录>" >&2
  exit 1
fi

if [ ! -f "${SOURCE}/SKILL.md" ] || [ ! -f "${SOURCE}/scripts/fetch.py" ]; then
  echo "错误：源目录不完整：${SOURCE}" >&2
  exit 1
fi

# 目标像不像一个 skill 目录？
#
# **不像就只警告、不删**：要删的那几样（references/、tests/）是极常见的
# 目录名，`bash skills/install-agent-skill.sh ~` 会把 ~/references 与 ~/tests
# 删掉 —— 那是用户自己的东西，不是 skill 的残留。
looks_like_skill_dir() {
  if [ -f "${TARGET}/SKILL.md" ]; then
    return 0
  fi

  if [ "$(basename "${TARGET}")" = "idea-manager" ]; then
    return 0
  fi

  return 1
}

CLEAN_STALE=0
if looks_like_skill_dir; then
  CLEAN_STALE=1
else
  echo "警告：${TARGET} 不像是一个 skill 目录（没有 SKILL.md，目录名也不是 idea-manager）——" >&2
  echo "      只覆盖 SKILL.md 与 scripts/fetch.py，**不清理**里面的任何东西。" >&2
fi

# 旧 skill 形状的确定残留：删掉。
#
# 只删这几种**认得出来**的旧形状 —— 它们一定是上一代 skill 留下的，
# 留着只会让 agent 读到过期内容。别的条目不认得，就不乱动（只警告）。
#
# **清理在 mkdir 之前**：先把 "${TARGET}/scripts" 建出来、再按 skill 布局
# 删它里面的东西，是把「本来不存在的东西」变成存在之后又去动它 ——
# 顺序反过来才不会。
if [ "$CLEAN_STALE" -eq 1 ]; then
  remove_path() {
    local path="$1"
    if [ -d "$path" ] && [ ! -L "$path" ]; then
      rm -rf "${path}"
    elif [ -e "$path" ] || [ -L "$path" ]; then
      rm -f "${path}"
    fi
  }

  for stale in "references" "tests" "scripts/idea.py" "scripts/run-tests.sh"; do
    if [ -e "${TARGET}/${stale}" ] || [ -L "${TARGET}/${stale}" ]; then
      remove_path "${TARGET}/${stale}"
      echo "清掉了旧 skill 的残留：${stale}"
    fi
  done
fi

mkdir -p "${TARGET}/scripts"

# 幂等：目标目录已存在就覆盖这两个文件（目录里的其它东西不动）。
cp "${SOURCE}/SKILL.md" "${TARGET}/SKILL.md"
cp "${SOURCE}/scripts/fetch.py" "${TARGET}/scripts/fetch.py"
chmod +x "${TARGET}/scripts/fetch.py"

# 不认识的条目：只警告，不删（可能是用户自己放的东西）。
for entry in "${TARGET}"/* "${TARGET}"/.[!.]*; do
  [ -e "$entry" ] || [ -L "$entry" ] || continue
  name="$(basename "$entry")"
  case "${name}" in
    SKILL.md | scripts) continue ;;
  esac
  echo "警告：${TARGET} 里有不认识的东西：${name}（没删，自己看一眼是不是该留）" >&2
done

for entry in "${TARGET}/scripts"/*; do
  [ -e "$entry" ] || [ -L "$entry" ] || continue
  name="$(basename "$entry")"
  case "${name}" in
    fetch.py) continue ;;
  esac
  echo "警告：${TARGET}/scripts 里有不认识的东西：${name}（没删，自己看一眼是不是该留）" >&2
done

echo "装好了：${TARGET}"
echo "  SKILL.md          指针（frontmatter + 怎么取说明书）"
echo "  scripts/fetch.py  取件器（已 chmod +x）"
echo
echo "下一步：给 agent 配这几个环境变量（凭据只走环境变量，不走命令行）——"
echo "  IDEA_BASE_URL  实例地址，如 https://ideas.example.com   （必填）"
echo "  IDEA_USER      nginx basic auth 用户名                 （可选，与密码同时存在时才发）"
echo "  IDEA_PASS      nginx basic auth 密码                     （可选）"
echo "  IDEA_TOKEN     API token，存在时额外发 X-Api-Key 头     （可选）"
echo
echo "自检（打得通就说明地址对、凭据对、服务端分发了 skill 内容）："
echo "  IDEA_BASE_URL=https://ideas.example.com IDEA_USER=ideauser IDEA_PASS='<密码>' \\"
echo "    python3 ${TARGET}/scripts/fetch.py"
echo "  注意：标准部署走 nginx basic auth —— 不带凭据跑自检会直接 auth_error（退出码 1）。"
echo "  只有不带 basic auth 的实例才用得着空着跑。"
echo
echo "注意：agent 需要重新扫一遍本地 skill 目录才会发现这个 skill ——"
echo "当前会话里还看不到的话，新开一个会话试试。"
echo
echo "装到别处（agent 的 skills 目录名不一样时）："
echo "  bash skills/install-agent-skill.sh ~/.claude/skills/idea-manager"
