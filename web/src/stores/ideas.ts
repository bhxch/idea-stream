import { ref } from 'vue';
import { apiGet } from '../api/client';
import type { Idea } from '../api/types';
import { toQueryParams, type IdeaFilter } from '../lib/filter';

/**
 * 当前作用域下的想法列表。
 *
 * 装的是**服务端过滤后**的结果（项目 / 状态 / 标签 / AND-OR / 关键词
 * 都在服务端过滤，见设计文档 §13.1），所以这份列表可以直接显示 ——
 * 显示层不再按条件筛一遍。
 */
export const ideas = ref<Idea[]>([]);

/** 列表是否正在加载。 */
export const loading = ref(false);

/**
 * 要定位的条目 id（合并出来的新条目）。
 *
 * 合并的源条目会被硬删、结果出现在列表最下面，用户多半没看见它；
 * F11 用这个 id 滚动定位过去。
 */
export const focusIdeaId = ref<string | null>(null);

/** 一页多少条（不含探针）。 */
export const PAGE_SIZE = 100;

/**
 * 往回翻时**实际请求**的条数：多要的那一条是探针（用来判断还有没有更旧的）。
 *
 * ⚠️ 探针落在这一页的**最老**那端（服务端按 updated_at 升序返回），
 * 所以满页时要丢掉的是**最老**的那一条 —— 丢掉的那条落在已加载区域的
 * **外侧**，下一轮请求窗口会把它重新要回来（集合中间不会留洞）。
 */
export const PROBE_LIMIT = PAGE_SIZE + 1;

/**
 * 服务端一次最多给多少条（`limit` 的上限）。
 *
 * 超了服务端回 400 `invalid_pagination` —— 一个 400 会让 SSE 的刷新彻底断掉，
 * 所以请求多少条一定要夹在这个上限里。
 */
const MAX_LIMIT = 1000;

/**
 * 用户往上翻过了多少条（已经翻出来、拼进列表的那些更旧的条目）。
 *
 * 这是 SSE 重拉的基准（见 {@link loadedLimit}）：重拉要保留用户翻过的深度，
 * 不然视口会突然跳回最新那批。
 *
 * ⚠️ **不能拿「列表当前长度」当基准**：重拉是**整表替换** —— 列表长度会变成
 * 这次要到的条数，下一个事件又按更大的长度再要一页 → 每来一个事件列表就长
 * 一页（4 个事件就能把 500 条全塞进 DOM，哪怕用户从没往上翻过），
 * `hasMore` 也会跟着失真。
 *
 * 深度与列表长度必须**始终对得上**：深度记的是「列表里有多少条是翻出来的
 * 历史」，两者一旦脱钩，下一次重拉就会按错的条数要 —— 要少了用户翻过的
 * 历史凭空消失，要多了塞进一堆用户没翻过的内容。
 */
export const scrolledItems = ref(0);

/**
 * 列表级请求的「代次」。loadIdeas 与 fetchOlderIdeas 每次发起时 +1。
 *
 * 代次只在**请求发起**时涨，不看响应什么时候回来 —— 两发请求的响应可能
 * 乱序回来（谁的网络快谁先到），代次记的是「请求的先后」。
 */
let listGeneration = 0;

/**
 * 最后一次**成功落地**的整表替换（loadIdeas）的代次。
 *
 * 它跟 {@link listGeneration} 差一层：那个记「谁最后发起」，这个记
 * 「现在共享状态里是谁的数据」。响应乱序时靠它判断谁该写：
 *
 * - 代次比它**旧**的响应是按更旧的状态要的 → 不写（否则会把刚落地的
 *   新数据覆盖掉；条件变了的那一发就会看到旧条件的结果）；
 * - 代次比它**新**的响应照写（更新的数据落地）；
 * - 比它新的那一发**失败**了 → 代次不变 → 旧的那一发仍然写得进去
 *   （不然新数据没拿到、旧数据也丢了，列表就空了）。
 *
 * 往回翻的那一页也看这个：它落地时若有更新的整表替换已经落地了，它就是
 * **过期**的 —— 那一页是按旧状态要的（它的条目可能已经被新列表收进去了），
 * 拼进去会重复或错位。
 */
let appliedGeneration = 0;

/**
 * SSE 重拉时要多少条：一页 + 一条（留给刚到的那条）+ 用户往上翻过的条数。
 *
 * 夹在服务端的上限（{@link MAX_LIMIT} = 1000）之内：`limit` 超了服务端回 400
 * `invalid_pagination`，一个 400 会让 SSE 的刷新彻底断掉。翻得再深也只能
 * 保留到 1000 条（更旧的那部分下次往回翻时重新取到）。
 *
 * @param scrolledItems 用户往上翻过的条数（**数字**，不是计数器本身）。
 */
