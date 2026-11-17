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
  releaseOlderIdeas,
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

/**
 * 造 n 条想法，**更新时间递增**。
 *
 * 服务端按 `updated_at ASC, id ASC` 排序：更新时间一样时按 id 排，
 * 而 id 是 'idea-0'、'idea-1'、'idea-10' 这种字典序 —— 与造出来的顺序对不上。
 * 所以要按「服务端会怎么排」来造数据：每条比上一条晚一秒。
 */
function makeOrderedIdeas(count: number): Idea[] {
  const base = Date.parse('2026-11-01T10:00:00Z');
  return Array.from({ length: count }, (_unused, index) => ({
    ...makeIdea(index),
    updatedAt: new Date(base + index * 1000).toISOString(),
  }));
}

/**
 * 服务端的分页切片（**照抄** `IdeaService.List` 的那段算术）。
 *
 * 为什么照抄而不是返回固定列表：要抓的 bug 是**客户端怎么用服务端给的这一页**，
 * 而「这一页是哪些条目」完全由服务端的切片算术决定 ——
 * 固定列表的假响应算不出它（早先漏掉这个 bug 的用例就是这么写的）。
 *
 * 算术（服务端，`IdeaService.List`）：
 *   ideas 按 updated_at ASC 排（最老的在 [0]，最新的在 [Count-1]）；
 *   end   = max(0, Count - offset);   // 排除「最新的 offset 条」
 *   start = max(0, end - limit);
 *   page  = ideas[start .. end-1];     // 仍是升序
 */
function serverSidePage(all: Idea[], limit: number, offset: number): Idea[] {
  const end = Math.max(0, all.length - offset);
  const start = Math.max(0, end - limit);
  return all.slice(start, end);
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

  it('fetchOlderIdeas 整页返回更旧的那一页，不动列表', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    ideas.value = makeIdeas(2);

    const older = await fetchOlderIdeas(makeFilter(), 2);

    // 这一页**整页**收下：服务端给的条数一条都不丢
    // （多要的那一条在这一页的**最老**那端，丢掉它就是在列表中间挖个洞）。
    expect(older).toHaveLength(PAGE_SIZE + 1);
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
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
  });

  it('只加载一页会把深度清零（列表被重置了）', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    await fetchOlderIdeas(makeFilter(), 2);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);

    // 只加载一页 = 整表替换成最新的一页：翻过的历史没了，深度也就没了。
    await loadIdeas(makeFilter());
    expect(scrolledItems.value).toBe(0);
  });

  it('丢掉的一页要把深度还回去', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    await fetchOlderIdeas(makeFilter(), 2);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);

    // 这一页最后没被留下（条件变了 / 页面卸载了）：深度也得还回去 ——
    // 深度比列表多一页的话，下一次 SSE 重拉会多要一页用户没翻过的内容。
    releaseOlderIdeas(PAGE_SIZE + 1);
    expect(scrolledItems.value).toBe(0);
  });

  it('还深度不会减成负数', () => {
    releaseOlderIdeas(50);
    expect(scrolledItems.value).toBe(0);
  });

  it('releaseOlderIdeas 的入参也要夹住（负数不会把深度抬高）', async () => {
    apiGet.mockResolvedValueOnce(makeIdeas(PAGE_SIZE + 1));
    await fetchOlderIdeas(makeFilter(), 2);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);

    // 负数入参要把深度抬高的话，下一次 SSE 重拉会按抬高的深度多要一页
    // 用户根本没翻过的内容。两个调用点传的都是 older.length，但这是
    // store 自己守得住的边界。
    releaseOlderIdeas(-50);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
  });

  it('过期的往回翻响应不写共享状态（期间有过更新的列表请求）', async () => {
    // 第一发挂住，让列表级请求先跑完。
    let releaseFirst!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        releaseFirst = resolve;
      }),
    );
    const stale = fetchOlderIdeas(makeFilter(), 0);

    // 期间有过更新的列表请求（条件变了的重拉、新页面挂载后的首次加载……）。
    apiGet.mockResolvedValueOnce(makeIdeas(PAGE_SIZE + 1));
    await loadIdeas(makeFilter());
    expect(hasMore.value).toBe(true);

    // 这时第一发才回来：它是**过期**的 —— 是个短页，要是不设防，
    // hasMore 会被它翻成 false（新页面的往回翻从此不再触发），
    // 深度也会被它多记一页（下一次 SSE 重拉多要一页没人翻过的历史）。
    releaseFirst(makeIdeas(PAGE_SIZE));
    await stale;

    expect(hasMore.value).toBe(true);
    expect(scrolledItems.value).toBe(0);
    expect(ideas.value).toHaveLength(PAGE_SIZE + 1);
  });

  it('过期页丢掉时 releaseOlderIdeas 不动作（深度没入过账）', async () => {
    let releaseFirst!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        releaseFirst = resolve;
      }),
    );
    const stale = fetchOlderIdeas(makeFilter(), 0);

    apiGet.mockResolvedValueOnce(makeIdeas(3));
    await loadIdeas(makeFilter());

    releaseFirst(makeIdeas(PAGE_SIZE + 1));
    await stale;

    // 过期页被调用方丢掉了：深度没入过账 —— release 再还一遍只会还过头。
    releaseOlderIdeas(PAGE_SIZE + 1);
    expect(scrolledItems.value).toBe(0);
  });

  it('loadIdeas 失败不清深度（列表与翻过的历史都还在）', async () => {
    // 先有一份成功的列表与一段翻过的历史。
    ideas.value = makeIdeas(3);
    apiGet.mockResolvedValueOnce(makeIdeas(PAGE_SIZE + 1));
    await fetchOlderIdeas(makeFilter(), 3);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);

    // 这次 loadIdeas 挂了：整表替换没有发生 —— 列表与深度都该保持原样
    // （深度要是在发起时就被清了，用户翻过的历史会凭空少一页）。
    apiGet.mockRejectedValueOnce(new Error('网络抖了一下'));
    await expect(loadIdeas(makeFilter())).rejects.toThrow('网络抖了一下');

    expect(ideas.value).toHaveLength(3);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
  });
});

