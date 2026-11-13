import { describe, expect, it } from 'vitest';
import { reorderToTarget } from './reorder';

describe('reorderToTarget', () => {
  it('把源挪到目标的位置上（顶替目标，不是插到它后面）', () => {
    // 拖 A 到 C 上：源落在目标的正上方（第二位），不是排到 C 后面。
    expect(reorderToTarget(['a', 'b', 'c'], 'a', 'c')).toEqual(['b', 'a', 'c']);
  });

  it('往后拖也是顶替目标的位置', () => {
    // 拖 C 到 A 上：源落在目标的正上方（第一位）。
    expect(reorderToTarget(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b']);
  });

  it('源已经在目标的正上方时顺序不变', () => {
    expect(reorderToTarget(['a', 'b', 'c'], 'a', 'b')).toEqual(['a', 'b', 'c']);
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
