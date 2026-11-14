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

import WebhookForm from './WebhookForm.vue';

/** 一个已存在的 webhook。 */
const webhook: Webhook = {
  id: 'wh-1',
  name: '我的 webhook',
  url: 'https://example.com/hook',
  method: 'POST',
  headers: { 'X-Token': 'abc' },
  bodyTemplate: '{"text":"{{idea.content}}"}',
  events: ['idea.created'],
  scope: 'global',
  projectId: null,
  enabled: true,
  hasSecret: false,
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:00:00Z',
};

/** 装表单。 */
function mountForm(props: { webhook?: Webhook | null; scope?: 'global' | 'project'; projectId?: string } = {}) {
  return mount(WebhookForm, {
    props: {
      webhook: props.webhook ?? null,
      scope: props.scope ?? 'global',
      ...(props.projectId ? { projectId: props.projectId } : {}),
    },
  });
}

/** 表单里某个输入框的值。 */
function valueOf(wrapper: ReturnType<typeof mountForm>, selector: string): string {
  return (wrapper.get(selector).element as HTMLInputElement | HTMLTextAreaElement).value;
}

/**
 * 勾上第一个订阅事件（idea.created）。
 *
 * 保存要求**至少订阅一个事件**（见 WebhookForm.vue 的 onSave），
 * 所以要走到「发请求」那一步的用例都得先勾一个。
 */
async function checkFirstEvent(wrapper: ReturnType<typeof mountForm>): Promise<void> {
  await wrapper.findAll('.webhook-form__event')[0].setValue(true);
}

beforeEach(() => {
  apiGet.mockReset();
  apiSend.mockReset();
  apiGet.mockResolvedValue([]);
  apiSend.mockResolvedValue(webhook);
});

