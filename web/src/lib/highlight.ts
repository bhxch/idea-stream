/**
 * 搜索命中的高亮片段。
 *
 * 命中位置由服务端给（字段 + 字符区间），这里把原文按区间切成片段，
 * 渲染时命中的那几段包 `<mark>`。
 *
 * 两条约定：
 * - 区间相对**完整原文**（后端给的就是这个基准），不是相对某一行的起点；
 * - 区间用 **UTF-16 code unit** 计数，跟 JS 的 `String.prototype.slice` 对齐，
 *   所以切片段不用做任何字符数换算。
 *
 * ⚠️ **不要用 `v-html` 渲染正文**：正文是用户随手写的文本，走 HTML 解析
 * 就成了注入面。这里切成片段、按文本插值渲染，转义交给 Vue。
 */
import type { SearchMatch } from '../api/types';

/** 一段文本及其是否命中。 */
export interface HighlightSegment {
  text: string;
  marked: boolean;
}

/** 一行（按 \n 切开）及其片段。 */
export interface HighlightLine {
  /** 这一行在原文里的起始偏移（UTF-16 code unit）。 */
  start: number;
  /** 这一行的片段序列。 */
  segments: HighlightSegment[];
}

/** 一段命中区间：闭开区间 `[start, end)`。 */
interface MatchRange {
  start: number;
  end: number;
}

/**
 * 把命中区间合并成互不重叠的一段段。
 *
 * 服务端允许给出**重复或重叠**的区间（查询「咖啡 咖啡」会给两处一模一样的区间，
 * 「哈哈哈哈」搜「哈哈」会给两处自重叠的区间）—— 契约里写明由调用方合并。
 * 不合并的话渲染出来会是嵌套的 `<mark>`，或者同一个词被标两次。
 *
 * @param matches 同一个字段的命中区间（不同字段的偏移基准不同，不能混在一起）。
 */
function mergeRanges(matches: SearchMatch[]): MatchRange[] {
  const ranges = matches
    .filter((match) => match.length > 0)
    .map((match) => ({ start: match.start, end: match.start + match.length }))
    .sort((left, right) => left.start - right.start);

  const merged: MatchRange[] = [];
  for (const range of ranges) {
    const last = merged[merged.length - 1];
    // 挨着的（`start === last.end`）也合并：中间没有空白，合成一段看不出来。
    if (last && range.start <= last.end) {
      last.end = Math.max(last.end, range.end);
      continue;
    }
    merged.push(range);
  }
  return merged;
}

/**
 * 切一行：按落在这一行里的命中区间把行切成片段。
 *
 * 命中跨过行界时，两行各留一段（各自都是命中的）—— 高亮不会丢。
 *
 * @param raw 这一行的文本（不含换行符）。
 * @param lineStart 这一行在原文里的起始偏移。
 * @param lineEnd 这一行在原文里的结束偏移（不含）。
 * @param ranges 已合并、按起点排序的命中区间。
 */
function cutLine(
  raw: string,
  lineStart: number,
  lineEnd: number,
  ranges: MatchRange[],
): HighlightSegment[] {
  const segments: HighlightSegment[] = [];
  let cursor = lineStart;

  for (const range of ranges) {
    if (range.end <= lineStart) {
      // 整段在这一行之前。
      continue;
    }
    if (range.start >= lineEnd) {
      // 整段在这一行之后（区间按起点排序，后面的只会更靠后）。
      break;
    }
    const from = Math.max(range.start, lineStart);
    const to = Math.min(range.end, lineEnd);
    if (from > cursor) {
      segments.push({ text: raw.slice(cursor - lineStart, from - lineStart), marked: false });
    }
    segments.push({ text: raw.slice(from - lineStart, to - lineStart), marked: true });
    cursor = to;
  }

  if (cursor < lineEnd) {
    segments.push({ text: raw.slice(cursor - lineStart, lineEnd - lineStart), marked: false });
  }
  return segments;
}

/**
 * 把原文按行切开，并在每行内按命中区间标出片段。
 * 命中区间相对**完整原文**（后端给的就是这个基准）。
 * 没有命中时返回每行一个未命中的片段。
 *
 * @param text 原文（通常是某个字段的完整文本）。
 * @param matches **同一个字段**的命中区间：不同字段的偏移基准不同，混在一起会切错。
 * @returns 每行的起始偏移与片段序列。
 */
export function highlightLines(text: string, matches: SearchMatch[]): HighlightLine[] {
  const ranges = mergeRanges(matches);
  const lines: HighlightLine[] = [];

  let start = 0;
  for (const raw of text.split('\n')) {
    const end = start + raw.length;
    lines.push({ start, segments: cutLine(raw, start, end, ranges) });
    // 下一行的起点：这一行的长度 + 换行符本身。
    start = end + 1;
  }
  return lines;
}

