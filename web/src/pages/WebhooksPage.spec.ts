import { beforeEach, describe, expect, it, vi } from 'vitest';
import { config, flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import type { Webhook } from '../api/types';
import ToastHost from '../components/ToastHost.vue';

const { apiGet, apiSend } = vi.hoisted(() => ({ apiGet: vi.fn(), apiSend: vi.fn() }));

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet, apiSend };
});

import WebhooksPage from './WebhooksPage.vue';

/** 两个全局 webhook。 */
const webhooks: Webhook[] = [
  {
    id: 'wh-1',
    name: '全局通知',
    url: 'https://example.com/hook',
    method: 'POST',
    headers: {},
    bodyTemplate: '{"text":"{{idea.content}}"}',
    events: ['idea.created'],
    scope: 'global',
    projectId: null,
    enabled: true,
    hasSecret: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
  {
    id: 'wh-2',
    name: '另一个',
    url: 'https://example.com/hook2',
    method: 'POST',
    headers: {},
    bodyTemplate: '{"text":"x"}',
    events: [],
    scope: 'global',
    projectId: null,
    enabled: false,
    hasSecret: true,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
];

/** 这一轮测试用的路由。 */
let router: Router;

/** 装页面。 */
function mountPage(): ReturnType<typeof mount> {
  return mount(WebhooksPage);
}

beforeEach(async () => {
  apiGet.mockReset();
  apiSend.mockReset();
  apiGet.mockImplementation((path: string) => {
    if (path === '/webhooks') {
      return Promise.resolve(webhooks);
    }
    if (path.startsWith('/webhooks/') && path.endsWith('/deliveries')) {
      return Promise.resolve([]);
    }
    return Promise.resolve([]);
  });
  apiSend.mockResolvedValue(webhooks[0]);

  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/projects', component: { template: '<div />' } },
      { path: '/trash', component: { template: '<div />' } },
      { path: '/webhooks', component: { template: '<div />' } },
      { path: '/projects/:id/webhooks', component: { template: '<div />' } },
    ],
  });
  await router.push('/webhooks');
  await router.isReady();
  config.global.plugins = [router];
});

describe('WebhooksPage', () => {
  it('列出全局作用域的 webhook（列表请求不带 projectId，但要 unmask=true）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    // 后端默认回传的是遮蔽版（url 的路径段与请求头的值都换了 ***），
    // 页面要完整配置，所以显式要原值。
    expect(apiGet).toHaveBeenCalledWith('/webhooks', { unmask: 'true' });
    const items = wrapper.findAll('.webhooks__item');
    expect(items).toHaveLength(2);
    expect(items[0].text()).toContain('全局通知');
    wrapper.unmount();
  });

  it('新建：表单存的是全局作用域', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('.webhooks__new').trigger('click');
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    // 至少要订阅一个事件（表单的校验要求），否则不发请求。
    await wrapper.findAll('.webhook-form__event')[0].setValue(true);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/webhooks', {
      name: '新 hook',
      url: 'https://example.com/x',
      method: 'POST',
      headers: {},
      bodyTemplate: '{"text":"x"}',
      events: ['idea.created'],
      scope: 'global',
      projectId: null,
      secret: null,
    });
    wrapper.unmount();
  });

  it('点「编辑」打开表单，表单里是这个 webhook 的配置', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-webhook-id="wh-1"] .webhooks__edit').trigger('click');

    const name = wrapper.get('.webhook-form__name').element as HTMLInputElement;
    expect(name.value).toBe('全局通知');
    wrapper.unmount();
  });

  it('启用/禁用：PATCH 只带 enabled，之后重新拉列表', async () => {
    const wrapper = mountPage();
    await flushPromises();

    const listCallsBefore = apiGet.mock.calls.filter((call) => call[0] === '/webhooks').length;

    await wrapper.get('[data-webhook-id="wh-2"] .webhooks__toggle').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PATCH', '/webhooks/wh-2', { enabled: true });

    // PATCH 的响应是遮蔽版，不能拿它覆盖列表里的行：toggle 之后要再拉一次列表。
    const listCallsAfter = apiGet.mock.calls.filter((call) => call[0] === '/webhooks').length;
    expect(listCallsAfter).toBe(listCallsBefore + 1);

    wrapper.unmount();
  });

  it('删除要二次确认：确认后才 DELETE', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-webhook-id="wh-1"] .webhooks__delete').trigger('click');
    // 还没确认，不该发请求。
    expect(apiSend).not.toHaveBeenCalledWith('DELETE', '/webhooks/wh-1');

    await wrapper.get('.webhooks__confirm-yes').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('DELETE', '/webhooks/wh-1');
    wrapper.unmount();
  });

  it('删除的二次确认可以取消（不发请求）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-webhook-id="wh-1"] .webhooks__delete').trigger('click');
    await wrapper.get('.webhooks__confirm-no').trigger('click');

    expect(apiSend).not.toHaveBeenCalledWith('DELETE', '/webhooks/wh-1');
    wrapper.unmount();
  });

  it('点「投递记录」列出这个 webhook 的投递记录', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-webhook-id="wh-1"] .webhooks__deliveries').trigger('click');
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/webhooks/wh-1/deliveries');
    expect(wrapper.find('.delivery-list').exists()).toBe(true);
    wrapper.unmount();
  });

  it('新建成功后给一条提示（用户知道「存上了」，不用靠列表变化猜）', async () => {
    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.webhooks__new').trigger('click');
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await wrapper.findAll('.webhook-form__event')[0].setValue(true);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('已创建');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('删除成功后给一条提示', async () => {
    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('[data-webhook-id="wh-1"] .webhooks__delete').trigger('click');
    await wrapper.get('.webhooks__confirm-yes').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('已删除');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('列表加载失败时给提示（不静默吞掉）', async () => {
    apiGet.mockImplementation((path: string) =>
      path === '/webhooks' ? Promise.reject(new Error('boom')) : Promise.resolve([]),
    );

    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('加载 webhook 列表失败');
    wrapper.unmount();
    toastHost.unmount();
  });
});
