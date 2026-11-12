import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
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

/** 挂一个条目：保存回调默认成功，补全默认没有候选。 */
function mountItem(overrides: Record<string, unknown> = {}) {
  return mount(MessageItem, {
    props: {
      idea,
      selected: false,
      suggest: () => [],
      onSaveContent: async () => true,
      onSaveTags: async () => true,
      ...overrides,
    },
  });
}

describe('MessageItem', () => {
  it('请求在飞时禁用「标记完成」按钮', () => {
    const wrapper = mountItem({ busy: true });

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('没有请求在飞时按钮可点', () => {
    const wrapper = mountItem();

    expect((wrapper.get('button.done').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('点「标记完成」发出 toggle-done', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.done').trigger('click');

    expect(wrapper.emitted('toggle-done')).toEqual([['idea-1']]);
  });

  it('正文里的特殊字符不会被当成 HTML 解析', () => {
    const wrapper = mountItem({
      idea: { ...idea, title: '标题', content: '一条 <b>想法</b> & 一点' },
    });

    const html = wrapper.get('.body').element.innerHTML;
    expect(html).not.toContain('<b>');
    expect(wrapper.get('.body').text()).toBe('一条 <b>想法</b> & 一点');
  });

  it('标题为空时首行只当视觉标题，正文里不再显示一遍', () => {
    const wrapper = mountItem({
      idea: { ...idea, content: '第一行\n第二行' },
    });

    expect(wrapper.get('.title').text()).toBe('第一行');
    expect(wrapper.get('.body').text()).toBe('第二行');
  });

  it('有标题时正文原样显示', () => {
    const wrapper = mountItem({
      idea: { ...idea, title: '标题', content: '第一行\n第二行' },
    });

    expect(wrapper.get('.title').text()).toBe('标题');
    expect(wrapper.get('.body').text()).toBe('第一行\n第二行');
  });

  it('点正文进入编辑态，Ctrl+Enter 保存', async () => {
    const onSaveContent = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    // `get` 找不到就抛错，能拿到就说明编辑框已经出来了。
    const editor = wrapper.get('textarea.editor');

    await editor.setValue('改过的正文');
    await editor.trigger('keydown', { key: 'Enter', ctrlKey: true });
    await flushPromises();

    expect(onSaveContent).toHaveBeenCalledWith('idea-1', '改过的正文');
    // 存好了就退出编辑态。
    expect(wrapper.findAll('textarea.editor')).toHaveLength(0);
  });

  it('失焦也保存', async () => {
    const onSaveContent = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await editor.trigger('blur');
    await flushPromises();

    expect(onSaveContent).toHaveBeenCalledWith('idea-1', '改过的正文');
  });

  it('Esc 取消编辑，不保存', async () => {
    const onSaveContent = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await editor.trigger('keydown', { key: 'Escape' });
    await flushPromises();

    expect(onSaveContent).not.toHaveBeenCalled();
    expect(wrapper.findAll('textarea.editor')).toHaveLength(0);
  });

  it('保存失败时留在编辑态（用户敲的正文不能丢）', async () => {
    const onSaveContent = vi.fn(async () => false);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await editor.trigger('keydown', { key: 'Enter', ctrlKey: true });
    await flushPromises();

    expect(wrapper.findAll('textarea.editor')).toHaveLength(1);
    expect((wrapper.get('textarea.editor').element as HTMLTextAreaElement).value).toBe('改过的正文');
  });

  it('正文一点没改时失焦不发请求（别为一次空改动打扰服务端）', async () => {
    const onSaveContent = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    await wrapper.get('textarea.editor').trigger('blur');
    await flushPromises();

    expect(onSaveContent).not.toHaveBeenCalled();
    // 没有要存的，直接收起编辑态。
    expect(wrapper.findAll('textarea.editor')).toHaveLength(0);
  });

  it('失焦保存撞上在飞时先攒着，在飞结束后自动补存（不能默默丢掉）', async () => {
    let resolveSave: ((ok: boolean) => void) | undefined;
    const onSaveContent = vi.fn(
      () =>
        new Promise<boolean>((resolve) => {
          resolveSave = resolve;
        }),
    );
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');

    // 同一条目上别的操作在飞（比如用户点了「标记完成」）。
    await wrapper.setProps({ busy: true });
    // textarea 被禁用时 vue-test-utils 不派发事件（`trigger` 会跳过 disabled 的元素），
    // 这里手动派发：真实浏览器里 textarea 一被禁用就会自动失焦。
    editor.element.dispatchEvent(new Event('blur'));
    await flushPromises();

    // 在飞时不能发（会被在飞保护挡下），但也不能就这么丢掉。
    expect(onSaveContent).not.toHaveBeenCalled();
    // 草稿还在框里。
    expect((wrapper.get('textarea.editor').element as HTMLTextAreaElement).value).toBe('改过的正文');

    // 在飞结束 → 自动补存。
    await wrapper.setProps({ busy: false });
    await flushPromises();

    expect(onSaveContent).toHaveBeenCalledWith('idea-1', '改过的正文');

    resolveSave?.(true);
    await flushPromises();
    // 存好了才退出编辑态。
    expect(wrapper.findAll('textarea.editor')).toHaveLength(0);
  });

  it('攒着的正文在 Esc 取消后不再补存（用户说了不要）', async () => {
    const onSaveContent = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');

    await wrapper.setProps({ busy: true });
    await editor.trigger('blur');
    await flushPromises();
    expect(onSaveContent).not.toHaveBeenCalled();

    // 用户按 Esc 说不要了：攒着的那一版作废。
    await editor.trigger('keydown', { key: 'Escape' });
    await wrapper.setProps({ busy: false });
    await flushPromises();

    // 取消之后不该再冒出来一次保存。
    expect(onSaveContent).not.toHaveBeenCalled();
  });

  it('删除要二次确认，确认后才发出 delete', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.action--delete').trigger('click');
    // 第一次点只是问一句，还没删。
    expect(wrapper.emitted('delete')).toBeUndefined();

    await wrapper.get('button.confirm__delete').trigger('click');
    expect(wrapper.emitted('delete')).toEqual([['idea-1']]);
  });

  it('删除的二次确认可以取消', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.action--delete').trigger('click');
    await wrapper.get('button.confirm__cancel').trigger('click');

    expect(wrapper.emitted('delete')).toBeUndefined();
    expect(wrapper.findAll('.confirm')).toHaveLength(0);
  });

  it('编辑标签：回车加标签后交给上层保存', async () => {
    const onSaveTags = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledWith('idea-1', ['新标签']);
  });

  it('编辑标签保存失败时草稿不丢（编辑器留着，标签还在）', async () => {
    const onSaveTags = vi.fn(async () => false);
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.get('.chip__name').text()).toBe('新标签');
  });

  it('标签保存成功后不收起编辑器（连续编辑：加第二个标签不用重新打开）', async () => {
    const onSaveTags = vi.fn(async () => true);
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('甲');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledWith('idea-1', ['甲']);
    // 编辑器还开着 —— 收起是用户自己的动作。
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);

    // 接着加第二个标签：不用重新点开，× 也还在（删标签同样）。
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲', '乙']);
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.findAll('.chip__name').map((chip) => chip.text())).toEqual(['甲', '乙']);
  });

  it('点「✓ 完成编辑」收起标签编辑器', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.action--tags').trigger('click');
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    // 措辞跟条目上的「标记完成」区分开：这里说的是「编辑完了」。
    expect(wrapper.get('button.action--tags').text()).toBe('✓ 完成编辑');

    await wrapper.get('button.action--tags').trigger('click');
    expect(wrapper.findAll('.tag-editor')).toHaveLength(0);
    expect(wrapper.get('button.action--tags').text()).toBe('🏷 标签');
  });

  it('保存还在飞时再改标签，会等前一次结束再存（不能丢）', async () => {
    let resolveSave: ((ok: boolean) => void) | undefined;
    const onSaveTags = vi.fn(
      () =>
        new Promise<boolean>((resolve) => {
          resolveSave = resolve;
        }),
    );
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('甲');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    expect(onSaveTags).toHaveBeenCalledTimes(1);

    // 父组件把这条标成在飞（保存请求还没回来）。
    await wrapper.setProps({ busy: true });
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    // 在飞时不能再发一次（会被在飞保护挡下）。
    expect(onSaveTags).toHaveBeenCalledTimes(1);

    // 前一次存好了、在飞解除 → 攒下的那一版补存。
    resolveSave?.(true);
    await flushPromises();
    await wrapper.setProps({ busy: false });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledTimes(2);
    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲', '乙']);
  });
});
