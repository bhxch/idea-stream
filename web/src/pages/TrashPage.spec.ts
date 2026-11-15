import { beforeEach, describe, expect, it, vi } from 'vitest';
import { config, flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { ApiError } from '../api/client';
import type { Idea, Project, TrashEntry } from '../api/types';
import ToastHost from '../components/ToastHost.vue';
import { projects } from '../stores/projects';

const { apiGet, apiSend } = vi.hoisted(() => ({ apiGet: vi.fn(), apiSend: vi.fn() }));

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return { ...actual, apiGet, apiSend };
});

import TrashPage from './TrashPage.vue';

/** 一条想法。 */
function makeIdea(id: string, content: string, projectId = 'project-1'): Idea {
  return {
    id,
    projectId,
    title: null,
    content,
    tags: [],
    done: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  };
}

/** 容器里的两条想法。 */
const childIdeas = [makeIdea('idea-a', '容器里的甲'), makeIdea('idea-b', '容器里的乙')];

/**
 * 顶层那条想法。
 *
 * 它的项目**还活着**（顶层条目的定义：项目没被删）—— 所以是 project-live，
 * 不是容器里那个已删的 project-1。
 */
const topIdea = makeIdea('idea-c', '顶层的一条', 'project-live');

/** 还活着的那个项目（顶层条目的原属项目）。 */
const liveProject: Project = {
  id: 'project-live',
  name: '想法的原项目',
  sortOrder: 1,
  ideaCount: 1,
  lastActivityAt: '2026-11-01T10:00:00Z',
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:00:00Z',
};

/**
 * 回收站的两类条目。
 *
 * 容器（已删项目）的 `project.ideaCount` 是 **0**（已删项目下没有未删想法），
 * 容器里的想法条数只能看 `entry.ideas.length` —— 用 ideaCount 的话会显示 0 条。
 */
const entries: TrashEntry[] = [
  {
    kind: 'project',
    project: {
      id: 'project-1',
      name: '删掉的项目',
      sortOrder: 0,
      ideaCount: 0,
      lastActivityAt: null,
      createdAt: '2026-11-01T10:00:00Z',
      updatedAt: '2026-11-01T10:00:00Z',
    },
    ideas: childIdeas,
    idea: null,
  },
  {
    kind: 'idea',
    project: null,
    ideas: [],
    idea: topIdea,
  },
];

/** 这一轮测试用的路由（页面里有跳转链接）。 */
let router: Router;

/** 装页面。 */
function mountPage(): ReturnType<typeof mount> {
  return mount(TrashPage);
}

beforeEach(async () => {
  apiGet.mockReset();
  apiSend.mockReset();
  apiGet.mockImplementation((path: string) => {
    if (path === '/trash') {
      return Promise.resolve(entries);
    }
    if (path === '/projects') {
      return Promise.resolve([liveProject]);
    }
    return Promise.resolve([]);
  });
  apiSend.mockResolvedValue({});
  // 项目 store 是模块级的：上一个用例留下的项目名会串到下一个用例里。
  projects.value = [];

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
  await router.push('/trash');
  await router.isReady();
  config.global.plugins = [router];
});

