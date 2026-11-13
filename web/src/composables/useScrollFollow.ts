import { getCurrentScope, nextTick, onScopeDispose, ref, watch, type Ref } from 'vue';

/**
 * 距底部多少像素之内算「贴着底部」。
 *
 * 不是 0：滚轮停在离底几个像素的地方时，人眼看到的还是底部，
 * 这时候来新内容就该跟着滚下去。差得多了才当成「用户在上翻」。
 */
const BOTTOM_THRESHOLD = 32;

/**
 * 滚动跟随：新内容来了要不要自动滚到底。
 *
 * - 用户贴着底部 → 自动滚到底；
 * - 用户往上翻看 → **不抢滚动**，改记一条「有 N 条新内容」。
 *
 * ⚠️ **滚动必须发生在 DOM 更新之后。**
 *
 * 内容变高是组件重渲染的结果：重渲染之前 `scrollHeight` 还是旧值，
 * 按旧值算出来的滚动位置会把新条目留在视野外（而且内容变高不触发 scroll 事件，
 * 「是否贴底」的状态也不会更新，下一条内容来了才会突然跳到底）。
 *
 * 所以这里不让调用方直接滚，而是用一个 `flush:'post'` 的 watch 来驱动：
 * Vue 保证它在组件重渲染**之后**跑，那时 DOM 才是新的。
 *
 * @param container 滚动容器（消息流那个可滚动的元素）
 */
export function useScrollFollow(container: Ref<HTMLElement | null>) {
  /** 当前是否贴着底部。 */
  const atBottom = ref(true);

  /** 用户往上翻看时积攒的新内容条数（用来显示「↓ 有 N 条新内容」）。 */
  const pendingCount = ref(0);

  /**
   * 内容版本号：每来一条新内容 +1。
   *
   * 它只是「有新内容了」的信号，真正滚动的是下面那个 post flush 的 watch。
   */
  const contentVersion = ref(0);

  /** 待处理的滚动意图：用户此刻是贴着底部的（跟着滚）还是在上翻（只记一笔）。 */
  let pendingFollow = false;

  /** 待记的新内容条数（一次 flush 里可能来好几条）。 */
  let pendingDelta = 0;

  /** 距底部还有多远（像素）。 */
  function distanceToBottom(element: HTMLElement): number {
    return element.scrollHeight - element.scrollTop - element.clientHeight;
  }

  /** 按容器当前的滚动位置重算「是否贴底」。 */
  function update(): void {
    const element = container.value;
    if (!element) {
      return;
    }
    atBottom.value = distanceToBottom(element) <= BOTTOM_THRESHOLD;
    if (atBottom.value) {
      // 用户自己滚回底部了：积攒的新内容他已经看见，计数清零。
      pendingCount.value = 0;
    }
  }

  /** 容器的 scroll 事件。 */
  function onScroll(): void {
    update();
  }

  // 容器是后绑定的（模板 ref 在挂载后才有值），所以盯着它换监听器。
  watch(
    container,
    (element, previous) => {
      previous?.removeEventListener('scroll', onScroll);
      element?.addEventListener('scroll', onScroll);
      if (element) {
        atBottom.value = distanceToBottom(element) <= BOTTOM_THRESHOLD;
      }
    },
    { immediate: true },
  );

  /** 滚到底部（并清掉新内容计数）。 */
  function scrollToBottom(): void {
    const element = container.value;
    if (!element) {
      return;
    }
    element.scrollTop = element.scrollHeight - element.clientHeight;
    atBottom.value = true;
    pendingCount.value = 0;
  }

  /**
   * 真正执行滚动。
   *
   * 只在 post flush 里被调用：那时组件已经重渲染完，`scrollHeight` 才是新的。
   */
  function applyPending(): void {
    if (pendingFollow) {
      // 贴着底部：跟到新的底部。
      pendingFollow = false;
      pendingDelta = 0;
      scrollToBottom();
      return;
    }
    if (pendingDelta > 0) {
      // 用户在上翻：不抢滚动，只告诉他有几条新的。
      pendingCount.value += pendingDelta;
      pendingDelta = 0;
    }
  }

  // flush:'post' —— 组件重渲染完之后才跑，DOM 这时才是新的。
  const stopFollow = watch(contentVersion, applyPending, { flush: 'post' });

  /**
   * 有新内容来了。
   *
   * 贴底就跟着滚到底；没贴底就只记一笔，等用户自己滚下去。
   *
   * 滚动本身**不是**这里做的：这里只记下用户此刻的意图，
   * 真正的滚动等 DOM 更新完再做（见 {@link applyPending}）。
   *
   * @returns 一个 Promise：DOM 更新完（滚动已经发生）之后 resolve。
   */
  function onNewContent(): Promise<void> {
    // 此刻 DOM 还没变，`atBottom` 还是真的 —— 先把用户的意图记下来。
    if (atBottom.value) {
      pendingFollow = true;
    } else {
      pendingDelta += 1;
    }
    contentVersion.value += 1;
    // 版本号一改，post flush 的 watch 就排上了；nextTick 等的就是这一轮 flush。
    return nextTick();
  }

  // 组件卸载时把监听器摘掉（不在组件里用时没有作用域，就不用摘）。
  if (getCurrentScope()) {
    onScopeDispose(() => {
      container.value?.removeEventListener('scroll', onScroll);
      stopFollow();
    });
  }

  return { atBottom, onNewContent, scrollToBottom, pendingCount };
}
