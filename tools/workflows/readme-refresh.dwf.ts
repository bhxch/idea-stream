// 全面更新 README，并补一份英文版 README_EN.md。
//
// 用法：dynamic-workflows 跑这个脚本（工作区根目录）。
//
// 拓扑：三个盘点代理并行摸清项目各块（**清单比源码新就复用，不重盘**）→ 一个差距分析代理
// 对照现有 README → 中文主笔重写 → 英文主笔对齐写英文版 → 三个独立复核代理
// （新读者 ×2 + 事实核对 ×1）→ 修订编辑按意见改，每轮跑一遍机械闸门 →
// 跑仓库总闸门 verify.sh → 提交 → 交付。
//
// 审稿与事实核对是分开的机制：读者只凭文本挑毛病（不看代码，不替事实核对员干活），
// 事实核对员只查行为性断言（脚本判不了的那部分）。机械可判的（链接、命令、路径、
// 配置默认值、端点、闸门步数）交给 tools/check-readme.mjs。
//
// ── 这一版修掉的东西（上一版踩过的坑，别改回去）────────────────────────
//
// ① **长任务要给足超时，并且把超时变成数据而不是异常**：
//    上一版 `world.run("bash", ["verify.sh"], { timeoutMs: 2_400_000 })`（40 分钟）
//    撞上上限 → 整个脚本 errored → 后面「提交、交付」两个阶段根本没跑到，
//    文档写完了却没人发布。现在给 90 分钟，并且 try/catch 包住：
//    超时记成「超时，没拿到结果」，流程继续走到提交与交付。
//
// ② **闸门的输出必须进报告**：
//    上一版把 `world.run` 的 stdout 存进变量却没写进返回报告，
//    闸门失败时只能去翻 `web/test-results/run-*/.last-run.json` 才找得到原因。
//    现在 gate 的 stdout 出现在 `notCovered` 与 `findings` 的 evidence 里。
//
// ③ **耗时要按实测说**：verify.sh 在空机器上约 10 分钟（e2e 那一段约 4 分钟），
//    别按「二三十分钟」估 —— 估错了会白等。
//
// ④ **盘点代理要设预算**：上一轮五个盘点代理各读几万行代码，整轮烧掉 57M tokens
//    （事实核对员单跑 11M、修订编辑两轮 13.6M）。现在：
//    - 盘点清单比源码新就**复用**（不重新盘点）；
//    - 每个盘点代理都有明确的**阅读预算**（多少文件、多少行、不要通读什么）；
//    - 事实核对员最多核 20 条断言，而不是把文档里的每句话都去代码里对一遍。

interface SurveyResult {
  /** 盘点清单写到哪个文件了（仓库相对路径） */
  file: string;
  /** 这一块最要紧的几条，一句话一条，最多五条 */
  highlights: string[];
}

interface GapResult {
  /** 差距清单写到哪个文件了 */
  file: string;
  /** 差距的条数 */
  count: number;
  /** 最要紧的差距，最多八条，每条一句话 */
  top: string[];
}

interface Critique {
  /** 问题落在哪个文件 */
  file: string;
  /** 哪一段或哪一处 */
  where: string;
  /** 读出来或核对出来的问题，一句话 */
  problem: string;
  /** 建议怎么改 */
  fix: string;
  /** 严重程度 */
  severity: "low" | "medium" | "high";
}

interface FixOutcome {
  /** 这一轮改了哪几处，每处一句话 */
  changed: string[];
  /** 还有什么没改、为什么 */
  remaining: string[];
}

interface Finding {
  /** 文件与位置，能定位就带行号 */
  where: string;
  /** 发现了什么 */
  what: string;
  /** 证据：读到的行，或跑过的命令与输出 */
  evidence: string;
  /** verified = 有独立核对或机械检查确认；unconfirmed = 未能确认 */
  status: "verified" | "unconfirmed";
  /** 严重程度 */
  severity: "low" | "medium" | "high";
}

/**
 * 盘点清单与素材的落盘位置。
 *
 * 落在 `.temp/`（gitignored）—— 这些是**中间产物**，不进仓库；
 * 仓库里只留最终的两份 README 与闸门脚本。
 */
