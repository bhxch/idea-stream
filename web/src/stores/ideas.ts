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
    return result.slice(0, PAGE_SIZE);
  } finally {
    loadingMore.value = false;
  }
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
