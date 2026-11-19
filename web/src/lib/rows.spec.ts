import { describe, expect, it } from 'vitest';
import type { Idea } from '../api/types';
import { toRows } from './rows';

/**
 * 造一条想法。
 *
 * `toRows` 只关心顺序与身份，所以其余字段照占位填齐。
 */
function makeIdea(id: string): Idea {
  return {
    id,
    projectId: 'project-1',
    content: `想法 ${id}`,
    tags: [],
    done: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  };
}

/** 造 n 条想法（id 依次是 1..n，顺序即传入顺序）。 */
function makeIdeas(count: number): Idea[] {
  return Array.from({ length: count }, (_, index) => makeIdea(String(index + 1)));
}

describe('toRows', () => {
  it('toRows_pairs_the_ideas_up', () => {
    const rows = toRows(makeIdeas(5));

    expect(rows).toHaveLength(3);
    // 第 1、3、5 条在左格。
    expect(rows.map((row) => row.left.id)).toEqual(['1', '3', '5']);
    // 第 2、4 条在右格；最后一条只有一条，右格留空。
    expect(rows.map((row) => row.right?.id ?? null)).toEqual(['2', '4', null]);
  });

  it('toRows_of_an_empty_list_is_empty', () => {
    expect(toRows([])).toEqual([]);
  });

  it('toRows_keeps_the_input_order', () => {
    const ideas = makeIdeas(5);

    const flattened = toRows(ideas).flatMap((row) =>
      row.right ? [row.left, row.right] : [row.left],
    );

    expect(flattened.map((idea) => idea.id)).toEqual(ideas.map((idea) => idea.id));
  });
});

/**
 * 每条想法的**位置序号**（1 = 最老的那条，屏幕最上）。
 *
 * 位置序号是**渲染层**算的（MessageList 的 `rowIndex * 2 + 1`）——
 * 这里照着那个算术从 `toRows` 的结果反推出来。
 *
 * @param ideas 已加载并排好序的想法。
 * @returns id → 位置序号。
 */
function positionNumbers(ideas: Idea[]): Map<string, number> {
  const numbers = new Map<string, number>();
  toRows(ideas).forEach((row, rowIndex) => {
    numbers.set(row.left.id, rowIndex * 2 + 1);
    if (row.right) {
      numbers.set(row.right.id, rowIndex * 2 + 2);
    }
  });
  return numbers;
}

describe('toRows 的奇偶（往回翻 prepend 之后）', () => {
  it('toRows_keeps_the_parity_when_an_even_number_of_items_is_prepended', () => {
    const existing = makeIdeas(4);
    // 原来那 4 条的位置序号是 1..4。
    const before = positionNumbers(existing);
    expect([before.get('1'), before.get('2'), before.get('3'), before.get('4')]).toEqual([
      1, 2, 3, 4,
    ]);

    // 在**头部** prepend 2 条（偶数）。
    const prepended = [makeIdea('pre-1'), makeIdea('pre-2')];
    const after = positionNumbers([...prepended, ...existing]);

    // 位置序号整体 +2。
    expect([after.get('1'), after.get('2'), after.get('3'), after.get('4')]).toEqual([
      3, 4, 5, 6,
    ]);
    // 奇偶不变 → 「奇数在左、偶数在右」不换边。
    const rows = toRows([...prepended, ...existing]);
    expect(rows.map((row) => row.left.id)).toEqual(['pre-1', '1', '3']);
    expect(rows.map((row) => row.right?.id ?? null)).toEqual(['pre-2', '2', '4']);
  });

  it('toRows_flips_the_parity_when_an_odd_number_of_items_is_prepended', () => {
    const existing = makeIdeas(2);
    const prepended = [makeIdea('pre-1'), makeIdea('pre-2'), makeIdea('pre-3')];

    const after = positionNumbers([...prepended, ...existing]);

    // 位置序号整体 +3。
    expect([after.get('1'), after.get('2')]).toEqual([4, 5]);
    // 奇偶翻转 → 整墙换边（规格接受的边界：只有翻到最开头那一页会发生一次）。
    const rows = toRows([...prepended, ...existing]);
    expect(rows.map((row) => row.left.id)).toEqual(['pre-1', 'pre-3', '2']);
    expect(rows.map((row) => row.right?.id ?? null)).toEqual(['pre-2', '1', null]);
  });
});
