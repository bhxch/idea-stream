import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { ApiError } from '../api/client';
import type { Idea, Project } from '../api/types';
import ToastHost from '../components/ToastHost.vue';
import { currentProjectId } from '../stores/projects';

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

beforeEach(() => {
  apiGet.mockReset();
  apiSend.mockReset();
  currentProjectId.value = null;
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
});

describe('HomePage', () => {
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
    // 下拉的值也跟着切过去（列表与下拉必须对得上）。
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('project-2');
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

    const select = wrapper.get('select');
    await select.setValue('project-1');
    await flushPromises();

    // 回滚到原来的「全部项目」，过滤条件也跟着回去（否则旧数据会被滤成空）。
    expect(currentProjectId.value).toBe(null);
    expect((select.element as HTMLSelectElement).value).toBe('');
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

    const select = wrapper.get('select');
    await select.setValue('project-2');
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

    const select = wrapper.get('select');
    await select.setValue('project-2');
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

    // 补救失败 → 回到原处，不能停在「下拉是 project-2、列表却是空的」。
    expect(currentProjectId.value).toBe(null);
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('');
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

    const select = wrapper.get('select');
    await select.setValue('project-2');
    await flushPromises();

    // 只给一条提示（不再对同一个项目切一次、也不再弹第二条）。
    const toasts = toastHost.findAll('.toast');
    expect(toasts.length).toBe(before + 1);
    expect(toasts.at(-1)?.text()).toContain('共 3 条超过单次上限');
    // 切不过去 → 回到原来的范围，下拉与列表对得上。
    expect(currentProjectId.value).toBe(null);
    expect((select.element as HTMLSelectElement).value).toBe('');
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
});
