import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { config, flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { ApiError } from '../api/client';
import type { Idea, IdeaEvent, Project } from '../api/types';
import MessageList from '../components/MessageList.vue';
import ToastHost from '../components/ToastHost.vue';
import type { SaveOptions, SaveOutcome } from '../lib/save';
import {
  fetchOlderIdeas,
  focusIdeaId,
  hasMore,
  ideas,
  loadingMore,
  PAGE_SIZE,
  scrolledItems,
} from '../stores/ideas';
import { currentProjectId } from '../stores/projects';

/**
 * 假的 `EventSource`。
 *
 * jsdom 不提供 `EventSource`（只有真实浏览器有），所以这里造一个替身：
 * 记下每个实例、记下注册的监听器，并让测试手动送事件帧。
 */
class FakeEventSource {
  /** 按创建顺序记录所有实例。 */
  static instances: FakeEventSource[] = [];

  readonly url: string;
  readonly withCredentials: boolean;

  readyState = 0;
  closed = false;
  onopen: ((ev: Event) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;

  private readonly listeners = new Map<string, Set<(ev: Event) => void>>();

  constructor(url: string, init?: EventSourceInit) {
    this.url = url;
    this.withCredentials = init?.withCredentials ?? false;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (ev: Event) => void): void {
    const set = this.listeners.get(type) ?? new Set<(ev: Event) => void>();
    set.add(listener);
    this.listeners.set(type, set);
  }

  removeEventListener(type: string, listener: (ev: Event) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }

  /** 手动送一帧事件。 */
  emit(type: string, data: unknown, lastEventId = ''): void {
    const ev = new MessageEvent(type, { data: JSON.stringify(data), lastEventId });
    for (const listener of [...(this.listeners.get(type) ?? [])]) {
      listener(ev);
    }
  }
}

/**
 * 假的 `IntersectionObserver`。
 *
 * jsdom 不实现它（只有真实浏览器有），所以这里造一个替身：记下每个实例、
 * 记下被观察的元素，并让测试手动送一次「进入视野」。
 *
 * 送一次「进入视野」就相当于「用户滚到了接近顶部的位置」——
 * 往回翻的哨兵在列表顶部，它进了视野就该预加载更旧的一页。
 */
class FakeIntersectionObserver {
  /** 按创建顺序记录所有实例。 */
  static instances: FakeIntersectionObserver[] = [];

  /** 被观察的元素。 */
  readonly targets = new Set<Element>();

  /** 观察的根元素（滚动容器）。 */
  readonly root: Element | Document | null;

  /** 提前多少像素就算相交（`'上 右 下 左'`）。 */
  readonly rootMargin: string;

  /** 已经断开（组件卸载时应该断开）。 */
  disconnected = false;

  private readonly callback: IntersectionObserverCallback;

  constructor(callback: IntersectionObserverCallback, options?: IntersectionObserverInit) {
    this.callback = callback;
    this.root = options?.root ?? null;
    this.rootMargin = options?.rootMargin ?? '0px 0px 0px 0px';
    FakeIntersectionObserver.instances.push(this);
  }

  observe(target: Element): void {
    this.targets.add(target);
  }

  unobserve(target: Element): void {
    this.targets.delete(target);
  }

  disconnect(): void {
    this.disconnected = true;
    this.targets.clear();
  }

  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }

  /** 手动送一次「哨兵进入视野」。 */
  trigger(): void {
    const entries: IntersectionObserverEntry[] = [...this.targets].map((target) => {
      const rect = target.getBoundingClientRect();
      return {
        target,
        isIntersecting: true,
        intersectionRatio: 1,
        boundingClientRect: rect,
        intersectionRect: rect,
        rootBounds: null,
        time: 0,
      };
    });
    this.callback(entries, this as unknown as IntersectionObserver);
  }
}

const { apiGet, apiSend } = vi.hoisted(() => ({ apiGet: vi.fn(), apiSend: vi.fn() }));

// 只换掉两个请求函数，ApiError 等其余导出用真的（`instanceof` 判定要靠它）。
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet, apiSend };
});

import HomePage from './HomePage.vue';

/** 一条想法。 */
const idea: Idea = {
  id: 'idea-1',
  projectId: 'project-1',
  title: null,
  content: '一条想法',
  tags: [],
  done: false,
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:00:00Z',
};

/** 两个项目：project-2 最近有活动。 */
const projects: Project[] = [
  {
    id: 'project-1',
    name: '老项目',
    sortOrder: 0,
    ideaCount: 1,
    lastActivityAt: '2026-11-01T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
  {
    id: 'project-2',
    name: '新项目',
    sortOrder: 1,
    ideaCount: 1,
    lastActivityAt: '2026-11-10T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-10T10:00:00Z',
  },
];

/** project-2 下的两条想法（切到 project-2 前后都在列表里，方便看勾选的变化）。 */
const ideaA: Idea = { ...idea, id: 'idea-a', projectId: 'project-2', content: '甲' };
const ideaB: Idea = { ...idea, id: 'idea-b', projectId: 'project-2', content: '乙' };

/** 造 n 条想法（内容各不相同）。 */
function makeManyIdeas(count: number): Idea[] {
  return Array.from({ length: count }, (_unused, index) => ({
    ...idea,
    id: `idea-many-${index}`,
    content: `第 ${index} 条`,
  }));
}

/**
 * 造 n 条**更旧**的想法（往回翻时拿到的那一页）。
 *
 * id 与更新时间都比 {@link makeManyIdeas} 更早：拼到列表前面之后，
 * 排序（`sortIdeas`）仍然把它们放在最上面 —— 也就看得出「拼到前面」了。
 */
function makeOlderIdeas(count: number): Idea[] {
  return Array.from({ length: count }, (_unused, index) => ({
    ...idea,
    id: `idea-older-${index}`,
    content: `更旧的第 ${index} 条`,
    updatedAt: '2026-10-01T10:00:00Z',
  }));
}

/** 关键词过滤用的两条想法：一条命中「点子」，一条不命中。 */
const ideaPoint: Idea = { ...idea, id: 'idea-point', content: '今天想到一个点子' };
const ideaMilk: Idea = { ...idea, id: 'idea-milk', content: '买牛奶' };

/** 正文里带英文的想法（测关键词忽略大小写）。 */
const ideaKafka: Idea = { ...idea, id: 'idea-kafka', content: 'Idea about Kafka' };

/** 有标题的想法：正文原样显示，改完能直接看到改了哪。 */
const titledIdea: Idea = { ...idea, title: '标题', content: '原正文' };

/**
 * 「命中数超过单次返回上限」的错误。
 *
 * 必须是**同步**造的：包一层 Promise 的话 `await` 抛出来的是那个 Promise，
 * `instanceof ApiError` 就不成立了（`isTooManyIdeas` 认不出来）。
 */
function tooManyError(): ApiError {
  return new ApiError({
    title: '请求失败',
    status: 400,
    detail: '命中 3 条想法，超过单次返回上限 2000，请收窄过滤条件',
    code: 'too_many_ideas',
  });
}

/** 这一轮测试用的路由（HomePage 要用 useRoute/useRouter）。 */
let router: Router;

beforeEach(async () => {
  apiGet.mockReset();
  apiSend.mockReset();
  FakeEventSource.instances = [];
  FakeIntersectionObserver.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  // 往回翻的哨兵靠它观察：jsdom 没有它，不装替身的话组件一挂载就报错。
  vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
  currentProjectId.value = null;
  focusIdeaId.value = null;
  // 想法列表是模块级单例（跨用例共享）：不清的话，上一个用例留下的数据
  // 会让下一个用例一上来就渲染出条目 —— 深链定位那种「等 DOM 更新」的用例
  // 会被这个残留状态掩盖（元素一开始就在 DOM 里，定位自然「成功」）。
  ideas.value = [];
  // 往上翻过的深度也是模块级单例（跨用例共享）：不清的话，上一个用例
  // 留下的深度会让这一次的重拉要多要一批条目。
  scrolledItems.value = 0;
  // 「还有没有更旧的」也是共享状态：不清的话，上一个用例翻到底了，
  // 这一次的哨兵就不会发往回翻的请求。
  hasMore.value = false;
  loadingMore.value = false;
  apiGet.mockImplementation((path: string) => {
    switch (path) {
      case '/projects':
        return Promise.resolve(projects);
      case '/settings':
        return Promise.resolve({});
      case '/tags':
        return Promise.resolve([]);
      case '/ideas':
        return Promise.resolve([idea]);
      default:
        return Promise.resolve([]);
    }
  });
  apiSend.mockResolvedValue({ ...idea, done: true });

  router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: { template: '<div />' } }],
  });
  await router.push('/');
  await router.isReady();
  // 装成全局插件：每个用例都挂同一个路由，省得每个 mount 都写一遍。
  config.global.plugins = [router];
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/**
 * 点侧栏里的某个项目（`null` 表示「全部项目」）。
 *
 * 宽屏侧栏与窄屏 Tab 渲染的是同一份数据，这里点侧栏那个。
 */
async function selectProject(wrapper: VueWrapper, id: string | null): Promise<void> {
  const key = id ?? 'all';
  await wrapper.get(`.project-sidebar [data-project-id="${key}"]`).trigger('click');
}

/** 侧栏里当前标着的是哪个项目（`null` 表示「全部项目」）。 */
function activeProject(wrapper: VueWrapper): string | null {
  const active = wrapper.findAll('.project-sidebar .project-sidebar__item.is-active');
  expect(active).toHaveLength(1);
  const id = active[0].attributes('data-project-id');
  return id === 'all' ? null : (id ?? null);
}

/** 造一条合法的事件载荷。 */
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

