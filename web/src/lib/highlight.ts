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
 * 判断每枚标签要不要标记（搜索的关键词打在标签名上）。
 *
 * 服务端的偏移基准是「所有标签名按返回顺序用空格连接」的那条串
 * （server 端 `string.Join(" ", tags)`，顺序就是 `idea.tags` 的顺序 ——
 * 两边都按名称排序），所以这里用同样的方式拼、同样的方式算每个标签的区间。
 *
 * 一个标签只要有**任何一段**命中区间落在它的字符范围内就算命中 ——
 * 标签很短，按「整枚 chip 要不要标记」算就够了，不做片段级的高亮。
 *
 * ⚠️ 只看 **tags 字段**的命中：其它字段的偏移基准不同（相对各自字段原文），
 * 混在一起会算错。
 *
 * @param tags 标签名列表（`idea.tags` 的顺序）。
 * @param matches 同一条想法的全部命中区间（字段在这里过滤）。
 */
export function tagHitFlags(tags: string[], matches: SearchMatch[]): boolean[] {
  const ranges = mergeRanges(matches.filter((match) => match.field === 'tags'));
  const flags: boolean[] = [];
  let cursor = 0;
  for (const tag of tags) {
    const start = cursor;
    const end = start + tag.length;
    // 空格分隔符不占标签的区间；命中区间也不会以空白开头或结尾（词元起、词元止），
    // 所以「与标签区间有交集」就是「打在这个标签上」。
    cursor = end + 1;
    flags.push(ranges.some((range) => range.start < end && range.end > start));
  }
  return flags;
}
