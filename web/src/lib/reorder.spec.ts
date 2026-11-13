import { describe, expect, it } from 'vitest';
import { reorderToTarget } from './reorder';

describe('reorderToTarget', () => {
  it('往下拖：落到目标之后', () => {
    // [A,B,C] 拖 A 到 C 上 → [B,C,A]
    expect(reorderToTarget(['a', 'b', 'c'], 'a', 'c')).toEqual(['b', 'c', 'a']);
  });

  it('往上拖：落到目标之前', () => {
    // [A,B,C] 拖 C 到 A 上 → [C,A,B]
    expect(reorderToTarget(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b']);
  });

  it('往下挪一格也有效果（没有「拖了等于没动」的死角）', () => {
    // [A,B,C] 拖 A 到 B 上 → [B,A,C]
    expect(reorderToTarget(['a', 'b', 'c'], 'a', 'b')).toEqual(['b', 'a', 'c']);
  });

  it('往上挪一格也有效果', () => {
    // [A,B,C] 拖 C 到 B 上 → [A,C,B]
    expect(reorderToTarget(['a', 'b', 'c'], 'c', 'b')).toEqual(['a', 'c', 'b']);
  });

  it('拖到自己身上不动', () => {
    expect(reorderToTarget(['a', 'b'], 'a', 'a')).toEqual(['a', 'b']);
  });

  it('源或目标不在列表里时原样返回', () => {
    expect(reorderToTarget(['a', 'b'], 'x', 'b')).toEqual(['a', 'b']);
    expect(reorderToTarget(['a', 'b'], 'a', 'x')).toEqual(['a', 'b']);
  });

  it('不改动传进来的数组', () => {
    const ids = ['a', 'b', 'c'];
    reorderToTarget(ids, 'a', 'c');
    expect(ids).toEqual(['a', 'b', 'c']);
  });
});