/**
 * 把多行的片段序列摊平成一段序列：行与行之间补上换行符。
 *
 * 片段是按行给的（每行一个 {@link HighlightLine}），渲染时却要一整段连续文本 ——
 * 行与行之间的换行也是正文的一部分，不能丢（正文是 `pre-wrap` 渲染的，换行看得见）。
 *
 * @param lines 按行切好的片段序列。
 */
export function flattenLines(lines: HighlightLine[]): HighlightSegment[] {
  const segments: HighlightSegment[] = [];
  lines.forEach((line, index) => {
    if (index > 0) {
      segments.push({ text: '\n', marked: false });
    }
    segments.push(...line.segments);
  });
  return segments;
}

/**
 * 去掉片段序列首尾的空白（组件的视觉标题是 trim 过的）。
 *
 * trim 要**跨片段**做：首尾的空白可能横跨好几段 —— 标题里的空行、
 * flattenLines 补的换行分隔符，各自都是独立的片段。
 * 只动首尾两段的话，夹在中间的「整段空白」会留下来，trim 出来的标题
 * 与 `title?.trim()` 对不上（`textContent` 与 aria-label 里会带着换行）。
 *
 * 做法：先算出整段纯文本里「非空白」的区间，再按区间重新切片段。
 * 命中区间从词元起、到词元止，不会以空白开头或结尾 —— 在边界上被切掉的
 * 只会是未命中的空白片段，高亮不会丢。（实现仍按「区间与片段的交集」切，
 * 就算命中真的压到了边界，剩下的那截还是 marked，不会静默丢高亮。）
 *
 * @param segments 片段序列。
 */
export function trimSegments(segments: HighlightSegment[]): HighlightSegment[] {
  if (segments.length === 0) {
    return [];
  }

  const plain = segments.map((segment) => segment.text).join('');
  const start = plain.search(/\S/);
  if (start < 0) {
    // 全是空白：trim 完什么都不剩。
    return [];
  }

  const trailing = plain.match(/\s+$/);
  // `match.index` 的类型是 `number | undefined`（正则没开 `d` 标志时 JS 的类型面保守）；
  // 带尾随空白时 index 一定有值，`?? plain.length` 兜的是类型面不是运行时。
  const end = trailing?.index ?? plain.length;

  const result: HighlightSegment[] = [];
  let cursor = 0;
  for (const segment of segments) {
    const segmentStart = cursor;
    const segmentEnd = segmentStart + segment.text.length;
    cursor = segmentEnd;

    const from = Math.max(segmentStart, start);
    const to = Math.min(segmentEnd, end);
    if (to <= from) {
      // 整段落在 trim 掉的区间里（空行、换行分隔符）—— 丢掉。
      continue;
    }
    result.push({
      text: segment.text.slice(from - segmentStart, to - segmentStart),
      marked: segment.marked,
    });
  }
  return result;
}

/** 一行的纯文本（把它的片段拼起来）。 */
function lineText(line: HighlightLine): string {
  return line.segments.map((segment) => segment.text).join('');
}

/** 去掉正文开头那些只装换行符的片段（正文紧挨着视觉标题，不要空行）。 */
function trimLeadingNewlines(segments: HighlightSegment[]): HighlightSegment[] {
  let index = 0;
  while (index < segments.length && segments[index].text === '\n') {
    index += 1;
  }
  return segments.slice(index);
}

/**
 * 把片段序列按「第一个非空行」分成视觉标题与正文两组。
 * 标题为空时，组件会把第一个非空行摘出来当视觉标题 ——
 * 落在这行上的命中要跟着标题走，不能错切到正文里。
 *
 * @param lines 按行切好的片段序列（见 {@link highlightLines}）。
 * @returns 标题组与正文组的片段序列。
 */
export function splitTitleAndBody(lines: HighlightLine[]): {
  title: HighlightSegment[];
  body: HighlightSegment[];
} {
  const titleIndex = lines.findIndex((line) => lineText(line).trim().length > 0);
  if (titleIndex < 0) {
    // 全是空行：没有标题，正文就是全部。
    return { title: [], body: flattenLines(lines) };
  }
  return {
    // 视觉标题是 trim 过的，片段的首尾空白也得去掉。
    title: trimSegments(lines[titleIndex].segments),
    // 正文从标题行之后开始；开头跟着的空行是分隔用的，一并去掉。
    body: trimLeadingNewlines(flattenLines(lines.slice(titleIndex + 1))),
  };
}
