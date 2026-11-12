import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { reactive } from 'vue';
import TagEditor from './TagEditor.vue';

describe('TagEditor', () => {
  it('输入后回车：emit update:value 带新标签', async () => {
    const wrapper = mount(TagEditor, { props: { value: [], suggest: () => [] } });

    await wrapper.get('input').setValue('新标签');
    await wrapper.get('input').trigger('keydown', { key: 'Enter' });

    expect(wrapper.emitted('update:value')).toEqual([[['新标签']]]);
  });

  it('空格与逗号分隔多个标签', async () => {
    const wrapper = mount(TagEditor, { props: { value: [], suggest: () => [] } });
    const input = wrapper.get('input');

    await input.setValue('甲');
    await input.trigger('keydown', { key: ' ' });
    await input.setValue('乙');
    await input.trigger('keydown', { key: ',' });

    expect(wrapper.emitted('update:value')).toEqual([[['甲']], [['甲', '乙']]]);
  });

  it('已选标签可以点 × 删掉', async () => {
    const wrapper = mount(TagEditor, { props: { value: ['甲', '乙'], suggest: () => [] } });

    await wrapper.findAll('.chip__remove')[0].trigger('click');

    expect(wrapper.emitted('update:value')).toEqual([[['乙']]]);
  });

  it('重复的标签不再加一遍（大小写不敏感）', async () => {
    const wrapper = mount(TagEditor, { props: { value: ['Idea'], suggest: () => [] } });

    await wrapper.get('input').setValue('idea');
    await wrapper.get('input').trigger('keydown', { key: 'Enter' });

    expect(wrapper.emitted('update:value')).toBeUndefined();
  });

  it('输入时给出补全下拉，点候选即添加', async () => {
    const suggest = vi.fn(() => ['标签甲', '标签乙']);
    const wrapper = mount(TagEditor, { props: { value: [], suggest } });

    await wrapper.get('input').setValue('标');

    const options = wrapper.findAll('.option');
    expect(options.map((option) => option.text())).toEqual(['标签甲', '标签乙']);

    await options[1].trigger('click');
    expect(wrapper.emitted('update:value')).toEqual([[['标签乙']]]);
  });

  it('补全结果异步到位后下拉自动补上', async () => {
    // 父组件的补全来源：第一次还没有结果，稍后才有。
    const cache = reactive<Record<string, string[]>>({});
    const suggest = (prefix: string) => cache[prefix] ?? [];
    const wrapper = mount(TagEditor, { props: { value: [], suggest } });

    await wrapper.get('input').setValue('甲');
    expect(wrapper.findAll('.option')).toHaveLength(0);

    // 父组件把补全结果写进它自己的响应式状态 —— 下拉要跟着更新。
    cache['甲'] = ['甲一'];
    await flushPromises();

    expect(wrapper.findAll('.option').map((option) => option.text())).toEqual(['甲一']);
  });
});