describe('WebhookForm', () => {
  it('新建时不传 enabled（由后端按作用域决定默认值）', async () => {
    const wrapper = mountForm();
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"{{idea.content}}"}');
    await checkFirstEvent(wrapper);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/webhooks', {
      name: '新 hook',
      url: 'https://example.com/x',
      method: 'POST',
      headers: {},
      bodyTemplate: '{"text":"{{idea.content}}"}',
      events: ['idea.created'],
      scope: 'global',
      projectId: null,
      secret: null,
    });
    // 请求体里压根没有 enabled 这个键。
    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect('enabled' in body).toBe(false);
    wrapper.unmount();
  });

  it('项目级的新建带上 scope 与 projectId', async () => {
    const wrapper = mountForm({ scope: 'project', projectId: 'project-1' });
    await wrapper.get('.webhook-form__name').setValue('项目 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await checkFirstEvent(wrapper);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.scope).toBe('project');
    expect(body.projectId).toBe('project-1');
    wrapper.unmount();
  });

  it('Body 模板为空时不发请求（后端会拒：模板不能为空）', async () => {
    const wrapper = mountForm();
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('点「Telegram 模板」按钮后 URL 与 Body 模板被预填', async () => {
    apiGet.mockResolvedValue({
      url: 'https://api.telegram.org/bot<BOT_TOKEN>/sendMessage',
      bodyTemplate:
        '{"chat_id":-1003800671233,"message_thread_id":89,"text":"[{{project}}] {{event}}\\n{{idea.title}}\\n{{idea.content}}\\n#{{idea.tags}}"}',
    });

    const wrapper = mountForm();
    await wrapper.get('.webhook-form__telegram').trigger('click');
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith('/webhooks/templates/telegram');
    expect(valueOf(wrapper, '.webhook-form__url')).toContain('api.telegram.org');
    const body = valueOf(wrapper, '.webhook-form__body');
    expect(body).toContain('message_thread_id');
    expect(body).toContain('{{idea.content}}');
    wrapper.unmount();
  });

  it('编辑时 Secret 留空表示不改（请求里是 null）', async () => {
    const wrapper = mountForm({ webhook });
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PATCH', '/webhooks/wh-1', {
      name: '我的 webhook',
      url: 'https://example.com/hook',
      method: 'POST',
      headers: { 'X-Token': 'abc' },
      bodyTemplate: '{"text":"{{idea.content}}"}',
      events: ['idea.created'],
      enabled: true,
      secret: null,
    });
    wrapper.unmount();
  });

  it('编辑时填了新 Secret 就替换成新的', async () => {
    const wrapper = mountForm({ webhook });
    await wrapper.get('.webhook-form__secret').setValue('新密钥');
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.secret).toBe('新密钥');
    wrapper.unmount();
  });

  it('Secret 的值原样发送（首尾的空白是有意义的，不能 trim）', async () => {
    const wrapper = mountForm({ webhook });
    await wrapper.get('.webhook-form__secret').setValue('  abc  ');
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.secret).toBe('  abc  ');
    wrapper.unmount();
  });

  it('填了新密钥就取消「清除密钥」（两者互斥，新填的不能被静默丢掉）', async () => {
    const withSecret: Webhook = { ...webhook, hasSecret: true };
    const wrapper = mountForm({ webhook: withSecret });

    // 勾上「清除密钥」：输入框里的内容被清掉（两者互斥）。
    await wrapper.get('.webhook-form__clear-secret').setValue(true);
    expect(valueOf(wrapper, '.webhook-form__secret')).toBe('');

    // 又填了新密钥：「清除密钥」自动取消勾选 —— 否则新填的会被静默丢掉。
    await wrapper.get('.webhook-form__secret').setValue('新密钥');
    expect(
      (wrapper.get('.webhook-form__clear-secret').element as HTMLInputElement).checked,
    ).toBe(false);

    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.secret).toBe('新密钥');
    wrapper.unmount();
  });

  it('只填了空白时「清除密钥」的勾选不被取消（判空口径与保存时一致）', async () => {
    const withSecret: Webhook = { ...webhook, hasSecret: true };
    const wrapper = mountForm({ webhook: withSecret });

    await wrapper.get('.webhook-form__clear-secret').setValue(true);
    // 只输入一个空格：这不是「填了内容」，勾选不该被静默取消 ——
    // 取消之后保存时又因 trim 后为空而发 null（= 不改），用户明确要的「清除」
    // 就被丢掉了，两个控件都没起作用。
    await wrapper.get('.webhook-form__secret').setValue(' ');
    expect(
      (wrapper.get('.webhook-form__clear-secret').element as HTMLInputElement).checked,
    ).toBe(true);

    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    // 明确要的「清除」发空串，不是 null（null = 不改）。
    expect(body.secret).toBe('');
    wrapper.unmount();
  });

  it('新建时启用开关按作用域给预期初值（全局不勾、项目级勾上）', () => {
    const global = mountForm({ scope: 'global' });
    expect(
      (global.get('.webhook-form__enabled').element as HTMLInputElement).checked,
    ).toBe(false);
    global.unmount();

    const project = mountForm({ scope: 'project', projectId: 'project-1' });
    expect(
      (project.get('.webhook-form__enabled').element as HTMLInputElement).checked,
    ).toBe(true);
    project.unmount();
  });

  it('编辑时勾「清除密钥」就发空串（后端的三态：null 不改、"" 清空）', async () => {
    const withSecret: Webhook = { ...webhook, hasSecret: true };
    const wrapper = mountForm({ webhook: withSecret });
    await wrapper.get('.webhook-form__clear-secret').setValue(true);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.secret).toBe('');
    wrapper.unmount();
  });

  it('订阅事件是多选，勾了哪些就发哪些', async () => {
    const wrapper = mountForm();
    const boxes = wrapper.findAll('.webhook-form__event');
    expect(boxes).toHaveLength(8);

    await boxes[0].setValue(true);
    await boxes[5].setValue(true);
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.events).toEqual(['idea.created', 'project.created']);
    wrapper.unmount();
  });

  it('自定义 Header 是 KV 编辑器：能加能删', async () => {
    const wrapper = mountForm();
    await wrapper.get('.webhook-form__header-add').trigger('click');
    const key = wrapper.get('.webhook-form__header-key');
    const val = wrapper.get('.webhook-form__header-value');
    await key.setValue('X-Token');
    await val.setValue('abc');

    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await checkFirstEvent(wrapper);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.headers).toEqual({ 'X-Token': 'abc' });
    wrapper.unmount();
  });

  it('删除一行 Header 就从请求里去掉', async () => {
    const wrapper = mountForm({ webhook });
    expect(wrapper.findAll('.webhook-form__header-row')).toHaveLength(1);

    await wrapper.get('.webhook-form__header-remove').trigger('click');
    expect(wrapper.findAll('.webhook-form__header-row')).toHaveLength(0);

    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    const body = apiSend.mock.calls[0][2] as Record<string, unknown>;
    expect(body.headers).toEqual({});
    wrapper.unmount();
  });

  it('保存成功后发出 saved（带上存好的 webhook）', async () => {
    const wrapper = mountForm();
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await checkFirstEvent(wrapper);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(wrapper.emitted('saved')).toEqual([[webhook]]);
    wrapper.unmount();
  });

  it('一个事件都不选时不发请求（不静默存成「不订阅任何事件」）', async () => {
    const wrapper = mountForm();
    const toastHost = mount(ToastHost);
    await wrapper.get('.webhook-form__name').setValue('没订阅事件的 hook');
    await wrapper.get('.webhook-form__url').setValue('https://example.com/x');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(apiSend).not.toHaveBeenCalled();
    expect(wrapper.emitted('saved')).toBeUndefined();
    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('事件');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('保存失败时给提示（不静默吞掉）', async () => {
    apiSend.mockRejectedValue(
      new ApiError({
        title: '请求失败',
        status: 400,
        detail: 'webhook 的 URL 必须是 http(s) 绝对地址',
        code: 'invalid_webhook_url',
      }),
    );

    const wrapper = mountForm();
    const toastHost = mount(ToastHost);
    await wrapper.get('.webhook-form__name').setValue('新 hook');
    await wrapper.get('.webhook-form__url').setValue('not-a-url');
    await wrapper.get('.webhook-form__body').setValue('{"text":"x"}');
    await checkFirstEvent(wrapper);
    await wrapper.get('.webhook-form__save').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('http(s) 绝对地址');
    expect(wrapper.emitted('saved')).toBeUndefined();
    wrapper.unmount();
    toastHost.unmount();
  });

  it('点「取消」发出 cancel', async () => {
    const wrapper = mountForm();
    await wrapper.get('.webhook-form__cancel').trigger('click');

    expect(wrapper.emitted('cancel')).toHaveLength(1);
    wrapper.unmount();
  });
});
