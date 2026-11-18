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
  prependOlder,
  scrolledItems,
} from './ideas';

/** 造一条想法，只覆盖要测的字段。 */
function makeIdea(index: number): Idea {
  return {
    id: `idea-${index}`,
    projectId: 'project-1',
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

  it('深度按这一页实际给了多少条重算（与列表长度始终对得上）', async () => {
    // 只加载一页：列表 = 头一页（PAGE_SIZE + 1 条），深度就是 0。
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    await loadIdeas(makeFilter());
    expect(scrolledItems.value).toBe(0);

    // 保留了深度的重拉：多要的那部分就是用户翻出来的历史。
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1 + 101));
    await loadIdeas(makeFilter(), loadedLimit(101));
    expect(scrolledItems.value).toBe(101);
    expect(ideas.value).toHaveLength(PAGE_SIZE + 1 + 101);
  });

  it('两发重拉乱序回来时，旧的那发不覆盖更新的那份', async () => {
    // 第一发挂住。
    let releaseFirst!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        releaseFirst = resolve;
      }),
    );
    const first = loadIdeas(makeFilter({ keyword: '旧条件' }));

    // 期间发了第二发（条件变了），它先落地。
    apiGet.mockResolvedValueOnce(makeIdeas(3));
    await loadIdeas(makeFilter({ keyword: '新条件' }));
    expect(ideas.value).toHaveLength(3);

    // 第一发这时才回来：它是按**旧条件**要的，不能覆盖已经落地的新数据。
    releaseFirst(makeIdeas(5));
    await first;

    expect(ideas.value).toHaveLength(3);
  });

  it('更新的那一发失败时，旧的那发仍然写得进去', async () => {
    // 第一发挂住。
    let releaseFirst!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        releaseFirst = resolve;
      }),
    );
    const first = loadIdeas(makeFilter());

    // 期间发了第二发，它**失败**了：共享状态里还没有更新的数据。
    apiGet.mockRejectedValueOnce(new Error('网络抖了一下'));
    await expect(loadIdeas(makeFilter())).rejects.toThrow('网络抖了一下');

    // 第一发这时才回来：它比失败的更新，没被更新的数据顶掉 —— 该写得进去。
    releaseFirst(makeIdeas(4));
    await first;

    expect(ideas.value).toHaveLength(4);
  });

  it('loadIdeas 失败不清深度（列表与翻过的历史都还在）', async () => {
    // 先有一份成功的列表与一段翻过的历史。
    ideas.value = makeIdeas(3);
    prependOlder(makeIdeas(PAGE_SIZE + 1));
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);

    // 这次 loadIdeas 挂了：整表替换没有发生 —— 列表与深度都该保持原样
    // （深度要是在发起时就被清了，用户翻过的历史会凭空少一页）。
    apiGet.mockRejectedValueOnce(new Error('网络抖了一下'));
    await expect(loadIdeas(makeFilter())).rejects.toThrow('网络抖了一下');

    expect(ideas.value).toHaveLength(3 + PAGE_SIZE + 1);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
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

  it('fetchOlderIdeas 整页返回更旧的那一页', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));

    const page = await fetchOlderIdeas(makeFilter(), 2);

    // 这一页**整页**收下：服务端给的条数一条都不丢
    // （多要的那一条在这一页的**最老**那端，丢掉它就是在列表中间挖个洞）。
    expect(page.items).toHaveLength(PAGE_SIZE + 1);
    expect(page.stale).toBe(false);
    expect(loadingMore.value).toBe(false);
  });

  it('取回来的那一页什么都不写进共享状态', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    await fetchOlderIdeas(makeFilter(), 2);

    // 「取」与「用」分成两步：取的时候不碰共享状态 ——
    // 中间插进来什么（页面卸载了、条件变了、列表被重拉换了），
    // 共享状态都不会被一页**过期**的响应改坏。
    expect(ideas.value).toHaveLength(2);
    expect(hasMore.value).toBe(true);
    expect(scrolledItems.value).toBe(0);
  });

  it('过期的往回翻响应标成 stale（期间有更新的整表替换落地了）', async () => {
    // 第一发挂住，让列表级请求先跑完。
    let releaseFirst!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        releaseFirst = resolve;
      }),
    );
    const stale = fetchOlderIdeas(makeFilter(), 0);

    // 期间有过更新的整表替换（条件变了的重拉、新页面挂载后的首次加载……）。
    apiGet.mockResolvedValueOnce(makeIdeas(PAGE_SIZE + 1));
    await loadIdeas(makeFilter());
    expect(hasMore.value).toBe(true);

    // 这时第一发才回来：它是**过期**的 —— 是个短页，要是不设防，
    // 它的条目可能已经被新列表收进去了（拼进去就重复了）。
    releaseFirst(makeIdeas(PAGE_SIZE));
    const page = await stale;

    expect(page.stale).toBe(true);
    // 过期页什么都不写：hasMore 是新页面刚算好的，深度也没被它多记一页。
    expect(hasMore.value).toBe(true);
    expect(scrolledItems.value).toBe(0);
    expect(ideas.value).toHaveLength(PAGE_SIZE + 1);
  });
});