export function loadedLimit(scrolledItems: number): number {
  return Math.min(PAGE_SIZE + 1 + scrolledItems, MAX_LIMIT);
}

/** 还有没有更旧的（往上翻历史）。 */
export const hasMore = ref(false);

/** 正在加载更旧的。 */
export const loadingMore = ref(false);

/**
 * 拉取想法列表并**整表替换**。
 *
 * 过滤条件（项目 / 状态 / 标签 / AND-OR / 关键词）全部交给后端：
 * 一次请求拿最终结果，前端不再把范围内的全部想法拉回来在内存里筛。
 *
 * 列表顺序由后端保证（`updated_at ASC, id ASC`，最老的在顶）。
 *
 * @param filter 当前过滤条件。
 * @param limit 请求多少条；不给就是 PAGE_SIZE + 1 —— 多要的那一条就在这一页里
 *              （首屏这一页是**整页收下**的；只有往回翻那一页才丢探针，
 *              见 {@link fetchOlderIdeas}），也用来判断还有没有更旧的。
 */
export async function loadIdeas(filter: IdeaFilter, limit = PAGE_SIZE + 1): Promise<void> {
  // 发起一次整表替换：代次 +1。
  const generation = ++listGeneration;
  loading.value = true;
  try {
    const params = toQueryParams(filter);
    params.limit = String(limit);
    const result = await apiGet<Idea[]>('/ideas', params);
    if (generation < appliedGeneration) {
      // 已经有**更新的**整表替换落地了：这一页是按更旧的状态要的
      // （条件变了的那一发晚回来），写进去会把刚落地的数据覆盖掉。
      // 什么也不写 —— 共享状态里已经是更新的那份了。
      return;
    }
    appliedGeneration = generation;
    // 整页收下（含多要的那一条）：列表长度 = 从最新一端数已经加载的条数，
    // 往回翻时的 offset 就拿它当基准（见 {@link fetchOlderIdeas}）。
    ideas.value = result;
    hasMore.value = result.length >= limit;
    // 深度按**这一页实际给了多少条**重算，而不是「要不要清零」。
    //
    // 列表 = 从最新一端数的若干条，头一页是 PAGE_SIZE + 1 条（多要的那条
    // 用来判断还有没有更旧的），多出来的那些就是用户翻出来的历史。
    // 按实收条数算，深度与列表长度**始终对得上**：不管这一发是「只加载
    // 一页」（深度归零）还是「保留深度的重拉」（深度照旧），还是两发同时
    // 在飞、落地顺序颠倒（后落地的那发按自己的条数重算），都不会脱钩。
    //
    // 之前是「发起时清零 / 成功时不动」：清零放在发起之前，请求一挂深度
    // 就没了（用户翻过的历史凭空少一页）；放在成功之后，又会出现「在途
    // 期间深度还是旧值」的缝 —— 两发重拉落地顺序一颠倒，列表和深度就对不上了。
    scrolledItems.value = Math.max(0, result.length - (PAGE_SIZE + 1));
  } finally {
    loading.value = false;
  }
}

/**
 * 往回翻取回的一页。
 */
export interface OlderPage {
  /** 这一页的条目（满页时已丢掉最老的那一条）。 */
  items: Idea[];
  /**
   * 这一页是不是已经**过期**。
   *
   * 过期 = 它回来的时候，已经有**更新的**整表替换落地了（条件变了触发的
   * 重拉、SSE 触发的重拉、新页面挂载后的首次加载……）。这一页是按旧状态
   * 要的：它的条目可能已经被新列表收进去了，拼进去会重复或错位。
   *
   * 过期页要**整页丢掉**（不能只丢一半）—— 丢一半的话，列表既不是旧的
   * 也不是新的，深度更对不上。
   */
  stale: boolean;
  /**
   * 服务端这一页是不是满的（满 = 还有更旧的）。
   *
   * 「满」= 服务端给满了 {@link PROBE_LIMIT} 条 —— 那一页被裁掉探针之后
   * 仍有 {@link PAGE_SIZE} 条，说明下面还有更旧的。
   */
  hasMore: boolean;
}

/**
 * 往回翻：取**更旧**的一页。
 *
 * **这个函数什么都不写进共享状态** —— 取回来的那一页由调用方决定要不要
 * （{@link prependOlder} 才是真正落地的那个动作）。这样「取」与「用」分成
 * 两步，中间不管插进来什么（页面卸载了、过滤条件变了、列表被重拉换了），
 * 共享状态都不会被一页**过期**的响应改坏。
 *
 * @param filter 当前过滤条件（服务端按它过滤）。
 * @param offset 已经翻过多少条（从**最新**一端数：`offset=0` 是最新那页）。
 */
