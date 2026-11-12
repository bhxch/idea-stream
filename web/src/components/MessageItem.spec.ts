import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Idea } from '../api/types';
import MessageItem from './MessageItem.vue';

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

describe('MessageItem', () => {
  it('请求在飞时禁用「标记完成」按钮', () => {
    const wrapper = mount(MessageItem, {
      props: { idea, selected: false, busy: true },
    });

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('没有请求在飞时按钮可点', () => {
    const wrapper = mount(MessageItem, {
      props: { idea, selected: false },
    });

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('点「标记完成」发出 toggle-done', async () => {
    const wrapper = mount(MessageItem, {
      props: { idea, selected: false },
    });

    await wrapper.get('button.done').trigger('click');

    expect(wrapper.emitted('toggle-done')).toEqual([['idea-1']]);
  });
});
