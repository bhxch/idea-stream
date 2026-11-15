import { describe, expect, it } from 'vitest';
import type { Idea } from '../api/types';
import { hasActiveFilter, matchesFilter, matchesKeyword, sortIdeas, type IdeaFilter } from './filter';

/** 造一条想法，只覆盖要测的字段。 */
function makeIdea(overrides: Partial<Idea> = {}): Idea {
  return {
    id: 'idea-1',
    projectId: 'project-1',
    title: null,
    content: '一条想法',
    tags: [],
    done: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
    ...overrides,
  };
}

/** 造一份过滤条件，默认不过滤任何东西。 */
function makeFilter(overrides: Partial<IdeaFilter> = {}): IdeaFilter {
  return {
    projectId: null,
    status: 'all',
    tags: [],
    tagMode: 'or',
    keyword: '',
    ...overrides,
  };
}

describe('matchesKeyword', () => {
  it('matchesKeyword 空关键词：一律命中（不过滤）', () => {
    const idea = makeIdea({ content: '一条想法' });
    expect(matchesKeyword(idea, '')).toBe(true);
  });

  it('matchesKeyword 只有空白：按空关键词处理（不是「一条都搜不到」）', () => {
    const idea = makeIdea({ content: '一条想法' });
    expect(matchesKeyword(idea, '   ')).toBe(true);
  });

  it('matchesKeyword 子串命中', () => {
    const idea = makeIdea({ content: '今天想到一个点子' });
    expect(matchesKeyword(idea, '点子')).toBe(true);
  });

  it('matchesKeyword 不命中', () => {
    const idea = makeIdea({ content: '今天想到一个点子' });
    expect(matchesKeyword(idea, '买牛奶')).toBe(false);
  });

  it('matchesKeyword 忽略大小写', () => {
    const idea = makeIdea({ content: 'Idea about Kafka' });
    expect(matchesKeyword(idea, 'kafka')).toBe(true);
    expect(matchesKeyword(idea, 'KAFKA')).toBe(true);
  });

  it('matchesKeyword 关键词两侧的空白先 trim 掉', () => {
    const idea = makeIdea({ content: '今天想到一个点子' });
    expect(matchesKeyword(idea, '  点子  ')).toBe(true);
  });
});

describe('matchesFilter', () => {
  it('matchesFilter or 模式：任一标签命中即可', () => {
    const idea = makeIdea({ tags: ['a', 'b'] });
    expect(matchesFilter(idea, makeFilter({ tags: ['b', 'c'], tagMode: 'or' }))).toBe(true);
  });

  it('matchesFilter and 模式：必须包含全部选中标签', () => {
    const idea = makeIdea({ tags: ['a', 'b'] });
    expect(matchesFilter(idea, makeFilter({ tags: ['a', 'b', 'c'], tagMode: 'and' }))).toBe(false);
  });

  it('matchesFilter 状态过滤：open 排除 done', () => {
    const idea = makeIdea({ done: true });
    expect(matchesFilter(idea, makeFilter({ status: 'open' }))).toBe(false);
  });

  it('matchesFilter 状态过滤：done 排除未完成', () => {
    const idea = makeIdea({ done: false });
    expect(matchesFilter(idea, makeFilter({ status: 'done' }))).toBe(false);
  });

  it('matchesFilter 项目过滤：项目不匹配就出局', () => {
    const idea = makeIdea({ projectId: 'project-1' });
    expect(matchesFilter(idea, makeFilter({ projectId: 'project-2' }))).toBe(false);
    expect(matchesFilter(idea, makeFilter({ projectId: 'project-1' }))).toBe(true);
  });

  it('matchesFilter 标签比较大小写不敏感', () => {
    const idea = makeIdea({ tags: ['A'] });
    expect(matchesFilter(idea, makeFilter({ tags: ['a'], tagMode: 'or' }))).toBe(true);
  });

  it('matchesFilter 关键词：正文不含关键词就出局', () => {
    const idea = makeIdea({ content: '今天想到一个点子' });
    expect(matchesFilter(idea, makeFilter({ keyword: '点子' }))).toBe(true);
    expect(matchesFilter(idea, makeFilter({ keyword: '买牛奶' }))).toBe(false);
  });

  it('matchesFilter 关键词：空关键词等于不过滤', () => {
    const idea = makeIdea({ content: '今天想到一个点子' });
    expect(matchesFilter(idea, makeFilter({ keyword: '' }))).toBe(true);
  });

  it('matchesFilter 关键词与标签取交集（两个条件都要满足）', () => {
    const idea = makeIdea({ content: '今天想到一个点子', tags: ['a'] });
    expect(matchesFilter(idea, makeFilter({ keyword: '点子', tags: ['a'] }))).toBe(true);
    expect(matchesFilter(idea, makeFilter({ keyword: '点子', tags: ['b'] }))).toBe(false);
  });
});

describe('hasActiveFilter', () => {
  it('hasActiveFilter 默认没有过滤生效', () => {
    expect(hasActiveFilter(makeFilter())).toBe(false);
  });

  it('hasActiveFilter 状态不是「全部」就算有过滤', () => {
    expect(hasActiveFilter(makeFilter({ status: 'open' }))).toBe(true);
    expect(hasActiveFilter(makeFilter({ status: 'done' }))).toBe(true);
  });

  it('hasActiveFilter 选了标签就算有过滤', () => {
    expect(hasActiveFilter(makeFilter({ tags: ['a'] }))).toBe(true);
  });

  it('hasActiveFilter 有关键词就算有过滤', () => {
    expect(hasActiveFilter(makeFilter({ keyword: '点子' }))).toBe(true);
  });

  it('hasActiveFilter 只有空白的关键词不算有过滤', () => {
    expect(hasActiveFilter(makeFilter({ keyword: '   ' }))).toBe(false);
  });
});

describe('sortIdeas', () => {
  it('sortIdeas 按 updatedAt 升序、id 升序', () => {
    const older = makeIdea({ id: 'c', updatedAt: '2026-11-10T10:00:00Z' });
    const sameTimeSmallId = makeIdea({ id: 'a', updatedAt: '2026-11-11T10:00:00Z' });
    const sameTimeBigId = makeIdea({ id: 'b', updatedAt: '2026-11-11T10:00:00Z' });

    const sorted = sortIdeas([sameTimeBigId, older, sameTimeSmallId]);

    expect(sorted.map((idea) => idea.id)).toEqual(['c', 'a', 'b']);
  });

  it('sortIdeas 不修改入参', () => {
    const input = [
      makeIdea({ id: 'b', updatedAt: '2026-11-11T10:00:00Z' }),
      makeIdea({ id: 'a', updatedAt: '2026-11-10T10:00:00Z' }),
    ];

    sortIdeas(input);

    expect(input.map((idea) => idea.id)).toEqual(['b', 'a']);
  });
});
