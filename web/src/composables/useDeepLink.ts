import { ref, watch, type Ref, type WatchSource } from 'vue';

/**
 * 高亮多久之后撤掉（毫秒）。
 */
const DEFAULT_HIGHLIGHT_MS = 1500;

export interface UseDeepLinkOptions {
  /** 滚动容器（消息流那个可滚动的元素）。 */
  container: Ref<HTMLElement | null>;

  /**
   * 候选锚点 id（按优先级）。
   *
   * 第一个「在当前可见列表里」的候选就是锚点 —— 比如合并出来的新条目优先于深链参数。
   * 返回 `null` / 空串表示没有候选。
   */
  anchorIds: () => (string | null)[];

  /** 那条 id 在不在当前可见列表里（不在就静默忽略：可能已被删、或被过滤挡住了）。 */
  isVisible: (id: string) => boolean;

  /** 盯着谁变：列表变了、锚点变了、路由变了都要重新定位。 */
  watchSources: WatchSource<unknown>[];

  /** 高亮持续多久（毫秒）。 */
  highlightMs?: number;
}

/**
 * 深链定位：`?idea=<id>` 打开后滚到那条并高亮。
 *
 * ⚠️ **定位必须发生在 DOM 更新之后。**
 *
 * Vue 的 `watch` 默认 `flush:'pre'` —— 回调在组件重渲染**之前**跑：
 * 那时新条目还没进 DOM，按 id 找元素根本找不到，滚动也就没发生
 * （而且找不到也不会报错，看起来就是「深链没反应」）。
 * 所以这里用 `flush:'post'`：Vue 保证它在重渲染之后跑，元素那时已经在 DOM 里。
 */
export function useDeepLink(options: UseDeepLinkOptions) {
  /** 要高亮的条目 id（定位到的那条）。 */
  const highlightedId = ref<string | null>(null);

  /**
   * **已经定位过的 id 集合**（不是一个 id！）。
   *
   * 为什么是集合：
   *
   * - 候选是多个（合并结果 `focusIdeaId` 优先，其次是深链 `?idea=`），
   *   而且深链 id 会**一直留在 URL 里** —— 只记「最后定位过的那一条」的话，
   *   深链定位过一次之后，每次列表变化都会命中它，
   *   合并出来的新条目（候选里优先级更高）反而永远轮不上定位。
   * - 反过来，已经定位过的 id **不能**再定位一次：
   *   深链那条在列表每次变化时都把用户拽回去，SSE 每来一条新内容就跳一次。
   *
   * 集合两条都满足：候选循环里跳过集合里已有的，定位到的就加进集合。
   *
   * ⚠️ 这里用**普通 Set**，不是响应式的 —— 它只是内部状态，不进模板。
   * （Vue 的响应式 Set 只追踪 `has`，不追踪 `add`/`delete`，
   * 原地改会让依赖它的计算失效不了 —— 参见 `busyIds` 的注释。）
   */
  const locatedIds = new Set<string>();

  const highlightMs = options.highlightMs ?? DEFAULT_HIGHLIGHT_MS;

  /** 高亮的撤掉定时器。 */
  let timer: ReturnType<typeof setTimeout> | undefined;

  /** 在滚动容器里找到那条想法的 DOM 元素。 */
  function findElement(id: string): HTMLElement | null {
    const container = options.container.value;
    if (!container) {
      return null;
    }
    // 不用选择器拼 id（深链里的 id 是用户可控的，拼进选择器可能抛异常）：
    // 直接比属性值。
    for (const element of container.querySelectorAll<HTMLElement>('[data-idea-id]')) {
      if (element.dataset.ideaId === id) {
        return element;
      }
    }
    return null;
  }

  /** 高亮那条（过一会儿撤掉）。 */
  function highlight(id: string): void {
    highlightedId.value = id;
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(() => {
      if (highlightedId.value === id) {
        highlightedId.value = null;
      }
    }, highlightMs);
  }

  /**
   * 定位到锚点：滚过去并高亮。
   *
   * **必须在 DOM 更新之后调用**（用 `flush:'post'` 或 `await nextTick()`）：
   * 之前元素还没进 DOM，找不到它，什么都不会发生。
   *
   * 候选按优先级逐个试：已经定位过的**跳过并继续往下找**
   * （合并结果优先于深链，深链定位过了不代表合并结果不用定位）。
   *
   * @returns true 表示「当前锚点已经定位过」——
   *   本次定位到了，或者之前就定位过（它还在候选里）。
   *   调用方据此决定「别再滚到底」：滚到底会把锚点挤出视野。
   */
  function locateAnchor(): boolean {
    const candidates = options.anchorIds().filter((id): id is string => !!id);
    /** 候选里有没有「早就定位过」的（定位过就不再重复定位）。 */
    let alreadyLocated = false;
    for (const id of candidates) {
      if (locatedIds.has(id)) {
        // 已经定位过这一条：不重复定位（否则列表一变就把用户拽回老锚点）。
        // 但它仍是当前锚点之一 —— 返回值要算上，**继续往下找**别的候选。
        alreadyLocated = true;
        continue;
      }
      if (!options.isVisible(id)) {
        // 不在可见列表里：那条可能已经被删了，或者被当前过滤条件挡住了 ——
        // 静默忽略，不报错。
        continue;
      }
      const element = findElement(id);
      if (!element) {
        // DOM 里还没有这条：等列表再变一次再试（**不**标记成已定位）。
        continue;
      }
      locatedIds.add(id);
      // jsdom 里没有 scrollIntoView（浏览器才有），可选调用。
      element.scrollIntoView?.({ block: 'center' });
      highlight(id);
      return true;
    }
    return alreadyLocated;
  }

  // flush:'post' —— 等组件重渲染完，条目已经在 DOM 里了，
  // 这时才找得到它，scrollIntoView 也才作用得到。
  watch(
    options.watchSources,
    () => {
      locateAnchor();
    },
    { flush: 'post' },
  );

  return { highlightedId, locateAnchor };
}