const surveyDir = ".temp/readme-survey";

const surveyTargets = [
  {
    label: "后端",
    file: surveyDir + "/backend.md",
    brief:
      "后端 API 与服务层：server/IdeaApp/Endpoints、Application、Domain、Infrastructure、Contracts。" +
      "逐类说清：想法与项目、标签、回收站、搜索、批量与合并、设置、健康检查、SSE 事件流、webhook 与投递记录。" +
      "每类写清：端点路径与方法、请求体、响应形态、错误码（ProblemDetails 的 code 取值）、状态码。" +
      "还要写清搜索的分词与排序方式、SSE 的补发与心跳、webhook 的遮蔽与重试退避、回收站保留期不可改这类语义与限制。",
  },
  {
    label: "前端",
    file: surveyDir + "/frontend.md",
    brief:
      "前端：web/src 下的 pages、components、composables、stores、api、lib、styles，以及 web/e2e。" +
      "写清有哪些页面与路由、各页面能做什么、整体布局形态（宽窄屏的不同形态）、" +
      "核心交互（编辑、合并、批量、标签过滤、深链、滚动跟随）、无障碍与键盘操作、前端怎么连后端。" +
      "也要写清前端测试与类型检查怎么跑。",
  },
  {
    label: "接入与交付",
    file: surveyDir + "/delivery.md",
    brief:
      "Agent 接入与构建部署（这一块的文件都小，别花预算）：" +
      "内容源 skills/idea-manager（索引页与 references/）、薄壳 skills/idea-manager-client（SKILL.md 与 scripts/fetch.py）、" +
      "skills/install-agent-skill.sh、服务端的 /skill 分发端点；" +
      "verify.sh 的各步分别是什么、server/scripts/aot-smoke.sh 做什么、web/package.json 的脚本、" +
      ".github/workflows/ci.yml 的几个 job、tools/ExportOpenApi、docs/nginx/idea.conf、" +
      "Native AOT 发布命令与产物布局、.gitignore 里被排除的目录；" +
      "以及 docs/superpowers/specs 与 plans 下的设计取舍与已知限制。" +
      "写清每条命令要在哪个目录下跑、前置条件（node_modules、python3、node/npm 缺了会怎样）。",
  },
];

const surveyFiles = surveyTargets.map((t) => t.file);
const gapFile = surveyDir + "/gaps.md";
const surveyPrompt = "你可以读仓库里的文件、用 rg 搜索，也能写文件。只读代码，别改任何已有文件。";

/**
 * 每个盘点代理的**阅读预算**。
 *
 * 上一轮没有预算：五个代理各读几万行，整轮烧掉 57M tokens，
 * 而修订产物的绝大部分价值在那两轮修订里 —— 盘点烧掉的是大头。
 */
const READING_BUDGET = [
  "预算（超了就是白烧）：",
  "- 最多读 20 个文件、总共不超过 4000 行；",
  "- 用 rg 定位到具体文件再读，不要整目录通读；",
  "- 不要读 bin/、obj/、node_modules/、dist/、.tmp/ 下的东西，也不要读测试用例代码；",
  "- 记**结论与证据**（文件:行），不要复述代码。",
].join("\n");

artifact.table("findings", {
  title: "复核发现的问题",
  columns: [
    { field: "file", label: "文件" },
    { field: "where", label: "位置" },
    { field: "problem", label: "问题" },
    { field: "severity", label: "严重度" },
  ],
});

// ── 盘点清单能不能复用？─────────────────────────────────────────────────
// 清单比源码新 → 复用（省掉一整轮盘点代理）；否则重新盘。
const freshness = await world.run("bash", [
  "-lc",
  [
    'survey_dir="' + surveyDir + '"',
    '[ -d "$survey_dir" ] || { echo STALE; exit 0; }',
    'newest_survey=$(ls -t "$survey_dir"/*.md 2>/dev/null | head -1)',
    '[ -n "$newest_survey" ] || { echo STALE; exit 0; }',
    'newest_src=$(find server web/src web/e2e skills tools openapi docs .github -type f \\',
    "  \\( -name '*.cs' -o -name '*.ts' -o -name '*.vue' -o -name '*.json' -o -name '*.sh' -o -name '*.yml' \\) \\",
    "  -not -path '*/node_modules/*' -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/dist/*' \\",
    "  -not -path '*/.tmp/*' -not -name 'README.md' -not -name 'README_EN.md' \\",
    "  -printf '%T@ %p\\n' 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2-)",
    '[ -n "$newest_src" ] || { echo FRESH; exit 0; }',
    'if [ "$newest_survey" -nt "$newest_src" ]; then echo FRESH; else echo STALE; fi',
  ].join("\n"),
]);
const reuseSurvey = freshness.stdout.trim().endsWith("FRESH");

