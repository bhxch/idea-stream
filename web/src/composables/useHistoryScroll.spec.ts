import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import { ref } from 'vue';
import type { Idea } from '../api/types';
import type { IdeaFilter } from '../lib/filter';

const { apiGet } = vi.hoisted(() => ({ apiGet: vi.fn() }));

// 只换掉请求函数，其余导出用真的。
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet };
});

import {
  hasMore,
  ideas,
  loadingMore,
  PAGE_SIZE,
  scrolledItems,
} from '../stores/ideas';
import { useHistoryScroll } from './useHistoryScroll';

/**
 * 假的 `IntersectionObserver`。
 *
 * jsdom 不实现它（只有真实浏览器有），所以这里造一个替身：记下每个实例、
 * 记下被观察的元素，并让测试手动送一次「进入视野」。
 */
class FakeIntersectionObserver {
  /** 按创建顺序记录所有实例。 */
  static instances: FakeIntersectionObserver[] = [];

  /** 被观察的元素。 */
  readonly targets = new Set<Element>();

  private readonly callback: IntersectionObserverCallback;

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback;
    FakeIntersectionObserver.instances.push(this);
  }

  observe(target: Element): void {
    this.targets.add(target);
  }

  unobserve(target: Element): void {
    this.targets.delete(target);
  }

  disconnect(): void {
    this.targets.clear();
  }

  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }

  /** 手动送一次「哨兵进入视野」。 */
  trigger(): void {
    const entries: IntersectionObserverEntry[] = [...this.targets].map((target) => ({
      target,
      isIntersecting: true,
      intersectionRatio: 1,
      boundingClientRect: target.getBoundingClientRect(),
      intersectionRect: target.getBoundingClientRect(),
      rootBounds: null,
      time: 0,
    }));
    this.callback(entries, this as unknown as IntersectionObserver);
  }
}

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

/**
 * 造一个滚动容器。
 *
 * `scrollHeight` 跟着列表长度走（每条 10px）：往回翻拼进去一页之后高度
 * 变高，composable 补 `scrollTop` 的量就是「多了多少」。
 */
function createContainer(): HTMLElement {
  const element = document.createElement('div');
  Object.defineProperty(element, 'scrollHeight', {
    get: () => ideas.value.length * 10,
    configurable: true,
  });
  element.scrollTop = 0;
  return element;
}

/** 把哨兵送一次「进入视野」。 */
function triggerSentinel(): void {
  const observer = FakeIntersectionObserver.instances.at(-1);
  observer?.trigger();
}

