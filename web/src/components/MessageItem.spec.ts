import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { Idea } from '../api/types';
import type { SaveOutcome } from '../lib/save';
import MessageItem from './MessageItem.vue';

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

/** 挂一个条目：保存回调默认成功，补全默认没有候选。 */
function mountItem(overrides: Record<string, unknown> = {}) {
  return mount(MessageItem, {
    props: {
      idea,
      selected: false,
      // position 是必填 prop：不在 props 里的话 vue-tsc 会报「缺少属性 position」。
      position: 1,
      suggest: () => [],
      onSaveContent: async () => 'saved' as const,
      onSaveTags: async () => 'saved' as const,
      ...overrides,
    },
  });
}

describe('MessageItem', () => {
  // `window.confirm` 的替身别漏到别的用例去（每个用例都自己 mock 一次）。
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('请求在飞时禁用「标记完成」按钮', () => {
    const wrapper = mountItem({ busy: true });

    expect((wrapper.get('button.meta__done').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('没有请求在飞时按钮可点', () => {
    const wrapper = mountItem();

    expect((wrapper.get('button.meta__done').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('点「标记完成」发出 toggle-done', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.meta__done').trigger('click');

    expect(wrapper.emitted('toggle-done')).toEqual([['idea-1']]);
  });

  it('正文里的特殊字符不会被当成 HTML 解析', () => {
    const wrapper = mountItem({
      idea: { ...idea, content: '一条 <b>想法</b> & 一点' },
    });

    const html = wrapper.get('.body').element.innerHTML;
    expect(html).not.toContain('<b>');
    expect(wrapper.get('.body').text()).toBe('一条 <b>想法</b> & 一点');
  });

  it('正文显示全文（含首行），没有标题层', () => {
    // 想法没有标题了：正文不再被摘掉首行，界面上也不再有一层「视觉标题」。
    const wrapper = mountItem({
      idea: { ...idea, content: '第一行\n第二行' },
    });

    expect(wrapper.find('.title').exists()).toBe(false);
    expect(wrapper.get('.body').text()).toBe('第一行\n第二行');
  });

  it('正文里命中的那几段被 <mark> 包住', () => {
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '买咖啡机',
        matches: [{ field: 'content', start: 1, length: 2 }],
      },
    });

    const marks = wrapper.get('.body').findAll('mark');
    expect(marks.map((mark) => mark.text())).toEqual(['咖啡']);
    // 没命中的那部分照旧是普通文本。
    expect(wrapper.get('.body').text()).toBe('买咖啡机');
  });

  it('关键词打在标签名上：命中的 chip 包 <mark>，没打的不包', () => {
    // tags 字段的偏移基准 = 标签名按返回顺序用空格连接（「咖啡 烘焙」）：
    // 「咖啡」落在 [0,2)，「烘焙」落在 [3,5)。
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '正文',
        tags: ['咖啡', '烘焙'],
        matches: [{ field: 'tags', start: 0, length: 2 }],
      },
    });

    const chips = wrapper.findAll('.chip');
    expect(chips).toHaveLength(2);
    expect(chips[0].find('mark').text()).toBe('咖啡');
    expect(chips[1].find('mark').exists()).toBe(false);
    expect(wrapper.get('.tags').text()).toBe('咖啡烘焙');
  });

  it('没有 tags 字段的命中时 chip 不包 <mark>', () => {
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '正文',
        tags: ['咖啡'],
        matches: [{ field: 'content', start: 0, length: 2 }],
      },
    });

    expect(wrapper.get('.chip').find('mark').exists()).toBe(false);
  });

  it('重复或重叠的命中不会渲染出嵌套的 <mark>', () => {
    // 服务端允许给出重复或重叠的区间（契约里写明由前端合并）：
    // 不合并的话 <mark> 会嵌套起来。
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '咖啡咖啡',
        matches: [
          { field: 'content', start: 0, length: 2 },
          { field: 'content', start: 0, length: 2 },
          { field: 'content', start: 1, length: 2 },
        ],
      },
    });

    const marks = wrapper.get('.body').findAll('mark');
    expect(marks.map((mark) => mark.text())).toEqual(['咖啡咖']);
    expect(wrapper.get('.body').find('mark mark').exists()).toBe(false);
  });

  it('落在首行的命中留在正文里（没有标题层可跟）', () => {
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '咖啡机\n水温不错',
        matches: [{ field: 'content', start: 0, length: 2 }],
      },
    });

    expect(wrapper.get('.body').findAll('mark').map((mark) => mark.text())).toEqual(['咖啡']);
    expect(wrapper.get('.body').text()).toBe('咖啡机\n水温不错');
  });

  it('落在正文行的命中留在正文里', () => {
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '咖啡机\n水温不错',
        matches: [{ field: 'content', start: 4, length: 2 }],
      },
    });

    expect(wrapper.get('.body').findAll('mark').map((mark) => mark.text())).toEqual(['水温']);
  });

  it('高亮不改变文字本身（DOM 里的文本与原文逐字相同）', () => {
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '第一行\n第二行\n\n第四行',
        matches: [{ field: 'content', start: 4, length: 2 }],
      },
    });

    // 正文是 pre-wrap 渲染的：多一个字符、少一个字符都看得见，
    // 所以这里比 textContent（text() 会 trim，掩盖掉首尾的差异）。
    expect(wrapper.get('.body').element.textContent).toBe('第一行\n第二行\n\n第四行');
  });

  it('正文显示全文，开头的空行也不动（逐字相同）', () => {
    const wrapper = mountItem({
      idea: { ...idea, content: '\n第一行\n\n第二行' },
    });

    expect(wrapper.get('.body').element.textContent).toBe('\n第一行\n\n第二行');
  });

  it('没有命中位置时不渲染 <mark>，文字与原来一样', () => {
    // 不带关键词的列表、SSE 推来的增量条目都没有命中位置：
    // 这时候渲染结果必须跟加高亮之前一模一样。
    const wrapper = mountItem({
      idea: { ...idea, content: '第一行\n第二行' },
    });

    expect(wrapper.get('.body').text()).toBe('第一行\n第二行');
    expect(wrapper.get('.body').findAll('mark')).toHaveLength(0);
  });

  it('勾选框的可访问名称用正文首行（标题层没了，得有个能认出这条想法的短文本）', () => {
    const wrapper = mountItem({
      idea: { ...idea, content: '给路由器固定 IP，省得每次 SSH 都记不住' },
    });

    const ariaLabel = wrapper.get('input.select').attributes('aria-label');
    expect(ariaLabel).toContain('给路由器固定 IP，省得每次 SSH 都记不住');
  });

  it('正文里的特殊字符不会被当成 HTML 解析（有命中时也一样）', () => {
    const wrapper = mountItem({
      idea: {
        ...idea,
        content: '一条 <b>想法</b> & 一点',
        matches: [{ field: 'content', start: 0, length: 2 }],
      },
    });

    const html = wrapper.get('.body').element.innerHTML;
    expect(html).not.toContain('<b>');
    expect(wrapper.get('.body').text()).toBe('一条 <b>想法</b> & 一点');
  });

  it('点正文进入编辑态，Ctrl+Enter 保存', async () => {
    const onSaveContent = vi.fn(async () => 'saved' as const);
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
    const onSaveContent = vi.fn(async () => 'saved' as const);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await editor.trigger('blur');
    await flushPromises();

    expect(onSaveContent).toHaveBeenCalledWith('idea-1', '改过的正文');
  });

  it('Esc 取消编辑，不保存', async () => {
    const onSaveContent = vi.fn(async () => 'saved' as const);
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
    const onSaveContent = vi.fn(async () => 'failed' as const);
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
    const onSaveContent = vi.fn(async () => 'saved' as const);
    const wrapper = mountItem({ onSaveContent });

    await wrapper.get('.body').trigger('click');
    await wrapper.get('textarea.editor').trigger('blur');
    await flushPromises();

    expect(onSaveContent).not.toHaveBeenCalled();
    // 没有要存的，直接收起编辑态。
    expect(wrapper.findAll('textarea.editor')).toHaveLength(0);
  });

  it('失焦保存撞上在飞时先攒着，在飞结束后自动补存（不能默默丢掉）', async () => {
    let resolveSave: ((ok: SaveOutcome) => void) | undefined;
    const onSaveContent = vi.fn(
      () =>
        new Promise<SaveOutcome>((resolve) => {
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

    resolveSave?.('saved');
    await flushPromises();
    // 存好了才退出编辑态。
    expect(wrapper.findAll('textarea.editor')).toHaveLength(0);
  });

  it('攒着的正文在 Esc 取消后不再补存（用户说了不要）', async () => {
    const onSaveContent = vi.fn(async () => 'saved' as const);
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

    await wrapper.get('button.meta__action--delete').trigger('click');
    // 第一次点只是问一句，还没删。
    expect(wrapper.emitted('delete')).toBeUndefined();

    await wrapper.get('button.confirm__delete').trigger('click');
    expect(wrapper.emitted('delete')).toEqual([['idea-1']]);
  });

  it('删除的二次确认可以取消', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.meta__action--delete').trigger('click');
    await wrapper.get('button.confirm__cancel').trigger('click');

    expect(wrapper.emitted('delete')).toBeUndefined();
    expect(wrapper.findAll('.confirm')).toHaveLength(0);
  });

  it('编辑标签：回车加标签后交给上层保存', async () => {
    const onSaveTags = vi.fn(async () => 'saved' as const);
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledWith('idea-1', ['新标签'], { silent: false });
  });

  it('编辑标签保存失败时草稿不丢（编辑器留着，标签还在）', async () => {
    const onSaveTags = vi.fn(async () => 'failed' as const);
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('新标签');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.get('.chip__name').text()).toBe('新标签');
  });

  it('标签保存成功后不收起编辑器（连续编辑：加第二个标签不用重新打开）', async () => {
    const onSaveTags = vi.fn(async () => 'saved' as const);
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('甲');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledWith('idea-1', ['甲'], { silent: false });
    // 编辑器还开着 —— 收起是用户自己的动作。
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);

    // 接着加第二个标签：不用重新点开，× 也还在（删标签同样）。
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲', '乙'], { silent: false });
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.findAll('.chip__name').map((chip) => chip.text())).toEqual(['甲', '乙']);
  });

  it('点「✓ 完成编辑」收起标签编辑器', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.meta__action--tags').trigger('click');
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    // 措辞跟条目上的「标记完成」区分开：这里说的是「编辑完了」。
    // 按钮上只画字形，完整名字给在 aria-label 上（屏幕阅读器读到的是完整名字）。
    expect(wrapper.get('button.meta__action--tags').text()).toBe('✓');
    expect(wrapper.get('button.meta__action--tags').attributes('aria-label')).toBe('✓ 完成编辑');

    await wrapper.get('button.meta__action--tags').trigger('click');
    expect(wrapper.findAll('.tag-editor')).toHaveLength(0);
    expect(wrapper.get('button.meta__action--tags').text()).toBe('🏷');
    expect(wrapper.get('button.meta__action--tags').attributes('aria-label')).toBe('🏷 标签');
  });

  it('草稿跟服务端不一致时收起会再问一次（不能静默丢掉未保存的改动）', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const onSaveTags = vi.fn(async () => 'failed' as const); // 保存失败：改动没落库
    const wrapper = mountItem({ idea: { ...idea, tags: ['甲'] }, onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();
    expect(onSaveTags).toHaveBeenCalledWith('idea-1', ['甲', '乙'], { silent: false });

    await wrapper.get('button.meta__action--tags').trigger('click');

    // 收起了要问一句：刚才那次保存失败了，用户可能没留意到。
    expect(confirmSpy).toHaveBeenCalledTimes(1);
    expect(String(confirmSpy.mock.calls[0][0])).toContain('未保存');
    // 用户说「不」：不收起，草稿也没被静默丢掉（还在编辑器里）。
    expect(wrapper.findAll('.tag-editor')).toHaveLength(1);
    expect(wrapper.findAll('.chip__name').map((chip) => chip.text())).toEqual(['甲', '乙']);
  });

  it('用户确定收起后编辑器关掉，但没存上的改动没丢（下次打开还在）', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const onSaveTags = vi.fn(async () => 'failed' as const);
    const wrapper = mountItem({ idea: { ...idea, tags: ['甲'] }, onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    await wrapper.get('button.meta__action--tags').trigger('click');
    expect(confirmSpy).toHaveBeenCalledTimes(1);
    expect(wrapper.findAll('.tag-editor')).toHaveLength(0);

    // 再打开：没存上的改动还在（并会再存一次），不用重敲一遍。
    await wrapper.get('button.meta__action--tags').trigger('click');
    expect(wrapper.findAll('.chip__name').map((chip) => chip.text())).toEqual(['甲', '乙']);
  });

  it('草稿跟服务端一致时收起不打扰用户（不提示）', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const wrapper = mountItem({ idea: { ...idea, tags: ['甲'] } });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('button.meta__action--tags').trigger('click');

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(wrapper.findAll('.tag-editor')).toHaveLength(0);
  });

  it('保存成功后服务端值已更新，收起时不再提示', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const onSaveTags = vi.fn(async () => 'saved' as const);
    const wrapper = mountItem({ idea: { ...idea, tags: ['甲'] }, onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    await wrapper.get('button.meta__action--tags').trigger('click');
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(wrapper.findAll('.tag-editor')).toHaveLength(0);
  });

  it('保存还在飞时再改标签，会等前一次结束再存（不能丢）', async () => {
    let resolveSave: ((ok: SaveOutcome) => void) | undefined;
    const onSaveTags = vi.fn(
      () =>
        new Promise<SaveOutcome>((resolve) => {
          resolveSave = resolve;
        }),
    );
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
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
    resolveSave?.('saved');
    await flushPromises();
    await wrapper.setProps({ busy: false });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledTimes(2);
    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲', '乙'], { silent: false });
  });

  it('补存时正文与标签串行发：标签要等正文存完（并发会被在飞保护挡下，那次改动就没了）', async () => {
    // 父组件的同款在飞保护：同一条目上同时只允许一个写操作，撞上了回 'busy'。
    const contentDone = { value: false };
    let resolveContent: ((ok: SaveOutcome) => void) | undefined;
    const onSaveContent = vi.fn(
      () =>
        new Promise<SaveOutcome>((resolve) => {
          resolveContent = resolve;
        }),
    );
    const onSaveTags = vi.fn(async (): Promise<SaveOutcome> =>
      contentDone.value ? 'saved' : 'busy',
    );
    const wrapper = mountItem({ onSaveContent, onSaveTags });

    // 攒下两份改动：正文草稿（编辑态里）+ 标签（标签编辑器里）。
    await wrapper.get('.body').trigger('click');
    const editor = wrapper.get('textarea.editor');
    await editor.setValue('改过的正文');
    await wrapper.get('button.meta__action--tags').trigger('click');

    // 在飞：正文的失焦与标签的改动都先攒着。
    await wrapper.setProps({ busy: true });
    editor.element.dispatchEvent(new Event('blur'));
    await flushPromises();
    await wrapper.get('.tag-editor input').setValue('甲');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();
    expect(onSaveContent).not.toHaveBeenCalled();
    expect(onSaveTags).not.toHaveBeenCalled();

    // 在飞结束 → 补存。
    await wrapper.setProps({ busy: false });
    await flushPromises();

    // 正文先发，标签还在等（串行：并发的话标签会被在飞保护挡下）。
    expect(onSaveContent).toHaveBeenCalledTimes(1);
    expect(onSaveTags).not.toHaveBeenCalled();

    // 正文存完了 → 标签才发，这次没被挡下（改动没丢）。
    contentDone.value = true;
    resolveContent?.('saved');
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledTimes(1);
    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲'], { silent: false });
  });

  it('被在飞保护挡下时（父组件回 busy）改动会重新入队，在飞结束后再补存（不能静默丢掉）', async () => {
    let blocked = true;
    const onSaveTags = vi.fn(async (): Promise<SaveOutcome> => (blocked ? 'busy' : 'saved'));
    const wrapper = mountItem({ onSaveTags });

    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('甲');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();

    // 第一次被挡下了：这次压根没发出去。
    expect(onSaveTags).toHaveBeenCalledTimes(1);

    // 父组件那边空出来了 → 补存那一版（不能就这么丢掉）。
    blocked = false;
    await wrapper.setProps({ busy: true });
    await wrapper.setProps({ busy: false });
    await flushPromises();

    expect(onSaveTags).toHaveBeenCalledTimes(2);
    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲'], { silent: false });
  });

  it('捡回上次没存上的标签后自动补存是静默的（用户没改任何东西，不该看到「保存标签失败」）', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const onSaveTags = vi.fn(async () => 'failed' as const);
    const wrapper = mountItem({ idea: { ...idea, tags: ['甲'] }, onSaveTags });

    // 先制造一份「没存上的改动」：保存失败 → 用户确认收起。
    await wrapper.get('button.meta__action--tags').trigger('click');
    await wrapper.get('.tag-editor input').setValue('乙');
    await wrapper.get('.tag-editor input').trigger('keydown', { key: 'Enter' });
    await flushPromises();
    await wrapper.get('button.meta__action--tags').trigger('click');
    expect(confirmSpy).toHaveBeenCalledTimes(1);

    // 再打开 → 捡回那版并自动补存：这次是静默的（用户没主动保存过）。
    await wrapper.get('button.meta__action--tags').trigger('click');
    await flushPromises();

    expect(onSaveTags).toHaveBeenLastCalledWith('idea-1', ['甲', '乙'], { silent: true });
  });

  it('删除的二次确认条在请求在飞时禁用（点了会被在飞保护挡下，用户只看到「没反应」）', async () => {
    const wrapper = mountItem();

    await wrapper.get('button.meta__action--delete').trigger('click');
    await wrapper.setProps({ busy: true });

    expect((wrapper.get('button.confirm__delete').element as HTMLButtonElement).disabled).toBe(true);
  });

  // ── 卡片 = 一整块正文 + 底部 meta 行 ────────────────────────────────────

  it('the_body_is_one_block_of_text', () => {
    // 正文是一整块：首行没被摘走（想法没有标题了）。
    const wrapper = mountItem({
      idea: { ...idea, content: '第一行\n第二行' },
    });

    const body = wrapper.get('.body').text();
    expect(body).toContain('第一行');
    expect(body).toContain('第二行');
  });

  it('there_is_no_title_element', () => {
    const wrapper = mountItem();

    expect(wrapper.find('h2.title').exists()).toBe(false);
    expect(wrapper.find('.title').exists()).toBe(false);
  });

  it('the_position_is_rendered_next_to_the_time', () => {
    const wrapper = mountItem({ position: 12 });

    expect(wrapper.get('.meta__num').text()).toBe('12');

    // 编号在时间前面（DOM 顺序）。
    const num = wrapper.get('.meta__num').element;
    const time = wrapper.get('.meta__time').element;
    expect(num.compareDocumentPosition(time) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('the_done_button_is_always_visible', () => {
    const wrapper = mountItem();

    const done = wrapper.find('.meta__done');
    expect(done.exists()).toBe(true);
    // 完成态是「翻看整理」时最常用的动作 —— 常驻：没有「悬停才显形」的标记。
    expect(done.attributes('data-hover-only')).toBeUndefined();
    expect(done.element.closest('[data-hover-only]')).toBeNull();
  });

  it('the_tag_and_delete_buttons_are_marked_as_hover_only', () => {
    const wrapper = mountItem();

    expect(wrapper.get('.meta__action--tags').attributes('data-hover-only')).toBeDefined();
    expect(wrapper.get('.meta__action--delete').attributes('data-hover-only')).toBeDefined();
  });

  it('the_checkbox_is_marked_as_hover_only', () => {
    const wrapper = mountItem();

    expect(wrapper.get('input.select').attributes('data-hover-only')).toBeDefined();
  });

  it('the_delete_button_still_asks_for_confirmation', async () => {
    const wrapper = mountItem();

    await wrapper.get('.meta__action--delete').trigger('click');
    // 先问一句，还没删。
    expect(wrapper.emitted('delete')).toBeUndefined();
    expect(wrapper.find('.confirm').exists()).toBe(true);

    await wrapper.get('button.confirm__delete').trigger('click');
    expect(wrapper.emitted('delete')).toEqual([['idea-1']]);
  });
});
