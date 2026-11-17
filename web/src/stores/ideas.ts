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

/** 一页多少条。 */
export const PAGE_SIZE = 100;

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
 */
export const scrolledItems = ref(0);

/**
 * 列表级请求的「代次」。loadIdeas 与 fetchOlderIdeas 每次发起时 +1，
 * 响应回来时对照：对不上 = 期间有过更新的列表请求（条件变了触发的重拉、
 * SSE 触发的重拉、新页面挂载后的首次加载……），这一页是按**旧**条件要的，
 * 什么都不能写进共享状态。
 *
 * 要防的场景：页面卸载时在飞的短页响应回来，把 hasMore 写成 false ——
 * 新页面刚算好的 hasMore 就被翻掉了，往回翻从此再也不触发。
 * 卸载本身不动代次，但新页面挂载后的 loadIdeas 会 —— 过期响应要么赶在它
 * 前面落地（马上被覆盖，无害），要么落在它后面（代次对不上，跳过）。
 */
let listGeneration = 0;

/**
 * 最近一次 fetchOlderIdeas 的响应有没有**入账**（hasMore 与深度都写过）。
 *
 * releaseOlderIdeas 靠它判断：没入过账的页没什么可还 —— 深度根本没记它，
 * 还回去反而会把深度还过头（比列表实际多翻的还少）。
 */
let lastOlderPageCounted = false;

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
 * @param limit 请求多少条；不给就是 PAGE_SIZE + 1 —— 多要的那一条既在这一页里
 *              （这一页**整页收下**，见 {@link fetchOlderIdeas}），
 *              也用来判断还有没有更旧的。
 */
export async function loadIdeas(filter: IdeaFilter, limit = PAGE_SIZE + 1): Promise<void> {
  // 有更新的列表请求在发起了：之前在飞的「往回翻」响应从此过期，
  // 它的 hasMore 与深度都不该再写进共享状态（见 {@link listGeneration} 的说明）。
  listGeneration += 1;
  loading.value = true;
  try {
    const params = toQueryParams(filter);
    params.limit = String(limit);
    ideas.value = await apiGet<Idea[]>('/ideas', params);
    // 整页收下（含多要的那一条）：列表长度 = 从最新一端数已经加载的条数，
    // 往回翻时的 offset 就拿它当基准（见 {@link fetchOlderIdeas}）。
    hasMore.value = ideas.value.length >= limit;
    // 只加载一页 = 列表被重置了：用户往上翻过的深度也就没了
    // （下次重拉不该再要那么多条）。要得更深（`limit` 大于一页）时保留深度。
    //
    // 清零放在**成功之后**而不是发起之前：请求挂了的话（网络抖了一下），
    // 列表还在、用户翻过的历史也还在 —— 深度不该替一次失败的请求清掉，
    // 不然下次重拉会少要一页，用户翻过的历史凭空少了（要再翻一次才回得来）。
    if (limit <= PAGE_SIZE + 1) {
      scrolledItems.value = 0;
    }
  } finally {
    loading.value = false;
  }
}

/**
 * 往回翻：取**更旧**的一页，返回新取到的那些（**不改动列表**）。
 *
 * 调用方负责把它们拼到列表前面，并在 DOM 更新后修正滚动位置。
 *
 * @param filter 当前过滤条件（服务端按它过滤）。
 * @param offset 已经翻过多少条（从**最新**一端数：`offset=0` 是最新那页）。
 */
export async function fetchOlderIdeas(filter: IdeaFilter, offset: number): Promise<Idea[]> {
  // 发起新的一页：上一次的入账标记作废。回来时若代次对不上
  // （期间有过更新的列表请求），这一页是按旧条件要的 —— 什么都不写。
  const generation = ++listGeneration;
  lastOlderPageCounted = false;
  loadingMore.value = true;
  try {
    const params = toQueryParams(filter);
    params.limit = String(PAGE_SIZE + 1);
    params.offset = String(offset);
    const result = await apiGet<Idea[]>('/ideas', params);
    // 这一页**整页收下**，一条都不丢。
    //
    // 服务端的切片是从**最新**一端数的（IdeaService.List）：
    //   end = Count - offset;  start = max(0, end - limit)
    // 返回的数组按 updated_at **升序**（最老的在顶、最新的在底），
    // 所以「多要的那一条」落在这一页的**最老**那端 —— 不是最新那端。
    //
    // 早先这里按 slice(0, PAGE_SIZE) 截断（留最老的一百条、丢掉最新的一条），
    // 丢掉的恰恰是**紧挨着已加载区域**的那一条：列表中间从此**缺一条**，
    // 而下一次请求拿「列表长度」当 offset —— 长度里没有那个洞，
    // 洞再也补不上（还会顺带多出一条重复的）。
    //
    // 整页收下就不会丢：列表长度 = 从最新一端数已经加载的条数，
    // 下一次的 offset 正好接在这一页的下面（见 HomePage.loadOlder）。
    //
    // 「还有没有更旧的」只看**条数**：要了 PAGE_SIZE + 1 条，
    // 服务端给满了就说明下面还有，没给满就是到底了。
    //
    // 这些写入只在响应仍是最新时做（代次对得上）。过期的响应什么都不写：
    // 它的 hasMore 会把新页面刚算好的翻掉（往回翻从此不触发），
    // 它的深度会被调用方丢掉的那一页 release 掉（可深度根本没记它，还回去就还过头）。
    // 过期页的清理由调用方直接丢（它连 release 都不必调 —— 见 releaseOlderIdeas）。
    lastOlderPageCounted = generation === listGeneration;
    if (lastOlderPageCounted) {
      hasMore.value = result.length >= PAGE_SIZE + 1;
      // 往上翻的深度记在这儿：翻出来多少条，SSE 重拉时就得保留多少条
      // （基准不能用「列表当前长度」—— 重拉是整表替换，那会自我膨胀）。
      scrolledItems.value += result.length;
    }
    return result;
  } finally {
    loadingMore.value = false;
  }
}

/**
 * 把**丢掉**的那一页的深度还回去。
 *
 * 深度由 {@link fetchOlderIdeas} 记（那儿是唯一记它的地方），但调用方拿到
 * 这一页之后可能决定不要它 —— 条件变了（这一页是按旧条件要的）、页面已经
 * 卸载了。那时候必须把深度还回去：深度比列表多一页的话，下一次 SSE 重拉
 * 会按 {@link loadedLimit} 多要一页用户根本没翻过的内容，列表凭空长一页。
 *
 * @param count 丢掉的那一页有多少条。
 */
export function releaseOlderIdeas(count: number): void {
  // 这一次的响应没入过账（期间有过更新的列表请求，hasMore 与深度什么都没写）——
  // 没什么可还的；还回去会把深度还过头。
  if (!lastOlderPageCounted) {
    return;
  }

  lastOlderPageCounted = false;
  // 入参也要夹住：负数会把深度**抬高**，大于这一页的条数会把深度减到比列表
  // 实际翻过的还少。两个调用点传的都是 older.length，但这是 store 自己守得住的边界。
  const dropped = Math.max(0, count);
  scrolledItems.value = Math.max(0, scrolledItems.value - dropped);
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
