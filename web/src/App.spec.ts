import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import App from './App.vue';
import { router } from './router';
import HomePage from './pages/HomePage.vue';

/**
 * 假的 `EventSource`。
 *
 * jsdom 不提供 `EventSource`，首页挂上后就会连事件流 —— 这里给个空壳，
 * 让挂载不至于抛错（事件流本身在 `api/events.spec.ts` 里测）。
 */
class FakeEventSource {
  readonly url: string;
  onopen: ((ev: Event) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;

  constructor(url: string) {
    this.url = url;
  }

  addEventListener(): void {}

  removeEventListener(): void {}

  close(): void {}
}

/**
 * 假的 `IntersectionObserver`。
 *
 * jsdom 不提供它，首页挂上后要观察「往回翻历史」的哨兵 —— 这里给个空壳，
 * 让挂载不至于抛错（往回翻本身在 `pages/HomePage.spec.ts` 里测）。
 */
class FakeIntersectionObserver {
  constructor(
    _callback: IntersectionObserverCallback,
    _options?: IntersectionObserverInit,
  ) {}

  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }
}

beforeEach(() => {
  vi.stubGlobal('EventSource', FakeEventSource);
  vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('应用骨架', () => {
  it('App 挂载后渲染出当前路由的页面', async () => {
    await router.push('/');
    await router.isReady();

    const wrapper = mount(App, { global: { plugins: [router] } });

    // 断言 RouterView 真的渲染出了首页组件，
    // 而不是只断言 html 非空（App.vue 无条件渲染 ToastHost，那条断言恒真）。
    expect(wrapper.findComponent(HomePage).exists()).toBe(true);
    wrapper.unmount();
  });

  it('路由表覆盖首页、项目、回收站与 webhook 入口', () => {
    const routes = router.getRoutes().map((route) => ({
      name: route.name,
      path: route.path,
    }));

    expect(routes).toEqual(
      expect.arrayContaining([
        { name: 'home', path: '/' },
        { name: 'projects', path: '/projects' },
        { name: 'trash', path: '/trash' },
        { name: 'webhooks', path: '/webhooks' },
        { name: 'project-webhooks', path: '/projects/:id/webhooks' },
      ]),
    );
  });
});
