import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
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
    const { ApiError } = await import('../api/client');
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
          return Promise.reject(
            new ApiError({
              title: '请求失败',
              status: 400,
              detail: '命中 3 条想法，超过单次返回上限 2000，请收窄过滤条件',
              code: 'too_many_ideas',
            }),
          );
        }
        return Promise.resolve([idea]);
      }
      return Promise.resolve([]);
    });

    const wrapper = mount(HomePage);
    const toastHost = mount(ToastHost);
    await flushPromises();

    expect(toastHost.text()).toContain('共 3 条超过单次上限');
    expect(currentProjectId.value).toBe('project-2');
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
});
