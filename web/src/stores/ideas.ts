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
 * @param limit 请求多少条；不给就是 PAGE_SIZE + 1（多一条用来判断还有没有更旧的）。
 */
export async function loadIdeas(filter: IdeaFilter, limit = PAGE_SIZE + 1): Promise<void> {
  // 只加载一页 = 列表被重置了：用户往上翻过的深度也就没了
  // （下次重拉不该再要那么多条）。要得更深（`limit` 大于一页）时保留深度。
  if (limit <= PAGE_SIZE + 1) {
    scrolledItems.value = 0;
  }
  loading.value = true;
  try {
    const params = toQueryParams(filter);
    params.limit = String(limit);
    ideas.value = await apiGet<Idea[]>('/ideas', params);
    hasMore.value = ideas.value.length >= limit;
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
  loadingMore.value = true;
  try {
    const params = toQueryParams(filter);
    params.limit = String(PAGE_SIZE + 1);
    params.offset = String(offset);
    const result = await apiGet<Idea[]>('/ideas', params);
    // 多出来的那条只是用来判断「还有没有更旧的」，不进列表。
    hasMore.value = result.length > PAGE_SIZE;
    const older = result.slice(0, PAGE_SIZE);
    // 往上翻的深度记在这儿：翻出来多少条，SSE 重拉时就得保留多少条
    // （基准不能用「列表当前长度」—— 重拉是整表替换，那会自我膨胀）。
    scrolledItems.value += older.length;
    return older;
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
  scrolledItems.value = Math.max(0, scrolledItems.value - count);
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
