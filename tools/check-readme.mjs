// README 文档闸门：检查 README.md / README_EN.md 里的可机械判定的事实。
//
// 用法：node tools/check-readme.mjs [仓库根目录]
// 退出码：0 = 没有问题；1 = 有问题（逐条打印在 stdout 上）。
//
// 查的是「机器能判定」的那一类：链接是否指向真实文件、代码块里的命令
// 是否真的存在、提到的 API 路径是否在契约里、配置表的默认值是否与代码
// 一致、闸门步数是否对得上、钉住的 SDK 版本是否与 global.json 一致。
// 语义层面的断言由复核代理负责（见 tools/workflows/ 下的流水线）。
//
// 仓库根默认取**本脚本所在目录的上一级**（tools/ → 仓库根）——
// 于是从哪个目录跑都指向同一个仓库（`cd web && node ../tools/check-readme.mjs`
// 也不会查错地方）。命令行参数可以显式指定别处。
//
// NOTE: 行是提示（不判失败），例如哪些契约端点没在 README 里出现。

import { readFileSync, existsSync, statSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join, resolve, dirname } from "node:path";

const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(process.argv[2] ?? join(SCRIPT_DIR, ".."));
const problems = [];
const notes = [];

const read = (rel) => readFileSync(join(ROOT, rel), "utf8");
const exists = (rel) => existsSync(join(ROOT, rel));
const isDir = (rel) => {
  try {
    return statSync(join(ROOT, rel)).isDirectory();
  } catch {
    return false;
  }
};

const DOCS = ["README.md", "README_EN.md"];

// ── 事实来源 ───────────────────────────────────────────────────────────
const contract = JSON.parse(read("openapi/idea-v1.json"));
const contractPaths = Object.keys(contract.paths ?? {});
const appOptionsSrc = read("server/IdeaApp/Infrastructure/AppOptions.cs");
const appsettings = JSON.parse(read("server/IdeaApp/appsettings.json"));

// 配置默认值：类名去掉 Options 后缀 → 分组名
const expectedDefaults = new Map();
{
  const classRe = /public sealed class (\w+Options)\b[\s\S]*?\n\}/g;
  let m;
  while ((m = classRe.exec(appOptionsSrc)) !== null) {
    const group = m[1].replace(/Options$/, "");
    const body = m[0];
    const propRe = /public\s+[\w?<>[\]]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}\s*=\s*([^;]+);/g;
    let p;
    while ((p = propRe.exec(body)) !== null) {
      expectedDefaults.set(`${group}:${p[1]}`, p[2].trim().replace(/^"|"$/g, ""));
    }
  }
  // AppOptions 顶层的属性（没有初始化器 → 空）
  const top = appOptionsSrc.slice(0, appOptionsSrc.indexOf("public sealed class DatabaseOptions"));
  const topRe = /public\s+[\w?<>[\]]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}/g;
  let t;
  while ((t = topRe.exec(top)) !== null) {
    expectedDefaults.set(t[1], "");
  }
  // appsettings.json 里显式写的（Urls 等 Kestrel 键）
  for (const [k, v] of Object.entries(appsettings)) {
    if (typeof v === "string" || typeof v === "number") {
      expectedDefaults.set(k, String(v));
    }
  }
}

