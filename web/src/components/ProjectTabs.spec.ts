import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Project } from '../api/types';
import ProjectTabs from './ProjectTabs.vue';

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

/** 挂一排 Tab。 */
function mountTabs(overrides: Record<string, unknown> = {}) {
  return mount(ProjectTabs, {
    props: {
      projects,
      currentId: null,
      mode: 'recent' as const,
      ...overrides,
    },
  });
}

describe('ProjectTabs', () => {
  it('渲染项目名与想法条数', () => {
    const wrapper = mountTabs();

    expect(wrapper.text()).toContain('老项目');
    expect(wrapper.text()).toContain('新项目');
    expect(wrapper.get('[data-project-id="project-1"] .project-tab__count').text()).toBe('3');
    expect(wrapper.get('[data-project-id="project-2"] .project-tab__count').text()).toBe('7');
  });

  it('「全部」单独一项，条数是各项目条数之和', () => {
    const wrapper = mountTabs();

    const all = wrapper.get('[data-project-id="all"]');
    expect(all.text()).toContain('全部');
    expect(all.get('.project-tab__count').text()).toBe('10');
  });

  it('点 Tab 发出 select（带项目 id），当前项目标出来', () => {
    const wrapper = mountTabs({ currentId: 'project-2' });

    expect(wrapper.get('[data-project-id="project-2"]').classes()).toContain('is-active');
    expect(wrapper.get('[data-project-id="project-1"]').classes()).not.toContain('is-active');

    void wrapper.get('[data-project-id="project-1"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([['project-1']]);
  });

  it('点「全部」发出 select（带 null）', () => {
    const wrapper = mountTabs();

    void wrapper.get('[data-project-id="all"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([[null]]);
  });

  it('「新建项目」发出 new-project', () => {
    const wrapper = mountTabs();

    void wrapper.get('.project-tabs__new').trigger('click');
    expect(wrapper.emitted('new-project')).toHaveLength(1);
  });

  it('「更多」发出 open-menu', () => {
    const wrapper = mountTabs();

    void wrapper.get('.project-tabs__menu').trigger('click');
    expect(wrapper.emitted('open-menu')).toHaveLength(1);
  });
});
