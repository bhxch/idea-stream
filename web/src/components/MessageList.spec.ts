import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Idea } from '../api/types';
import MessageItem from './MessageItem.vue';
import MessageList from './MessageList.vue';

/** 一条想法。 */
const idea: Idea = {
  id: 'idea-1',
  projectId: 'project-1',
  content: '一条想法',
  tags: [],
  done: false,
  createdAt: '2026-11-01T10:00:00Z',
  updatedAt: '2026-11-01T10:00:00Z',
};

/** 第二条想法（比第一条新，列表顺序按传入顺序渲染）。 */
const newerIdea: Idea = { ...idea, id: 'idea-2', content: '另一条想法' };

/** 第三条想法（两列交错流里它落在第 2 排的左格）。 */
const thirdIdea: Idea = { ...idea, id: 'idea-3', content: '第三条想法' };

/** 挂一个列表。 */
function mountList(overrides: Record<string, unknown> = {}) {
  return mount(MessageList, {
    props: {
      ideas: [idea],
      selectedIds: new Set<string>(),
      busyIds: new Set<string>(),
      atBottom: true,
      suggest: () => [],
      onSaveContent: async () => 'saved' as const,
      onSaveTags: async () => 'saved' as const,
      ...overrides,
    },
  });
}

describe('MessageList', () => {
  it('把 suggest / onSaveContent / onSaveTags 转给条目（转发链是单点风险）', () => {
    const suggest = vi.fn(() => ['候选']);
    const onSaveContent = vi.fn(async () => 'saved' as const);
    const onSaveTags = vi.fn(async () => 'saved' as const);

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
    await wrapper.get('button.meta__done').trigger('click');
    await wrapper.get('button.meta__action--delete').trigger('click');
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

  it('加载中不给空状态（数据还没到就说「没有想法」是骗人）', () => {
    const wrapper = mountList({ ideas: [], loading: true });

    expect(wrapper.find('.empty').exists()).toBe(false);
  });

  it('加载中但已经有条目时，条目照常渲染', () => {
    const wrapper = mountList({ ideas: [idea], loading: true });

    expect(wrapper.findAllComponents(MessageItem)).toHaveLength(1);
    expect(wrapper.find('.empty').exists()).toBe(false);
  });

  it('加载结束后空状态才出现', async () => {
    const wrapper = mountList({ ideas: [], loading: true });
    expect(wrapper.find('.empty').exists()).toBe(false);

    await wrapper.setProps({ loading: false });

    expect(wrapper.get('.empty').text()).toBe('这里还没有想法。');
  });

  it('the_odd_ones_are_on_the_left', () => {
    const wrapper = mountList({ ideas: [idea, newerIdea, thirdIdea] });

    // 第 1、3 条在左格，第 2 条在右格 —— 编号与左右由同一个位置序号决定。
    expect(
      wrapper.get('.row:nth-child(1) .cell--left .idea').attributes('data-idea-id'),
    ).toBe('idea-1');
    expect(
      wrapper.get('.row:nth-child(1) .cell--right .idea').attributes('data-idea-id'),
    ).toBe('idea-2');
    expect(
      wrapper.get('.row:nth-child(2) .cell--left .idea').attributes('data-idea-id'),
    ).toBe('idea-3');
  });

  it('the_last_row_leaves_the_right_cell_empty', () => {
    const wrapper = mountList({ ideas: [idea, newerIdea, thirdIdea] });

    // 3 条 → 第 2 排只有左格，右格空着（不是一条都不渲染）。
    expect(wrapper.findAll('.row')).toHaveLength(2);
    expect(wrapper.get('.row:nth-child(2) .cell--right').find('.idea').exists()).toBe(false);
  });

  it('the_position_is_the_index_plus_one', () => {
    const wrapper = mountList({ ideas: [idea, newerIdea, thirdIdea] });

    expect(wrapper.findAll('.meta__num').map((num) => num.text())).toEqual(['1', '2', '3']);
  });
});