// ── 工具 ───────────────────────────────────────────────────────────────
const stripPunct = (s) => s.replace(/^[\s"'`([{<]+/, "").replace(/[\s"'`)\]},.;:!?，。；：！？]+$/, "");

const knownRoots = ["server", "web", "skills", "docs", "openapi", "tools", ".github"];
const rootFiles = ["verify.sh", "IdeaApp.sln", "Directory.Build.props", "README.md", "README_EN.md"];

// 构建产物与依赖目录：干净克隆里本来就不存在（或在 .gitignore 里），查它们只会误报。
//
// ⚠️ 按**路径的每一段**判，不能只判"路径中间带斜杠的写法"：
// 早先那版判的是 `/node_modules/` 这种带前后斜杠的子串，于是
// `web/node_modules`（**结尾**就是它）漏网 —— 本地因为 node_modules 装着
// 看不出来，CI 的干净检出里就报「提到的路径 web/node_modules 不存在」。
// 判据改成"任何一段命中"就对了。
const generatedDirNames = new Set([
  "bin",
  "obj",
  "node_modules",
  "wwwroot",
  "dist",
  ".temp",
  ".tmp",
  "test-results",
  "playwright-report",
]);

function looksLikeRepoPath(token) {
  if (!token || /\s/.test(token)) return false;
  if (!/^[\w.\-@/]+$/.test(token)) return false;
  if (token.includes("*") || token.includes("…")) return false;
  if (token.startsWith("/")) return false;
  if (token.includes("://")) return false;
  if (token.split("/").some((segment) => generatedDirNames.has(segment))) return false;
  const head = token.split("/")[0];
  return knownRoots.includes(head) || rootFiles.includes(token);
}

function normalizeApiPath(p) {
  let s = p.replace(/[?#].*$/, "").replace(/\/+$/, "");
  s = s.replace(/\$\{?[A-Za-z_][A-Za-z0-9_]*\}?/g, "*"); // $ID / ${ID}
  s = s.replace(/\{[^}]*\}/g, "*"); // {id}
  s = s.replace(/\*/g, "*");
  return s;
}
const contractNormalized = new Set(contractPaths.map(normalizeApiPath));

// ── 逐个文档检查 ──────────────────────────────────────────────────────
for (const doc of DOCS) {
  if (!exists(doc)) {
    problems.push(`${doc}: 文件不存在`);
    continue;
  }
  const text = read(doc);
  const rel = doc;

  // 1) Markdown 链接
  const linkRe = /\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
  let lm;
  while ((lm = linkRe.exec(text)) !== null) {
    let target = lm[1];
    if (/^(https?:|mailto:|#)/.test(target)) continue;
    target = target.split("#")[0];
    if (!target) continue;
    if (target.startsWith("/")) {
      problems.push(`${rel}: 链接指向绝对路径「${lm[1]}」，应写成仓库内相对路径`);
      continue;
    }
    const abs = resolve(dirname(join(ROOT, rel)), target);
    if (!existsSync(abs)) {
      problems.push(`${rel}: 链接「${lm[1]}」指向不存在的路径`);
    }
  }

  // 2) 行内代码里的仓库路径
  const inlineRe = /`([^`\n]+)`/g;
  let im;
  while ((im = inlineRe.exec(text)) !== null) {
    for (const raw of im[1].split(/\s+/)) {
      const token = stripPunct(raw);
      if (!looksLikeRepoPath(token)) continue;
      if (!exists(token)) {
        problems.push(`${rel}: 提到的路径「${token}」不存在`);
      }
    }
  }

  // 3) 命令：npm 脚本 / dotnet / bash 脚本 / cd 目录
  const pkg = JSON.parse(read("web/package.json"));
  const scripts = new Set(Object.keys(pkg.scripts ?? {}));
  const npmRunRe = /\bnpm (?:--prefix web )?run ([A-Za-z0-9:_-]+)/g;
  let nm;
  while ((nm = npmRunRe.exec(text)) !== null) {
    if (!scripts.has(nm[1])) {
      problems.push(`${rel}: 用了 web/package.json 里没有的脚本「npm run ${nm[1]}」`);
    }
  }
  const dotnetRe = /\bdotnet (?:test|build|run|publish)\s+([^\s|]+)/g;
  let dm;
  while ((dm = dotnetRe.exec(text)) !== null) {
    let target = stripPunct(dm[1]);
    if (!target || target.startsWith("-")) continue;
    if (!exists(target)) {
      problems.push(`${rel}: dotnet 命令里的路径「${target}」不存在`);
    }
  }
  const bashRe = /\bbash\s+([^\s|]+\.sh)/g;
  let bm;
  while ((bm = bashRe.exec(text)) !== null) {
    const target = stripPunct(bm[1]);
    if (target.includes("$")) continue;
    if (!exists(target)) {
      problems.push(`${rel}: bash 命令里的脚本「${target}」不存在`);
    }
  }
  const cdRe = /\bcd\s+([A-Za-z0-9_./-]+)/g;
  let cm;
  while ((cm = cdRe.exec(text)) !== null) {
    const target = stripPunct(cm[1]);
    if (!target || target.includes("*")) continue;
    if (!isDir(target)) {
      problems.push(`${rel}: cd 到的目录「${target}」不存在`);
    }
  }

  // 4) 提到的 API 路径必须在契约里
  const apiRe = /(?<![\w`])\/(api|skill)\/[A-Za-z0-9_\-/{}$.*]*/g;
  let am;
  const mentioned = new Set();
  while ((am = apiRe.exec(text)) !== null) {
    if (am[0].includes("**")) continue; // 代码里的 catch-all 兜底路由，不是契约路径
    const norm = normalizeApiPath(am[0]);
    if (!norm || norm === "/api" || norm === "/skill") continue;
    mentioned.add(norm);
    if (!contractNormalized.has(norm)) {
      problems.push(`${rel}: 提到的路径「${am[0]}」不在 openapi/idea-v1.json 的契约里`);
    }
  }

  // 5) 配置表的默认值要与代码一致
  const tableRowRe = /^\|\s*`([^`]+)`\s*\|([^|]*)\|/gm;
  let tr;
  while ((tr = tableRowRe.exec(text)) !== null) {
    const key = tr[1].trim();
    if (!expectedDefaults.has(key)) continue;
    const cell = tr[2].replace(/[`*]/g, "").trim();
    const expected = expectedDefaults.get(key);
    if (expected === "") {
      if (cell && !/^(空|empty|无|none|null|未设置|"-")/i.test(cell)) {
        problems.push(`${rel}: 配置项 ${key} 的默认值应为「空」，README 写的是「${cell}」`);
      }
      continue;
    }
    const num = /^-?\d+(\.\d+)?/.exec(cell);
    if (/^-?\d+$/.test(expected)) {
      if (!num || num[0] !== expected) {
        problems.push(`${rel}: 配置项 ${key} 的默认值应为 ${expected}，README 写的是「${cell}」`);
      }
      continue;
    }
    const got = cell.replace(/^\s*["'`]|["'`]\s*$/g, "").trim();
    if (got !== expected) {
      problems.push(`${rel}: 配置项 ${key} 的默认值应为 ${expected}，README 写的是「${cell}」`);
    }
  }

  // 6) 「仓库没有 CI」这类说法与实际对不上（全局检查见下）
}

