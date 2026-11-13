#!/usr/bin/env bash
#
# 把 agent 侧的 skill（薄壳 + 取件器）装到 agent 的 skills 目录。
#
# 装的是**拷贝**，不是软链：薄壳（skills/idea-manager-client/）与内容源
# （skills/idea-manager/）是两个东西 —— 软链内容源会把里面的索引页
# 当成薄壳装过去，agent 手上就又握着一份会过期的副本。
#
# 脚本自己不读也不写凭据：连接配置由 agent 运行时从环境变量读
# （取件器的命令行不接受密码）。
#
# 用法：bash skills/install-agent-skill.sh [目标目录]
#       默认目标：~/.zcode/skills/idea-manager
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE="${ROOT}/skills/idea-manager-client"
TARGET="${1:-${HOME}/.zcode/skills/idea-manager}"

if [ ! -f "${SOURCE}/SKILL.md" ] || [ ! -f "${SOURCE}/scripts/fetch.py" ]; then
  echo "错误：源目录不完整：${SOURCE}" >&2
  exit 1
fi

mkdir -p "${TARGET}/scripts"

# 幂等：目标目录已存在就覆盖这两个文件（目录里的其它东西不动）。
cp "${SOURCE}/SKILL.md" "${TARGET}/SKILL.md"
cp "${SOURCE}/scripts/fetch.py" "${TARGET}/scripts/fetch.py"
chmod +x "${TARGET}/scripts/fetch.py"

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
