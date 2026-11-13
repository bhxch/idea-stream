#!/usr/bin/env bash
#
# 把 agent 侧的 skill（薄壳 + 取件器）装到 agent 的 skills 目录。
#
# 装的是**拷贝**，不是软链：薄壳（skills/idea-manager-client/）与内容源
# （skills/idea-manager/）是两个东西 —— 软链内容源会把里面的索引页
# 当成薄壳装过去，agent 手上就又握着一份会过期的副本。
#
# **目标目录是这个 skill 专用的**：装进去的应该只有薄壳的两个文件。
# 旧的安装说明教的是 `cp -r skills/idea-manager ~/.zcode/skills/`，
# 装出来正是同一个目录 —— 里面会剩下旧的 references/ 与旧的命令层脚本。
# 留着它们，薄壳说的「本目录只有两个文件」当场不成立，agent 也可能直接
# 去读那份过期手册（正是这套改造要消除的东西）。所以这里把**旧形状的
# 确定残留**删掉；其它不认识的条目只警告、不删。
#
# 脚本自己不读也不写凭据：连接配置由 agent 运行时从环境变量读
# （取件器的命令行不接受密码）。
#
# 用法：bash skills/install-agent-skill.sh [目标目录]
#       默认目标：~/.zcode/skills/idea-manager
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="${ROOT}/skills/idea-manager-client"

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

mkdir -p "${TARGET}/scripts"

# 幂等：目标目录已存在就覆盖这两个文件（目录里的其它东西不动）。
cp "${SOURCE}/SKILL.md" "${TARGET}/SKILL.md"
cp "${SOURCE}/scripts/fetch.py" "${TARGET}/scripts/fetch.py"
chmod +x "${TARGET}/scripts/fetch.py"

# 旧 skill 形状的确定残留：删掉。
#
# 只删这几种**认得出来**的旧形状 —— 它们一定是上一代 skill 留下的，
# 留着只会让 agent 读到过期内容。别的条目不认得，就不乱动（只警告）。
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
echo "  IDEA_BASE_URL=... ${TARGET}/scripts/fetch.py"
echo
echo "装到别处（agent 的 skills 目录名不一样时）："
echo "  bash skills/install-agent-skill.sh ~/.claude/skills/idea-manager"
