import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Idea } from '../api/types';
import MessageItem from './MessageItem.vue';
import MessageList from './MessageList.vue';

/** 一条想法。 */
const idea: Idea = {
  id: 'idea-1',
  projectId: 'project-1',
  title: null,
  content: '一条想法',
  tags: [],
  done: false,
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:00:00Z',
};

/** 第二条想法（比第一条新，列表顺序按传入顺序渲染）。 */
const newerIdea: Idea = { ...idea, id: 'idea-2', content: '另一条想法' };

/** 挂一个列表。 */
function mountList(overrides: Record<string, unknown> = {}) {
  return mount(MessageList, {
    props: {
      ideas: [idea],
      selectedIds: new Set<string>(),
      busyIds: new Set<string>(),
      atBottom: true,
      suggest: () => [],
      onSaveContent: async () => true,
      onSaveTags: async () => true,
      ...overrides,
    },
  });
}

describe('MessageList', () => {
  it('把 suggest / onSaveContent / onSaveTags 转给条目（转发链是单点风险）', () => {
    const suggest = vi.fn(() => ['候选']);
    const onSaveContent = vi.fn(async () => true);
    const onSaveTags = vi.fn(async () => true);

    const wrapper = mountList({ suggest, onSaveContent, onSaveTags });

    const item = wrapper.findComponent(MessageItem);
    expect(item.props('suggest')).toBe(suggest);
    expect(item.props('onSaveContent')).toBe(onSaveContent);
    expect(item.props('onSaveTags')).toBe(onSaveTags);
  });

  it('条目按传进来的顺序渲染，组件自己不重排', () => {
    const wrapper = mountList({ ideas: [newerIdea, idea] });

    const items = wrapper.findAllComponents(MessageItem);
    expect(items).toHaveLength(2);
    expect(items[0].props('idea').id).toBe('idea-2');
    expect(items[1].props('idea').id).toBe('idea-1');
  });

  it('勾选状态与在飞状态传给条目', () => {
    const wrapper = mountList({
      ideas: [idea, newerIdea],
      selectedIds: new Set(['idea-2']),
      busyIds: new Set(['idea-1']),
    });

    const items = wrapper.findAllComponents(MessageItem);
    expect(items[0].props('selected')).toBe(false);
    expect(items[0].props('busy')).toBe(true);
    expect(items[1].props('selected')).toBe(true);
    expect(items[1].props('busy')).toBe(false);
  });

  it('条目上的操作往上冒（toggle-select / toggle-done / delete）', async () => {
    const wrapper = mountList();

    await wrapper.get('input.select').trigger('change');
    await wrapper.get('button.done').trigger('click');
    await wrapper.get('button.action--delete').trigger('click');
    await wrapper.get('button.confirm__delete').trigger('click');

    expect(wrapper.emitted('toggle-select')).toEqual([['idea-1']]);
    expect(wrapper.emitted('toggle-done')).toEqual([['idea-1']]);
    expect(wrapper.emitted('delete')).toEqual([['idea-1']]);
  });

  it('一条想法都没有时给一句提示', () => {
    const wrapper = mountList({ ideas: [] });

    expect(wrapper.findAllComponents(MessageItem)).toHaveLength(0);
    expect(wrapper.get('.empty').text()).toBe('这里还没有想法。');
  });
});
