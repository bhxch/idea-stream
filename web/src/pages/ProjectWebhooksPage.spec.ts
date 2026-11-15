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

import ProjectWebhooksPage from './ProjectWebhooksPage.vue';

/** 一个项目级 webhook。 */
const webhooks: Webhook[] = [
  {
    id: 'wh-p1',
    name: '项目通知',
    url: 'https://example.com/hook',
    method: 'POST',
    headers: {},
    bodyTemplate: '{"text":"{{idea.content}}"}',
    events: ['idea.created'],
    scope: 'project',
    projectId: 'project-1',
    enabled: true,
    hasSecret: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
];

/** 这一轮测试用的路由。 */
let router: Router;

/** 装页面。 */
function mountPage(): ReturnType<typeof mount> {
  return mount(ProjectWebhooksPage);
}

beforeEach(async () => {
  apiGet.mockReset();
  apiSend.mockReset();
  apiGet.mockImplementation((path: string) => {
    if (path === '/webhooks') {
      return Promise.resolve(webhooks);
    }
    if (path === '/projects') {
      return Promise.resolve([
        {
          id: 'project-1',
          name: '我的项目',
          sortOrder: 0,
          ideaCount: 3,
          lastActivityAt: '2026-11-01T10:00:00Z',
          createdAt: '2026-11-01T10:00:00Z',
          updatedAt: '2026-11-01T10:00:00Z',
        },
      ]);
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
  await router.push('/projects/project-1/webhooks');
  await router.isReady();
  config.global.plugins = [router];
});

describe('ProjectWebhooksPage', () => {
  it('用路由参数里的项目 id 列 webhook（列表请求带 projectId，也要 unmask=true）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    // 后端默认回传的是遮蔽版，页面要完整配置，所以显式要原值。
    expect(apiGet).toHaveBeenCalledWith('/webhooks', {
      projectId: 'project-1',
      unmask: 'true',
    });
    expect(wrapper.findAll('.webhooks__item')).toHaveLength(1);
    expect(wrapper.get('.webhooks__item .webhooks__name').text()).toBe('项目通知');
    wrapper.unmount();
  });

  it('新建：表单存的是项目作用域，带上路由里的项目 id', async () => {
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

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.scope).toBe('project');
    expect(body.projectId).toBe('project-1');
    wrapper.unmount();
  });

  it('标题里带上项目名', async () => {
    const wrapper = mountPage();
    await flushPromises();

    expect(wrapper.get('h1').text()).toContain('我的项目');
    wrapper.unmount();
  });

  it('新建成功后给一条提示（项目页同样有确认）', async () => {
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
});
