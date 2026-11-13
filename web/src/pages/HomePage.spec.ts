import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { config, flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { ApiError } from '../api/client';
import type { Idea, IdeaEvent, Project } from '../api/types';
import MessageList from '../components/MessageList.vue';
import ToastHost from '../components/ToastHost.vue';
import type { SaveOptions, SaveOutcome } from '../lib/save';
import { focusIdeaId, ideas } from '../stores/ideas';
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
  vi.stubGlobal('EventSource', FakeEventSource);
  currentProjectId.value = null;
  focusIdeaId.value = null;
  // 想法列表是模块级单例（跨用例共享）：不清的话，上一个用例留下的数据
  // 会让下一个用例一上来就渲染出条目 —— 深链定位那种「等 DOM 更新」的用例
  // 会被这个残留状态掩盖（元素一开始就在 DOM 里，定位自然「成功」）。
  ideas.value = [];
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
    expect(apiGet).toHaveBeenCalledWith('/ideas', { projectId: 'project-2' });

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

  it('改成不符合当前状态过滤的条目不会被从数据集里删掉（切回过滤条件还在）', async () => {
    const wrapper = mount(HomePage);
    await flushPromises();

    // 切到「未完成」视图。
    await clickStatus(wrapper, '未完成');
    await flushPromises();
    expect(wrapper.findAll('.idea')).toHaveLength(1);

    // 别处把它标记完成了：它不该再出现在「未完成」视图里。
    const done: Idea = { ...idea, done: true, updatedAt: '2026-11-12T10:00:00Z' };
    FakeEventSource.instances[0].emit('idea.updated', makeEvent('idea.updated', { idea: done }), 'e1');
    await flushPromises();
    expect(wrapper.findAll('.idea')).toHaveLength(0);

    // 切回「全部」：那条还在（不能因为中途的过滤被从数据集里删掉）。
    await clickStatus(wrapper, '全部');
    await flushPromises();
    expect(wrapper.findAll('.idea')).toHaveLength(1);
    wrapper.unmount();
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
});