/**
 * 往回翻到底：一条不漏、一条不重。
 *
 * 这条用例走的是**服务端的切片算术**（{@link serverSidePage}），不是
 * 「返回一个固定列表」的假响应 —— 漏条与重条只有真的按「从最新一端数」的
 * offset 切片才会出现（固定列表算不出它们）。
 *
 * 250 条 = 三页（首屏 101 + 101 + 48）：**超过两页**才看得出来 ——
 * 两页以内时「丢掉紧挨着已加载区域的那一条」不会在列表中间留洞。
 */
describe('往回翻（按服务端的切片算术走）', () => {
  it('翻到顶：列表里的条目与库里的一模一样（不漏也不重）', async () => {
    const all = makeOrderedIdeas(250);
    apiGet.mockImplementation((_path: string, params?: Record<string, string | undefined>) =>
      Promise.resolve(
        serverSidePage(all, Number(params?.limit ?? 0), Number(params?.offset ?? 0)),
      ),
    );

    // 首屏：一页 + 一条（多要的那条用来判断还有没有更旧的）。
    await loadIdeas(makeFilter());
    expect(ideas.value).toHaveLength(PAGE_SIZE + 1);
    expect(hasMore.value).toBe(true);

    // 一直往回翻，直到没有更旧的了。
    // 拼法与 HomePage.loadOlder 一致：往回翻的那一页拼到列表**前面**，
    // offset 拿「列表长度」当（从最新一端数已经加载了多少条）。
    for (let round = 0; round < 10 && hasMore.value; round += 1) {
      const older = await fetchOlderIdeas(makeFilter(), ideas.value.length);
      ideas.value = [...older, ...ideas.value];
    }
    // 翻到底了（没有更旧的了），不是翻够了 10 轮还在翻。
    expect(hasMore.value).toBe(false);

    // 一条不漏：翻出来的正好是库里那 250 条，顺序也对（最老的在前）。
    expect(ideas.value.map((idea) => idea.id)).toEqual(all.map((idea) => idea.id));
    // 一条不重：id 唯一。
    expect(new Set(ideas.value.map((idea) => idea.id)).size).toBe(all.length);
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