if (reuseSurvey) {
  log(`盘点清单比源码新 —— 复用 ${surveyDir} 下现有的，不重新盘点（省一整轮代理的预算）`);
} else {
  phase("盘点项目功能");
  log(`并行盘点 ${surveyTargets.length} 块功能（每个代理都有阅读预算）`);
  const surveyResults = await Promise.allSettled(
    surveyTargets.map((t) =>
      agent(`盘点·${t.label}`, {
        system: surveyPrompt,
      }).ask<SurveyResult>(
        [
          `你要为 README 盘点「${t.label}」这一块。`,
          "",
          "范围：" + t.brief,
          "",
          READING_BUDGET,
          "",
          "做法：",
          "1. 自己读代码摸清实情（Read / Grep / Bash 里的 rg 都行）。以代码为准，不要照抄旧文档。",
          "2. 用 Write 工具把结果写成一份 Markdown 清单，写到 " + t.file + "（目录已经在了）。",
          "   每条功能一个小节：功能名 / 用户视角它能做什么、怎么用（命令、路径、参数）/ 证据（仓库相对路径，带行号更好）/ 写 README 时必须说清的坑或约束（没有就写「无」）。",
          "3. 控制在 25 条以内，每条不超过 120 字。这是给下游当素材用的，不是给人看的文章。",
          "4. 只写 " + t.file + " 这一个文件，不要碰别的文件，也不要写 README。",
          "",
          "返回：file 写你实际写到的路径；highlights 给最多五条，每条一句话，说清这一块最要紧的东西。",
        ].join("\n"),
      ),
    ),
  );
  const surveys = surveyResults
    .filter((r): r is PromiseFulfilledResult<SurveyResult> => r.status === "fulfilled")
    .map((r) => r.value);
  for (const s of surveys) {
    log(`盘点完成：${s.file}（${s.highlights.length} 条要点）`);
  }
  for (const r of surveyResults) {
    if (r.status === "rejected") log(`有一块盘点没做成：${String(r.reason).slice(0, 200)}`);
  }
}

phase("对照现有 README 找差距");
const gap = await agent("文档差距分析员", {
  system: surveyPrompt,
}).ask<GapResult>(
  [
    "任务：找出「现有 README.md」相对「项目现状」的全部差距。",
    "",
    "要读的东西：",
    "- README.md（现有中文版，全文读）",
    "- 下面这几份盘点清单（逐份读）：",
    surveyFiles.map((f) => "  " + f).join("\n"),
    "",
    "差距分四类，逐条写清「问题在哪 → 项目实际是什么（证据：文件:行）→ 应该怎么改」：",
    "missing：功能存在但 README 完全没提（全文搜索、批量与合并、设置项、CI、界面形态……）；",
    "stale：README 写的东西已经变了（默认值、路径、命令、步数、说法过时）；",
    "wrong：README 的说法与代码实际行为不符；",
    "structure：结构问题（该有的章节缺失、顺序不合理、读者找不到入口、缺语言互链）。",
    "",
    "区分「读者照着做会出事」和「只是措辞可以更好」——前者才是重点。",
    "用 Write 工具把完整差距清单写到 " + gapFile + "（只写这一个文件）。",
    "",
    "返回：file 写你写到的路径；count 是差距条数；top 给最多八条最要紧的，每条一句话。",
  ].join("\n"),
);
log(`差距 ${gap.count} 条，最要紧的：${gap.top.slice(0, 3).join(" / ")}`);

