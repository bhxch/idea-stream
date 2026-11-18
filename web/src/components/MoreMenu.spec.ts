import { describe, expect, it } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import MoreMenu from './MoreMenu.vue';

/** 挂一个「⋯」菜单。 */
function mountMenu(): VueWrapper {
  return mount(MoreMenu);
}

/** 菜单里的一项（按文字找）。 */
function item(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAll('.more-menu__item').find((button) => button.text() === label);
  expect(found, `菜单里没有「${label}」这一项`).toBeTruthy();
  return found!;
}

/** 在组件外面点一下（点 body：不在组件内部）。 */
function clickOutside(): void {
  document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

describe('MoreMenu', () => {
  it('the_three_management_entries_are_reachable', async () => {
    const wrapper = mountMenu();

    await wrapper.get('.more-menu__trigger').trigger('click');

    const panel = wrapper.get('.more-menu__panel');
    expect(panel.text()).toContain('项目管理');
    expect(panel.text()).toContain('回收站');
    expect(panel.text()).toContain('Webhook');
  });

  it('each_entry_emits_its_own_event', async () => {
    const wrapper = mountMenu();

    await wrapper.get('.more-menu__trigger').trigger('click');
    await item(wrapper, '回收站').trigger('click');
    expect(wrapper.emitted('trash')).toHaveLength(1);
    // 「自己的事件」：别的两个事件不该跟着响。
    expect(wrapper.emitted('webhooks')).toBeUndefined();
    expect(wrapper.emitted('manageProjects')).toBeUndefined();

    await wrapper.get('.more-menu__trigger').trigger('click');
    await item(wrapper, 'Webhook').trigger('click');
    expect(wrapper.emitted('webhooks')).toHaveLength(1);

    await wrapper.get('.more-menu__trigger').trigger('click');
    await item(wrapper, '项目管理').trigger('click');
    expect(wrapper.emitted('manageProjects')).toHaveLength(1);
  });

  it('clicking_outside_closes_the_menu', async () => {
    const wrapper = mountMenu();

    await wrapper.get('.more-menu__trigger').trigger('click');
    expect(wrapper.find('.more-menu__panel').exists()).toBe(true);

    clickOutside();
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.more-menu__panel').exists()).toBe(false);
  });

  it('按 Esc 收起菜单', async () => {
    const wrapper = mountMenu();

    await wrapper.get('.more-menu__trigger').trigger('click');
    expect(wrapper.find('.more-menu__panel').exists()).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.more-menu__panel').exists()).toBe(false);
  });
});
