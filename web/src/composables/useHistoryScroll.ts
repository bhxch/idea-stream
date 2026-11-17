import { nextTick, type Ref } from 'vue';
import type { Idea } from '../api/types';
import type { IdeaFilter } from '../lib/filter';
import { fetchOlderIdeas, hasMore, ideas, loadingMore, releaseOlderIdeas } from '../stores/ideas';

/** 往回翻需要的外部依赖。 */
export interface HistoryScrollOptions {
  /**
   * 消息流的滚动容器。
   *
   * 双重身份：`IntersectionObserver` 的 `root`（列表在它里面滚），
   * 也是视口修正量的那个元素（prepend 之后补 `scrollTop`）。
   */
  container: Ref<HTMLElement | null>;
  /** 往回翻的哨兵（列表最上面那个元素）。 */
  sentinel: Ref<HTMLElement | null>;
  /**
   * 当前的过滤条件。
   *
   * 取函数而不是快照是有意的：请求发起与响应回来**各取一次** ——
   * 等这一页的时候条件可能变，变了这一页就得丢（它是按旧条件要的）。
   */
  filter: () => IdeaFilter;
  /** 失败提示：往回翻的请求挂了要给用户一条说明，不然就是没人管的 rejection。 */
  reportError: (error: unknown, fallback: string) => void;
}

/**
 * 往回翻历史的无限滚动：接近顶部就预加载更旧的一页，prepend 后把视口钉回原处。
 *
 * 看板是聊天软件式的（打开页面停在最下边，底部是当下，往上才是历史），
 * 所以「无限滚动」的方向是**上**：
 *
 * - 哨兵放在列表顶部，`IntersectionObserver` 用 `rootMargin: '400px …'`
 *   在距离顶部还有 400px 时就发请求 —— 不等用户真的翻到顶；
 * - `root` 是滚动容器而不是 window：列表是在它里面滚的；
 * - prepend 会把用户正在看的内容往下推，量一次「多了多少」补回 `scrollTop`，
 *   视口才不会跳（预加载的意义就是「无感」）。
 *
 * 深度与 `hasMore` 的记账都在 store（`fetchOlderIdeas` / `releaseOlderIdeas`）：
 * 这里只负责触发、丢弃与渲染层的视口修正。
 */
export function useHistoryScroll(options: HistoryScrollOptions) {
  /** 哨兵的观察者（页面没了就要断开）。 */
  let observer: IntersectionObserver | undefined;

  /**
   * 页面已经卸载了。
   *
   * 往回翻的请求在飞的时候用户可能直接离开页面 —— 那一页回来后没人看了，
   * 也不能再写进 store（store 是模块级的，下一个页面还在用它）。所以在
   * `await` 之后要重新看一眼这个标志。
   */
  let disposed = false;

  /**
   * 往回翻：取更旧的一页，拼到列表**前面**。
   *
   * 三件事：
   * 1. 防重复触发：已经在翻了就不再发（`loadingMore`）；这也替代了
   *    「加载完成前把哨兵摘掉」的做法 —— 哨兵一直观察着，靠这个开关挡住
   *    重复请求就行；
   * 2. 没有更旧的了就不发（`hasMore`）：翻到底了就别再打后端；
   * 3. prepend 之后把视口钉回原来那条 —— 内容插到视口**上方**会把用户
   *    正在看的那条往下推，不补 `scrollTop` 的话预加载反而让视口跳了。
   */
  async function loadOlder(): Promise<void> {
    if (loadingMore.value || !hasMore.value) {
      return;
    }
    const container = options.container.value;
    // 过滤条件先记下来：等这一页的时候条件可能变（见下面）。
    const requestedFilter = options.filter();
    let older: Idea[];
    try {
      // offset = 已经加载的条数：服务端从**最新**一端数（设计文档 §11.1）。
      older = await fetchOlderIdeas(requestedFilter, ideas.value.length);
    } catch (error) {
      // 失败必须给一条提示：不然就是一次没人管的 Promise rejection ——
      // 用户只看到「往上翻没反应」，不知道是翻完了还是请求挂了。
      options.reportError(error, '加载更早的想法失败');
      return;
    }
    if (older.length === 0) {
      // 到底了。hasMore 由 store 自己写（fetchOlderIdeas）—— 这里不要再写一遍：
      // 若这一页是**过期**的（期间有过更新的列表请求），store 什么都没写，
      // 这里再写就是把过期结果写进共享状态（hasMore 是新页面刚算好的）。
      return;
    }
    if (disposed) {
      // 页面已经没了：这一页没人看，也不该写进共享的 store。
      // 深度要还回去 —— 这一页没进列表，深度不能替它记着。
      // （若这一页其实已经过期 —— store 没入过账 —— release 自己会跳过。）
      releaseOlderIdeas(older.length);
      return;
    }
    if (requestedFilter !== options.filter()) {
      // 等这一页的时候过滤条件变了：这一页是按**旧**条件要的，拼到新列表上
      // 就是把不匹配当前条件的条目塞给用户。重拉会按新条件重新要一份，
      // 这一页直接丢掉（深度也还回去：它没进列表）。
      releaseOlderIdeas(older.length);
      return;
    }
    // prepend 之前量一次内容高度：之后靠它算出「多了多少」，好把视口钉回原处。
    //
    // 量在**赋值之前**（而不是请求之前）：这一来一回期间到达的内容（SSE 在
    // 底部追加的条目、加载提示的出现与消失）也得算进「之前的高度」—— 不然
    // 差额里会多出那部分，视口就偏了。先 `await nextTick()` 把待渲染的改动
    // 落进 DOM，量到的才是真的高度。
    await nextTick();
    const before = container?.scrollHeight ?? 0;
    ideas.value = [...older, ...ideas.value];
    // 必须等 DOM 更新完再量：重渲染之前量到的还是旧高度，补出来的位置是错的。
    await nextTick();
    if (container) {
      container.scrollTop += container.scrollHeight - before;
    }
  }

  /**
   * 观察往回翻的哨兵：接近顶部就预加载更旧的一页。
   *
   * `rootMargin` 的 `'400px 0px 0px 0px'` = **顶部**提前 400px 就触发：
   * 列表遍历方向的尽头是顶部（底部是当下，新内容靠 SSE 进来），
   * 「快到底时预加载」在这里就是「快到顶时预加载」。
   */
  function observe(): void {
    observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          void loadOlder();
        }
      },
      { root: options.container.value, rootMargin: '400px 0px 0px 0px' },
    );
    if (options.sentinel.value) {
      observer.observe(options.sentinel.value);
    }
  }

  /** 页面卸载：断开哨兵。在飞的请求回来后也不会再写 store。 */
  function dispose(): void {
    disposed = true;
    observer?.disconnect();
    observer = undefined;
  }

  return { observe, dispose };
}