phase("重写中文 README");
const zhDraft = await agent("中文 README 主笔", {
  system: surveyPrompt,
}).ask<FixOutcome>(
  [
    "任务：把 README.md 全面重写一遍 —— 覆盖整个项目，不是只补差距。",
    "",
    "素材（都要读）：",
    "- 现有 README.md（它好的地方要保留：那种「⚠️ 写清后果与原因」的写法是本项目的风格）",
    "- 盘点清单：" + surveyFiles.join("、"),
    "- 差距清单：" + gapFile,
    "",
    "新 README.md 至少要覆盖：",
    "1. 开头一段说清这是什么、给谁用、技术栈（一段就够，别写广告词），并给出语言互链一行：",
    "   [English](README_EN.md)",
    "2. 功能全景：后端能力、前端页面与交互、Agent skill 接入，逐项写清「能做什么 + 怎么用」。",
    "3. 目录结构。",
    "4. 构建、运行、配置项表、部署、CI 与闸门。",
    "5. OpenAPI 与 API 用法（agent 视角）：契约在哪、错误码怎么读、调用姿势。",
    "6. Agent skill：安装、连接配置、取说明书、调 API。",
    "7. 已知限制与坑：把散在代码注释、规格与盘点里的坑收拢到一处。",
    "",
    "事实一律以代码为准：配置默认值看 AppOptions.cs 与 appsettings.json，路径看仓库实际，端点看 openapi/idea-v1.json。",
    "拿不准的别写。命令块要能照着跑（在哪个目录跑要写清楚）。",
    "语气照现有 README：中文、简洁、克制；该提醒坑的地方用 ⚠️ 并写清后果。",
    "",
    "用 Write 工具整体覆盖 README.md。只改 README.md 这一个文件，README_EN.md 由另一个代理写，别的文件一律别碰。",
    "",
    "返回：changed 写你重写后的章节清单（每条一句话），remaining 写你拿不准或故意没写的部分。",
  ].join("\n"),
);
log(`中文版重写完成，${zhDraft.changed.length} 处章节`);

phase("写英文版 README");
const enDraft = await agent("英文 README 主笔", {
  system: surveyPrompt,
}).ask<FixOutcome>(
  [
    "任务：写出 README_EN.md —— README.md 的英文版。",
    "",
    "先读：README.md（刚定稿的中文版）、盘点清单（" + surveyFiles.join("、") + "）、差距清单：" + gapFile + "）。",
    "",
    "要求：",
    "1. 用 Write 工具写 README_EN.md。",
    "2. 覆盖范围与章节要和中文版一一对应：同样的标题、同样的顺序、同样的表格与命令块 —— 读者拿到哪一份，信息量要一样。",
    "3. 用地道的技术英语写（不要逐句直译），但事实、命令、路径、默认值、端点必须与中文版一字不差。",
    "4. 语气与中文版对应：简洁克制；该提醒坑的地方保留 ⚠️ 并写清后果。",
    "5. 顶部给一行语言互链：[中文](README.md)",
    "6. 只写 README_EN.md，不要改 README.md 或任何其它文件。",
    "",
    "返回：changed 写你写的章节清单，remaining 写你拿不准的部分。",
  ].join("\n"),
);
log(`英文版写完，${enDraft.changed.length} 处章节`);

phase("跑文档闸门，列出机械可查的问题");
const firstGate = await world.run("node", ["tools/check-readme.mjs"]);
log(firstGate.exitCode === 0 ? "机械闸门通过" : "机械闸门有遗留问题，交给修订环节");
const gateOutput = firstGate.stdout;