/**
 * 把消息流容器伪造成「能滚动」的样子。
 *
 * jsdom 不做布局：scrollHeight/clientHeight 永远是 0，
 * 滚动跟随会以为一直贴着底部。这里自己钉上这几个属性。
 *
 * 内容高度**跟着 DOM 里已有的条目数走**：真实浏览器就是这样 ——
 * 条目渲染出来之后内容才变高。所以「滚到底」必须发生在渲染之后，
 * 否则算出来的位置是旧的（新条目落在视野外）。
 */
function makeScrollable(element: HTMLElement, scrollTop: number): void {
  Object.defineProperty(element, 'scrollHeight', {
    configurable: true,
    get: () => 400 + element.querySelectorAll('.idea').length * 300,
  });
  Object.defineProperty(element, 'clientHeight', { value: 400, configurable: true });
  Object.defineProperty(element, 'scrollTop', { value: scrollTop, writable: true, configurable: true });
}

/**
 * 把「滚动盒子」钉在原型上。
 *
 * 首屏的滚动发生在挂载后不久，那时元素上还没来得及打桩 ——
 * 只有原型级的桩才能从头就生效（用来断言「有没有滚到底」）。
 */
function stubLayout(): { restore: () => void } {
  const originalHeight = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollHeight');
  const originalClient = Object.getOwnPropertyDescriptor(Element.prototype, 'clientHeight');
  Object.defineProperty(Element.prototype, 'scrollHeight', { configurable: true, get: () => 1000 });
  Object.defineProperty(Element.prototype, 'clientHeight', { configurable: true, get: () => 400 });
  return {
    restore(): void {
      if (originalHeight) {
        Object.defineProperty(Element.prototype, 'scrollHeight', originalHeight);
      }
      if (originalClient) {
        Object.defineProperty(Element.prototype, 'clientHeight', originalClient);
      }
    },
  };
}

/**
 * jsdom 里没有 `scrollIntoView`（浏览器才有）：打一个桩，
 * 才能断言「确实滚到那条去了」。
 */
function stubScrollIntoView(): ReturnType<typeof vi.fn> {
  const spy = vi.fn();
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    configurable: true,
    writable: true,
    value: spy,
  });
  return spy;
}

/** 摘掉 `scrollIntoView` 的桩。 */
function restoreScrollIntoView(): void {
  Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
}

/** 点状态过滤按钮（全部 / 未完成 / 已完成）。 */
async function clickStatus(wrapper: VueWrapper, label: string): Promise<void> {
  const button = wrapper
    .findAll('.filter-bar__status-button')
    .find((item) => item.text() === label);
  expect(button, `找不到状态过滤按钮「${label}」`).toBeTruthy();
  await button!.trigger('click');
}

/** 发过的 `/ideas` 请求（路径在第一位，查询参数在第二位）。 */
function ideasCalls(): { path: string; params?: Record<string, string | undefined> }[] {
  return apiGet.mock.calls
    .filter((call) => call[0] === '/ideas')
    .map((call) => ({ path: call[0], params: call[1] }));
}

/** 往回翻（带 `offset`）的那些 `/ideas` 请求。 */
function olderIdeasCalls(): { path: string; params?: Record<string, string | undefined> }[] {
  return ideasCalls().filter((call) => call.params?.offset !== undefined);
}

/**
 * 手动送一次「哨兵进入视野」。
 *
 * 相当于用户滚到了接近顶部的位置：往回翻的哨兵在列表顶部，它进了视野
 * 就该预加载更旧的一页。
 */
function triggerHistorySentinel(): void {
  const observer = FakeIntersectionObserver.instances.at(-1);
  expect(observer, '没有创建 IntersectionObserver（哨兵没被观察）').toBeTruthy();
  observer!.trigger();
}

/**
 * 让服务端按分页回答 `/ideas`。
 *
 * - 不带 `offset`（普通拉取）→ 最新的那一页，**满满一整页**
 *   （服务端就是靠「多要一条」来判断还有没有更旧的，见设计文档 §11.1）；
 * - 带 `offset`（往回翻）→ `olderCount` 条更旧的。
 */
function mockPagedIdeas(olderCount: number): void {
  apiGet.mockImplementation((path: string, params?: { offset?: string }) => {
    switch (path) {
      case '/projects':
        return Promise.resolve(projects);
      case '/settings':
        return Promise.resolve({});
      case '/tags':
        return Promise.resolve([]);
      case '/ideas':
        return Promise.resolve(
          params?.offset === undefined ? makeManyIdeas(PAGE_SIZE + 1) : makeOlderIdeas(olderCount),
        );
      default:
        return Promise.resolve([]);
    }
  });
}

/**
 * 让「往回翻」的那一页**卡住**（普通拉取照常返回最新的一页）。
 *
 * 好在它卡着的时候做点别的：连着触发两次哨兵、离开页面、加载提示……
 *
 * @returns 放行的函数：调用它这一页就回来了。
 */
function mockPendingOlderIdeas(older: Idea[] = makeOlderIdeas(PAGE_SIZE + 1)): () => void {
  let release: ((value: Idea[]) => void) | undefined;
  apiGet.mockImplementation((path: string, params?: { offset?: string }) => {
    switch (path) {
      case '/projects':
        return Promise.resolve(projects);
      case '/settings':
        return Promise.resolve({});
      case '/tags':
        return Promise.resolve([]);
      case '/ideas':
        if (params?.offset !== undefined) {
          // 往回翻的这一页卡住。
          return new Promise<Idea[]>((resolve) => {
            release = resolve;
          });
        }
        return Promise.resolve(makeManyIdeas(PAGE_SIZE + 1));
      default:
        return Promise.resolve([]);
    }
  });
  return () => release?.(older);
}

/**
 * 等防抖过去并让重拉落地。
 *
 * 过滤条件变了要走「防抖 250ms → 重拉」，所以断言列表变化前得把这段等出来。
 * 只能在**假定时器**下用（真定时器就得干等 250ms）。
 */
async function waitForReload(): Promise<void> {
  await vi.advanceTimersByTimeAsync(300);
  await flushPromises();
}

/** 造一个矩形（jsdom 里没有真的布局，只能自己拼）。 */
function rectOf(top: number, bottom: number): DOMRect {
  return {
    top,
    bottom,
    left: 0,
    right: 0,
    width: 0,
    height: bottom - top,
    x: 0,
    y: top,
    toJSON: () => ({}),
  } as DOMRect;
}

/**
 * 给 jsdom 补上「位置」：`getBoundingClientRect` 恒返回全 0，
 * 视口相关的断言就没法做。
 *
 * 条目按列表里的次序从上往下排（每条 300px，与 {@link makeScrollable} 一致），
 * 滚动容器本身占 400px。
 *
 * @returns 还原函数。
 */
function stubItemRects(): () => void {
  const original = Element.prototype.getBoundingClientRect;
  Element.prototype.getBoundingClientRect = function (this: Element): DOMRect {
    const id = (this as HTMLElement).dataset?.ideaId;
    if (!id) {
      return rectOf(0, 400);
    }
    const container = this.closest('.home__scroll');
    const items = container ? [...container.querySelectorAll('[data-idea-id]')] : [];
    const top = items.indexOf(this) * 300 - (container?.scrollTop ?? 0);
    return rectOf(top, top + 300);
  };
  return () => {
    Element.prototype.getBoundingClientRect = original;
  };
}

