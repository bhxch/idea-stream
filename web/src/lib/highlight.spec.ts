import { describe, expect, it } from 'vitest';
import type { SearchMatch } from '../api/types';
import { highlightLines, splitTitleAndBody } from './highlight';

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

describe('splitTitleAndBody', () => {
  it('splitTitleAndBody_puts_the_first_non_empty_line_in_the_title', () => {
    const { title, body } = splitTitleAndBody(highlightLines('咖啡\n水温', []));

    expect(title).toEqual([{ text: '咖啡', marked: false }]);
    expect(body).toEqual([{ text: '水温', marked: false }]);
  });

  it('splitTitleAndBody_skips_leading_blank_lines', () => {
    const { title, body } = splitTitleAndBody(highlightLines('\n咖啡\n水温', []));

    expect(title).toEqual([{ text: '咖啡', marked: false }]);
    expect(body).toEqual([{ text: '水温', marked: false }]);
  });

  it('splitTitleAndBody_drops_the_blank_lines_between_the_title_and_the_body', () => {
    // 标题行后面的空行是分隔用的：正文紧挨着视觉标题。
    const { title, body } = splitTitleAndBody(highlightLines('咖啡\n\n水温', []));

    expect(title).toEqual([{ text: '咖啡', marked: false }]);
    expect(body).toEqual([{ text: '水温', marked: false }]);
  });

  it('splitTitleAndBody_keeps_the_hit_on_the_title_line_with_the_title', () => {
    // 落在标题行上的命中归标题 —— 不能错切到正文里。
    const { title, body } = splitTitleAndBody(highlightLines('咖啡\n水温', [match(0, 2)]));

    expect(title).toEqual([{ text: '咖啡', marked: true }]);
    expect(body).toEqual([{ text: '水温', marked: false }]);
  });

  it('splitTitleAndBody_trims_the_title', () => {
    // 组件的视觉标题是 trim 过的：片段的首尾空白也要去掉。
    const { title } = splitTitleAndBody(highlightLines('  咖啡  \n水温', []));

    expect(title).toEqual([{ text: '咖啡', marked: false }]);
  });
});