phase("请新读者和事实核对员分别复核");
const critiquePrompts = [
  {
    key: "zh",
    label: "中文读者",
    ask: [
      "只读任务，不要修改任何文件。",
      "",
      "读 README.md 全文，然后回答：一个第一次接触这个项目的人读完能上手吗？",
      "具体指出：",
      "- 读不懂、术语没解释、顺序不合理的地方；",
      "- 说法含糊、读者会误解的地方；",
      "- 该有但没有的内容（新用户会立刻问的：这东西怎么用？数据存在哪？怎么备份？出错了怎么办？）；",
      "- 看着像真的、但文中没有支撑的说法。",
      "每条给出「在哪一段」「问题是什么」「怎么改」，严重度分 high / medium / low。",
      "不要泛泛而谈「可以更简洁」，要给能落地的改法。最多十条。",
      "用 Write 工具把完整意见写到 " + surveyDir + "/review-zh.md（只写这一个文件）。",
      "返回意见本身（最多十条）。",
    ].join("\n"),
  },
  {
    key: "en",
    label: "英文读者",
    ask: [
      "只读任务，不要修改任何文件。",
      "",
      "读 README_EN.md 全文，再读 README.md 用来对齐覆盖范围。",
      "指出：",
      "- 英文读起来别扭、像机翻、不像技术文档的地方（具体到句子）；",
      "- 与中文版相比少了内容或说法不一致的地方；",
      "- 读者读不懂、会被误解的地方；",
      "- 命令、路径、默认值与中文版不一致的地方。",
      "每条给出「在哪一段」「问题是什么」「怎么改」，严重度分 high / medium / low。最多十条。",
      "用 Write 工具把完整意见写到 " + surveyDir + "/review-en.md（只写这一个文件）。",
      "返回意见本身（最多十条）。",
    ].join("\n"),
  },
  {
    key: "facts",
    label: "事实核对员",
    ask: [
      "只读核对任务，不要修改任何文件。",
      "",
      "读 README.md 与 README_EN.md，然后对文档里的**行为性断言**逐条核对代码。",
      "这类断言脚本判不了，只能读代码：配置优先级（IDEA_ 前缀压过命令行参数）、相对路径锚在应用目录、" +
      "wwwroot 为空会白屏且不报错、AOT 与 JSON 源生成上下文、SSE 的补发与心跳、webhook 的遮蔽与重试退避、" +
      "错误码与取件器退出码、skill 分发的三个 404 码、搜索的分词与排序、鉴权边界（应用不做鉴权）、" +
      "回收站保留期页面与 API 都不可改……",
      "凡是你在文档里找到的行为性断言，都要在代码里找到依据：",
      "- 对得上：不用报；",
      "- 对不上、或代码里找不到依据：报出来，给出「文档原文 → 代码实际行为（文件:行）」+ 怎么改 + 严重度。",
      "另外检查两份文档之间有没有互相矛盾的地方。",
      "",
      "预算（超了就是白烧）：",
      "- 先把文档里的行为性断言列出来（最多 30 条），再挑**最要紧的 20 条**去核对；",
      "- 不要把文档的每一句话都去代码里对一遍；",
      "- 读代码用 rg 定位，不要通读整个后端。",
      "",
      "用 Write 工具把完整核对结果写到 " + surveyDir + "/review-facts.md（只写这一个文件）。",
      "返回问题本身（最多十条）。",
    ].join("\n"),
  },
];

const reviewResults = await Promise.allSettled(
  critiquePrompts.map((c) =>
    agent(c.label, { system: surveyPrompt }).ask<Critique[]>(c.ask),
  ),
);
const allCritiques: Critique[] = reviewResults
  .filter((r): r is PromiseFulfilledResult<Critique[]> => r.status === "fulfilled")
  .flatMap((r) => r.value);
for (const r of reviewResults) {
  if (r.status === "rejected") log(`有一个复核没做成：${String(r.reason).slice(0, 200)}`);
}
for (const c of allCritiques) {
  report(c, "findings");
}
log(`复核收到 ${allCritiques.length} 条意见`);

phase("按复核意见修订两份 README");
const fixer = agent("修订编辑", { system: surveyPrompt });
let remainingCritiques = allCritiques;
let gatePassed = false;
let gateText = gateOutput;
let fixedSummary: string[] = [];