describe('TrashPage', () => {
  it('列出两类条目：容器（已删项目）与顶层想法', async () => {
    const wrapper = mountPage();
    await flushPromises();

    expect(wrapper.findAll('.trash__entry--container')).toHaveLength(1);
    expect(wrapper.findAll('.trash__entry--idea')).toHaveLength(1);
    expect(wrapper.get('.trash__entry--container .trash__name').text()).toBe('删掉的项目');
    expect(wrapper.get('.trash__entry--idea .trash__body').text()).toBe('顶层的一条');
    wrapper.unmount();
  });

  it('容器条目的条数取 ideas.length（不是 project.ideaCount）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    // project.ideaCount 是 0（已删项目下没有未删的想法），ideas.length 是 2。
    expect(wrapper.get('.trash__entry--container .trash__count').text()).toContain('2');
    wrapper.unmount();
  });

  it('顶层条目显示它原来属于哪个项目', async () => {
    const wrapper = mountPage();
    await flushPromises();

    // 顶层条目的原属项目还活着 → 项目名查得到，条目上要看得见。
    const origin = wrapper.get('.trash__entry--idea .trash__origin');
    expect(origin.text()).toContain('想法的原项目');
    // 项目名与说明写在同一个文本节点里：整段文本不等于项目名本身。
    expect(origin.text()).not.toBe('想法的原项目');
    wrapper.unmount();
  });

  it('项目列表拉不到时，回收站照常显示（只是少了原属项目那行）', async () => {
    apiGet.mockImplementation((path: string) => {
      if (path === '/trash') {
        return Promise.resolve(entries);
      }
      if (path === '/projects') {
        return Promise.reject(new ApiError({
          title: '请求失败',
          status: 500,
          detail: '炸了',
          code: 'boom',
        }));
      }
      return Promise.resolve([]);
    });

    const wrapper = mountPage();
    await flushPromises();

    expect(wrapper.get('.trash__entry--idea .trash__body').text()).toBe('顶层的一条');
    expect(wrapper.findAll('.trash__origin')).toHaveLength(0);
    wrapper.unmount();
  });

  it('容器可以展开看它下面的想法', async () => {
    const wrapper = mountPage();
    await flushPromises();

    expect(wrapper.findAll('.trash__child')).toHaveLength(0);

    await wrapper.get('.trash__entry--container .trash__toggle').trigger('click');

    const children = wrapper.findAll('.trash__child');
    expect(children).toHaveLength(2);
    expect(children[0].text()).toContain('容器里的甲');
    wrapper.unmount();
  });

  it('恢复项目：POST /projects/{id}/restore，之后刷新列表', async () => {
    const wrapper = mountPage();
    await flushPromises();
    const before = apiGet.mock.calls.filter((call) => call[0] === '/trash').length;

    await wrapper.get('.trash__entry--container .trash__restore').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/projects/project-1/restore');
    expect(apiGet.mock.calls.filter((call) => call[0] === '/trash').length).toBe(before + 1);
    wrapper.unmount();
  });

  it('恢复顶层想法：POST /ideas/{id}/restore', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('.trash__entry--idea .trash__restore').trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/idea-c/restore');
    wrapper.unmount();
  });

  it('恢复项目成功给一句提示（info）', async () => {
    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.trash__entry--container .trash__restore').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('项目已恢复');
    expect(toastHost.findAll('.toast').at(-1)?.classes()).toContain('toast--info');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('恢复想法成功给一句提示（info）', async () => {
    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.trash__entry--idea .trash__restore').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('想法已恢复');
    expect(toastHost.findAll('.toast').at(-1)?.classes()).toContain('toast--info');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('容器里的想法能单独恢复（项目还在回收站时也恢复得了）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    await wrapper.get('.trash__entry--container .trash__toggle').trigger('click');
    await wrapper.findAll('.trash__child .trash__restore-idea')[1].trigger('click');
    await flushPromises();

    expect(apiSend).toHaveBeenCalledWith('POST', '/ideas/idea-b/restore');
    wrapper.unmount();
  });

  it('恢复失败（不在回收站里）时给提示', async () => {
    apiSend.mockRejectedValue(
      new ApiError({
        title: '请求失败',
        status: 400,
        detail: '想法不在回收站里，无需恢复：idea-c',
        code: 'not_in_trash',
      }),
    );

    const wrapper = mountPage();
    const toastHost = mount(ToastHost);
    await flushPromises();

    await wrapper.get('.trash__entry--idea .trash__restore').trigger('click');
    await flushPromises();

    expect(toastHost.findAll('.toast').at(-1)?.text()).toContain('不在回收站里');
    wrapper.unmount();
    toastHost.unmount();
  });

  it('页面上没有「彻底删除」：到期自动清理是唯一出口', async () => {
    const wrapper = mountPage();
    await flushPromises();
    await wrapper.get('.trash__entry--container .trash__toggle').trigger('click');

    expect(wrapper.text()).not.toContain('彻底删除');
    wrapper.unmount();
  });

  it('页面上说明回收站里的内容到期会自动清理（不说保留期天数）', async () => {
    const wrapper = mountPage();
    await flushPromises();

    const hint = wrapper.get('.trash__hint').text();
    // 用户得知道这些东西怎么才能真没了：到期自动清理。
    expect(hint).toContain('自动清理');
    // 保留期是服务端配置的事，页面与 API 都不提供设置入口 —— 要说清楚。
    expect(hint).toContain('保留期');
    // 保留期由服务端配置决定，页面不提供设置入口：不说具体天数。
    expect(hint).not.toMatch(/\d+\s*天/);
    wrapper.unmount();
  });

  it('回收站是空的时给一句说明', async () => {
    apiGet.mockImplementation((path: string) =>
      Promise.resolve(path === '/trash' ? [] : []),
    );

    const wrapper = mountPage();
    await flushPromises();

    expect(wrapper.get('.trash__empty').text()).toContain('空');
    wrapper.unmount();
  });
});
