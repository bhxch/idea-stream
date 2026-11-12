import { describe, expect, it } from 'vitest';
import type { IdeaEvent, ProjectSnapshot } from './types';

/**
 * 这些用例主要是**类型层面**的回归护栏：
 * `npm run typecheck` 会因为它们编译不过而报错，
 * 防止有人再把 `changed` 写回「可能缺失」或把项目快照的 `sortOrder` 弄丢。
 */
describe('IdeaEvent 的线上报文形状', () => {
  it('changed 与 mergedFrom 可以是 null —— 后端恒写出这两个键', () => {
    const evt: IdeaEvent = {
      id: 'e1',
      type: 'idea.created',
      timestamp: '2026-11-12T00:00:00Z',
      project: null,
      idea: null,
      changed: null,
      mergedFrom: null,
    };

    expect(evt.changed).toBeNull();
    expect(evt.mergedFrom).toBeNull();
  });

  it('changed 也可以是空数组与非空数组（三态都要能表达）', () => {
    const base = {
      id: 'e2',
      type: 'idea.updated',
      timestamp: '2026-11-12T00:00:00Z',
      project: null,
      idea: null,
    } satisfies Omit<IdeaEvent, 'changed' | 'mergedFrom'>;

    const empty: IdeaEvent = { ...base, changed: [], mergedFrom: null };
    const filled: IdeaEvent = { ...base, changed: ['content'], mergedFrom: ['src-1'] };

    expect(empty.changed).toHaveLength(0);
    expect(filled.changed).toEqual(['content']);
    expect(filled.mergedFrom).toEqual(['src-1']);
  });

  it('项目快照带着 sortOrder —— 订阅端要靠它更新本地顺序', () => {
    const snapshot: ProjectSnapshot = { id: 'p1', name: '项目', sortOrder: 3 };
    const evt: IdeaEvent = {
      id: 'e3',
      type: 'project.updated',
      timestamp: '2026-11-12T00:00:00Z',
      project: snapshot,
      idea: null,
      changed: ['sortOrder'],
      mergedFrom: null,
    };

    expect(evt.project?.sortOrder).toBe(3);
    expect(evt.project?.name).toBe('项目');
  });
});
