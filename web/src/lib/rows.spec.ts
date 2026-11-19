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
