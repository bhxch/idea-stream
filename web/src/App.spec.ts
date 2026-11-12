import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import App from './App.vue';
import { router } from './router';
import HomePage from './pages/HomePage.vue';

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