describe('prependOlder', () => {
  it('把这一页拼到列表前面，并记下翻过的深度', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    const page = await fetchOlderIdeas(makeFilter(), 2);
    ideas.value = makeIdeas(2);

    prependOlder(page.items);

    expect(ideas.value).toHaveLength(2 + PAGE_SIZE + 1);
    expect(ideas.value.at(-1)?.id).toBe('idea-1');
    // 深度是「用户往上翻过了多少条」：翻出来多少条就记多少条，
    // SSE 重拉时按它保留深度。
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
    expect(hasMore.value).toBe(true);
  });

  it('不满的一页 = 到底了（hasMore 变 false）', async () => {
    apiGet.mockResolvedValue(makeIdeas(3));
    const page = await fetchOlderIdeas(makeFilter(), 2);

    prependOlder(page.items);

    expect(hasMore.value).toBe(false);
  });

  it('空页不动列表（到底了也不用换一个新数组）', async () => {
    ideas.value = makeIdeas(2);
    const before = ideas.value;

    prependOlder([]);

    expect(ideas.value).toBe(before);
    expect(hasMore.value).toBe(false);
    expect(scrolledItems.value).toBe(0);
  });

  it('列表与深度始终对得上（深度 = 列表长度 - 头一页）', async () => {
    // 首屏。
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    await loadIdeas(makeFilter());
    expect(scrolledItems.value).toBe(ideas.value.length - (PAGE_SIZE + 1));

    // 翻一页。
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    const page = await fetchOlderIdeas(makeFilter(), ideas.value.length);
    prependOlder(page.items);
    expect(scrolledItems.value).toBe(ideas.value.length - (PAGE_SIZE + 1));
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
    // 拼法与 useHistoryScroll.loadOlder 一致：往回翻的那一页拼到列表**前面**，
    // offset 拿「列表长度」当（从最新一端数已经加载了多少条）。
    for (let round = 0; round < 10 && hasMore.value; round += 1) {
      const page = await fetchOlderIdeas(makeFilter(), ideas.value.length);
      expect(page.stale).toBe(false);
      prependOlder(page.items);
    }
    // 翻到底了（没有更旧的了），不是翻够了 10 轮还在翻。
    expect(hasMore.value).toBe(false);

    // 一条不漏：翻出来的正好是库里那 250 条，顺序也对（最老的在前）。
    expect(ideas.value.map((idea) => idea.id)).toEqual(all.map((idea) => idea.id));
    // 一条不重：id 唯一。
    expect(new Set(ideas.value.map((idea) => idea.id)).size).toBe(all.length);
    // 深度与列表长度对得上。
    expect(scrolledItems.value).toBe(ideas.value.length - (PAGE_SIZE + 1));
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
