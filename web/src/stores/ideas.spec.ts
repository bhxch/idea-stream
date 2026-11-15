import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Idea } from '../api/types';
import type { IdeaFilter } from '../lib/filter';

const { apiGet } = vi.hoisted(() => ({ apiGet: vi.fn() }));

// 只换掉请求函数，其余导出用真的。
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet };
});

import {
  fetchOlderIdeas,
  hasMore,
  ideas,
  loadedLimit,
  loadIdeas,
  loadingMore,
  PAGE_SIZE,
  scrolledItems,
} from './ideas';

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
  scrolledItems.value = 0;
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

  it('fetchOlderIdeas 记下往上翻过的深度', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));

    await fetchOlderIdeas(makeFilter(), 2);

    // 翻出来多少条就记多少条：SSE 重拉时按它保留深度。
    expect(scrolledItems.value).toBe(PAGE_SIZE);
  });

  it('只加载一页会把深度清零（列表被重置了）', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    await fetchOlderIdeas(makeFilter(), 2);
    expect(scrolledItems.value).toBe(PAGE_SIZE);

    // 只加载一页 = 整表替换成最新的一页：翻过的历史没了，深度也就没了。
    await loadIdeas(makeFilter());
    expect(scrolledItems.value).toBe(0);
  });
});

describe('loadedLimit', () => {
  it('loaded_limit_keeps_the_scrolled_depth', () => {
    // 没往上翻过：一页 + 一条（多出来的那条用来判断还有没有更旧的）。
    expect(loadedLimit(0)).toBe(PAGE_SIZE + 1);
    // 往上翻过一页：那一页也得留在重拉回来的列表里。
    expect(loadedLimit(PAGE_SIZE)).toBe(PAGE_SIZE + 1 + PAGE_SIZE);
  });

  it('loaded_limit_is_clamped_to_the_server_maximum', () => {
    // 服务端 `limit` 的上限是 1000：超了回 400 `invalid_pagination`，
    // 一个 400 会让 SSE 的刷新彻底断掉。
    expect(loadedLimit(10_000)).toBe(1000);
  });
});
