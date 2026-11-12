import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import Composer from './Composer.vue';

/** 造一个 Composer：提交回调按 `ok` 返回成功或失败。 */
function mountComposer(ok: boolean) {
  return mount(Composer, {
    props: { onSubmit: async () => ok },
    attachTo: document.body,
  });
}

describe('Composer', () => {
  it('提交成功后清空输入框，焦点留在输入框', async () => {
    const wrapper = mountComposer(true);
    const textarea = wrapper.get('textarea');

    await textarea.setValue('一条想法');
    await textarea.trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect((textarea.element as HTMLTextAreaElement).value).toBe('');
    expect(document.activeElement).toBe(textarea.element);
    wrapper.unmount();
  });

  it('提交失败时保留输入内容（用户敲的正文不能丢）', async () => {
    const wrapper = mountComposer(false);
    const textarea = wrapper.get('textarea');

    await textarea.setValue('一条想法');
    await textarea.trigger('keydown', { key: 'Enter' });
    await flushPromises();

    expect((textarea.element as HTMLTextAreaElement).value).toBe('一条想法');
    wrapper.unmount();
  });

  it('提交在飞时忽略重复提交', async () => {
    let resolveSubmit: ((ok: boolean) => void) | undefined;
    const onSubmit = vi.fn(
      () =>
        new Promise<boolean>((resolve) => {
          resolveSubmit = resolve;
        }),
    );
    const wrapper = mount(Composer, { props: { onSubmit } });
    const textarea = wrapper.get('textarea');

    await textarea.setValue('一条想法');
    await textarea.trigger('keydown', { key: 'Enter' });
    await textarea.trigger('keydown', { key: 'Enter' });
    await flushPromises();

    // 只发出去一次：连按 Enter 不会连存好几条。
    expect(onSubmit).toHaveBeenCalledTimes(1);

    resolveSubmit?.(true);
    await flushPromises();
    wrapper.unmount();
  });
});