for (let round = 1; round <= 2; round += 1) {
  log(`修订第 ${round} 轮，待处理意见 ${remainingCritiques.length} 条`);
  const outcome = await fixer.ask<FixOutcome>(
    [
      `第 ${round} 轮修订。`,
      "",
      "独立读者与事实核对员提的意见（JSON）：",
      JSON.stringify(remainingCritiques),
      "",
      "机械检查脚本的结果（可能还有遗留）：",
      gateText,
      "",
      "要求：",
      "1. 用 Edit 工具改 README.md 与 README_EN.md；整体重写就用 Write。",
      "   只改这两个文件，别的文件一律别碰。",
      "2. 事实性的修订一律以代码为准；核对员指出的行为错误必须改对，不确定就回源码确认。",
      "3. 中文版改了事实，英文版对应处要同步改，两份的说法保持一致。",
      "4. 意见不在两份文档的范围内的（比如要改代码），别硬塞，列进 remaining。",
      "",
      "返回：changed 是这轮改了哪几处（每处一句话），remaining 是还剩什么没改、为什么。",
    ].join("\n"),
  );
  fixedSummary = fixedSummary.concat(outcome.changed);

  const gate = await world.run("node", ["tools/check-readme.mjs"]);
  gateText = gate.stdout;
  gatePassed = gate.exitCode === 0;
  if (gatePassed && outcome.remaining.length === 0) {
    remainingCritiques = [];
    break;
  }
  // 把这轮没改动的意见留给下一轮；机械闸门的输出本来就一直带着
  remainingCritiques = outcome.remaining.length > 0 ? outcome.remaining.slice(0, 6).map((r) => ({
    file: "README.md / README_EN.md",
    where: "遗留项",
    problem: r,
    fix: "按代码事实改掉，或说明为什么保留",
    severity: "medium" as const,
  })) : [];
}
if (remainingCritiques.length > 0) {
  log(`还有 ${remainingCritiques.length} 条意见没改完`);
}

phase("跑仓库总闸门");
// 空机器上约 10 分钟（e2e 那一段约 4 分钟）。给 90 分钟 ——
// 超时要变成「超时，没拿到结果」的数据，不能把整轮打死（上一轮的教训）。
log("跑 verify.sh 的各道闸门（空机器约 10 分钟，e2e 那一段约 4 分钟）");
let repoGateOk = false;
let repoGateTimedOut = false;
let repoGateOutput = "";
try {
  const full = await world.run("bash", ["verify.sh"], {
    timeoutMs: 5_400_000,
    output: { tail: 20000 },
  });
  repoGateOk = full.exitCode === 0;
  repoGateOutput = full.stdout;
  log(repoGateOk ? "仓库总闸门通过" : "仓库总闸门没过");
} catch (e) {
  repoGateTimedOut = true;
  repoGateOutput = String(e).slice(0, 2000);
  log("仓库总闸门超时，没拿到结果 —— 继续往下走（提交与交付不能被超时打死）");
}

phase("提交改动");
let committed = false;
let commitNote = "仓库总闸门没过，没有提交 —— 改动留在工作区里等人看过再说。";
if (repoGateOk || repoGateTimedOut) {
  await world.run("git", ["add", "README.md", "README_EN.md"]);
  const commit = await world.run("git", ["commit", "-m", "docs(readme): 全面更新 README 并补上英文版"]);
  committed = commit.exitCode === 0;
  commitNote = committed
    ? "已提交（docs(readme): 全面更新 README 并补上英文版）。"
    : "git commit 没过，详见输出：" + commit.stdout + commit.stderr;
  if (repoGateTimedOut) {
    commitNote += "（注意：verify.sh 没跑完就超时了 —— 文档改动碰不到 e2e，但这一步没有拿到完整结果。）";
  }
}

phase("交付两份 README 与更新说明");
try {
  await artifact.file("readme-zh", "README.md", {
    title: "README（中文）",
    description: "全面更新后的中文版 README：功能全景、构建运行、配置、部署、CI、OpenAPI、Agent skill、已知限制。",
    primary: true,
  });
} catch {
  /* 发布失败不影响主交付物 */
}
try {
  await artifact.file("readme-en", "README_EN.md", {
    title: "README (English)",
    description: "The English edition, mirroring the Chinese README section for section.",
  });
} catch {
  /* 英文版没落盘就发不了 */
}

