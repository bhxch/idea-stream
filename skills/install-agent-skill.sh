#!/usr/bin/env bash
#
# 把 agent 侧的 skill（薄壳 + 取件器）装到 agent 的 skills 目录。
#
# 装的是**拷贝**，不是软链：薄壳（skills/idea-manager-client/）与内容源
# （skills/idea-manager/）是两个东西 —— 软链内容源会把里面的索引页
# 当成薄壳装过去，agent 手上就又握着一份会过期的副本。
#
# **目标目录必须是一个 skill 目录**：里面有 SKILL.md，或者目录名就叫
# idea-manager。
#
# 清理的尺度：**只删旧 skill 特有的那几个具体文件**，目录只在**空了之后**
# 用 rmdir 删掉；用户自己的东西一律留着。
#
# 为什么不能「整个目录 rm -rf」：references 与 tests 是极常见的目录名，
# 里面装的是**用户自己的东西**。上一版脚本「看到 SKILL.md 就整目录删」——
# 而 SKILL.md 恰恰是**脚本自己写的**：跑第二遍时它已经在了，于是护栏被
# 自己写的文件绕过去，用户自己的 references/keep.txt 在第二遍被删掉。
# 现在改成「只删认得出来的旧文件 + rmdir 空目录」，rmdir 碰到非空目录会
# 失败并把它留下 —— keep.txt 因此活得下来。
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
  不像 skill 目录时，脚本只警告，不会动里面的任何东西。

  清理的尺度：**只删旧 skill 留下的那几个已知文件** ——
  scripts/idea.py、scripts/run-tests.sh、references/openapi.json、
  tests/test_cli.py，以及 __pycache__ 目录里的 .pyc。
  目录只在**空了之后**用 rmdir 删掉（里面还有你自己的文件就留着）。

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
# **不像就只警告、不清理**：它挡的是「第一次就指错目录」——
# `bash skills/install-agent-skill.sh ~` 指的是家目录，那不是 skill 目录。
#
# 注意它**不是唯一的防线**：SKILL.md 是脚本自己写进去的，跑第二遍时它
# 已经在了 —— 只靠这个判断，第一遍挡得住、第二遍挡不住。
# 真正的防线在下面的清理里：只删旧 skill 那几个具体文件，目录只用 rmdir
# 删空的（用户自己的 references/keep.txt 因此活得下来）。
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
# **只删具体文件 + 只删空目录**，不 rm -rf 任何目录：
#
#   * 具体文件：那几个名字是**上一代 skill 特有的**（别的 skill、用户自己的
#     文件不会撞上），认得出就删。
#   * 目录：references/ 与 tests/ 只在**空了之后**用 rmdir 删掉。
#     rmdir 碰到非空目录会失败并留下它 —— 用户自己的 references/keep.txt
#     因此活得下来（这正是要防的伤害）。
#
# 前面那个「像不像 skill 目录」的前置判断保留着，但它**不是唯一的防线**：
# 它挡的是「第一次就指错目录」；真正的防线是下面这条「只删具体文件 + rmdir」——
# 上一版只有前者，而 SKILL.md 恰恰是脚本自己写的，跑第二遍就把前者绕过去了。
#
# **清理在 mkdir 之前**：先把 "${TARGET}/scripts" 建出来、再按 skill 布局
# 删它里面的东西，是把「本来不存在的东西」变成存在之后又去动它 ——
# 顺序反过来才不会。
if [ "$CLEAN_STALE" -eq 1 ]; then
  # 旧 skill 特有的文件：认得出就删，别的名字一律不动。
  for stale_file in \
    "scripts/idea.py" \
    "scripts/run-tests.sh" \
    "references/openapi.json" \
    "tests/test_cli.py"; do
    if [ -f "${TARGET}/${stale_file}" ] || [ -L "${TARGET}/${stale_file}" ]; then
      rm -f "${TARGET}/${stale_file}"
      echo "清掉了旧 skill 的残留：${stale_file}"
    fi
  done

  # __pycache__ 里是 .pyc：先删里面的 .pyc，再 rmdir 这个目录。
  # 目录里还有别的东西就留下（不猜），只警告。
  for pycache in "scripts/__pycache__" "tests/__pycache__"; do
    if [ -d "${TARGET}/${pycache}" ]; then
      for pyc in "${TARGET}/${pycache}"/*.pyc; do
        [ -e "$pyc" ] || continue
        rm -f "$pyc"
        echo "清掉了旧 skill 的残留：${pycache}/$(basename "$pyc")"
      done
      if rmdir "${TARGET}/${pycache}" 2>/dev/null; then
        echo "清掉了旧 skill 的残留：${pycache}/（空目录）"
      else
        echo "警告：${TARGET}/${pycache} 里还有别的东西，没有删这个目录" >&2
      fi
    fi
  done

  # 旧形状的两个目录：**只删空了的那个**。
  # 非空就留下（用户自己的东西），并说清楚「没删」。
  #
  # scripts/ 不在名单里：脚本自己装的 fetch.py 就住在那儿。
  for stale_dir in "references" "tests"; do
    if [ -d "${TARGET}/${stale_dir}" ]; then
      if rmdir "${TARGET}/${stale_dir}" 2>/dev/null; then
        echo "清掉了旧 skill 的残留：${stale_dir}/（空目录）"
      else
        echo "警告：${TARGET}/${stale_dir} 里还有别的东西，目录没删（只删了旧 skill 那几个文件）" >&2
      fi
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
