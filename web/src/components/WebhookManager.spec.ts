import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { ApiError } from '../api/client';
import type { Webhook } from '../api/types';
import ToastHost from './ToastHost.vue';

const { apiGet, apiSend } = vi.hoisted(() => ({ apiGet: vi.fn(), apiSend: vi.fn() }));

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet, apiSend };
});

import WebhookManager from './WebhookManager.vue';

/** 列表里的 webhook（配置是合法的：能直接存回去）。 */
const webhook: Webhook = {
  id: 'wh-1',
  name: '我的 webhook',
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
};

/** 装管理器。 */
function mountManager(props: { scope?: 'global' | 'project'; projectId?: string } = {}) {
  return mount(WebhookManager, {
    props: {
      scope: props.scope ?? 'global',
      ...(props.projectId ? { projectId: props.projectId } : {}),
    },
  });
}

/** 最后一条提示（提示队列是模块级的，跨用例累积）。 */
function lastToast(toastHost: ReturnType<typeof mount>): string {
  return toastHost.findAll('.toast').at(-1)?.text() ?? '';
}

beforeEach(() => {
  apiGet.mockReset();
  apiSend.mockReset();
  apiGet.mockImplementation((path: string) =>
    path === '/webhooks' ? Promise.resolve([webhook]) : Promise.resolve([]),
  );
  apiSend.mockResolvedValue(webhook);
});

describe('WebhookManager', () => {
  it('列出 webhook（要 unmask=true，否则拿不到完整 url）', async () => {
    const wrapper = mountManager();
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/webhooks', { unmask: 'true' });
    expect(wrapper.findAll('.webhooks__item')).toHaveLength(1);
    expect(wrapper.get('.webhooks__name').text()).toBe('我的 webhook');
    wrapper.unmount();
  });

  it('新建成功后给一条 info 提示（用户能确认「存上了」）', async () => {
    const wrapper = mountManager();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.webhooks__new').trigger('click');
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await wrapper.findAll('.webhook-form__event')[0].setValue(true);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/webhooks', expect.anything());
    expect(lastToast(toastHost)).toContain('已创建');
    // 是「普通提示」不是「错误提示」。
    expect(toastHost.findAll('.toast').at(-1)?.classes()).toContain('toast--info');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('编辑成功后给一条 info 提示', async () => {
    const wrapper = mountManager();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.webhooks__edit').trigger('click');
    await wrapper.get('.webhook-form__name').setValue('改了名字');
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PATCH', '/webhooks/wh-1', expect.anything());
    expect(lastToast(toastHost)).toContain('已保存');
    expect(toastHost.findAll('.toast').at(-1)?.classes()).toContain('toast--info');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('删除成功后给一条 info 提示', async () => {
    const wrapper = mountManager();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.webhooks__delete').trigger('click');
    await wrapper.get('.webhooks__confirm-yes').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('DELETE', '/webhooks/wh-1');
    expect(lastToast(toastHost)).toContain('已删除');
    expect(toastHost.findAll('.toast').at(-1)?.classes()).toContain('toast--info');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('删除失败时给的是错误提示（不是「已删除」）', async () => {
    apiSend.mockRejectedValue(
      new ApiError({
        title: '请求失败',
        status: 500,
        detail: '删除 webhook 失败',
        code: 'boom',
      }),
    );

    const wrapper = mountManager();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.webhooks__delete').trigger('click');
    await wrapper.get('.webhooks__confirm-yes').trigger('click');
    await flushPromises();

    expect(lastToast(toastHost)).not.toContain('已删除');
    expect(toastHost.findAll('.toast').at(-1)?.classes()).toContain('toast--error');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('点「投递记录」展开面板（面板里能自己刷新）', async () => {
    const wrapper = mountManager();
    await flushPromises();

    expect(wrapper.find('.delivery-list').exists()).toBe(false);
    await wrapper.get('.webhooks__deliveries').trigger('click');
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/webhooks/wh-1/deliveries');
    expect(wrapper.find('.delivery-list').exists()).toBe(true);
    expect(wrapper.find('.delivery__refresh').exists()).toBe(true);
    wrapper.unmount();
  });

  it('项目作用域：列表请求带上 projectId', async () => {
    const wrapper = mountManager({ scope: 'project', projectId: 'project-1' });
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/webhooks', {
      projectId: 'project-1',
      unmask: 'true',
    });
    wrapper.unmount();
  });
});
