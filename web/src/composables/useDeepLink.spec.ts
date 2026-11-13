import { describe, expect, it, vi } from 'vitest';
import { nextTick, ref, shallowRef, watch } from 'vue';
import { useDeepLink } from './useDeepLink';

/**
 * 造一条在容器里的条目元素。
 *
 * `data-idea-id` 是定位用的钩子（MessageItem 就是这么标的）；
 * `scrollIntoView` jsdom 里没有（浏览器才有），自己打桩才能断言「滚过去了」。
 */
function makeItem(container: HTMLElement, id: string): HTMLElement {
  const element = document.createElement('article');
  element.dataset.ideaId = id;
  element.scrollIntoView = vi.fn();
  container.appendChild(element);
  return element;
}

describe('useDeepLink', () => {
  it('找到锚点时滚过去并高亮', () => {
    const container = document.createElement('div');
    const item = makeItem(container, 'idea-1');

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => ['idea-1'],
      isVisible: () => true,
      watchSources: [ref(0)],
    });

    expect(deepLink.locateAnchor()).toBe(true);
    expect(item.scrollIntoView).toHaveBeenCalledWith({ block: 'center' });
    expect(deepLink.highlightedId.value).toBe('idea-1');
  });

  it('锚点不在可见列表里时静默忽略（不报错、不高亮、不滚）', () => {
    const container = document.createElement('div');
    const item = makeItem(container, 'idea-1');

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => ['idea-nope'],
      isVisible: () => false,
      watchSources: [ref(0)],
    });

    expect(deepLink.locateAnchor()).toBe(false);
    expect(deepLink.highlightedId.value).toBe(null);
    expect(item.scrollIntoView).not.toHaveBeenCalled();
  });

  it('DOM 里还没有那条时不定位，等它进 DOM 后再定位', async () => {
    const container = document.createElement('div');
    const ids = ref<string[]>([]);

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => ['idea-1'],
      isVisible: (id) => ids.value.includes(id),
      watchSources: [ids],
    });

    // 列表里已经有它了，但 DOM 里还没有：定位不到，也不该标记成「定位过了」。
    ids.value = ['idea-1'];
    await nextTick();
    expect(deepLink.locateAnchor()).toBe(false);
    expect(deepLink.highlightedId.value).toBe(null);

    // 它进 DOM 之后再来一次变化：这时才定位得到。
    const item = makeItem(container, 'idea-1');
    ids.value = ['idea-1', 'idea-2'];
    await nextTick();

    expect(item.scrollIntoView).toHaveBeenCalledWith({ block: 'center' });
    expect(deepLink.locateAnchor()).toBe(true);
  });

  it('定位发生在 DOM 更新之后：条目刚进 DOM 也能定位到', async () => {
    // 回归用例：定位发生在重渲染**之前**时，条目还没进 DOM，
    // 按 id 找元素根本找不到 —— 深链看起来就是「没反应」。
    const container = document.createElement('div');
    const ids = ref<string[]>([]);

    // 模拟组件重渲染：列表变了之后，条目才进 DOM（post flush）。
    watch(
      ids,
      (next) => {
        container.innerHTML = '';
        for (const id of next) {
          makeItem(container, id);
        }
      },
      { flush: 'post' },
    );

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => ['idea-1'],
      isVisible: (id) => ids.value.includes(id),
      watchSources: [ids],
    });

    ids.value = ['idea-1'];
    await nextTick();

    const item = container.querySelector('[data-idea-id="idea-1"]') as HTMLElement;
    expect(item.scrollIntoView).toHaveBeenCalledWith({ block: 'center' });
    expect(deepLink.highlightedId.value).toBe('idea-1');
  });

  it('同一条不重复定位', () => {
    const container = document.createElement('div');
    const item = makeItem(container, 'idea-1');

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => ['idea-1'],
      isVisible: () => true,
      watchSources: [ref(0)],
    });

    deepLink.locateAnchor();
    deepLink.locateAnchor();

    expect(item.scrollIntoView).toHaveBeenCalledTimes(1);
  });

  it('锚点换成另一条时会重新定位', () => {
    const container = document.createElement('div');
    const first = makeItem(container, 'idea-1');
    const second = makeItem(container, 'idea-2');
    const anchor = ref<string | null>('idea-1');

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => [anchor.value],
      isVisible: () => true,
      watchSources: [anchor],
    });

    deepLink.locateAnchor();
    expect(first.scrollIntoView).toHaveBeenCalledTimes(1);

    // 深链换了另一条：得重新滚过去（不能死抱着上一条不放）。
    anchor.value = 'idea-2';
    deepLink.locateAnchor();
    expect(second.scrollIntoView).toHaveBeenCalledTimes(1);
    expect(deepLink.highlightedId.value).toBe('idea-2');
  });

  it('深链定位过之后，另一个候选（合并结果）仍会被定位', () => {
    // 回归用例：只记「最后定位过的那一条」的话，深链定位过一次之后
    // 就永远命中它 —— 合并出来的新条目（候选里优先级更高）再也轮不上定位。
    const container = document.createElement('div');
    const deepLinkItem = makeItem(container, 'idea-1');
    const mergedItem = makeItem(container, 'idea-merged');
    const focus = ref<string | null>(null);

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => [focus.value, 'idea-1'],
      isVisible: () => true,
      watchSources: [focus],
    });

    // 深链那条先定位到（URL 里的 ?idea= 会一直留着）。
    expect(deepLink.locateAnchor()).toBe(true);
    expect(deepLinkItem.scrollIntoView).toHaveBeenCalledTimes(1);

    // 合并结果出来了：它也得被定位。
    focus.value = 'idea-merged';
    expect(deepLink.locateAnchor()).toBe(true);
    expect(mergedItem.scrollIntoView).toHaveBeenCalledTimes(1);
    expect(deepLink.highlightedId.value).toBe('idea-merged');
  });

  it('已经定位过的候选不会在列表再次变化时把用户拽回去', async () => {
    const container = document.createElement('div');
    const item = makeItem(container, 'idea-1');
    const ids = ref<string[]>([]);

    const deepLink = useDeepLink({
      container: shallowRef<HTMLElement | null>(container),
      anchorIds: () => ['idea-1'],
      isVisible: (id) => ids.value.includes(id),
      watchSources: [ids],
    });

    ids.value = ['idea-1'];
    await nextTick();
    expect(item.scrollIntoView).toHaveBeenCalledTimes(1);

    // 列表又变了（SSE 来新内容）：不该再滚回去把用户拽回老锚点。
    ids.value = ['idea-1', 'idea-2'];
    await nextTick();
    expect(item.scrollIntoView).toHaveBeenCalledTimes(1);
    // 但它仍是当前锚点：返回 true（调用方据此别再无脑滚到底）。
    expect(deepLink.locateAnchor()).toBe(true);
    expect(item.scrollIntoView).toHaveBeenCalledTimes(1);
  });

  it('高亮过一会儿自己撤掉', () => {
    vi.useFakeTimers();
    try {
      const container = document.createElement('div');
      makeItem(container, 'idea-1');

      const deepLink = useDeepLink({
        container: shallowRef<HTMLElement | null>(container),
        anchorIds: () => ['idea-1'],
        isVisible: () => true,
        watchSources: [ref(0)],
        highlightMs: 1500,
      });

      deepLink.locateAnchor();
      expect(deepLink.highlightedId.value).toBe('idea-1');

      vi.advanceTimersByTime(1500);
      expect(deepLink.highlightedId.value).toBe(null);
    } finally {
      vi.useRealTimers();
    }
  });
});
