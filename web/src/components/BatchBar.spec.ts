import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import BatchBar from './BatchBar.vue';

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

  it('加标签：选好标签后 emit add-tags 带标签', async () => {
    const wrapper = mountBar();

    await wrapper.get('button.action--add-tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await wrapper.get('button.action--apply-tags').trigger('click');

    expect(wrapper.emitted('add-tags')).toEqual([[['新标签']]]);
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
});
