import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Idea } from '../api/types';
import type { IdeaFilter } from '../lib/filter';

const { apiGet } = vi.hoisted(() => ({ apiGet: vi.fn() }));

// 只换掉请求函数，其余导出用真的。
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet };
});

import { fetchOlderIdeas, hasMore, ideas, loadIdeas, loadingMore, PAGE_SIZE } from './ideas';

/** 造一条想法，只覆盖要测的字段。 */
function makeIdea(index: number): Idea {
  return {
    id: `idea-${index}`,
    projectId: 'project-1',
    title: null,
    content: `第 ${index} 条想法`,
    tags: [],
    done: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  };
}

/** 造 n 条想法。 */
function makeIdeas(count: number): Idea[] {
  return Array.from({ length: count }, (_unused, index) => makeIdea(index));
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

beforeEach(() => {
  apiGet.mockReset();
  ideas.value = [];
  hasMore.value = false;
  loadingMore.value = false;
});

describe('loadIdeas', () => {
  it('loadIdeas_sends_all_filters_to_the_server', async () => {
    apiGet.mockResolvedValue([]);

    await loadIdeas(
      makeFilter({
        projectId: 'project-1',
        status: 'open',
        tags: ['a', 'b'],
        tagMode: 'and',
        keyword: '咖啡',
      }),
    );

    // 过滤在服务端做：条件全部作为查询参数发过去。
    expect(apiGet).toHaveBeenCalledWith('/ideas', {
      projectId: 'project-1',
      status: 'open',
      tags: 'a,b',
      tagMode: 'and',
      q: '咖啡',
      limit: String(PAGE_SIZE + 1),
    });
  });

  it('loadIdeas_sets_hasMore_when_the_page_is_full', async () => {
    // 多要了一条（PAGE_SIZE + 1）：满页说明后面还有更旧的。
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    await loadIdeas(makeFilter());
    expect(hasMore.value).toBe(true);

    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE));
    await loadIdeas(makeFilter());
    expect(hasMore.value).toBe(false);
  });

  it('loadIdeas 整表替换本地列表', async () => {
    apiGet.mockResolvedValue(makeIdeas(3));

    await loadIdeas(makeFilter());

    expect(ideas.value).toHaveLength(3);
    expect(ideas.value.map((idea) => idea.id)).toEqual(['idea-0', 'idea-1', 'idea-2']);
  });
});

describe('fetchOlderIdeas', () => {
  it('fetchOlderIdeas_uses_the_given_offset', async () => {
    apiGet.mockResolvedValue([]);

    await fetchOlderIdeas(makeFilter(), 250);

    expect(apiGet).toHaveBeenCalledWith(
      '/ideas',
      expect.objectContaining({ offset: '250', limit: String(PAGE_SIZE + 1) }),
    );
  });

  it('fetchOlderIdeas 只返回更旧的那一页，不动列表', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    ideas.value = makeIdeas(2);

    const older = await fetchOlderIdeas(makeFilter(), 2);

    // 多出来的那条只是用来判断「还有没有更旧的」，不进列表。
    expect(older).toHaveLength(PAGE_SIZE);
    expect(ideas.value).toHaveLength(2);
    expect(hasMore.value).toBe(true);
    expect(loadingMore.value).toBe(false);
  });

  it('fetchOlderIdeas 没有更多时 hasMore 为 false', async () => {
    apiGet.mockResolvedValue(makeIdeas(3));

    const older = await fetchOlderIdeas(makeFilter(), 2);

    expect(older).toHaveLength(3);
    expect(hasMore.value).toBe(false);
  });
});
