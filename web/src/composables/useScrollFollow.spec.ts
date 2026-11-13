import { describe, expect, it } from 'vitest';
import { ref, shallowRef, watch } from 'vue';
import { useScrollFollow } from './useScrollFollow';

/**
 * 造一个能滚动的容器。
 *
 * jsdom 不做布局：`scrollHeight` / `clientHeight` 永远是 0，
 * `scrollTop` 写进去也读不回来（没有可滚动的盒子）。所以这里自己把
 * 这三个属性钉在元素上，滚动行为才是可断言的。
 */
function makeContainer(scrollHeight: number, clientHeight: number, scrollTop: number): HTMLElement {
  const element = document.createElement('div');
  let top = scrollTop;
  Object.defineProperty(element, 'scrollHeight', { value: scrollHeight, configurable: true });
  Object.defineProperty(element, 'clientHeight', { value: clientHeight, configurable: true });
  Object.defineProperty(element, 'scrollTop', {
    configurable: true,
    get: () => top,
    set: (value: number) => {
      top = value;
    },
  });
  return element;
}

/** 改一下容器的内容高度（模拟来了新内容）。 */
function setContentHeight(element: HTMLElement, scrollHeight: number): void {
  Object.defineProperty(element, 'scrollHeight', { value: scrollHeight, configurable: true });
}

describe('useScrollFollow', () => {
  it('贴底时新内容自动滚动到底部', async () => {
    const element = makeContainer(1000, 400, 600);
    const follow = useScrollFollow(shallowRef<HTMLElement | null>(element));

    expect(follow.atBottom.value).toBe(true);

    // 来了一条新消息：内容变高了。
    setContentHeight(element, 1200);
    await follow.onNewContent();

    // 跟着滚到底（1200 - 400）。
    expect(element.scrollTop).toBe(800);
    expect(follow.pendingCount.value).toBe(0);
  });

  it('未贴底时不抢滚动，pendingCount 增加', async () => {
    const element = makeContainer(1000, 400, 0);
    const follow = useScrollFollow(shallowRef<HTMLElement | null>(element));

    expect(follow.atBottom.value).toBe(false);

    setContentHeight(element, 1200);
    await follow.onNewContent();

    // 位置不动（用户正在往上翻看，不能抢滚动）。
    expect(element.scrollTop).toBe(0);
    expect(follow.pendingCount.value).toBe(1);
  });

  it('距底 32px 以内视为贴底', async () => {
    const element = makeContainer(1000, 400, 568);
    const follow = useScrollFollow(shallowRef<HTMLElement | null>(element));

    expect(follow.atBottom.value).toBe(true);

    await follow.onNewContent();
    expect(element.scrollTop).toBe(600);
  });

  it('滚动事件更新是否贴底，滚回底部后新内容计数清零', async () => {
    const element = makeContainer(1000, 400, 0);
    const follow = useScrollFollow(shallowRef<HTMLElement | null>(element));

    // 用户往上翻看了：计数涨了。
    await follow.onNewContent();
    expect(follow.pendingCount.value).toBe(1);
    expect(follow.atBottom.value).toBe(false);

    // 用户自己滚回底部：计数清零。
    element.scrollTop = 600;
    element.dispatchEvent(new Event('scroll'));
    expect(follow.atBottom.value).toBe(true);
    expect(follow.pendingCount.value).toBe(0);
  });

  it('scrollToBottom 滚到底并清零计数', async () => {
    const element = makeContainer(1000, 400, 0);
    const follow = useScrollFollow(shallowRef<HTMLElement | null>(element));

    await follow.onNewContent();
    expect(follow.pendingCount.value).toBe(1);

    follow.scrollToBottom();

    expect(element.scrollTop).toBe(600);
    expect(follow.atBottom.value).toBe(true);
    expect(follow.pendingCount.value).toBe(0);
  });

  it('DOM 更新之后才滚：内容变高后要滚到新的底部', async () => {
    // 回归用例：滚动发生在 DOM 更新**之前**时，`scrollHeight` 还是旧值，
    // 算出来的滚动位置不对 —— 新条目落在视野外（而且不会触发 scroll 事件，
    // 「是否贴底」也不更新，下一条内容来了才会突然跳到底）。
    const element = makeContainer(1000, 400, 600);
    const follow = useScrollFollow(shallowRef<HTMLElement | null>(element));
    expect(follow.atBottom.value).toBe(true);

    // 模拟 Vue：数据变了 → 组件重渲染 → DOM 这时才变高（post flush）。
    const rendered = ref(0);
    watch(rendered, () => setContentHeight(element, 1200), { flush: 'post' });

    // 新内容来了（此刻 DOM 还没更新，高度还是旧的 1000）。
    rendered.value = 1;
    await follow.onNewContent();

    // 滚到了**新**底部（1200 - 400），不是旧高度算出来的 600。
    expect(element.scrollTop).toBe(800);
  });
});
