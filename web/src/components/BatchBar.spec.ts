import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import BatchBar from './BatchBar.vue';
// 组件源码（?raw）：触摸目标的断点写在 CSS 里，渲染出来的 DOM 上读不到。
import batchBarSource from './BatchBar.vue?raw';

/** 挂一个批量操作条。 */
function mountBar(overrides: Record<string, unknown> = {}) {
  return mount(BatchBar, {
    props: { count: 3, suggest: () => [], ...overrides },
  });
}

describe('BatchBar', () => {
  it('显示勾选了几条', () => {
    const wrapper = mountBar();

    expect(wrapper.get('.count').text()).toContain('3');
  });

  it('各个操作各自发出对应的事件', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--complete').trigger('click');
    await wrapper.get('button.action--undo-complete').trigger('click');
    await wrapper.get('button.action--merge').trigger('click');
    await wrapper.get('button.action--clear').trigger('click');

    expect(wrapper.emitted('complete')).toHaveLength(1);
    expect(wrapper.emitted('undo-complete')).toHaveLength(1);
    expect(wrapper.emitted('merge')).toHaveLength(1);
    expect(wrapper.emitted('clear')).toHaveLength(1);
  });

  it('不足两条时不能合并（合并至少要两条）', () => {
    const wrapper = mountBar({ count: 1 });

    expect((wrapper.get('button.action--merge').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('加标签：选好标签后 emit add-tags 带标签（还带一个回执）', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--add-tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await wrapper.get('button.action--apply-tags').trigger('click');

    const emitted = wrapper.emitted('add-tags');
    expect(emitted).toHaveLength(1);
    expect(emitted?.[0]?.[0]).toEqual(['新标签']);
    // 第二个参数是回执：父组件发完请求后调用它，操作条才知道成没成。
    expect(typeof emitted?.[0]?.[1]).toBe('function');
  });

  it('父组件回执成功后才清草稿、收起面板', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--add-tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await wrapper.get('button.action--apply-tags').trigger('click');

    // 回执还没来：草稿还在，面板还开着。
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.get('.chip__name').text()).toBe('新标签');

    const ack = wrapper.emitted('add-tags')?.[0]?.[1] as (ok: boolean) => void;
    ack(true);
    await wrapper.vm.$nextTick();

    expect(wrapper.findAll('.tag-editor')).toHaveLength(0);
    expect(wrapper.findAll('.chip__name')).toHaveLength(0);
  });

  it('父组件回执失败时草稿不丢（用户能直接重试）', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--add-tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await wrapper.get('button.action--apply-tags').trigger('click');

    const ack = wrapper.emitted('add-tags')?.[0]?.[1] as (ok: boolean) => void;
    ack(false);
    await wrapper.vm.$nextTick();

    // 面板还开着，标签还在：用户敲的没白敲。
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.get('.chip__name').text()).toBe('新标签');
  });

  it('删除要二次确认，确认后才发出 delete', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--delete').trigger('click');
    expect(wrapper.emitted('delete')).toBeUndefined();

    await wrapper.get('button.confirm__delete').trigger('click');
    expect(wrapper.emitted('delete')).toHaveLength(1);
  });

  it('请求在飞时按钮禁用（防重复提交）', () => {
    const wrapper = mountBar({ busy: true });

    expect((wrapper.get('button.action--complete').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('请求在飞时每个按钮都禁用（不能只禁一个）', () => {
    const wrapper = mountBar({ busy: true });

    const selectors = [
      'button.action--complete',
      'button.action--undo-complete',
      'button.action--merge',
      'button.action--add-tags',
      'button.action--delete',
      'button.action--clear',
    ];
    for (const selector of selectors) {
      expect((wrapper.get(selector).element as HTMLButtonElement).disabled, selector).toBe(true);
    }
  });

  it('请求在飞时「加到 N 条」也禁用（别发第二个批量请求）', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--add-tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });

    await wrapper.setProps({ busy: true });

    expect((wrapper.get('button.action--apply-tags').element as HTMLButtonElement).disabled).toBe(true);
  });

  /**
   * 触摸目标的断点：源码级守卫。
   *
   * jsdom 不求值媒体查询，「窄屏上按钮有没有被撑到 32px 高」在单测里读不出来
   * —— 视觉验证归 e2e。这里钉住**断点值本身**：32×32 的规则跟两列断点对齐
   * （≤767px），否则 641–767px 那一段既按手机渲染又不满足触摸目标。
   */
  it('触摸目标的断点统一到 767px', () => {
    expect(batchBarSource).toContain('@media (max-width: 767px)');
    // 旧的 640px 那一档得整个没了（不是「再加一条 767px」）。
    expect(batchBarSource).not.toContain('@media (max-width: 640px)');
  });
});