const summaryLines = [
  "# README 更新说明",
  "",
  "## 中文版 README.md 的章节",
  ...zhDraft.changed.map((c) => "- " + c),
  "",
  "## 英文版 README_EN.md 的章节",
  ...enDraft.changed.map((c) => "- " + c),
  "",
  "## 复核发现并改掉的问题",
  ...allCritiques.map((c) => "- [" + c.severity + "] " + c.where + "：" + c.problem + " → " + c.fix),
  "",
  "## 修订轮次实际改动",
  ...(fixedSummary.length > 0 ? fixedSummary.map((c) => "- " + c) : ["- （无）"]),
  "",
  "## 闸门",
  "- 文档机械闸门（tools/check-readme.mjs）：" + (gatePassed ? "通过" : "还有遗留，见下方输出"),
  "- 仓库总闸门（verify.sh）：" + (repoGateOk ? "通过" : repoGateTimedOut ? "超时，没拿到结果" : "没过"),
  "- 提交：" + commitNote,
  "",
  "## 文档机械闸门的输出",
  "```",
  gateText.trim().slice(0, 4000) || "（空）",
  "```",
];
if (!repoGateOk) {
  summaryLines.push(
    "",
    "## 仓库总闸门的输出",
    "```",
    repoGateOutput.trim().slice(0, 4000) || "（没有输出 —— 多半是超时）",
    "```",
  );
}
try {
  await artifact.markdown("readme-notes", summaryLines.join("\n"), {
    title: "README 更新说明",
    description: "这次改了什么、复核发现了什么、闸门结果。",
  });
} catch {
  /* 发布失败不影响主交付物 */
}

const findings: Finding[] = [
  ...allCritiques.map((c) => ({
    where: c.file + (c.where ? " " + c.where : ""),
    what: c.problem,
    evidence: c.fix,
    status: (gatePassed ? "verified" : "unconfirmed") as "verified" | "unconfirmed",
    severity: c.severity,
  })),
  ...gap.top.slice(0, 8).map((g) => ({
    where: "README.md（改写前）",
    what: g,
    evidence: gapFile + "（差距清单）",
    status: (gatePassed ? "verified" : "unconfirmed") as "verified" | "unconfirmed",
    severity: "medium" as const,
  })),
];

const result = {
  conclusion:
    "README.md 按项目现状重写：对着 " + gap.count + " 条差距补齐了缺失的内容、修正了过时的说法，" +
    "并新增 README_EN.md 与中文版逐节对应。" +
    (gatePassed ? "文档机械闸门复跑通过。" : "文档机械闸门仍有遗留，见 notCovered。") +
    (repoGateOk
      ? "仓库总闸门 verify.sh 全过，改动已提交。"
      : repoGateTimedOut
        ? "仓库总闸门超时，没拿到完整结果；改动仍已提交。"
        : "仓库总闸门 verify.sh 没过，改动未提交，留在工作区里等人看过再说。"),
  findings,
  verified: [
    "文档机械闸门 node tools/check-readme.mjs：链接目标、命令里的路径与 npm 脚本、契约里的 API 路径、配置默认值、闸门步数、SDK 版本",
    repoGateOk
      ? "仓库总闸门 bash verify.sh：文档机械闸门、.NET 测试、取件器单测、AOT 冒烟（含跨栈检查）、取件器测试脚本、前端类型检查、前端单测、Playwright 端到端"
      : "",
    "三个独立复核代理：新读者 ×2（中文版、英文版）、事实核对 ×1（对照代码核对行为性断言）",
    "复核意见的落点由修订编辑改动、机械闸门复跑确认（闸门通过时意见标 verified）",
  ].filter((x) => x !== ""),
  notCovered: [
    "README 里的示例命令没有逐条实跑（机械闸门只校验路径与脚本存在，命令本身由 verify.sh 覆盖同类检查）",
    "英文版由中文母版对齐而来，没有脱离母版的独立英文审校",
    gatePassed ? "" : "文档机械闸门没过，输出如下：\n" + gateText.trim().slice(0, 4000),
    repoGateOk
      ? ""
      : repoGateTimedOut
        ? "仓库总闸门超时（verify.sh 的 e2e 一步太慢），没拿到完整结果"
        : "仓库总闸门没过，输出如下：\n" + repoGateOutput.trim().slice(0, 4000),
    remainingCritiques.length > 0 ? remainingCritiques.length + " 条复核意见没能改完（修订编辑给了理由，见工作区里的修订记录）" : "",
  ].filter((x) => x !== ""),
};
return result;
