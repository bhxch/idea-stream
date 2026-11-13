import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { ApiError } from '../api/client';
import type { Delivery } from '../api/types';
import ToastHost from './ToastHost.vue';

const { apiGet, apiSend } = vi.hoisted(() => ({ apiGet: vi.fn(), apiSend: vi.fn() }));

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet, apiSend };
});

import DeliveryList from './DeliveryList.vue';

/** 一条投递记录（失败的那次）。 */
const delivery: Delivery = {
  id: 'delivery-1',
  webhookId: 'wh-1',
  eventId: 'event-1',
  eventType: 'idea.created',
  payload: '{"text":"一条想法"}',
  status: 'failed',
  attempts: 4,
  lastError: '连接超时',
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:05:00Z',
};

beforeEach(() => {
  apiGet.mockReset();
  apiSend.mockReset();
  apiGet.mockResolvedValue([delivery]);
  apiSend.mockResolvedValue({ ...delivery, status: 'pending' });
});

describe('DeliveryList', () => {
  it('列出这个 webhook 的投递记录', async () => {
    const wrapper = mount(DeliveryList, { props: { webhookId: 'wh-1' } });
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/webhooks/wh-1/deliveries');
    const rows = wrapper.findAll('.delivery__row');
    expect(rows).toHaveLength(1);
    expect(rows[0].text()).toContain('idea.created');
    expect(rows[0].text()).toContain('连接超时');
    wrapper.unmount();
  });

  it('点「重投」调 retry，之后刷新列表', async () => {
    const wrapper = mount(DeliveryList, { props: { webhookId: 'wh-1' } });
    await flushPromises();
    const before = apiGet.mock.calls.filter(
      (call) => call[0] === '/webhooks/wh-1/deliveries',
    ).length;

    await wrapper.get('.delivery__retry').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith(
      'POST',
      '/webhooks/wh-1/deliveries/delivery-1/retry',
    );
    expect(
      apiGet.mock.calls.filter((call) => call[0] === '/webhooks/wh-1/deliveries').length,
    ).toBe(before + 1);
    wrapper.unmount();
  });

  it('重投失败（队列满了）时给提示', async () => {
    apiSend.mockRejectedValue(
      new ApiError({
        title: '请求失败',
        status: 503,
        detail: '投递队列已满，请稍后重试',
        code: 'delivery_queue_full',
      }),
    );

    const wrapper = mount(DeliveryList, { props: { webhookId: 'wh-1' } });
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.delivery__retry').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('投递队列已满');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('没有投递记录时给一句说明', async () => {
    apiGet.mockResolvedValue([]);

    const wrapper = mount(DeliveryList, { props: { webhookId: 'wh-1' } });
    await flushPromises();

    expect(wrapper.get('.delivery__empty').text()).toContain('没有');
    wrapper.unmount();
  });
});