describe('HomePage', () => {
  it('同一份项目数据渲染两次：宽屏侧栏与窄屏 Tab 都在（互斥显示靠 CSS 媒体查询）', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    expect(wrapper.find('.project-sidebar').exists()).toBe(true);
    expect(wrapper.find('.project-tabs').exists()).toBe(true);
    // 两边渲染的是同一批项目。
    expect(wrapper.get('.project-sidebar').text()).toContain('新项目');
    expect(wrapper.get('.project-tabs').text()).toContain('新项目');
  });

  it('点「标记完成」会落库，并用返回的想法刷新本地状态', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    const button = wrapper.get('button.done');
    expect(button.text()).toBe('标记完成');

    await button.trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/idea-1/complete', { done: true });
    expect(wrapper.get('button.done').text()).toBe('已完成');
    wrapper.unmount();
  });

  it('在录入框里提交会走 POST /ideas，成功后清空输入框', async () => {
    apiSend.mockResolvedValue(idea);
    const wrapper = mount(HomePage);
    await flushPromises();

    const textarea = wrapper.get('.composer textarea');
    await textarea.setValue('一条新想法');
    await textarea.trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas', {
      projectId: null,
      content: '一条新想法',
      title: null,
      tags: [],
    });
    expect((textarea.element as HTMLTextAreaElement).value).toBe('');
    wrapper.unmount();
  });

  it('「全部项目」超出单次上限时给提示，并切到最近有活动的项目', async () => {
    apiGet.mockImplementation((path: string, params?: { projectId?: string }) => {
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      if (path === '/tags') {
        return Promise.resolve([]);
      }
      if (path === '/ideas') {
        if (!params?.projectId) {
          // 「全部项目」：一次要全库，超了服务端的上限。
          return Promise.reject(tooManyError());
        }
        return Promise.resolve([idea]);
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    // 提示语说的是实际发生的事：应用已经替他切好了。
    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('已切到最近有活动的项目「新项目」');
    expect(currentProjectId.value).toBe('project-2');
    // 侧栏里标着的也是那个项目（列表与导航必须对得上）。
    expect(activeProject(wrapper)).toBe('project-2');
    // 过滤条件全部作为查询参数发过去（过滤在服务端做）。
    expect(apiGet).toHaveBeenCalledWith('/ideas', {
      projectId: 'project-2',
      status: 'all',
      tagMode: 'or',
      limit: String(PAGE_SIZE + 1),
    });

    wrapper.unmount();
    toastHost.unmount();
  });

  it('切项目失败时回滚项目选择，不让列表变成空的', async () => {
    apiGet.mockImplementation((path: string) => {
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      if (path === '/tags') {
        return Promise.resolve([]);
      }
      if (path === '/ideas') {
        return Promise.resolve([idea]);
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    await flushPromises();
    expect(currentProjectId.value).toBe(null);

    // 让下一次拉取想法失败。
    apiGet.mockImplementation((path: string) => {
      if (path === '/ideas') {
        return Promise.reject(new Error('boom'));
      }
      return Promise.resolve([]);
    });

    await selectProject(wrapper, 'project-1');
    await flushPromises();

    // 回滚到原来的「全部项目」，过滤条件也跟着回去（否则旧数据会被滤成空）。
    expect(currentProjectId.value).toBe(null);
    expect(activeProject(wrapper)).toBe(null);
    expect(wrapper.get('.list').text()).toContain('一条想法');
    wrapper.unmount();
  });

  it('切项目失败回滚时，勾选保持原样（项目其实没换）', async () => {
    apiGet.mockImplementation((path: string) => {
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      if (path === '/tags') {
        return Promise.resolve([]);
      }
      if (path === '/ideas') {
        return Promise.resolve([ideaA, ideaB]);
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkbox = wrapper.findAll('input.select')[0];
    await checkbox.setValue(true);
    expect((checkbox.element as HTMLInputElement).checked).toBe(true);

    // 让下一次拉取想法失败。
    apiGet.mockImplementation((path: string) => {
      if (path === '/ideas') {
        return Promise.reject(new Error('boom'));
      }
      return Promise.resolve([]);
    });

    await selectProject(wrapper, 'project-2');
    await flushPromises();

    // 回滚了：项目没换，勾选不该跟着没。
    expect(currentProjectId.value).toBe(null);
    expect((wrapper.findAll('input.select')[0].element as HTMLInputElement).checked).toBe(true);
    wrapper.unmount();
  });

  it('切项目成功后清空勾选（范围变了，老勾的不再相关）', async () => {
    apiGet.mockImplementation((path: string) => {
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      if (path === '/tags') {
        return Promise.resolve([]);
      }
      if (path === '/ideas') {
        return Promise.resolve([ideaA, ideaB]);
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkbox = wrapper.findAll('input.select')[0];
    await checkbox.setValue(true);
    expect((checkbox.element as HTMLInputElement).checked).toBe(true);

    await selectProject(wrapper, 'project-2');
    await flushPromises();

    expect(currentProjectId.value).toBe('project-2');
    expect((wrapper.findAll('input.select')[0].element as HTMLInputElement).checked).toBe(false);
    wrapper.unmount();
  });

  it('自动切到最近有活动的项目失败时回滚，下拉与列表仍然对得上', async () => {
    apiGet.mockImplementation((path: string, params?: { projectId?: string }) => {
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      if (path === '/tags') {
        return Promise.resolve([]);
      }
      if (path === '/ideas') {
        if (!params?.projectId) {
          return Promise.reject(tooManyError());
        }
        // 自动切过去的那次也失败：补救没成。
        return Promise.reject(new Error('boom'));
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    // 补救失败 → 回到原处，不能停在「侧栏标着 project-2、列表却是空的」。
    expect(currentProjectId.value).toBe(null);
    expect(activeProject(wrapper)).toBe(null);
    // 提示队列是模块级单例，前面的用例留下的还在 —— 只看新弹的那一条。
    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('切到最近有活动的项目也失败了');

    wrapper.unmount();
    toastHost.unmount();
  });

  it('最近有活动的项目就是刚失败的那个时，不再切一次、只给一条提示', async () => {
    apiGet.mockImplementation((path: string, params?: { projectId?: string }) => {
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      if (path === '/tags') {
        return Promise.resolve([]);
      }
      if (path === '/ideas') {
        if (!params?.projectId) {
          return Promise.resolve([idea]);
        }
        // project-2（最近有活动的那个）本身就超了上限。
        return Promise.reject(tooManyError());
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();
    const before = toastHost.findAll('.toast').length;

    await selectProject(wrapper, 'project-2');
    await flushPromises();

    // 只给一条提示（不再对同一个项目切一次、也不再弹第二条）。
    const toasts = toastHost.findAll('.toast');
    expect(toasts.length).toBe(before + 1);
    expect(toasts.at(-1)?.text()).toContain('共 3 条超过单次上限');
    // 切不过去 → 回到原来的范围，侧栏与列表对得上。
    expect(currentProjectId.value).toBe(null);
    expect(activeProject(wrapper)).toBe(null);
    // 没有对 project-2 发起第二次请求。
    const project2Calls = apiGet.mock.calls.filter((call) => call[1]?.projectId === 'project-2');
    expect(project2Calls).toHaveLength(1);

    wrapper.unmount();
    toastHost.unmount();
  });

  it('「标记完成」请求在飞时禁用按钮，连点两次只发一次请求', async () => {
    let resolveToggle: ((value: Idea) => void) | undefined;
    apiSend.mockImplementation(
      () =>
        new Promise<Idea>((resolve) => {
          resolveToggle = resolve;
        }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    const button = wrapper.get('button.done');
    // 连点两次，第二次赶在重新渲染之前（真实的手速就是这样）：
    // 两次都会进处理器，只能靠请求在飞的保护挡住第二次。
    button.trigger('click');
    button.trigger('click');
    await flushPromises();

    // 不能再发一次（否则两次都从同一个旧状态算出 done:true，等于没切换）。
    expect(apiSend).toHaveBeenCalledTimes(1);
    // 请求在飞：按钮禁用。
    expect((button.element as HTMLButtonElement).disabled).toBe(true);

    resolveToggle?.({ ...idea, done: true });
    await flushPromises();

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(false);
    expect(wrapper.get('button.done').text()).toBe('已完成');
    wrapper.unmount();
  });

  it('编辑正文会 PATCH，并用返回的想法刷新本地', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([titledIdea]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockImplementation((method: string) =>
      method === 'PATCH'
        ? Promise.resolve({ ...titledIdea, content: '改过的正文' })
        : Promise.resolve({ ...titledIdea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await editor.trigger('keydown', { key: 'Enter', ctrlKey: true });
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PATCH', '/ideas/idea-1', { content: '改过的正文' });
    // 用后端返回的想法刷新本地（updatedAt 由后端给，前端不猜）。
    expect(wrapper.get('.body').text()).toBe('改过的正文');
    wrapper.unmount();
  });

  it('保存正文在飞时条目标成在飞（HomePage → busyIds → MessageList → MessageItem）', async () => {
    let resolvePatch: ((value: Idea) => void) | undefined;
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([titledIdea]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockImplementation((method: string) =>
      method === 'PATCH'
        ? new Promise<Idea>((resolve) => {
            resolvePatch = resolve;
          })
        : Promise.resolve({ ...titledIdea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await editor.trigger('keydown', { key: 'Enter', ctrlKey: true });
    await flushPromises();

    // 在飞：条目上的按钮禁用（这条传播链不能断）。
    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(true);
    expect((wrapper.get('button.action--tags').element as HTMLButtonElement).disabled).toBe(true);

    resolvePatch?.({ ...titledIdea, content: '改过的正文' });
    await flushPromises();

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(false);
    wrapper.unmount();
  });

  it('保存标签在飞时条目标成在飞', async () => {
    let resolvePut: ((value: Idea) => void) | undefined;
    apiSend.mockImplementation((method: string) =>
      method === 'PUT'
        ? new Promise<Idea>((resolve) => {
            resolvePut = resolve;
          })
        : Promise.resolve({ ...idea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    // 在飞：条目上的按钮禁用。
    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(true);
    expect((wrapper.get('button.action--tags').element as HTMLButtonElement).disabled).toBe(true);

    resolvePut?.({ ...idea, tags: ['新标签'] });
    await flushPromises();

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(false);
    wrapper.unmount();
  });

  it('删除在飞时条目标成在飞', async () => {
    let resolveDelete: ((value: void) => void) | undefined;
    apiSend.mockImplementation((method: string) =>
      method === 'DELETE'
        ? new Promise<void>((resolve) => {
            resolveDelete = resolve;
          })
        : Promise.resolve({ ...idea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('button.action--delete').trigger('click');
    await wrapper.get('button.confirm__delete').trigger('click');
    await flushPromises();

    // 在飞：条目上的按钮禁用。
    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(true);
    expect((wrapper.get('button.action--delete').element as HTMLButtonElement).disabled).toBe(true);

    resolveDelete?.(undefined);
    await flushPromises();

    expect(wrapper.findAll('.idea')).toHaveLength(0);
    wrapper.unmount();
  });

  it('在飞保护挡下保存时返回 busy 而不是失败（调用方要能分出「压根没发出去」）', async () => {
    let resolvePatch: ((value: Idea) => void) | undefined;
    apiSend.mockImplementation((method: string) =>
      method === 'PATCH'
        ? new Promise<Idea>((resolve) => {
            resolvePatch = resolve;
          })
        : Promise.resolve({ ...idea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    // 先发起一次保存（这一条进入在飞）。
    const list = wrapper.findComponent(MessageList);
    const onSaveContent = list.props('onSaveContent') as (
      id: string,
      content: string,
    ) => Promise<SaveOutcome>;
    const onSaveTags = list.props('onSaveTags') as (
      id: string,
      tags: string[],
      options?: SaveOptions,
    ) => Promise<SaveOutcome>;

    const first = onSaveContent('idea-1', '第一版');
    await flushPromises();

    // 在飞期间再发：这次压根没发出去（不是失败 —— 失败有提示，这里没有）。
    expect(await onSaveContent('idea-1', '第二版')).toBe('busy');
    expect(await onSaveTags('idea-1', ['新标签'])).toBe('busy');
    // 挡下不算失败：不弹错误提示。
    expect(apiSend).toHaveBeenCalledTimes(1);

    resolvePatch?.({ ...idea, content: '第一版' });
    expect(await first).toBe('saved');
    wrapper.unmount();
  });

  it('静默保存失败时不弹提示（自动补存用：用户没改任何东西）', async () => {
    apiSend.mockImplementation((method: string) =>
      method === 'PUT' ? Promise.reject(new Error('boom')) : Promise.resolve({ ...idea, done: true }),
    );

    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    const list = wrapper.findComponent(MessageList);
    const onSaveTags = list.props('onSaveTags') as (
      id: string,
      tags: string[],
      options?: SaveOptions,
    ) => Promise<SaveOutcome>;

    // 用户主动保存：失败要提示。
    expect(await onSaveTags('idea-1', ['新标签'])).toBe('failed');
    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('保存标签失败');
    const before = toastHost.findAll('.toast').length;

    // 自动补存（捡回上次没存上的那版）：失败不提示（用户没主动保存过）。
    expect(await onSaveTags('idea-1', ['新标签'], { silent: true })).toBe('failed');
    expect(toastHost.findAll('.toast')).toHaveLength(before);

    wrapper.unmount();
    toastHost.unmount();
  });

  it('标签补全走 GET /tags/suggest，带上项目与前缀', async () => {
    currentProjectId.value = 'project-1';
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([idea]);
        case '/tags/suggest':
          return Promise.resolve(['建议标签']);
        default:
          return Promise.resolve([]);
      }
    });

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('建');
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/tags/suggest', { projectId: 'project-1', prefix: '建' });
    // 候选到了以后下拉自动补上（补全是异步的）。
    expect(wrapper.get('.tag-editor__option').text()).toBe('建议标签');
    wrapper.unmount();
  });

  it('编辑标签会 PUT 覆盖式设置标签', async () => {
    apiSend.mockImplementation((method: string) =>
      method === 'PUT' ? Promise.resolve({ ...idea, tags: ['新标签'] }) : Promise.resolve({ ...idea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PUT', '/ideas/idea-1/tags', { tags: ['新标签'] });
    expect(wrapper.get('.chip__name').text()).toBe('新标签');
    wrapper.unmount();
  });

  it('删除要点二次确认，确认后 DELETE 并把条目移出列表', async () => {
    apiSend.mockImplementation((method: string) =>
      method === 'DELETE' ? Promise.resolve(undefined) : Promise.resolve({ ...idea, done: true }),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    await wrapper.get('button.action--delete').trigger('click');
    // 还没确认，不该发请求。
    expect(apiSend).not.toHaveBeenCalled();

    await wrapper.get('button.confirm__delete').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('DELETE', '/ideas/idea-1');
    expect(wrapper.findAll('.idea')).toHaveLength(0);
    wrapper.unmount();
  });

  it('勾选后批量标记完成会调 bulk/complete', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockResolvedValue([{ ...ideaA, done: true }, { ...ideaB, done: true }]);

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[0].setValue(true);
    await checkboxes[1].setValue(true);

    await wrapper.get('.batch-bar button.action--complete').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/bulk/complete', {
      ids: ['idea-a', 'idea-b'],
      done: true,
    });
    wrapper.unmount();
  });

  it('批量加标签会调 bulk/tags', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockResolvedValue([ideaA, ideaB]);

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[0].setValue(true);
    await checkboxes[1].setValue(true);

    await wrapper.get('.batch-bar button.action--add-tags').trigger('click');
    await wrapper.get('.batch-bar .tag-editor input').setValue('新标签');
    await wrapper.get('.batch-bar .tag-editor input').trigger('keydown', { key: 'Enter' });
    await wrapper.get('.batch-bar button.action--apply-tags').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/bulk/tags', {
      ids: ['idea-a', 'idea-b'],
      tags: ['新标签'],
      replace: false,
    });
    wrapper.unmount();
  });

  it('批量加标签失败时操作条上的草稿不丢（能直接重试）', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockImplementation((_method: string, path: string) =>
      path === '/ideas/bulk/tags'
        ? Promise.reject(new Error('boom'))
        : Promise.resolve([ideaA, ideaB]),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[0].setValue(true);
    await checkboxes[1].setValue(true);

    await wrapper.get('.batch-bar button.action--add-tags').trigger('click');
    await wrapper.get('.batch-bar .tag-editor input').setValue('新标签');
    await wrapper.get('.batch-bar .tag-editor input').trigger('keydown', { key: 'Enter' });
    await wrapper.get('.batch-bar button.action--apply-tags').trigger('click');
    await flushPromises();

    // 失败时面板还开着、标签还在：用户敲的没白敲。
    expect(wrapper.findAll('.batch-bar .tag-editor')).toHaveLength(1);
    expect(wrapper.get('.batch-bar .chip__name').text()).toBe('新标签');
    wrapper.unmount();
  });

  it('批量删除会调 bulk/delete 并把条目移出列表', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockResolvedValue([ideaA, ideaB]);

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[0].setValue(true);
    await checkboxes[1].setValue(true);

    await wrapper.get('.batch-bar button.action--delete').trigger('click');
    await wrapper.get('.batch-bar button.confirm__delete').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/bulk/delete', {
      ids: ['idea-a', 'idea-b'],
    });
    expect(wrapper.findAll('.idea')).toHaveLength(0);
    wrapper.unmount();
  });

  it('合并会调 merge：源条目消失、新条目出现，并记下新条目的 id', async () => {
    const merged: Idea = { ...ideaA, id: 'idea-merged', content: '甲\n\n乙' };
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockImplementation((_method: string, path: string) =>
      path === '/ideas/merge'
        ? Promise.resolve(merged)
        : Promise.resolve([ideaA, ideaB]),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[0].setValue(true);
    await checkboxes[1].setValue(true);

    await wrapper.get('.batch-bar button.action--merge').trigger('click');
    await flushPromises();

    // 对话框里的初始正文就是各条以空行拼接的结果。
    expect((wrapper.get('.merge-dialog textarea').element as HTMLTextAreaElement).value).toBe('甲\n\n乙');

    await wrapper.get('button.action--confirm').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/merge', {
      ids: ['idea-a', 'idea-b'],
      content: '甲\n\n乙',
    });
    // 源条目没了，只剩合并出来的新条目。
    expect(wrapper.findAll('.idea')).toHaveLength(1);
    expect(wrapper.get('.title').text()).toBe('甲');
    // 记下新条目的 id，供 F11 滚动定位。
    expect(focusIdeaId.value).toBe('idea-merged');
    wrapper.unmount();
  });

  it('合并请求里的 ids 按列表顺序（跟预览一致），不是勾选顺序', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockImplementation((_method: string, path: string) =>
      path === '/ideas/merge'
        ? Promise.resolve({ ...ideaA, id: 'idea-merged', content: '甲\n\n乙' })
        : Promise.resolve([ideaA, ideaB]),
    );

    const wrapper = mount(HomePage);
    await flushPromises();

    // 先勾第二条、再勾第一条：勾选顺序跟列表顺序不一样。
    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[1].setValue(true);
    await checkboxes[0].setValue(true);

    await wrapper.get('.batch-bar button.action--merge').trigger('click');
    await flushPromises();
    await wrapper.get('button.action--confirm').trigger('click');
    await flushPromises();

    // 预览是按列表顺序拼的，请求里的 ids 得是同一个顺序。
    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/merge', {
      ids: ['idea-a', 'idea-b'],
      content: '甲\n\n乙',
    });
    wrapper.unmount();
  });

  it('收到 idea.created 事件会把新条目插进列表', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    const created: Idea = {
      ...idea,
      id: 'idea-new',
      content: '别处记的',
      updatedAt: '2026-11-12T10:00:00Z',
    };
    FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
    await flushPromises();

    expect(wrapper.findAll('.idea')).toHaveLength(2);
    expect(wrapper.get('.list').text()).toContain('别处记的');
    wrapper.unmount();
  });

  it('收到 idea.updated 事件会就地改写列表里的那条', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    // 这条没有标题，正文首行当视觉标题 —— 所以断言标题那一行。
    const updated: Idea = { ...idea, content: '改过的正文' };
    FakeEventSource.instances[0].emit('idea.updated', makeEvent('idea.updated', { idea: updated }), 'e1');
    await flushPromises();

    expect(wrapper.findAll('.idea')).toHaveLength(1);
    expect(wrapper.get('.title').text()).toBe('改过的正文');
    wrapper.unmount();
  });

  it('收到 idea.created 事件会把项目的条数加上（侧栏里的条数不能是旧的）', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    // 「老项目」条数是 1。
    expect(wrapper.get('.project-sidebar [data-project-id="project-1"] .project-sidebar__count').text()).toBe(
      '1',
    );

    const created: Idea = {
      ...idea,
      id: 'idea-new',
      content: '别处记的',
      updatedAt: '2026-11-12T10:00:00Z',
    };
    FakeEventSource.instances[0].emit(
      'idea.created',
      makeEvent('idea.created', {
        idea: created,
        project: { id: 'project-1', name: '老项目', sortOrder: 0 },
      }),
      'e1',
    );
    await flushPromises();

    expect(wrapper.get('.project-sidebar [data-project-id="project-1"] .project-sidebar__count').text()).toBe(
      '2',
    );
    wrapper.unmount();
  });

  it('收到 resync 会重新拉一次列表（整表替换）', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();
    const before = apiGet.mock.calls.filter((call) => call[0] === '/ideas').length;

    FakeEventSource.instances[0].emit('resync', {});
    await flushPromises();

    expect(apiGet.mock.calls.filter((call) => call[0] === '/ideas').length).toBe(before + 1);
    wrapper.unmount();
  });

  it('用户往上翻看时来了新内容：不抢滚动，只加计数', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    // 用户翻到了上面（不是贴着底部）。
    const scroller = wrapper.get('.home__scroll').element as HTMLElement;
    makeScrollable(scroller, 0);
    scroller.dispatchEvent(new Event('scroll'));

    const created: Idea = {
      ...idea,
      id: 'idea-new',
      content: '别处记的',
      updatedAt: '2026-11-12T10:00:00Z',
    };
    FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
    await flushPromises();

    // 滚动位置没被动。
    expect(scroller.scrollTop).toBe(0);
    // 但告诉用户有几条新的。
    expect(wrapper.get('.new-content').text()).toContain('有 1 条新内容');
    wrapper.unmount();
  });

  it('整表重拉列表期间来的事件不会丢（替换完补回去）', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    // 让下一次拉列表卡住，好在它卡着的时候送一条事件。
    let release: ((value: Idea[]) => void) | undefined;
    apiGet.mockImplementation((path: string) => {
      if (path === '/ideas') {
        return new Promise<Idea[]>((resolve) => {
          release = resolve;
        });
      }
      if (path === '/projects') {
        return Promise.resolve(projects);
      }
      if (path === '/settings') {
        return Promise.resolve({});
      }
      return Promise.resolve([]);
    });

    // 切项目 → 触发一次整表重拉。
    await selectProject(wrapper, 'project-1');

    // 重拉还没回来，这时来了一条新想法。
    const created: Idea = {
      ...idea,
      id: 'idea-new',
      content: '别处记的',
      updatedAt: '2026-11-12T10:00:00Z',
    };
    FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');

    // 重拉的响应回来了（只有原来那条）。
    release?.([idea]);
    await flushPromises();

    // 整表替换把事件抹掉了，但它被补回去了 —— 新条目还在列表里。
    expect(wrapper.findAll('.idea')).toHaveLength(2);
    expect(wrapper.get('.list').text()).toContain('别处记的');
    wrapper.unmount();
  });

  it('深链 ?idea=<id> 打开后定位到那条并高亮', async () => {
    await router.push('/?idea=idea-1');

    const wrapper = mount(HomePage);
    await flushPromises();

    expect(wrapper.get('.idea').classes()).toContain('highlight');
    wrapper.unmount();
  });

  it('深链里的 id 不在列表里时静默忽略（不报错、不白屏）', async () => {
    await router.push('/?idea=idea-nope');

    const wrapper = mount(HomePage);
    await flushPromises();

    expect(wrapper.findAll('.idea')).toHaveLength(1);
    expect(wrapper.find('.idea.highlight').exists()).toBe(false);
    wrapper.unmount();
  });

  it('高亮 1.5 秒后自己撤掉', async () => {
    vi.useFakeTimers();
    try {
      await router.push('/?idea=idea-1');
      const wrapper = mount(HomePage);
      await flushPromises();
      expect(wrapper.get('.idea').classes()).toContain('highlight');

      vi.advanceTimersByTime(1500);
      await wrapper.vm.$nextTick();

      expect(wrapper.get('.idea').classes()).not.toContain('highlight');
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('深链 ?idea=<id> 打开后会滚到那条（scrollIntoView 真的被调用）', async () => {
    // 回归用例：定位发生在重渲染之前时，条目还没进 DOM，
    // 按 id 找元素根本找不到 —— 深链看起来就是「只高亮、不滚动」。
    const scrollIntoView = stubScrollIntoView();
    try {
      await router.push('/?idea=idea-1');
      const wrapper = mount(HomePage);
      await flushPromises();

      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      // 滚的正是那一条。
      expect(scrollIntoView.mock.instances[0]).toBe(wrapper.get('.idea').element);
      wrapper.unmount();
    } finally {
      restoreScrollIntoView();
    }
  });

  it('有深链锚点时打开页面不会滚到底（锚点不被挤出视野）', async () => {
    const layout = stubLayout();
    try {
      await router.push('/?idea=idea-1');
      const wrapper = mount(HomePage);
      await flushPromises();

      const scroller = wrapper.get('.home__scroll').element as HTMLElement;
      // 滚到底会是 1000 - 400 = 600；有锚点时不能那样滚。
      expect(scroller.scrollTop).not.toBe(600);
      wrapper.unmount();
    } finally {
      layout.restore();
    }
  });

  it('没有深链锚点时打开页面会滚到底（最新的一条在视野里）', async () => {
    const layout = stubLayout();
    try {
      const wrapper = mount(HomePage);
      await flushPromises();

      const scroller = wrapper.get('.home__scroll').element as HTMLElement;
      expect(scroller.scrollTop).toBe(600);
      wrapper.unmount();
    } finally {
      layout.restore();
    }
  });

  it('SSE 来新内容时贴着底部就跟着滚到底（滚到 DOM 更新后的新底部）', async () => {
    // 回归用例：滚动发生在 DOM 更新之前时，内容高度还是旧的，
    // 滚过去的位置不对 —— 新条目落在视野外。
    const wrapper = mount(HomePage);
    await flushPromises();

    const scroller = wrapper.get('.home__scroll').element as HTMLElement;
    // 一条内容时：高度 400 + 300 = 700，贴底的位置是 300。
    makeScrollable(scroller, 300);
    scroller.dispatchEvent(new Event('scroll'));

    const created: Idea = {
      ...idea,
      id: 'idea-new',
      content: '别处记的',
      updatedAt: '2026-11-12T10:00:00Z',
    };
    FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
    await flushPromises();

    // 两条内容：高度 400 + 600 = 1000，贴底的位置是 600。
    expect(scroller.scrollTop).toBe(600);
    wrapper.unmount();
  });

  it('自己记的一条也会滚进视野（滚到 DOM 更新后的新底部）', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    const scroller = wrapper.get('.home__scroll').element as HTMLElement;
    makeScrollable(scroller, 300);
    scroller.dispatchEvent(new Event('scroll'));

    apiSend.mockResolvedValue({
      ...idea,
      id: 'idea-new',
      content: '新记的',
      updatedAt: '2026-11-12T10:00:00Z',
    });

    const textarea = wrapper.get('.composer textarea');
    await textarea.setValue('新记的');
    await textarea.trigger('keydown', { key: 'Enter' });
    await flushPromises();

    // 自己刚记的那条也要在视野里（滚到两条内容的新底部）。
    expect(scroller.scrollTop).toBe(600);
    wrapper.unmount();
  });

  it('项目列表重拉失败时给提示（不静默吞掉）', async () => {
    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    // 让下一次拉项目列表失败。
    apiGet.mockImplementation((path: string) => {
      if (path === '/projects') {
        return Promise.reject(new Error('boom'));
      }
      if (path === '/ideas') {
        return Promise.resolve([idea]);
      }
      return Promise.resolve([]);
    });

    // 一个本地没有的项目（别处新建的）→ 需要重拉项目列表。
    FakeEventSource.instances[0].emit(
      'project.created',
      makeEvent('project.created', { project: { id: 'project-new', name: '新项目', sortOrder: 2 } }),
      'e1',
    );
    await flushPromises();

    // 提示队列是模块级单例（跨用例共享）：看有没有这条提示，别只看最后一条。
    expect(toastHost.findAll('.toast').map((toast) => toast.text()).join('\n')).toContain('加载项目失败');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('当前项目被删、切回全部项目失败时给提示', async () => {
    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    // 先切到 project-1。
    await selectProject(wrapper, 'project-1');
    await flushPromises();

    // 让下一次拉列表失败（切回「全部项目」要用它）。
    apiGet.mockImplementation((path: string) => {
      if (path === '/ideas') {
        return Promise.reject(new Error('boom'));
      }
      return Promise.resolve([]);
    });

    FakeEventSource.instances[0].emit(
      'project.deleted',
      makeEvent('project.deleted', { project: { id: 'project-1', name: '老项目', sortOrder: 0 } }),
      'e1',
    );
    await flushPromises();

    // 列表还停在已删项目的旧数据上 —— 这种情况必须给用户一条提示。
    expect(toastHost.findAll('.toast').map((toast) => toast.text()).join('\n')).toContain('切换项目失败');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('改成不符合当前状态过滤的条目会从当前视图里消失（切回「全部」还在）', async () => {
    vi.useFakeTimers();
    try {
      const wrapper = mount(HomePage);
      await flushPromises();

      // 切到「未完成」视图。
      await clickStatus(wrapper, '未完成');
      await waitForReload();
      expect(wrapper.findAll('.idea')).toHaveLength(1);

      // 别处把它标记完成了：它不该再出现在「未完成」视图里。
      const done: Idea = { ...idea, done: true, updatedAt: '2026-11-12T10:00:00Z' };
      FakeEventSource.instances[0].emit('idea.updated', makeEvent('idea.updated', { idea: done }), 'e1');
      await flushPromises();
      expect(wrapper.findAll('.idea')).toHaveLength(0);

      // 切回「全部」：重拉之后那条还在（过滤在服务端做，条目由服务端带回来）。
      await clickStatus(wrapper, '全部');
      await waitForReload();
      expect(wrapper.findAll('.idea')).toHaveLength(1);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('URL 带 ?idea= 时，合并出来的新条目仍被定位（滚动 + 高亮）', async () => {
    // 回归用例：只记「最后定位过的那一条」的话，深链 id 一直留在 URL 里，
    // 合并结果（候选里优先级更高）就永远轮不上定位 —— 用户合并完看不到那条。
    const scrollIntoView = stubScrollIntoView();
    try {
      await router.push('/?idea=idea-a');
      apiGet.mockImplementation((path: string) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            return Promise.resolve([ideaA, ideaB]);
          default:
            return Promise.resolve([]);
        }
      });
      const merged: Idea = { ...ideaA, id: 'idea-merged', content: '甲\n\n乙' };
      apiSend.mockImplementation((_method: string, path: string) =>
        path === '/ideas/merge' ? Promise.resolve(merged) : Promise.resolve([ideaA, ideaB]),
      );

      const wrapper = mount(HomePage);
      await flushPromises();

      // 深链那条先定位到了。
      expect(scrollIntoView).toHaveBeenCalledTimes(1);

      // 合并两条。
      const checkboxes = wrapper.findAll('input.select');
      await checkboxes[0].setValue(true);
      await checkboxes[1].setValue(true);
      await wrapper.get('.batch-bar button.action--merge').trigger('click');
      await flushPromises();
      await wrapper.get('button.action--confirm').trigger('click');
      await flushPromises();

      // 合并出来的新条目被定位了（滚过去 + 高亮）。
      const mergedElement = wrapper.get('[data-idea-id="idea-merged"]').element;
      expect(scrollIntoView).toHaveBeenCalledTimes(2);
      expect(scrollIntoView.mock.instances[1]).toBe(mergedElement);
      expect(wrapper.get('[data-idea-id="idea-merged"]').classes()).toContain('highlight');
      wrapper.unmount();
    } finally {
      restoreScrollIntoView();
    }
  });

  it('看不见但还勾着的条目不计入批量条数、也不被批量操作处理', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          return Promise.resolve([ideaA, ideaB]);
        default:
          return Promise.resolve([]);
      }
    });
    apiSend.mockResolvedValue([ideaA, ideaB]);

    const wrapper = mount(HomePage);
    await flushPromises();

    // 只看「未完成」。
    await clickStatus(wrapper, '未完成');
    await flushPromises();

    // 两条都勾上。
    const checkboxes = wrapper.findAll('input.select');
    await checkboxes[0].setValue(true);
    await checkboxes[1].setValue(true);
    expect(wrapper.get('.batch-bar .count').text()).toContain('已选 2 条');

    // 另一台设备把其中一条标记完成了：它在「未完成」视图里消失了，
    // 但勾选还留着（用户看不见它，也没法取消勾选）。
    const doneIdea: Idea = { ...ideaB, done: true, updatedAt: '2026-11-12T10:00:00Z' };
    FakeEventSource.instances[0].emit('idea.updated', makeEvent('idea.updated', { idea: doneIdea }), 'e1');
    await flushPromises();

    // 看不见的那条不计入条数。
    expect(wrapper.findAll('.idea')).toHaveLength(1);
    expect(wrapper.get('.batch-bar .count').text()).toContain('已选 1 条');

    // 批量操作也只处理看得见的那条。
    await wrapper.get('.batch-bar button.action--complete').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/bulk/complete', {
      ids: ['idea-a'],
      done: true,
    });
    wrapper.unmount();
  });

  it('加载中不显示空状态（数据没到就先闪一次空列表）', async () => {
    let release: ((value: Idea[]) => void) | undefined;
    apiGet.mockImplementation((path: string) => {
      if (path === '/ideas') {
        return new Promise<Idea[]>((resolve) => {
          release = resolve;
        });
      }
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        default:
          return Promise.resolve([]);
      }
    });

    const wrapper = mount(HomePage);
    await flushPromises();

    // 列表还没到：给的是「加载中」，不是「这里还没有想法。」
    expect(wrapper.get('.hint').text()).toBe('加载中…');
    expect(wrapper.find('.empty').exists()).toBe(false);

    release?.([idea]);
    await flushPromises();

    // 数据到了：条目在，空状态仍然不在。
    expect(wrapper.findAll('.idea')).toHaveLength(1);
    expect(wrapper.find('.empty').exists()).toBe(false);
    expect(wrapper.find('.hint').exists()).toBe(false);
    wrapper.unmount();
  });

  it('输入关键词后只显示正文命中的条目', async () => {
    vi.useFakeTimers();
    try {
      apiGet.mockImplementation((path: string, params?: { q?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            // 服务端按关键词过滤（前端不再自己筛一遍）。
            return Promise.resolve(params?.q ? [ideaPoint] : [ideaPoint, ideaMilk]);
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();
      expect(wrapper.findAll('.idea')).toHaveLength(2);

      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      await waitForReload();

      expect(wrapper.findAll('.idea')).toHaveLength(1);
      expect(wrapper.get('.list').text()).toContain('点子');
      expect(wrapper.get('.list').text()).not.toContain('买牛奶');
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('关键词忽略大小写', async () => {
    vi.useFakeTimers();
    try {
      apiGet.mockImplementation((path: string, params?: { q?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            // 服务端按关键词过滤（大小写不敏感）。
            return Promise.resolve(
              params?.q && ideaKafka.content.toLowerCase().includes(params.q.toLowerCase())
                ? [ideaKafka]
                : [],
            );
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();

      await wrapper.get('input[aria-label="搜索想法"]').setValue('kafka');
      await waitForReload();

      expect(wrapper.findAll('.idea')).toHaveLength(1);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('SSE 推来的新条目不匹配关键词时不显示（清掉关键词后它还在）', async () => {
    vi.useFakeTimers();
    try {
      const created: Idea = {
        ...idea,
        id: 'idea-new',
        content: '别处记的',
        updatedAt: '2026-11-12T10:00:00Z',
      };
      apiGet.mockImplementation((path: string, params?: { q?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            // 服务端按关键词过滤：带关键词时只有命中的那条，清掉关键词后两条都在。
            return Promise.resolve(params?.q ? [ideaPoint] : [ideaPoint, created]);
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();

      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      await waitForReload();
      expect(wrapper.findAll('.idea')).toHaveLength(1);

      // 别的设备记了一条，正文不含关键词：不该出现在列表里。
      FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
      await flushPromises();

      expect(wrapper.findAll('.idea')).toHaveLength(1);
      expect(wrapper.get('.list').text()).not.toContain('别处记的');

      // 清掉关键词：重拉之后它还在（过滤在服务端做，条目由服务端带回来）。
      await wrapper.get('.filter-bar__clear').trigger('click');
      await waitForReload();

      expect(wrapper.findAll('.idea')).toHaveLength(2);
      expect(wrapper.get('.list').text()).toContain('别处记的');
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('「清除」按钮把状态、标签、关键词一起清掉', async () => {
    apiGet.mockImplementation((path: string) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([{ name: '想法', count: 1 }]);
        case '/ideas':
          return Promise.resolve([ideaPoint]);
        default:
          return Promise.resolve([]);
      }
    });

    const wrapper = mount(HomePage);
    await flushPromises();

    // 三个条件都加上：状态、标签、关键词。
    await clickStatus(wrapper, '未完成');
    await wrapper.get('.tag-filter__trigger').trigger('click');
    await wrapper.get('.tag-filter__option input').setValue(true);
    await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
    await flushPromises();

    // 有过滤生效 → 「清除」按钮出现。
    expect(wrapper.find('.filter-bar__clear').exists()).toBe(true);

    await wrapper.get('.filter-bar__clear').trigger('click');
    await flushPromises();

    // 三样都清掉了。
    expect((wrapper.get('input[aria-label="搜索想法"]').element as HTMLInputElement).value).toBe('');
    expect(wrapper.get('.tag-filter__trigger').text()).toBe('标签');
    expect(wrapper.get('.filter-bar__status-button.is-active').text()).toBe('全部');
    expect(wrapper.find('.filter-bar__clear').exists()).toBe(false);
    wrapper.unmount();
  });

  it('filter_change_triggers_a_reload_after_the_debounce', async () => {
    vi.useFakeTimers();
    try {
      const wrapper = mount(HomePage);
      await flushPromises();
      const before = ideasCalls().length;

      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');

      // 250ms 内不发请求：每敲一个键就打一次后端是打不起的。
      await vi.advanceTimersByTimeAsync(200);
      expect(ideasCalls().length).toBe(before);

      // 过了防抖才发，而且关键词要作为 `q` 送过去（过滤在服务端做）。
      await vi.advanceTimersByTimeAsync(100);
      await flushPromises();
      expect(ideasCalls().length).toBe(before + 1);
      expect(ideasCalls().at(-1)?.params?.q).toBe('点子');
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('sse_event_triggers_a_reload_that_keeps_the_loaded_depth', async () => {
    vi.useFakeTimers();
    try {
      apiGet.mockImplementation((path: string, params?: { offset?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            // 往回翻（带 offset）→ 满满一页；正常拉取 → 两条。
            return Promise.resolve(
              params?.offset === undefined ? [ideaA, ideaB] : makeManyIdeas(PAGE_SIZE + 1),
            );
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();
      expect(wrapper.findAll('.idea')).toHaveLength(2);

      // 用户往上翻了一页（滚动接上是下一个任务的事，这里直接调 store 的接口
      // 模拟「翻过了」这个动作）：深度会记在 store 里。
      await fetchOlderIdeas(
        { projectId: null, status: 'all', tags: [], tagMode: 'or', keyword: '' },
        ideas.value.length,
      );
      expect(scrolledItems.value).toBe(PAGE_SIZE);

      const created: Idea = {
        ...idea,
        id: 'idea-new',
        content: '别处记的',
        updatedAt: '2026-11-12T10:00:00Z',
      };
      FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
      await flushPromises();

      await waitForReload();

      // 重拉要带上「往上翻过的条数」：用户翻过的历史不能被这一拉抹掉
      // （否则视口会突然跳回最新那批）。
      const limit = Number(ideasCalls().at(-1)?.params?.limit);
      expect(limit).toBe(PAGE_SIZE + 1 + PAGE_SIZE);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('SSE 重拉的条数不会随着事件一个一个地膨胀', async () => {
    vi.useFakeTimers();
    try {
      // 服务端给多少就返回多少（重拉是整表替换：列表长度会变成这次要到的条数）。
      apiGet.mockImplementation((path: string, params?: { limit?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            return Promise.resolve(makeManyIdeas(Number(params?.limit ?? PAGE_SIZE + 1)));
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();
      const before = ideasCalls().length;

      // 连着来四个事件（每来一个都要重拉一次）。
      for (let index = 0; index < 4; index += 1) {
        const created: Idea = {
          ...idea,
          id: `idea-new-${index}`,
          content: '别处记的',
          updatedAt: `2026-11-12T10:00:0${index}Z`,
        };
        FakeEventSource.instances[0].emit(
          'idea.created',
          makeEvent('idea.created', { idea: created }),
          `e${index}`,
        );
        await flushPromises();
        await waitForReload();
      }

      // 每次重拉的条数都一样：用户没往上翻过，就一直只要一页。
      // （用「列表当前长度」当基准的话，这里会一次比一次多 —— 每来一个
      // 事件就长一页，四个事件就能把整库塞进 DOM。）
      const limits = ideasCalls().slice(before).map((call) => Number(call.params?.limit));
      expect(limits).toEqual([PAGE_SIZE + 1, PAGE_SIZE + 1, PAGE_SIZE + 1, PAGE_SIZE + 1]);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('filter_change_resets_to_one_page_and_scrolls_to_the_bottom', async () => {
    vi.useFakeTimers();
    const layout = stubLayout();
    try {
      // 服务端按 limit 给多少就返回多少（这样「列表有多长」才说明得了问题）。
      apiGet.mockImplementation((path: string, params?: { limit?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            return Promise.resolve(makeManyIdeas(Number(params?.limit ?? PAGE_SIZE + 1)));
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();
      expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1);

      // 用户翻到上面去了（离底部远）。
      const scroller = wrapper.get('.home__scroll').element as HTMLElement;
      scroller.scrollTop = 0;

      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      await waitForReload();

      // 重置到一页：条件变了，命中集合也变了，之前的浏览深度没有意义。
      expect(Number(ideasCalls().at(-1)?.params?.limit)).toBe(PAGE_SIZE + 1);
      expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1);

      // 并且滚到底部（最新的一条在视野里）。
      expect(scroller.scrollTop).toBe(600);
      wrapper.unmount();
    } finally {
      layout.restore();
      vi.useRealTimers();
    }
  });

  it('SSE 事件不会顶掉还没执行的「条件变化」重拉', async () => {
    vi.useFakeTimers();
    const layout = stubLayout();
    try {
      apiGet.mockImplementation((path: string) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            return Promise.resolve([ideaA, ideaB]);
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();
      const before = ideasCalls().length;

      const scroller = wrapper.get('.home__scroll').element as HTMLElement;
      scroller.scrollTop = 0;
      scroller.dispatchEvent(new Event('scroll'));

      // 用户刚改了关键词（防抖还没走完），别处又来了一条。
      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      const created: Idea = {
        ...idea,
        id: 'idea-new',
        content: '别处记的',
        updatedAt: '2026-11-12T10:00:00Z',
      };
      FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
      await flushPromises();

      await waitForReload();

      // 两次重拉都要发生：条件变化的那次不能被 SSE 的重拉顶掉
      // （顶掉了用户看到的还是旧条件的结果，也不会滚到底部）。
      expect(ideasCalls().length).toBe(before + 2);
      // 「条件变化」那次重拉会把视口滚到底部（用户不在底部时才看得出差别）。
      expect(scroller.scrollTop).toBe(600);
      wrapper.unmount();
    } finally {
      layout.restore();
      vi.useRealTimers();
    }
  });

  it('visible_ideas_are_not_filtered_again_on_the_client', async () => {
    vi.useFakeTimers();
    try {
      // 服务端判定两条都命中（正文「咖 啡」那种：服务端的词元相邻比前端子串宽松）。
      apiGet.mockImplementation((path: string) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            return Promise.resolve([ideaPoint, ideaMilk]);
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();

      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      await waitForReload();

      // 服务端返回 2 条 → 显示 2 条：显示层不再按关键词过滤一遍
      // （前端子串判断比服务端词元相邻更严格，再过滤一遍会误杀）。
      expect(wrapper.findAll('.idea')).toHaveLength(2);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('后台重拉时不闪「加载中…」（列表已经有内容）', async () => {
    vi.useFakeTimers();
    try {
      // 第一次拉取正常返回，之后的重拉卡住 —— 好在它卡着的时候看一眼提示。
      let release: ((value: Idea[]) => void) | undefined;
      let calls = 0;
      apiGet.mockImplementation((path: string) => {
        if (path === '/ideas') {
          calls += 1;
          if (calls === 1) {
            return Promise.resolve([idea]);
          }
          return new Promise<Idea[]>((resolve) => {
            release = resolve;
          });
        }
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();
      expect(wrapper.findAll('.idea')).toHaveLength(1);

      // 改关键词 → 防抖后重拉，重拉卡着。
      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      await vi.advanceTimersByTimeAsync(300);
      await flushPromises();

      // 列表里已经有内容了：这是后台刷新，不是「首次加载」，不该闪提示。
      expect(wrapper.find('.hint').exists()).toBe(false);
      expect(wrapper.findAll('.idea')).toHaveLength(1);

      release?.([idea]);
      await flushPromises();
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('SSE 重拉之后视口不跳（用户在看的那条还在原处）', async () => {
    vi.useFakeTimers();
    const restoreRects = stubItemRects();
    try {
      const older: Idea = { ...idea, id: 'idea-older', content: '更旧的', updatedAt: '2026-10-01T10:00:00Z' };
      const newer: Idea = { ...idea, id: 'idea-newer', content: '更新的', updatedAt: '2026-11-20T10:00:00Z' };
      apiGet.mockImplementation((path: string, params?: { limit?: string; offset?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            if (params?.offset !== undefined) {
              // 往回翻：满满一页（翻出来的条目由下一个任务拼到列表前面）。
              return Promise.resolve(makeManyIdeas(PAGE_SIZE + 1));
            }
            // 重拉要的比「一页」多（用户往上翻过）→ 服务端多给了更旧的一条，
            // 列表顶部会长出来。
            return Promise.resolve(
              params?.limit && Number(params.limit) > PAGE_SIZE + 1
                ? [older, ideaA, ideaB, newer]
                : [ideaA, ideaB],
            );
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();

      // 用户往上翻过（深度记在 store 里；拼到列表前面是下一个任务的事）。
      await fetchOlderIdeas(
        { projectId: null, status: 'all', tags: [], tagMode: 'or', keyword: '' },
        ideas.value.length,
      );

      // 用户翻到了上面（不是贴着底部）。
      const scroller = wrapper.get('.home__scroll').element as HTMLElement;
      makeScrollable(scroller, 150);
      scroller.dispatchEvent(new Event('scroll'));

      const anchorTop = (): number =>
        scroller.querySelector('[data-idea-id="idea-a"]')!.getBoundingClientRect().top;
      const before = anchorTop();

      // 别处来了一条：触发一次 SSE 重拉（列表顶部会多出更旧的条目）。
      const created: Idea = {
        ...idea,
        id: 'idea-created',
        content: '别处记的',
        updatedAt: '2026-11-21T10:00:00Z',
      };
      FakeEventSource.instances[0].emit('idea.created', makeEvent('idea.created', { idea: created }), 'e1');
      await flushPromises();
      await waitForReload();

      // 用户没滚动 → 看到的内容不该变（重拉多要了一页，内容从上面长出来了）。
      expect(anchorTop()).toBe(before);
      wrapper.unmount();
    } finally {
      restoreRects();
      vi.useRealTimers();
    }
  });

  it('scroll_to_the_top_loads_older_ideas', async () => {
    mockPagedIdeas(PAGE_SIZE + 1);

    const wrapper = mount(HomePage);
    await flushPromises();
    expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1);

    // 用户滚到接近顶部 → 哨兵进入视野 → 预加载更旧的一页。
    triggerHistorySentinel();
    await flushPromises();

    // 请求带上了「已经翻过多少条」：offset 从**最新**一端数（设计文档 §11.1）。
    const olderCalls = olderIdeasCalls();
    expect(olderCalls).toHaveLength(1);
    expect(olderCalls[0].params?.offset).toBe(String(PAGE_SIZE + 1));

    // 更旧的条目拼到了列表**前面**。
    expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);
    expect(wrapper.findAll('.idea')[0].attributes('data-idea-id')).toBe('idea-older-0');
    wrapper.unmount();
  });

  it('prepending_older_ideas_keeps_the_viewport_in_place', async () => {
    mockPagedIdeas(PAGE_SIZE + 1);

    const wrapper = mount(HomePage);
    await flushPromises();

    // jsdom 不做布局：自己钉上滚动相关的高度（每条 300px，见 makeScrollable）。
    const scroller = wrapper.get('.home__scroll').element as HTMLElement;
    makeScrollable(scroller, 0);

    triggerHistorySentinel();
    await flushPromises();

    // 更旧的一页插到了视口**上方**（100 条 × 300px）→ scrollTop 得补回去，
    // 否则用户正在看的那条会被顶下去（预加载反而让视口跳了）。
    expect(scroller.scrollTop).toBe(PAGE_SIZE * 300);
    wrapper.unmount();
  });

  it('往回翻时请求期间到达的内容不算进视口的修正', async () => {
    // 往回翻的这一页卡住：好在它卡着的时候别处来了一条。
    const releaseOlder = mockPendingOlderIdeas();

    const wrapper = mount(HomePage);
    await flushPromises();

    // jsdom 不做布局：自己钉上滚动相关的高度（每条 300px，见 makeScrollable）。
    const scroller = wrapper.get('.home__scroll').element as HTMLElement;
    makeScrollable(scroller, 0);

    triggerHistorySentinel();
    await flushPromises();

    // 请求还在飞的时候，列表底部多了一条（模拟 SSE 追加一条新内容）。
    ideas.value = [
      ...ideas.value,
      { ...idea, id: 'idea-new', updatedAt: '2026-11-21T10:00:00Z' },
    ];
    await flushPromises();

    releaseOlder();
    await flushPromises();

    // 视口的修正只该算「插到上面去的那一页」：请求期间到达的内容本来就在
    // 下面，不该把视口推走 —— 算进去的话视口会偏一条的高度。
    expect(scroller.scrollTop).toBe(PAGE_SIZE * 300);
    wrapper.unmount();
  });

  it('往回翻的哨兵在消息流的最上面，观察的是那个滚动容器', async () => {
    mockPagedIdeas(PAGE_SIZE + 1);

    const wrapper = mount(HomePage);
    await flushPromises();

    const scroller = wrapper.get('.home__scroll').element as HTMLElement;
    const sentinel = wrapper.get('.home__history-sentinel').element as HTMLElement;

    // 哨兵必须在列表**前面**：往回翻的方向是上（底部是当下，新内容靠 SSE
    // 送过来）。放到底部的话，触发的是「快到最新」而不是「快到最旧」。
    expect(scroller.firstElementChild).toBe(sentinel);
    const list = scroller.querySelector('.list');
    expect(list, '找不到消息流列表').not.toBeNull();
    expect([...scroller.children].indexOf(sentinel)).toBeLessThan(
      [...scroller.children].indexOf(list as Element),
    );

    // 观察的是消息流那个滚动容器（不是 window）：列表是在它里面滚的。
    const observer = FakeIntersectionObserver.instances.at(-1);
    expect(observer, '没有创建 IntersectionObserver（哨兵没被观察）').toBeTruthy();
    expect(observer!.root).toBe(scroller);
    // 提前量在**顶部**（'上 右 下 左'）：'快到顶时预加载'。
    expect(observer!.rootMargin).toBe('400px 0px 0px 0px');
    // 而且观察的正是哨兵本身。
    expect(observer!.targets.has(sentinel)).toBe(true);
    wrapper.unmount();
  });

  it('更旧的一页拼到 store 数组的前面（不是后面）', async () => {
    mockPagedIdeas(PAGE_SIZE + 1);

    const wrapper = mount(HomePage);
    await flushPromises();

    triggerHistorySentinel();
    await flushPromises();

    // store 里的数组是**没排序**的（排序是显示层做的）：更旧的一页要拼在
    // 前面，第一条就是最旧的那条。拼到后面的话这条就跑到末尾去了 ——
    // 显示层再排序也看不出差别，所以必须断言没排序的那一份。
    expect(ideas.value).toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);
    expect(ideas.value[0].id).toBe('idea-older-0');
    expect(ideas.value.at(-1)?.id).toBe(`idea-many-${PAGE_SIZE}`);
    wrapper.unmount();
  });

  it('往回翻只记一次深度（UI 层不再重复累加）', async () => {
    mockPagedIdeas(PAGE_SIZE + 1);

    const wrapper = mount(HomePage);
    await flushPromises();

    triggerHistorySentinel();
    await flushPromises();

    // 深度是「用户往上翻过了多少条」：这一页 100 条。
    // UI 层不能再加一次 —— 加了的话 SSE 重拉会按它多要一页用户没翻过的内容。
    expect(scrolledItems.value).toBe(PAGE_SIZE);
    wrapper.unmount();
  });

  it('no_more_older_ideas_stops_the_requests', async () => {
    // 往回翻的这一页**不满**：服务端没给够，说明没有更旧的了。
    mockPagedIdeas(PAGE_SIZE);

    const wrapper = mount(HomePage);
    await flushPromises();

    triggerHistorySentinel();
    await flushPromises();

    // 更旧的都翻到手了：到底了。
    expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);
    expect(hasMore.value).toBe(false);

    // 再滚到顶部也不发请求了（没有更旧的了，别再打后端）。
    triggerHistorySentinel();
    await flushPromises();

    expect(olderIdeasCalls()).toHaveLength(1);
    wrapper.unmount();
  });

  it('哨兵连续触发不会连发请求（请求在飞时就挡住）', async () => {
    // 往回翻的这一页卡住：好在它卡着的时候再触发几次。
    const releaseOlder = mockPendingOlderIdeas();

    const wrapper = mount(HomePage);
    await flushPromises();

    triggerHistorySentinel();
    await flushPromises();
    expect(olderIdeasCalls()).toHaveLength(1);

    // 请求还在飞：再触发也不该再发一个 —— `loadingMore` 挡着。
    // （这一页回来之前 `hasMore` 还是真的，所以只有 `loadingMore` 能挡。）
    triggerHistorySentinel();
    triggerHistorySentinel();
    await flushPromises();
    expect(olderIdeasCalls()).toHaveLength(1);

    releaseOlder();
    await flushPromises();

    // 只拼进去了一页（没有重复的那几页）。
    expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1 + PAGE_SIZE);
    wrapper.unmount();
  });

  it('往回翻请求失败时给提示（不让它变成没人管的 rejection）', async () => {
    apiGet.mockImplementation((path: string, params?: { offset?: string }) => {
      switch (path) {
        case '/projects':
          return Promise.resolve(projects);
        case '/settings':
          return Promise.resolve({});
        case '/tags':
          return Promise.resolve([]);
        case '/ideas':
          if (params?.offset !== undefined) {
            // 往回翻的这一页挂了。
            return Promise.reject(new Error('boom'));
          }
          return Promise.resolve(makeManyIdeas(PAGE_SIZE + 1));
        default:
          return Promise.resolve([]);
      }
    });

    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    triggerHistorySentinel();
    await flushPromises();

    // 失败要有提示：不然用户只看到「往上翻没反应」，不知道是翻完了还是挂了。
    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('加载更早的想法失败');
    // 列表没被动（没有拼到一半的东西）。
    expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1);
    wrapper.unmount();
    toastHost.unmount();
  });

  it('往回翻的那一页在等待期间改了过滤条件就丢掉（别塞不匹配的条目）', async () => {
    vi.useFakeTimers();
    try {
      let release: ((value: Idea[]) => void) | undefined;
      apiGet.mockImplementation((path: string, params?: { offset?: string }) => {
        switch (path) {
          case '/projects':
            return Promise.resolve(projects);
          case '/settings':
            return Promise.resolve({});
          case '/tags':
            return Promise.resolve([]);
          case '/ideas':
            if (params?.offset !== undefined) {
              // 往回翻的这一页卡住：好在它卡着的时候改条件。
              return new Promise<Idea[]>((resolve) => {
                release = resolve;
              });
            }
            return Promise.resolve(makeManyIdeas(PAGE_SIZE + 1));
          default:
            return Promise.resolve([]);
        }
      });

      const wrapper = mount(HomePage);
      await flushPromises();

      triggerHistorySentinel();
      await flushPromises();

      // 等这一页的时候改了过滤条件。
      await wrapper.get('input[aria-label="搜索想法"]').setValue('点子');
      await waitForReload();

      // 这一页是按**旧**条件要的：它回来时不能拼到新列表上。
      release?.(makeOlderIdeas(PAGE_SIZE + 1));
      await flushPromises();

      expect(wrapper.findAll('.idea')).toHaveLength(PAGE_SIZE + 1);
      expect(wrapper.get('.list').text()).not.toContain('更旧的第 0 条');
      // 深度也要还回去：这一页没进列表，深度不能替它记着 ——
      // 深度多一页的话，下一次 SSE 重拉会多要一页用户根本没翻过的内容。
      expect(scrolledItems.value).toBe(0);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('往回翻的时候给出「加载更早的…」提示', async () => {
    // 往回翻的这一页卡住：好在它卡着的时候看一眼提示。
    const releaseOlder = mockPendingOlderIdeas();

    const wrapper = mount(HomePage);
    await flushPromises();

    triggerHistorySentinel();
    await flushPromises();

    // 加载中要有提示：不然用户以为页面没反应（设计文档 §13.3）。
    expect(wrapper.text()).toContain('加载更早的…');

    releaseOlder();
    await flushPromises();

    // 加载完了提示就该没了。
    expect(wrapper.text()).not.toContain('加载更早的…');
    wrapper.unmount();
  });

  it('往回翻的请求在页面离开后作废（不写进 store）', async () => {
    // 往回翻的这一页卡住：好在它卡着的时候离开页面。
    const releaseOlder = mockPendingOlderIdeas();

    const wrapper = mount(HomePage);
    await flushPromises();
    const before = ideas.value.length;

    triggerHistorySentinel();
    await flushPromises();
    expect(olderIdeasCalls()).toHaveLength(1);

    // 用户直接离开页面。
    wrapper.unmount();

    // 这一页这时候才回来。
    releaseOlder();
    await flushPromises();

    // 它不该写进 store（store 是模块级的，下一个页面还在用它）。
    expect(ideas.value).toHaveLength(before);
    expect(ideas.value[0].id).not.toBe('idea-older-0');
    // 深度也要还回去：这一页没进列表。
    expect(scrolledItems.value).toBe(0);
  });
});
