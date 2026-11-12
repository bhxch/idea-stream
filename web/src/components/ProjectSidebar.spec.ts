import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Project } from '../api/types';
import ProjectSidebar from './ProjectSidebar.vue';

/** 两个项目。 */
const projects: Project[] = [
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
];

/** 挂一个侧栏。 */
function mountSidebar(overrides: Record<string, unknown> = {}) {
  return mount(ProjectSidebar, {
    props: {
      projects,
      currentId: null,
      mode: 'recent' as const,
      ...overrides,
    },
  });
}

describe('ProjectSidebar', () => {
  it('渲染项目名与想法条数', () => {
    const wrapper = mountSidebar();

    expect(wrapper.text()).toContain('老项目');
    expect(wrapper.text()).toContain('新项目');
    expect(wrapper.get('[data-project-id="project-1"] .project-sidebar__count').text()).toBe('3');
    expect(wrapper.get('[data-project-id="project-2"] .project-sidebar__count').text()).toBe('7');
  });

  it('「全部项目」单独一项，条数是各项目条数之和', () => {
    const wrapper = mountSidebar();

    const all = wrapper.get('[data-project-id="all"]');
    expect(all.text()).toContain('全部项目');
    expect(all.get('.project-sidebar__count').text()).toBe('10');
  });

  it('点项目发出 select（带项目 id），当前项目标出来', () => {
    const wrapper = mountSidebar({ currentId: 'project-2' });

    expect(wrapper.get('[data-project-id="project-2"]').classes()).toContain('is-active');
    expect(wrapper.get('[data-project-id="project-1"]').classes()).not.toContain('is-active');

    void wrapper.get('[data-project-id="project-1"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([['project-1']]);
  });

  it('点「全部项目」发出 select（带 null）', () => {
    const wrapper = mountSidebar();

    void wrapper.get('[data-project-id="all"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([[null]]);
  });

  it('「新建项目」发出 new-project', () => {
    const wrapper = mountSidebar();

    void wrapper.get('.project-sidebar__new').trigger('click');
    expect(wrapper.emitted('new-project')).toHaveLength(1);
  });

  it('切换排序方式发出 change-mode', () => {
    const wrapper = mountSidebar({ mode: 'recent' });

    void wrapper.get('.project-sidebar__mode').trigger('click');
    expect(wrapper.emitted('change-mode')).toEqual([['manual']]);
  });

  it('手动排序模式下拖动项目，发出 reorder（带新的 id 顺序）', async () => {
    const wrapper = mountSidebar({ mode: 'manual' });

    const first = wrapper.get('[data-project-id="project-1"]');
    const second = wrapper.get('[data-project-id="project-2"]');
    expect(first.classes()).toContain('is-draggable');

    await first.trigger('dragstart');
    await second.trigger('drop');

    expect(wrapper.emitted('reorder')).toEqual([[['project-2', 'project-1']]]);
  });

  it('最近活动排序模式下不能拖动（顺序由后端定）', async () => {
    const wrapper = mountSidebar({ mode: 'recent' });

    const first = wrapper.get('[data-project-id="project-1"]');
    expect(first.classes()).not.toContain('is-draggable');

    await first.trigger('dragstart');
    await wrapper.get('[data-project-id="project-2"]').trigger('drop');
    expect(wrapper.emitted('reorder')).toBeUndefined();
  });
});
