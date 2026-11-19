import { describe, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import PageNav from './PageNav.vue';
// 组件源码（?raw）：触摸目标的断点写在 CSS 里，渲染出来的 DOM 上读不到。
import pageNavSource from './PageNav.vue?raw';

/** 路由表：与 web/src/router.ts 一致（这里只需要路径能匹配上）。 */
const routes = [
  { path: '/', component: { template: '<div />' } },
  { path: '/projects', component: { template: '<div />' } },
  { path: '/trash', component: { template: '<div />' } },
  { path: '/webhooks', component: { template: '<div />' } },
  { path: '/projects/:id/webhooks', component: { template: '<div />' } },
];

/** 在指定路径上装导航条。 */
async function mountAt(path: string): Promise<VueWrapper> {
  const router = createRouter({ history: createMemoryHistory(), routes });
  await router.push(path);
  await router.isReady();
  return mount(PageNav, { global: { plugins: [router] } });
}

/** 导航条里某个文字的链接（按可访问名取，避免同名元素）。 */
function linkByText(wrapper: VueWrapper, text: string) {
  const found = wrapper.findAll('a').find((item) => item.text() === text);
  if (!found) {
    throw new Error(`导航条里没有「${text}」这个链接`);
  }
  return found;
}

describe('PageNav', () => {
  it('有回主列表页的入口（指向 /）', async () => {
    const wrapper = await mountAt('/projects');

    const home = wrapper.get('.page-nav__home');
    expect(home.attributes('href')).toBe('/');
    expect(home.text()).toBe('想法');
    wrapper.unmount();
  });

  it('在 / 上，回首页的入口带 aria-current="page"', async () => {
    const wrapper = await mountAt('/');

    expect(wrapper.get('.page-nav__home').attributes('aria-current')).toBe('page');
    wrapper.unmount();
  });

  it('在 / 上，只有回首页的入口带 aria-current（别的入口不该被点亮）', async () => {
    const wrapper = await mountAt('/');

    const current = wrapper.findAll('a[aria-current="page"]');
    expect(current).toHaveLength(1);
    expect(current[0].classes()).toContain('page-nav__home');
    wrapper.unmount();
  });

  it('在管理页上，当前一页的入口带 aria-current="page"', async () => {
    for (const [path, label] of [
      ['/projects', '项目'],
      ['/trash', '回收站'],
      ['/webhooks', 'Webhook'],
    ]) {
      const wrapper = await mountAt(path);

      const current = wrapper.findAll('a[aria-current="page"]');
      expect(current).toHaveLength(1);
      expect(current[0].text()).toBe(label);
      wrapper.unmount();
    }
  });

  it('在项目级 webhook 页上，「Webhook」带 aria-current="page"', async () => {
    // vue-router 的「完全匹配」在这个路由上不成立（没有哪个链接正好指向它），
    // 所以 aria-current 得靠 PageNav 自己算 —— 与高亮 class 同一个判据。
    const wrapper = await mountAt('/projects/project-1/webhooks');

    const current = wrapper.findAll('a[aria-current="page"]');
    expect(current).toHaveLength(1);
    expect(current[0].text()).toBe('Webhook');
    // 高亮与 aria-current 说的是同一件事，不能一个亮一个不亮。
    expect(linkByText(wrapper, 'Webhook').classes()).toContain('is-active');
    expect(linkByText(wrapper, '项目').classes()).not.toContain('is-active');
    wrapper.unmount();
  });

  it('在项目级 webhook 页上，回首页的入口不带 aria-current', async () => {
    const wrapper = await mountAt('/projects/project-1/webhooks');

    expect(wrapper.get('.page-nav__home').attributes('aria-current')).toBeUndefined();
    wrapper.unmount();
  });

  /**
   * 触摸目标的断点：源码级守卫。
   *
   * jsdom 不求值媒体查询（`window.matchMedia` 是 undefined），
   * 「窄屏上链接有没有被撑到 32px 高」在单测里读不出来 —— 视觉验证归 e2e。
   * 这里能钉住的只有**断点值本身**：触摸目标的 32×32 规则必须跟两列断点对齐
   * （≤767px），否则 641–767px 那一段既按手机渲染又不满足触摸目标。
   */
  it('触摸目标的断点统一到 767px', () => {
    expect(pageNavSource).toContain('@media (max-width: 767px)');
    // 旧的 640px 那一档得整个没了（不是「再加一条 767px」—— 两条都在的话，
    // 641–767px 那一段照样落进手机档）。
    expect(pageNavSource).not.toContain('@media (max-width: 640px)');
  });
});
