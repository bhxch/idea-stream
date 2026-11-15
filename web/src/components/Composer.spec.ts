import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import Composer from './Composer.vue';
// 组件源码原文（`?raw`）：样式断言要对着它做，见下面的说明。
import composerSource from './Composer.vue?raw';

/** 造一个 Composer：提交回调按 `ok` 返回成功或失败。 */
function mountComposer(ok: boolean) {
  return mount(Composer, {
    props: { onSubmit: async () => ok },
    attachTo: document.body,
  });
}

/**
 * Composer.vue 的源码。
 *
 * 「只有一层边框」这条只能对着源码断言：vitest 默认不处理 CSS
 * （vite.config.ts 里没有开 `test.css`），组件的 scoped 样式压根没进 DOM，
 * jsdom 的 getComputedStyle 拿不到边框，只会读到初始值。
 */

/** `<style>` 块里的 CSS（先去掉注释，免得注释里的文字干扰解析）。 */
const styleText = /<style[^>]*>([\s\S]*?)<\/style>/.exec(composerSource)?.[1]?.replace(/\/\*[\s\S]*?\*\//g, '') ?? '';

/**
 * 取某条规则的声明：属性名 → 值。
 *
 * @param selector 选择器（单个类名，形如 `.input`）。
 */
function declarationsOf(selector: string): Record<string, string> {
  const match = new RegExp(`(?:^|[{}])\\s*${selector}\\s*\\{([^}]*)\\}`).exec(styleText);
  if (!match) {
    throw new Error(`样式里没有 ${selector} 这条规则`);
  }
  const declarations: Record<string, string> = {};
  for (const item of match[1].split(';')) {
    const trimmed = item.trim();
    if (!trimmed) {
      continue;
    }
    const index = trimmed.indexOf(':');
    declarations[trimmed.slice(0, index).trim()] = trimmed.slice(index + 1).trim();
  }
  return declarations;
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

describe('Composer 的边框', () => {
  it('只有一层视觉边框：边框在输入框上，外层不再画框', () => {
    const composer = declarationsOf('.composer');
    const input = declarationsOf('.input');

    // 外层不能有边框线（border-radius 是圆角，不算边框线）。
    const borderLines = Object.keys(composer).filter(
      (name) => name.startsWith('border') && name !== 'border-radius',
    );
    expect(borderLines).toEqual([]);

    // 输入框这一层就是唯一的一层。
    expect(input.border).toBe('1px solid var(--border)');
  });

  it('外层不垫底色也不加内边距（不然还是「框里套框」的观感）', () => {
    const composer = declarationsOf('.composer');

    expect(composer.background).toBeUndefined();
    expect(composer.padding).toBeUndefined();
  });

  it('缩放手柄还在输入框上（resize: vertical 不动）', () => {
    // 手柄画在 textarea 自己的右下角 —— 前提是输入框这一层边框还在。
    expect(declarationsOf('.input').resize).toBe('vertical');
  });
});
