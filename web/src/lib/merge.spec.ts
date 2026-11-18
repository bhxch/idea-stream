import { describe, expect, it } from 'vitest';
import type { Idea, IdeaEvent } from '../api/types';
import { applyEvent, applyResync, findAnchorIndex } from './merge';
import type { IdeaFilter } from './filter';

/** 一条想法。 */
function makeIdea(overrides: Partial<Idea> = {}): Idea {
  return {
    id: 'idea-1',
    projectId: 'project-1',
    content: '一条想法',
    tags: [],
    done: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
    ...overrides,
  };
}

/** 不带任何过滤的过滤条件（全都显示）。 */
const looseFilter: IdeaFilter = {
  projectId: null,
  status: 'all',
  tags: [],
  tagMode: 'or',
};

/** 只看未完成。 */
const openFilter: IdeaFilter = { ...looseFilter, status: 'open' };

/** 造一条事件。 */
function makeEvent(type: string, overrides: Partial<IdeaEvent> = {}): IdeaEvent {
  return {
    id: 'event-1',
    type,
    timestamp: '2026-11-12T00:00:00Z',
    project: null,
    idea: null,
    changed: null,
    mergedFrom: null,
    ...overrides,
  };
}

describe('applyEvent', () => {
  it('idea.created 追加到列表末尾', () => {
    const existing = makeIdea({ id: 'idea-1' });
    const created = makeIdea({ id: 'idea-2', updatedAt: '2026-11-02T10:00:00Z' });

    const result = applyEvent([existing], makeEvent('idea.created', { idea: created }), looseFilter);

    expect(result.map((idea) => idea.id)).toEqual(['idea-1', 'idea-2']);
  });

  it('idea.updated 替换并重排到末尾（改了 updatedAt 的那条排到最后）', () => {
    const older = makeIdea({ id: 'idea-1', updatedAt: '2026-11-01T10:00:00Z' });
    const newer = makeIdea({ id: 'idea-2', updatedAt: '2026-11-05T10:00:00Z' });
    const updated = makeIdea({
      id: 'idea-1',
      content: '改过的正文',
      updatedAt: '2026-11-09T10:00:00Z',
    });

    const result = applyEvent([older, newer], makeEvent('idea.updated', { idea: updated }), looseFilter);

    // 换成了事件里的那条，并且因为 updatedAt 最新而排到了末尾。
    expect(result.map((idea) => idea.id)).toEqual(['idea-2', 'idea-1']);
    expect(result[1].content).toBe('改过的正文');
  });

  it('idea.merged 移除源 id 并加入新条目', () => {
    const a = makeIdea({ id: 'idea-a' });
    const b = makeIdea({ id: 'idea-b' });
    const other = makeIdea({ id: 'idea-c' });
    const merged = makeIdea({ id: 'idea-merged', content: '甲\n\n乙' });

    const result = applyEvent(
      [a, b, other],
      makeEvent('idea.merged', { idea: merged, mergedFrom: ['idea-a', 'idea-b'] }),
      looseFilter,
    );

    expect(result.map((idea) => idea.id)).toEqual(['idea-c', 'idea-merged']);
  });

  it('不符合过滤条件的 idea 不插入', () => {
    const done = makeIdea({ id: 'idea-done', done: true });

    const result = applyEvent([], makeEvent('idea.created', { idea: done }), openFilter);

    expect(result).toEqual([]);
  });

  it('对已存在的同一 id 不产生重复', () => {
    const created = makeIdea({ id: 'idea-1' });
    const event = makeEvent('idea.created', { idea: created });

    const once = applyEvent([], event, looseFilter);
    const twice = applyEvent(once, event, looseFilter);

    expect(twice).toHaveLength(1);
    expect(twice[0].id).toBe('idea-1');
  });

  it('idea.updated 之后条目不再符合过滤条件，就把它从列表里拿掉', () => {
    const list = [makeIdea({ id: 'idea-1', done: false })];

    const result = applyEvent(
      list,
      makeEvent('idea.updated', { idea: makeIdea({ id: 'idea-1', done: true }) }),
      openFilter,
    );

    expect(result).toEqual([]);
  });

  it('idea.deleted 移除对应条目', () => {
    const list = [makeIdea({ id: 'idea-1' }), makeIdea({ id: 'idea-2' })];

    const result = applyEvent(
      list,
      makeEvent('idea.deleted', { idea: makeIdea({ id: 'idea-1' }) }),
      looseFilter,
    );

    expect(result.map((idea) => idea.id)).toEqual(['idea-2']);
  });

  it('idea.restored 追加条目', () => {
    const restored = makeIdea({ id: 'idea-9' });

    const result = applyEvent(
      [makeIdea({ id: 'idea-1' })],
      makeEvent('idea.restored', { idea: restored }),
      looseFilter,
    );

    expect(result.map((idea) => idea.id)).toEqual(['idea-1', 'idea-9']);
  });

  it('project.* 事件不动想法列表（项目变更归项目 store 管）', () => {
    const list = [makeIdea({ id: 'idea-1' })];

    expect(applyEvent(list, makeEvent('project.updated'), looseFilter)).toBe(list);
    expect(applyEvent(list, makeEvent('project.created'), looseFilter)).toBe(list);
    expect(applyEvent(list, makeEvent('project.deleted'), looseFilter)).toBe(list);
  });
});

describe('applyResync', () => {
  it('用新列表整体替换', () => {
    const list = [makeIdea({ id: 'idea-1' })];
    const fresh = [makeIdea({ id: 'idea-2' }), makeIdea({ id: 'idea-3' })];

    const result = applyResync(list, fresh);

    expect(result.map((idea) => idea.id)).toEqual(['idea-2', 'idea-3']);
  });

  it('新列表是空的就清空（服务端说范围内一条都没有）', () => {
    const result = applyResync([makeIdea({ id: 'idea-1' })], []);

    expect(result).toEqual([]);
  });
});

describe('findAnchorIndex', () => {
  it('找到目标返回下标', () => {
    const list = [makeIdea({ id: 'idea-1' }), makeIdea({ id: 'idea-2' })];

    expect(findAnchorIndex(list, 'idea-2')).toBe(1);
  });

  it('找不到返回 -1', () => {
    const list = [makeIdea({ id: 'idea-1' })];

    expect(findAnchorIndex(list, 'idea-x')).toBe(-1);
  });

  it('id 为 undefined 返回 -1', () => {
    const list = [makeIdea({ id: 'idea-1' })];

    expect(findAnchorIndex(list, undefined)).toBe(-1);
  });
});
