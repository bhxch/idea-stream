import { beforeEach, describe, expect, it, vi } from 'vitest';
import { config, flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { ApiError } from '../api/client';
import type { Project } from '../api/types';
import ToastHost from '../components/ToastHost.vue';
import { projectSortMode, projects } from '../stores/projects';

const { apiGet, apiSend } = vi.hoisted(() => ({ apiGet: vi.fn(), apiSend: vi.fn() }));

// 只换掉两个请求函数，ApiError 等其余导出用真的（`instanceof` 判定要靠它）。
vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet, apiSend };
});

import ProjectsPage from './ProjectsPage.vue';

/** 三个项目。 */
const projectList: Project[] = [
  {
    id: 'project-1',
    name: '老项目',
    sortOrder: 0,
    ideaCount: 3,
    lastActivityAt: '2026-11-01T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
  {
    id: 'project-2',
    name: '新项目',
    sortOrder: 1,
    ideaCount: 7,
    lastActivityAt: '2026-11-10T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-10T10:00:00Z',
  },
  {
    id: 'project-3',
    name: '第三个项目',
    sortOrder: 2,
    ideaCount: 0,
    lastActivityAt: '2026-11-10T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-10T10:00:00Z',
  },
];

/** 这一轮测试用的路由（页面里有跳转链接）。 */
let router: Router;

/** 装页面。 */
function mountPage(): ReturnType<typeof mount> {
  return mount(ProjectsPage);
}

beforeEach(async () => {
  apiGet.mockReset();
  apiSend.mockReset();
  // 项目列表与排序方式是模块级单例（跨用例共享）：清掉上一个用例留下的。
  projects.value = [];
  projectSortMode.value = 'recent';

  apiGet.mockImplementation((path: string) => {
    switch (path) {
      case '/projects':
        return Promise.resolve(projectList);
      case '/settings':
        return Promise.resolve({ 'project.sort_mode': 'recent' });
      default:
        return Promise.resolve([]);
    }
  });
  apiSend.mockImplementation((method: string) => {
    if (method === 'POST') {
      return Promise.resolve(projectList);
    }
    return Promise.resolve({ 'project.sort_mode': 'recent' });
  });

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
  await router.push('/projects');
  await router.isReady();
  config.global.plugins = [router];
});

describe('ProjectsPage', () => {
  it('列出后端给的项目（顺序由后端定，前端不重排）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    const names = wrapper.findAll('.projects__name').map((item) => item.text());
    expect(names).toEqual(['老项目', '新项目', '第三个项目']);
    wrapper.unmount();
  });

  it('新建项目：填名字提交后 POST /projects，并刷新列表', async () => {
    const wrapper = mountPage();
    await flushPromises();
    const before = apiGet.mock.calls.filter((call) => call[0] === '/projects').length;

    await wrapper.get('.projects__new-input').setValue('新项目');
    await wrapper.get('form.projects__new').trigger('submit');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/projects', { name: '新项目' });
    // 建完之后重新拉一次列表（后端给的顺序才是真的）。
    expect(apiGet.mock.calls.filter((call) => call[0] === '/projects').length).toBe(before + 1);
    wrapper.unmount();
  });

  it('新建失败时给提示（不静默吞掉）', async () => {
    apiSend.mockImplementation((method: string) => {
      if (method === 'POST') {
        return Promise.reject(
          new ApiError({ title: '请求失败', status: 400, detail: '项目名称不能为空', code: 'invalid_project_name' }),
        );
      }
      return Promise.resolve(projectList);
    });

    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.projects__new-input').setValue('新项目');
    await wrapper.get('form.projects__new').trigger('submit');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('项目名称不能为空');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('切到手动排序：先用当前显示顺序初始化 sort_order，再切设置', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('.projects__mode--manual').trigger('click');
    await flushPromises();

    // 顺序很重要：先把当前显示顺序写进 sort_order，切过去后顺序才不会跳位。
    expect(apiSend.mock.calls[0]).toEqual([
      'POST',
      '/projects/reorder',
      { ids: ['project-1', 'project-2', 'project-3'] },
    ]);
    expect(apiSend.mock.calls[1]).toEqual([
      'PUT',
      '/settings/project.sort_mode',
      { value: 'manual' },
    ]);
    wrapper.unmount();
  });

  it('切回最近活动排序：只切设置，不重排', async () => {
    apiGet.mockImplementation((path: string) => {
      if (path === '/projects') {
        return Promise.resolve(projectList);
      }
      if (path === '/settings') {
        return Promise.resolve({ 'project.sort_mode': 'manual' });
      }
      return Promise.resolve([]);
    });

    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('.projects__mode--recent').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PUT', '/settings/project.sort_mode', { value: 'recent' });
    expect(apiSend).not.toHaveBeenCalledWith('POST', '/projects/reorder', expect.anything());
    wrapper.unmount();
  });

  it('手动排序模式下拖动项目：POST /projects/reorder（带新的 id 顺序）', async () => {
    apiGet.mockImplementation((path: string) => {
      if (path === '/projects') {
        return Promise.resolve(projectList);
      }
      if (path === '/settings') {
        return Promise.resolve({ 'project.sort_mode': 'manual' });
      }
      return Promise.resolve([]);
    });

    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-project-id="project-1"]').trigger('dragstart');
    await wrapper.get('[data-project-id="project-3"]').trigger('drop');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/projects/reorder', {
      ids: ['project-2', 'project-1', 'project-3'],
    });
    wrapper.unmount();
  });

  it('最近活动排序模式下拖不动（顺序由后端定）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-project-id="project-1"]').trigger('dragstart');
    await wrapper.get('[data-project-id="project-3"]').trigger('drop');
    await flushPromises();

    expect(apiSend).not.toHaveBeenCalledWith('POST', '/projects/reorder', expect.anything());
    wrapper.unmount();
  });

  it('行内改名：PATCH /projects/{id}', async () => {
    apiSend.mockImplementation((method: string) => {
      if (method === 'PATCH') {
        return Promise.resolve({ ...projectList[0], name: '改过的名字' });
      }
      return Promise.resolve(projectList);
    });

    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-project-id="project-1"] .projects__edit').trigger('click');
    await wrapper.get('.projects__rename').setValue('改过的名字');
    await wrapper.get('.projects__save-rename').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('PATCH', '/projects/project-1', { name: '改过的名字' });
    wrapper.unmount();
  });

  it('删除要二次确认：确认后才 DELETE', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('[data-project-id="project-2"] .projects__delete').trigger('click');
    // 还没确认，不该发请求。
    expect(apiSend).not.toHaveBeenCalledWith('DELETE', '/projects/project-2');

    await wrapper.get('.projects__confirm-yes').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('DELETE', '/projects/project-2');
    wrapper.unmount();
  });

  it('删除内置收件箱失败时给提示（页面不崩）', async () => {
    apiSend.mockImplementation((method: string) => {
      if (method === 'DELETE') {
        return Promise.reject(
          new ApiError({
            title: '请求失败',
            status: 400,
            detail: '内置收件箱不能删除',
            code: 'inbox_not_deletable',
          }),
        );
      }
      return Promise.resolve(projectList);
    });

    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('[data-project-id="project-2"] .projects__delete').trigger('click');
    await wrapper.get('.projects__confirm-yes').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('内置收件箱不能删除');
    wrapper.unmount();
    toastHost.unmount();
  });
});