beforeEach(() => {
  apiGet.mockReset();
  FakeIntersectionObserver.instances = [];
  vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
  ideas.value = [];
  hasMore.value = false;
  loadingMore.value = false;
  scrolledItems.value = 0;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useHistoryScroll', () => {
  it('哨兵进入视野就往回翻一页，拼到列表前面', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    // 这一页拼到了列表**前面**。
    expect(ideas.value).toHaveLength(2 + PAGE_SIZE + 1);
    expect(ideas.value.at(-1)?.id).toBe('idea-1');
    // 深度也记上了：SSE 重拉按它保留用户翻过的历史。
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
    expect(hasMore.value).toBe(true);
  });

  it('过期的那一页整页丢掉（不拼进列表、不改 hasMore、不记深度）', async () => {
    // 往回翻的这一页挂住。
    let release!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        release = resolve;
      }),
    );
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    // 等这一页的时候，列表被整表替换了（条件变了触发的重拉、SSE 重拉……）。
    // 新的这一页落地后把 hasMore 算成 true。
    apiGet.mockResolvedValueOnce(makeIdeas(3));
    const { loadIdeas } = await import('../stores/ideas');
    await loadIdeas(makeFilter());

    // 这一页这时才回来：它是**过期**的 —— 按旧状态要的。
    release(makeIdeas(PAGE_SIZE + 1));
    await flushPromises();

    // 整页丢掉：列表没被它动过（多出来的那一页不能拼上去 ——
    // 拼上去列表就比深度多一页，下一次重拉会把用户翻过的历史砍掉一半）。
    expect(ideas.value).toHaveLength(3);
    expect(scrolledItems.value).toBe(0);
    // hasMore 也不能被过期页翻掉（新列表刚算好的）。
    expect(hasMore.value).toBe(false);
  });

  it('页面卸载后回来的那一页丢掉', async () => {
    let release!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        release = resolve;
      }),
    );
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    // 用户离开页面。
    history.dispose();

    release(makeIdeas(PAGE_SIZE + 1));
    await flushPromises();

    // 这一页没人看了，也不该写进共享的 store。
    expect(ideas.value).toHaveLength(2);
    expect(scrolledItems.value).toBe(0);
  });

  it('等这一页的时候条件变了就丢掉', async () => {
    let release!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        release = resolve;
      }),
    );
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    let filter = makeFilter();
    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => filter,
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    // 等这一页的时候条件变了。
    filter = makeFilter({ keyword: '咖啡' });

    release(makeIdeas(PAGE_SIZE + 1));
    await flushPromises();

    // 这一页是按**旧**条件要的：不能塞给用户。
    expect(ideas.value).toHaveLength(2);
    expect(scrolledItems.value).toBe(0);
  });

  it('条件内容没变（只是换了个对象）不算变', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    // 每次求值都返回**新对象**，内容一模一样 —— 过滤条件是 computed 时就是这样。
    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    // 内容没变就不是「条件变了」：这一页照常用。
    expect(ideas.value).toHaveLength(2 + PAGE_SIZE + 1);
    expect(scrolledItems.value).toBe(PAGE_SIZE + 1);
  });

  it('请求失败时给提示', async () => {
    apiGet.mockRejectedValueOnce(new Error('boom'));
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const errors: string[] = [];
    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: (_error, fallback) => errors.push(fallback),
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    // 不然就是一次没人管的 Promise rejection —— 用户只看到「往上翻没反应」。
    expect(errors).toEqual(['加载更早的想法失败']);
    expect(ideas.value).toHaveLength(2);
  });

  it('请求在飞时重复触发不连发请求', async () => {
    let release!: (value: Idea[]) => void;
    apiGet.mockImplementationOnce(
      () => new Promise<Idea[]>((resolve) => {
        release = resolve;
      }),
    );
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();
    triggerSentinel();
    triggerSentinel();
    await flushPromises();

    // 只发了一次（`loadingMore` 挡着）。
    expect(apiGet).toHaveBeenCalledTimes(1);

    release(makeIdeas(PAGE_SIZE + 1));
    await flushPromises();

    expect(ideas.value).toHaveLength(2 + PAGE_SIZE + 1);
  });

  it('到底了（这一页是空的）就把「还有没有更旧的」清掉', async () => {
    apiGet.mockResolvedValue([]);
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    triggerSentinel();
    await flushPromises();

    expect(hasMore.value).toBe(false);
    expect(ideas.value).toHaveLength(2);
  });

  it('拼进去之后把视口钉回原来那条', async () => {
    apiGet.mockResolvedValue(makeIdeas(PAGE_SIZE + 1));
    ideas.value = makeIdeas(2);
    hasMore.value = true;

    const element = createContainer();
    const container = ref<HTMLElement | null>(element);
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    element.scrollTop = 300;
    triggerSentinel();
    await flushPromises();

    // 拼进去之后列表变高 —— 内容从上面长出来，视口要跟着往下挪，
    // 不然用户看到的内容就跳了。挪的量 = 多了多少高度。
    const grown = (PAGE_SIZE + 1) * 10;
    expect(element.scrollTop).toBe(300 + grown);
  });

  it('dispose 之后断开哨兵', () => {
    const container = ref<HTMLElement | null>(createContainer());
    const sentinel = ref<HTMLElement | null>(document.createElement('div'));
    const history = useHistoryScroll({
      container,
      sentinel,
      filter: () => makeFilter(),
      reportError: () => undefined,
    });
    history.observe();

    const observer = FakeIntersectionObserver.instances.at(-1);
    expect(observer?.targets.size).toBe(1);

    history.dispose();

    expect(observer?.targets.size).toBe(0);
  });
});