// 6) 「仓库没有 CI」这类说法与实际对不上；反之，README 没提 CI 也要提个醒
{
  const ciFiles = [];
  try {
    for (const f of readdirSync(join(ROOT, ".github/workflows"))) ciFiles.push(f);
  } catch {
    /* 没有 .github/workflows */
  }
  const claimHolders = ["README.md", "README_EN.md", "verify.sh"].filter((f) => exists(f));
  for (const f of claimHolders) {
    const t = read(f);
    const claimsNoCi = /没有\s*(?:任何\s*)?CI/.test(t) || /\bno\s+CI\b/i.test(t) || /without\s+CI/i.test(t);
    if (claimsNoCi && ciFiles.length > 0) {
      problems.push(`${f}: 声称仓库没有 CI，但 .github/workflows/ 下有 ${ciFiles.join("、")}`);
    }
  }
  if (ciFiles.length > 0) {
    const readmeMentions = ["README.md", "README_EN.md"]
      .filter((f) => exists(f))
      .some((f) => /\bCI\b|GitHub Actions|持续集成/.test(read(f)));
    if (!readmeMentions) {
      notes.push(`仓库有 CI（.github/workflows/${ciFiles.join("、")}），但两份 README 都没提 CI`);
    }
  }
}
// 7) 闸门步数与 CI 的 job 数：verify.sh ↔ README ↔ ci.yml
//
// 只查「文档里出现了这个数」是不够的 —— 改一处、忘一处照样漂。
// 这里查的是**闸门那一节**里的每一个计数：步数、道数、表里的行号、job 数，
// 全都要与 verify.sh / ci.yml 对得上。
{
  const src = read("verify.sh");
  const m = /TOTAL_STEPS=(\d+)/.exec(src);
  const cn = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];
  const en = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

  // 中文数字 → 数字（只到十，够用了）。
  const cnNumber = (word) => {
    if (/^\d+$/.test(word)) return Number(word);
    if (word === "十") return 10;
    const digits = "零一二三四五六七八九";
    let current = 0;
    for (const ch of word) {
      const d = digits.indexOf(ch);
      if (d < 0) return NaN;
      if (d !== 0) current = d;
    }
    return current;
  };
  const enNumber = (word) => {
    const lower = word.toLowerCase();
    if (/^\d+$/.test(lower)) return Number(lower);
    const i = en.indexOf(lower);
    return i < 0 ? NaN : i;
  };

  // 取出标题匹配的所有小节（到**同级或更高级**的标题为止）。
  const sections = (text, headingPattern) => {
    const lines = text.split("\n");
    const found = [];
    for (let i = 0; i < lines.length; i++) {
      const heading = /^(#{2,6})\s+(.*)$/.exec(lines[i]);
      if (!heading || !headingPattern.test(heading[2])) continue;
      const level = heading[1].length;
      const body = [lines[i]];
      for (let j = i + 1; j < lines.length; j++) {
        const next = /^(#{2,6})\s/.exec(lines[j]);
        if (next && next[1].length <= level) break;
        body.push(lines[j]);
      }
      found.push(body.join("\n"));
    }
    return found;
  };

  // ci.yml 里 job 的个数（jobs: 下面两格缩进的那些键）。
  const ciJobCount = (() => {
    if (!exists(".github/workflows/ci.yml")) return null;
    const lines = read(".github/workflows/ci.yml").split("\n");
    const at = lines.findIndex((line) => /^jobs:\s*$/.test(line));
    if (at < 0) return null;
    let count = 0;
    for (let i = at + 1; i < lines.length; i++) {
      if (/^  [a-z][a-z0-9_-]*:\s*$/.test(lines[i])) count++;
      else if (/^\S/.test(lines[i]) && lines[i].trim() !== "") break;
    }
    return count;
  })();

  if (m) {
    const n = Number(m[1]);
    const zh = read("README.md");
    const enDoc = exists("README_EN.md") ? read("README_EN.md") : "";

    // 闸门那几节（中文：「CI 与闸门」/「测试」；英文：「CI and Gates」/「Testing」）。
    //
    // 「一」/one 不参与比对：中文的「任一步」「这一步」与英文的「one step at a time」
    // 是量词，不是计数 —— 把它们当计数会把没漂移的文档判成漂了。
    const zhGates = sections(zh, /(闸门|测试)/);
    const enGates = sections(enDoc, /(Gates|Testing)/i);

    if (zhGates.length === 0) {
      problems.push("README.md: 找不到讲闸门的那一节（「## CI 与闸门」/「## 测试」）");
    }
    for (const block of zhGates) {
      for (const hit of block.matchAll(/([零二三四五六七八九十\d]+)\s*(步|道闸门)/g)) {
        if (cnNumber(hit[1]) !== n) {
          problems.push(`README.md: 闸门那节里写着「${hit[0]}」，但 verify.sh 是 ${n} 步`);
        }
      }
      // 步表里的行号必须恰好是 1..N。
      const rows = [...block.matchAll(/^\|\s*(\d+)\s*\|/gm)].map((hit) => Number(hit[1]));
      if (rows.length > 0) {
        const expected = Array.from({ length: n }, (_, i) => i + 1);
        if (rows.join(",") !== expected.join(",")) {
          problems.push(`README.md: 闸门那节的步表行号是 [${rows.join(",")}]，应当恰好是 1..${n}`);
        }
      }
    }

    if (enDoc) {
      if (enGates.length === 0) {
        problems.push("README_EN.md: 找不到讲闸门的那一节（「## CI and Gates」/「## Testing」）");
      }
      for (const block of enGates) {
        for (const hit of block.matchAll(
          /\b(two|three|four|five|six|seven|eight|nine|ten|\d+)\s+(steps?|gates?)\b/gi,
        )) {
          if (enNumber(hit[1]) !== n) {
            problems.push(`README_EN.md: 闸门那节里写着「${hit[0]}」，但 verify.sh 是 ${n} 步`);
          }
        }
        const rows = [...block.matchAll(/^\|\s*(\d+)\s*\|/gm)].map((hit) => Number(hit[1]));
        if (rows.length > 0) {
          const expected = Array.from({ length: n }, (_, i) => i + 1);
          if (rows.join(",") !== expected.join(",")) {
            problems.push(`README_EN.md: 闸门那节的步表行号是 [${rows.join(",")}]，应当恰好是 1..${n}`);
          }
        }
      }
    }

    // job 数：文档说的与 ci.yml 里实际的要对得上。
    if (ciJobCount !== null) {
      const zhJob = sections(zh, /闸门/).join("\n");
      const enJob = sections(enDoc, /Gates/i).join("\n");
      for (const [file, text, pattern] of [
        ["README.md", zhJob, /([零二三四五六七八九十\d]+)\s*个\s*(?:独立\s*)?job/gi],
        ["README_EN.md", enJob, /\b(two|three|four|five|six|seven|eight|nine|ten|\d+)\s+(?:independent\s+)?jobs?\b/gi],
      ]) {
        if (!text) continue;
        for (const hit of text.matchAll(pattern)) {
          const value = file === "README.md" ? cnNumber(hit[1]) : enNumber(hit[1]);
          if (value !== ciJobCount) {
            problems.push(`${file}: 写着「${hit[0]}」，但 .github/workflows/ci.yml 里是 ${ciJobCount} 个 job`);
          }
        }
      }
    }
  }
}

// 8) 钉住的 SDK 版本：global.json ↔ README ↔ CI
{
  const gj = "global.json";
  if (!exists(gj)) {
    problems.push("global.json 不在仓库根 —— SDK 版本没有任何工具层面的兜底");
  } else {
    // global.json 允许注释，用 JSON.parse 读不了：先去掉 // 与 /* */ 注释。
    const raw = read(gj)
      .replace(/\/\*[\s\S]*?\*\//g, "")
      .replace(/^\s*\/\/.*$/gm, "");
    let pinned = null;
    try {
      pinned = JSON.parse(raw)?.sdk?.version ?? null;
    } catch {
      problems.push("global.json 解析失败（JSON 非法？）");
    }
    if (pinned) {
      for (const f of DOCS.filter((d) => exists(d))) {
        if (!read(f).includes(pinned)) {
          problems.push(`${f}: 没写 global.json 钉住的 SDK 版本 ${pinned}`);
        }
      }
      const ci = read(".github/workflows/ci.yml");
      if (!ci.includes(pinned)) {
        problems.push(`.github/workflows/ci.yml: 没写 global.json 钉住的 SDK 版本 ${pinned}`);
      }
    }
  }
  // README 说「仓库里没有 global.json」而仓库里其实有 —— 那句话本身就是漂移。
  for (const f of DOCS.filter((d) => exists(d))) {
    const t = read(f);
    const claimsNone =
      /没有\s*`?global\.json`?/.test(t) || /no\s+`?global\.json`?/i.test(t);
    if (claimsNone && exists(gj)) {
      problems.push(`${f}: 说仓库里没有 global.json，但仓库根有（global.json 把 SDK 钉在 ${pinned ?? "?"}）`);
    }
  }
}

// ── 探测器自检 ─────────────────────────────────────────────────────────
//
// 「闸门没报错」有两种可能：文档没问题，或者**探测器哑了**。
// 这里把探测器拿几种已知的写法喂一遍，确认它该认的认、该跳的跳 ——
// 判据改坏时当场发现，而不是等到 CI 上误报一轮。
{
  const detectorCases = [
    // [写法, 该不该当成「仓库里必须存在的路径」]
    ["web/src/main.ts", true],
    ["openapi/idea-v1.json", true],
    ["verify.sh", true],
    ["skills/idea-manager/SKILL.md", true],
    // 产物目录：干净检出（CI）里本来就不存在，不能当「必须有」。
    ["web/node_modules", false],
    ["web/dist", false],
    ["server/IdeaApp/wwwroot", false],
    ["web/e2e/.tmp", false],
    ["web/test-results", false],
    // 不是仓库路径的东西，一律不当路径查。
    ["https://example.com/x", false],
    ["/etc/passwd", false],
    ["some other text", false],
  ];

  for (const [token, expected] of detectorCases) {
    const actual = looksLikeRepoPath(token);

    if (actual !== expected) {
      problems.push(
        `探测器自检没过：「${token}」${expected ? "应该" : "不应该"}被当成仓库路径`
        + `（实际${actual ? "被当成" : "没被当成"}）—— 判据改坏了，闸门会误报或漏报。`,
      );
    }
  }
}

// ── 提示（不判失败）────────────────────────────────────────────────────
if (exists("README.md") && exists("README_EN.md")) {
  const both = read("README.md") + "\n" + read("README_EN.md");
  const mentioned = new Set();
  const apiRe = /(?<![\w`])\/(api|skill)\/[A-Za-z0-9_\-/{}$.*]*/g;
  let am;
  while ((am = apiRe.exec(both)) !== null) {
    mentioned.add(normalizeApiPath(am[0]));
  }
  const missing = contractPaths.filter((p) => !mentioned.has(normalizeApiPath(p)));
  if (missing.length > 0) {
    notes.push(`两份 README 都没提到的契约端点（${missing.length} 个）：${missing.join(" ")}`);
  }
}

// ── 输出 ───────────────────────────────────────────────────────────────
for (const n of notes) console.log(`NOTE: ${n}`);
for (const p of problems) console.log(`PROBLEM: ${p}`);
if (problems.length > 0) {
  console.log(`FAIL: ${problems.length} 处问题。`);
  process.exit(1);
}
console.log(`OK: 两份 README 的链接、命令、API 路径、配置默认值、闸门步数、SDK 版本都没有漂移。`);
