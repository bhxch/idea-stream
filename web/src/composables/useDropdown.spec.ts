import { afterEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { mount, type VueWrapper } from '@vue/test-utils';
import { useDropdown } from './useDropdown';

/**
 * 一个最小的下拉：触发器 + 面板。
 *
 * 面板在 `open` 为真时才渲染，`root` 绑在最外层的容器上 ——
 * 「点组件外面收起」靠的就是它判断点击落在哪儿。
 */
const Host = defineComponent({
  setup() {
    const { open, root } = useDropdown();
    return () =>
      h('div', { ref: root }, [
        h(
          'button',
          { class: 'trigger', type: 'button', onClick: () => (open.value = !open.value) },
          '打开',
        ),
        open.value ? h('div', { class: 'panel' }, '面板') : null,
      ]);
  },
});

/**
 * 挂着的组件：每个用例结束后都要**卸载**。
 *
 * 只清 `document.body.innerHTML` 是不够的：组件还「挂」着，
 * useDropdown 注册在 document 上的 click / keydown 监听也不会被摘掉，
 * 会留到后面的用例里去。
 */
const mounted = new Set<VueWrapper>();

/**
 * 挂一个下拉。
 *
 * `attachTo: document.body` 是必须的：组件内部的点击要能冒泡到 document，
 * 「点组件内部不收起」那条才不是空转的。
 */
function mountHost(): VueWrapper {
  const wrapper = mount(Host, { attachTo: document.body });
  mounted.add(wrapper);
  return wrapper;
}

afterEach(() => {
  // 卸载：组件的 onBeforeUnmount 会把 document 上的监听摘掉。
  for (const wrapper of mounted) {
    wrapper.unmount();
  }
  mounted.clear();
  // 挂在 body 上的 DOM 也要清掉：不然残留会影响后面的用例。
  document.body.innerHTML = '';
});

/** 在组件外面点一下（点 body：不在组件内部）。 */
function clickOutside(): void {
  document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

/** 按一下 Esc。 */
function pressEscape(): void {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
}

describe('useDropdown', () => {
  it('初始是收起的，点触发器展开', async () => {
    const wrapper = mountHost();

    expect(wrapper.find('.panel').exists()).toBe(false);

    await wrapper.get('.trigger').trigger('click');

    expect(wrapper.find('.panel').exists()).toBe(true);
  });

  it('点组件外面收起面板', async () => {
    const wrapper = mountHost();

    await wrapper.get('.trigger').trigger('click');
    expect(wrapper.find('.panel').exists()).toBe(true);

    clickOutside();
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.panel').exists()).toBe(false);
  });

  it('按 Esc 收起面板', async () => {
    const wrapper = mountHost();

    await wrapper.get('.trigger').trigger('click');
    expect(wrapper.find('.panel').exists()).toBe(true);

    pressEscape();
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.panel').exists()).toBe(false);
  });

  it('点组件内部（面板里的内容）不收起面板', async () => {
    const wrapper = mountHost();

    await wrapper.get('.trigger').trigger('click');
    await wrapper.get('.panel').trigger('click');
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.panel').exists()).toBe(true);
  });

  it('组件卸载后把 document 上的监听摘掉', () => {
    const remove = vi.spyOn(document, 'removeEventListener');
    const wrapper = mountHost();

    wrapper.unmount();
    // 已经卸载过：afterEach 里别再卸一次（Vue 会警告「app 没挂载」）。
    mounted.delete(wrapper);

    expect(remove).toHaveBeenCalledWith('click', expect.any(Function));
    expect(remove).toHaveBeenCalledWith('keydown', expect.any(Function));
    remove.mockRestore();
  });
});