export async function fetchOlderIdeas(filter: IdeaFilter, offset: number): Promise<OlderPage> {
  // 发起新的一页：代次 +1。
  const generation = ++listGeneration;
  loadingMore.value = true;
  try {
    const params = toQueryParams(filter);
    params.limit = String(PROBE_LIMIT);
    params.offset = String(offset);
    const result = await apiGet<Idea[]>('/ideas', params);
    // 满页 → 丢掉**最老**的那一条（探针），净增 100 条（偶数）。
    //
    // ⚠️ **要丢的是 result 的第一个元素**（服务端按 updated_at 升序返回，最老的在顶）：
    // 多要的那一条落在这一页的**最老**那端 —— 丢它不会在集合中间留洞
    // （丢掉的那条落在已加载区域的**外侧**，下一轮的请求窗口会把它要回来）。
    //
    // ⚠️ **别写成 slice(0, result.length - 1)**：那丢的是这一页**最新**的那一条 ——
    // 它紧挨着已加载区域，丢掉就在集合中间留洞（洞永远补不上，还会多出一条重复）。
    // 仓库里已经踩过一次这个坑（`slice(0, PAGE_SIZE)`），`stores/ideas.ts` 的注释里写着。
    //
    // 验算（总数 C、已加载 k 条，距离区间 [C-k, C)）：本轮请求返回距离
    // [C-k-101, C-k) 的 101 条 → 丢掉最老的那个（距离 C-k-101，数组下标 0）
    // → 收下 [C-k-100, C-k) 的 100 条 → 已加载变成 [C-k-100, C) 的 k+100 条 ✓
    // 连续 ✓；下一轮 offset = k+100 → 同样丢最老的那条 ✓。
    const full = result.length >= PROBE_LIMIT;
    const items = full ? result.slice(1) : result;
    return { items, stale: generation < appliedGeneration, hasMore: full };
  } finally {
    loadingMore.value = false;
  }
}

/**
 * 把往回翻取回的一页**用到**列表上：拼到列表前面，并把深度与「还有没有
 * 更旧的」一起改掉。
 *
 * 三件事必须在**同一个动作**里做完 —— 列表变了而深度没变（或反过来），
 * 两者就脱钩了：下一次 SSE 重拉按 {@link loadedLimit} 要条目，深度少了
 * 用户翻过的历史凭空消失，深度多了塞进一堆用户没翻过的内容。
 *
 * ⚠️ **加多少条就是多少条**（`scrolledItems.value += page.items.length`）：
 * 「丢掉探针」发生在 {@link fetchOlderIdeas} 里（它把满页 101 条裁成 100 条），
 * 这一层拿到的已经是**裁过之后**的条数。
 *
 * @param page 往回翻取回的那一页（可以为空 —— 空页就是「到底了」）。
 */
export function prependOlder(page: OlderPage): void {
  // 「还有没有更旧的」由**取页时**的判定给（服务端这一页满不满）。
  hasMore.value = page.hasMore;
  if (page.items.length === 0) {
    // 到底了：列表与深度都不动（换一个新数组只会白白触发一次重渲染）。
    return;
  }
  ideas.value = [...page.items, ...ideas.value];
  // 翻出来多少条，深度就记多少条：SSE 重拉时按它保留深度
  // （基准不能用「列表当前长度」—— 重拉是整表替换，那会自我膨胀）。
  //
  // ⚠️ 满页时记的是 **100** 条（探针已经被裁掉）：净增偶数条 → 已有卡片的
  // 位置序号奇偶不变 → 两列布局不换边。
  scrolledItems.value += page.items.length;
}

/**
 * 插入或覆盖一条想法（本地乐观更新）。
 *
 * 已存在就整条替换 —— 后端返回的字段才是最新的，不要自己猜 `updatedAt`；
 * 不存在就追加到末尾（新想法是最新的，在列表最下面）。
 */
export function applyLocalUpsert(idea: Idea): void {
  const index = ideas.value.findIndex((item) => item.id === idea.id);
  if (index < 0) {
    ideas.value = [...ideas.value, idea];
    return;
  }
  const next = [...ideas.value];
  next[index] = idea;
  ideas.value = next;
}

/** 从本地列表里移除一条想法（软删除成功后调用）。 */
export function removeLocal(id: string): void {
  ideas.value = ideas.value.filter((item) => item.id !== id);
}
