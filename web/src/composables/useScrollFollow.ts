import { getCurrentScope, onScopeDispose, ref, watch, type Ref } from 'vue';

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
 * @param container 滚动容器（消息流那个可滚动的元素）
 */
export function useScrollFollow(container: Ref<HTMLElement | null>) {
  /** 当前是否贴着底部。 */
  const atBottom = ref(true);

  /** 用户往上翻看时积攒的新内容条数（用来显示「↓ 有 N 条新内容」）。 */
  const pendingCount = ref(0);

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

  // 组件卸载时把监听器摘掉（不在组件里用时没有作用域，就不用摘）。
  if (getCurrentScope()) {
    onScopeDispose(() => {
      container.value?.removeEventListener('scroll', onScroll);
    });
  }

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
   * 有新内容来了。
   *
   * 贴底就跟着滚到底；没贴底就只记一笔，等用户自己滚下去。
   */
  function onNewContent(): void {
    if (atBottom.value) {
      scrollToBottom();
      return;
    }
    pendingCount.value += 1;
  }

  return { atBottom, onNewContent, scrollToBottom, pendingCount };
}
