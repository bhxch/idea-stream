import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Project } from '../api/types';
import ProjectDropdown from './ProjectDropdown.vue';

/** 两个项目。 */
const projects: Project[] = [
  {
    id: 'project-1',
    name: '杂事',
    sortOrder: 0,
    ideaCount: 3,
    lastActivityAt: '2026-11-01T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
  {
    id: 'project-2',
    name: '工作复盘',
    sortOrder: 1,
    ideaCount: 7,
    lastActivityAt: '2026-11-10T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-10T10:00:00Z',
  },
];

/** 挂一个项目下拉。 */
function mountDropdown(overrides: Record<string, unknown> = {}) {
  return mount(ProjectDropdown, {
    props: {
      projects,
      currentId: null,
      ...overrides,
    },
  });
}

describe('ProjectDropdown', () => {
  it('the_trigger_shows_the_current_project_name', () => {
    const wrapper = mountDropdown({ currentId: 'project-2' });

    expect(wrapper.get('.project-dropdown__trigger').text()).toContain('工作复盘');
  });

  it('the_trigger_shows_all_projects_when_none_is_selected', () => {
    const wrapper = mountDropdown({ currentId: null });

    expect(wrapper.get('.project-dropdown__trigger').text()).toContain('全部项目');
  });

  it('picking_a_project_emits_select', async () => {
    const wrapper = mountDropdown({ currentId: null });

    await wrapper.get('.project-dropdown__trigger').trigger('click');
    await wrapper.get('.project-dropdown__item[data-project-id="project-2"]').trigger('click');

    expect(wrapper.emitted('select')).toEqual([['project-2']]);
  });

  it('picking_all_projects_emits_null', async () => {
    const wrapper = mountDropdown({ currentId: 'project-2' });

    await wrapper.get('.project-dropdown__trigger').trigger('click');
    await wrapper.get('.project-dropdown__item[data-project-id="all"]').trigger('click');

    expect(wrapper.emitted('select')).toEqual([[null]]);
  });
});
