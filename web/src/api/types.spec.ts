import { describe, expect, it } from 'vitest';
import type { Idea, IdeaEvent, ProjectSnapshot, SearchMatch } from './types';

/**
 * 这些用例主要是**类型层面**的回归护栏：
 * `npm run typecheck` 会因为它们编译不过而报错，
 * 防止有人再把 `changed` 写回「可能缺失」或把项目快照的 `sortOrder` 弄丢。
 */
describe('Idea 的线上报文形状', () => {
  it('Idea 没有 title —— 想法就是一段文字', () => {
    const idea: Idea = {
      id: 'i1',
      projectId: 'p1',
      content: '正文',
      tags: [],
      done: false,
      createdAt: '2026-11-12T00:00:00Z',
      updatedAt: '2026-11-12T00:00:00Z',
    };

    expect(idea.content).toBe('正文');

    // @ts-expect-error 想法没有标题：谁把这个字段加回来，编译期就红。
    expect(idea.title).toBeUndefined();
  });

  it('SearchMatch.field 只剩三种取值（命中位置不再有 title）', () => {
    const match: SearchMatch = { field: 'content', start: 0, length: 2 };

    expect(match.field).toBe('content');

    // @ts-expect-error title 不再是命中字段：搜标题这件事整个没有了。
    const bad: SearchMatch = { field: 'title', start: 0, length: 2 };
    expect(bad).toBeDefined();
  });
});

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

  it('与 undefined 比较不会被 TS 拦下 —— 那是运行时恒假的写法，判定要用 === null', () => {
    const evt: IdeaEvent = {
      id: 'e4',
      type: 'idea.created',
      timestamp: '2026-11-12T00:00:00Z',
      project: null,
      idea: null,
      changed: null,
      mergedFrom: null,
    };

    // 说明：TypeScript **不会**为 `x === undefined` 报 TS2367
    // （相等性比较的可比性检查在目标类型是 null/undefined 时直接放行），
    // 所以这条断言编译得过、运行时恒为 false —— 正好演示「写 === undefined 会静默走错分支」。
    expect(evt.changed === undefined).toBe(false);
    expect(evt.mergedFrom === undefined).toBe(false);
  });

  it('两个键是必填的：漏写会编译不过（TS2739）', () => {
    // @ts-expect-error 后端恒写出 changed 与 mergedFrom，构造事件时必须给全。
    const incomplete: IdeaEvent = {
      id: 'e5',
      type: 'idea.created',
      timestamp: '2026-11-12T00:00:00Z',
      project: null,
      idea: null,
    };

    expect(incomplete).toBeDefined();
  });

  it('不判空就取 length 会编译不过（TS18047）', () => {
    const evt: IdeaEvent = {
      id: 'e6',
      type: 'idea.updated',
      timestamp: '2026-11-12T00:00:00Z',
      project: null,
      idea: null,
      changed: ['content'],
      mergedFrom: null,
    };

    // @ts-expect-error changed 的类型含 null，编译期强制先判空再用。
    expect(evt.changed.length).toBe(1);
  });
});
