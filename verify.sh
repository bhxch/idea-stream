#!/usr/bin/env bash
#
# 一条命令跑完全部闸门。
#
# 为什么要有这个脚本：各条检查散在仓库各处（文档的机械闸门、.NET 的测试、
# 取件器的单测、AOT 冒烟脚本、取件器自己的测试脚本、前端的类型检查与测试），
# 仓库里**也有 CI**（.github/workflows/ci.yml，把这八条命令分派到 6 个 job）——
# 但 CI 只在 push / pull_request 上跑，本地提交前想一次跑完、还想看清
# 每一步的名字与输出，就靠这个脚本。这一条把它们串起来：任一步
# 失败即失败，而且输出说清是哪一步。
#
# 八步：
#   1. 文档机械闸门 —— 两份 README 里的链接、命令、API 路径、配置默认值、
#      闸门步数、SDK 版本不许与仓库对不上（tools/check-readme.mjs，秒级）；
#   2. dotnet test —— 服务端（含 skill 分发端点、契约快照、文档漂移守卫）；
#   3. python3 -m unittest —— 取件器的单测（清单校验、降级、管道、重定向……）；
#   4. AOT 冒烟 —— 真 AOT 产物 + **跨栈**检查（真产物 ↔ 真取件器）；
#   5. 取件器的测试脚本 —— 它自己带一条真 HTTP 服务端的冒烟；
#   6. 前端类型检查 —— 应用（vue-tsc）+ e2e（另一份 tsconfig，见下）；
#   7. 前端单测 —— vitest；
#   8. 端到端用例 —— Playwright（自己起服务、自己建库、自己挑端口）。
#
# 文档闸门排第一：它最便宜（不到一秒），而改文档是最常见的改动 ——
# 漂了就该当场看见，不必等十分钟跑完 e2e。
#
# 用法：bash verify.sh
# 退出码：0 = 八步全过；非 0 = 至少一步没过。

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

TOTAL_STEPS=8
STEP=0
FAILED=0

# 缺 python3 是**失败**不是跳过：agent 侧本来就需要 python3（规格里的硬依赖），
# 缺了说明环境不对 —— 而跨栈检查（第四步）正需要它。
if ! command -v python3 >/dev/null 2>&1; then
    echo "错误：没有 python3。这个项目的 agent 侧本来就需要它（缺了说明环境不对）。" >&2
    exit 1
fi

# 缺 node 同理：文档闸门（第一步）与前端的闸门（第六到第八步）都要它。
if ! command -v node >/dev/null 2>&1 || ! command -v npm >/dev/null 2>&1; then
    echo "错误：没有 node/npm。文档闸门与前端的三道闸门要它们（缺了说明环境不对）。" >&2
    exit 1
fi

# 缺前端依赖也是**失败**不是跳过：没有 node_modules 时 vitest 与 playwright
# 根本跑不起来，那道闸门等于没跑。
if [ ! -d web/node_modules ]; then
    echo "错误：web/node_modules 不在。先跑一次（cd web && npm ci）。" >&2
    exit 1
fi

run_step() {
    local name="$1"
    shift

    STEP=$((STEP + 1))
    echo
    echo "==> [${STEP}/${TOTAL_STEPS}] ${name}"

    if "$@"; then
        echo "--- 通过：${name}"
    else
        echo "!!! 失败：${name}（上面的输出是原因）" >&2
        FAILED=$((FAILED + 1))
    fi
}

run_step "文档机械闸门（两份 README 的链接/命令/路径/默认值/步数/SDK 版本）" \
    node tools/check-readme.mjs

run_step "服务端测试（dotnet test）" \
    dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj --nologo -v q

run_step "取件器单测（python3 -m unittest）" \
    python3 -m unittest discover -s skills/idea-manager-client/tests

run_step "AOT 冒烟（含跨栈检查）" \
    bash server/scripts/aot-smoke.sh

run_step "取件器测试脚本（含真 HTTP 服务端冒烟）" \
    bash skills/idea-manager-client/scripts/run-tests.sh

# 前端的三道闸门走 npm --prefix web：npm 会把脚本的工作目录设成 prefix ✓
# （vitest 与 vue-tsc 都要在 web/ 里找配置）。
run_step "前端类型检查（应用 + e2e）" \
    npm --prefix web run typecheck

run_step "前端单测（vitest）" \
    npm --prefix web test

run_step "端到端用例（Playwright）" \
    bash web/e2e/run-e2e.sh

echo

if [ "$FAILED" -ne 0 ]; then
    echo "FAIL: ${FAILED} 步没过（共 ${TOTAL_STEPS} 步），见上面每一段的输出。" >&2
    exit 1
fi

echo "PASS: 八步全过 —— 文档机械闸门、服务端测试、取件器单测、AOT 冒烟（含跨栈检查）、取件器测试脚本、前端类型检查（应用 + e2e）、前端单测、端到端用例。"
exit 0
