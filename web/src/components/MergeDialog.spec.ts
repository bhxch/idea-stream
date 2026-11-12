import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Idea } from '../api/types';
import MergeDialog from './MergeDialog.vue';

/** 一条想法。 */
function makeIdea(id: string, content: string): Idea {
  return {
    id,
    projectId: 'project-1',
    title: null,
    content,
    tags: [],
    done: false,
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  };
}

describe('MergeDialog', () => {
  it('初始正文是各条以空行拼接（与后端一致）', () => {
    const wrapper = mount(MergeDialog, {
      props: { open: true, ideas: [makeIdea('idea-1', 'A'), makeIdea('idea-2', 'B')] },
    });

    expect((wrapper.get('textarea').element as HTMLTextAreaElement).value).toBe('A\n\nB');
  });

  it('确认时 emit confirm 带改过的正文', async () => {
    const wrapper = mount(MergeDialog, {
      props: { open: true, ideas: [makeIdea('idea-1', 'A'), makeIdea('idea-2', 'B')] },
    });

    await wrapper.get('textarea').setValue('A\n\nB\n\nC');
    await wrapper.get('button.action--confirm').trigger('click');

    expect(wrapper.emitted('confirm')).toEqual([['A\n\nB\n\nC']]);
  });

  it('取消时 emit cancel', async () => {
    const wrapper = mount(MergeDialog, {
      props: { open: true, ideas: [makeIdea('idea-1', 'A'), makeIdea('idea-2', 'B')] },
    });

    await wrapper.get('button.action--cancel').trigger('click');

    expect(wrapper.emitted('cancel')).toHaveLength(1);
    expect(wrapper.emitted('confirm')).toBeUndefined();
  });

  it('没打开时不渲染', () => {
    const wrapper = mount(MergeDialog, {
      props: { open: false, ideas: [makeIdea('idea-1', 'A')] },
    });

    expect(wrapper.find('.merge-dialog').exists()).toBe(false);
  });

  it('再次打开时正文回到初始拼接结果', async () => {
    const wrapper = mount(MergeDialog, {
      props: { open: true, ideas: [makeIdea('idea-1', 'A'), makeIdea('idea-2', 'B')] },
    });

    await wrapper.get('textarea').setValue('改过的');
    await wrapper.setProps({ open: false });
    await wrapper.setProps({ open: true });

    expect((wrapper.get('textarea').element as HTMLTextAreaElement).value).toBe('A\n\nB');
  });
});
