// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import App from './App.vue';
import { router } from './router';

describe('应用骨架', () => {
  it('App 挂载后渲染出当前路由的页面', async () => {
    await router.push('/');
    await router.isReady();

    const wrapper = mount(App, { global: { plugins: [router] } });

    expect(wrapper.html()).not.toBe('');
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
