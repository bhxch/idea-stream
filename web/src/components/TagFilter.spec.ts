import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Tag } from '../api/types';
import TagFilter from './TagFilter.vue';

/** 两个标签。 */
const allTags: Tag[] = [
  { name: '想法', count: 3 },
  { name: '待办', count: 1 },
];

/** 挂一个标签过滤器。 */
function mountFilter() {
  return mount(TagFilter, {
    props: {
      allTags,
      selected: [],
      mode: 'or' as const,
    },
  });
}

/** 在组件外面点一下（点 body：不在组件内部）。 */
function clickOutside(): void {
  document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

/** 按一下 Esc。 */
function pressEscape(): void {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
}

describe('TagFilter', () => {
  it('点触发器展开面板，勾选标签发出 update:selected', async () => {
    const wrapper = mountFilter();

    expect(wrapper.find('.tag-filter__panel').exists()).toBe(false);

    await wrapper.get('.tag-filter__trigger').trigger('click');
    expect(wrapper.find('.tag-filter__panel').exists()).toBe(true);

    await wrapper.get('.tag-filter__option input').setValue(true);
    expect(wrapper.emitted('update:selected')).toEqual([[['想法']]]);
  });

  it('点组件外面收起面板', async () => {
    const wrapper = mountFilter();

    await wrapper.get('.tag-filter__trigger').trigger('click');
    expect(wrapper.find('.tag-filter__panel').exists()).toBe(true);

    clickOutside();
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tag-filter__panel').exists()).toBe(false);
  });

  it('按 Esc 收起面板', async () => {
    const wrapper = mountFilter();

    await wrapper.get('.tag-filter__trigger').trigger('click');
    expect(wrapper.find('.tag-filter__panel').exists()).toBe(true);

    pressEscape();
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tag-filter__panel').exists()).toBe(false);
  });

  it('点组件内部（勾标签）不收起面板', async () => {
    const wrapper = mountFilter();

    await wrapper.get('.tag-filter__trigger').trigger('click');
    await wrapper.get('.tag-filter__option input').setValue(true);
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tag-filter__panel').exists()).toBe(true);
  });
});
