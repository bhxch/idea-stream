import { describe, expect, it } from 'vitest';
import type { SearchMatch } from '../api/types';
import { flattenLines, highlightLines, tagHitFlags } from './highlight';

/** 造一处命中（默认在正文里）。 */
function match(start: number, length: number, field: SearchMatch['field'] = 'content'): SearchMatch {
  return { field, start, length };
}

describe('highlightLines', () => {
  it('highlightLines_marks_the_matching_range', () => {
    const lines = highlightLines('咖啡机滤芯', [match(0, 2)]);

    expect(lines[0].segments).toEqual([
      { text: '咖啡', marked: true },
      { text: '机滤芯', marked: false },
    ]);
  });

  it('highlightLines_splits_unmatched_text', () => {
    const lines = highlightLines('买咖啡机', [match(1, 2)]);

    expect(lines[0].segments).toEqual([
      { text: '买', marked: false },
      { text: '咖啡', marked: true },
      { text: '机', marked: false },
    ]);
  });

  it('highlightLines_offsets_are_relative_to_the_whole_text', () => {
    const lines = highlightLines('第一行\n第二行咖啡', [match(7, 2)]);

    // 第一行整行都没命中。
    expect(lines[0].segments).toEqual([{ text: '第一行', marked: false }]);
    // 命中在第二行末尾（偏移是相对**整段原文**的，不是相对这一行）。
    expect(lines[1].segments).toEqual([
      { text: '第二行', marked: false },
      { text: '咖啡', marked: true },
    ]);
  });

  it('highlightLines_with_no_matches_returns_plain_lines', () => {
    const lines = highlightLines('第一行\n第二行', []);

    expect(lines).toEqual([
      { start: 0, segments: [{ text: '第一行', marked: false }] },
      { start: 4, segments: [{ text: '第二行', marked: false }] },
    ]);
  });

  it('highlightLines_merges_duplicate_and_overlapping_ranges', () => {
    // 服务端允许给出重复或重叠的区间（契约里写明由前端合并）：
    // 不合并的话 <mark> 会嵌套、或者同一个词被标两次。
    const lines = highlightLines('咖啡咖啡', [match(0, 2), match(0, 2), match(1, 2)]);

    expect(lines[0].segments).toEqual([
      { text: '咖啡咖', marked: true },
      { text: '啡', marked: false },
    ]);
  });

  it('highlightLines_keeps_the_highlight_of_a_range_crossing_a_line_boundary', () => {
    // 命中跨过行界：两行各自留一段，高亮不丢。
    const lines = highlightLines('咖啡\n水温', [match(0, 5)]);

    expect(lines[0].segments).toEqual([{ text: '咖啡', marked: true }]);
    expect(lines[1].segments).toEqual([{ text: '水温', marked: true }]);
  });
});

describe('flattenLines', () => {
  it('行与行之间补上换行符（正文是 pre-wrap 渲染的，换行看得见）', () => {
    const segments = flattenLines(highlightLines('第一行\n第二行', []));

    expect(segments).toEqual([
      { text: '第一行', marked: false },
      { text: '\n', marked: false },
      { text: '第二行', marked: false },
    ]);
  });

  it('命中的那几段在摊平之后仍然带着 marked', () => {
    const segments = flattenLines(highlightLines('第一行\n咖啡', [match(4, 2)]));

    expect(segments).toEqual([
      { text: '第一行', marked: false },
      { text: '\n', marked: false },
      { text: '咖啡', marked: true },
    ]);
  });
});

describe('tagHitFlags', () => {
  it('偏移基准是「标签名按返回顺序用空格连接」的那条串', () => {
    // 「咖啡」落在 [0,2)，「烘焙」落在 [3,5)：命中打在第二个上。
    expect(tagHitFlags(['咖啡', '烘焙'], [match(3, 2, 'tags')])).toEqual([false, true]);
  });

  it('多个标签被同一段命中压住时各自都算命中', () => {
    // 查询「咖 烘」不会发生（词元相邻），但一段命中跨过两个短标签是可能的
    // （标签 a、b，命中区间压住它们之间的空格也是 server 给得出的形状）。
    expect(tagHitFlags(['a', 'b'], [match(0, 3, 'tags')])).toEqual([true, true]);
  });

  it('其它字段的命中不算（偏移基准不同，不能混）', () => {
    // content 字段里也有 [0,2)，但那是正文的坐标 —— 打在标签上的是 tags 字段。
    expect(tagHitFlags(['咖啡'], [match(0, 2)])).toEqual([false]);
  });

  it('没有命中时全是 false', () => {
    expect(tagHitFlags(['咖啡', '烘焙'], [])).toEqual([false, false]);
  });
});
