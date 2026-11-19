import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Tag } from '../api/types';
import type { IdeaFilter } from '../lib/filter';
import FilterBar from './FilterBar.vue';

/** 标签下拉的选项。 */
const allTags: Tag[] = [{ name: '想法', count: 3 }];

/** 造一份过滤条件，默认不过滤任何东西。 */
function makeFilter(overrides: Partial<IdeaFilter> = {}): IdeaFilter {
  return {
    projectId: null,
    status: 'all',
    tags: [],
    tagMode: 'or',
    keyword: '',
    ...overrides,
  };
}

/** 挂一个过滤栏。 */
function mountBar(filter: IdeaFilter = makeFilter(), options: { attachTo?: Element } = {}) {
  return mount(FilterBar, {
    props: { filter, allTags },
    ...options,
  });
}

/** 搜索框（按可访问名找，跟用户看到的标签一致）。 */
function searchInput(wrapper: ReturnType<typeof mountBar>) {
  return wrapper.get('input[aria-label="搜索想法"]');
}

describe('FilterBar', () => {
  it('过滤栏里有状态按钮组与关键词搜索框', () => {
    const wrapper = mountBar();

    // get() 找不到就会抛错：拿到就算在。
    expect(wrapper.get('[role="group"][aria-label="状态过滤"]').element).toBeTruthy();
    expect(searchInput(wrapper).element).toBeTruthy();
  });

  it('输入关键词发出 update:filter（输入即过滤）', async () => {
    const wrapper = mountBar();

    await searchInput(wrapper).setValue('点子');

    expect(wrapper.emitted('update:filter')).toEqual([[makeFilter({ keyword: '点子' })]]);
  });

  it('搜索框里显示当前的关键词', () => {
    const wrapper = mountBar(makeFilter({ keyword: '点子' }));

    expect((searchInput(wrapper).element as HTMLInputElement).value).toBe('点子');
  });

  it('没有过滤生效时不显示「清除」按钮', () => {
    const wrapper = mountBar();

    expect(wrapper.find('.filter-bar__clear').exists()).toBe(false);
  });

  it('状态过滤生效时显示「清除」按钮', () => {
    const wrapper = mountBar(makeFilter({ status: 'open' }));

    expect(wrapper.find('.filter-bar__clear').exists()).toBe(true);
  });

  it('选了标签时显示「清除」按钮', () => {
    const wrapper = mountBar(makeFilter({ tags: ['想法'] }));

    expect(wrapper.find('.filter-bar__clear').exists()).toBe(true);
  });

  it('有关键词时显示「清除」按钮', () => {
    const wrapper = mountBar(makeFilter({ keyword: '点子' }));

    expect(wrapper.find('.filter-bar__clear').exists()).toBe(true);
  });

  it('过滤条件变了，「清除」按钮跟着出现/消失', async () => {
    const wrapper = mountBar();
    expect(wrapper.find('.filter-bar__clear').exists()).toBe(false);

    await wrapper.setProps({ filter: makeFilter({ status: 'open' }) });
    expect(wrapper.find('.filter-bar__clear').exists()).toBe(true);

    await wrapper.setProps({ filter: makeFilter() });
    expect(wrapper.find('.filter-bar__clear').exists()).toBe(false);
  });

  it('点「清除」把状态、标签、关键词一起清掉（项目不动）', async () => {
    const wrapper = mountBar(
      makeFilter({ projectId: 'project-1', status: 'done', tags: ['想法'], keyword: '点子' }),
    );

    await wrapper.get('.filter-bar__clear').trigger('click');

    // 三样一起清掉；项目是列表的范围，不是过滤条件，不动它。
    expect(wrapper.emitted('update:filter')).toEqual([[makeFilter({ projectId: 'project-1' })]]);
  });

  it('点状态按钮只改状态，不碰关键词与标签', async () => {
    const wrapper = mountBar(makeFilter({ tags: ['想法'], keyword: '点子' }));

    await wrapper.get('.filter-bar__status-button:last-child').trigger('click');

    expect(wrapper.emitted('update:filter')).toEqual([[makeFilter({ status: 'done', tags: ['想法'], keyword: '点子' })]]);
  });

  it('the_status_buttons_are_replaced_by_a_dropdown_on_narrow_screens', async () => {
    const wrapper = mountBar();

    expect(wrapper.find('.filter-bar__status-panel').exists()).toBe(false);

    await wrapper.get('.filter-bar__status-trigger').trigger('click');

    // 面板里有状态三态（窄屏形态：三个分段按钮收成这一个下拉）。
    const panel = wrapper.get('.filter-bar__status-panel');
    expect(panel.text()).toContain('全部');
    expect(panel.text()).toContain('未完成');
    expect(panel.text()).toContain('已完成');
  });

  it('picking_a_status_emits_the_filter_update', async () => {
    const wrapper = mountBar();

    await wrapper.get('.filter-bar__status-trigger').trigger('click');
    const option = wrapper
      .findAll('.filter-bar__status-option')
      .find((item) => item.text() === '已完成');
    expect(option, '下拉里没有「已完成」这一项').toBeTruthy();
    await option!.trigger('click');

    expect(wrapper.emitted('update:filter')).toEqual([[makeFilter({ status: 'done' })]]);
  });

  it('focusSearch_puts_the_focus_in_the_search_box', async () => {
    // 焦点断言要真的 document：元素得挂在 document 里（不然 focus() 不生效）。
    const wrapper = mountBar(makeFilter(), { attachTo: document.body });
    try {
      wrapper.vm.focusSearch();
      await wrapper.vm.$nextTick();

      expect(document.activeElement).toBe(searchInput(wrapper).element);
    } finally {
      wrapper.unmount();
      document.body.innerHTML = '';
    }
  });
});
