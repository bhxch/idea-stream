import type { Idea } from '../api/types';

/** 状态过滤：全部 / 未完成 / 已完成。 */
export type StatusFilter = 'all' | 'open' | 'done';

/**
 * 标签匹配模式。
 *
 * `or`：想法的标签与选中标签有交集即命中；
 * `and`：选中标签全部命中才命中。
 *
 * 这是**每台设备各自的偏好**（存在 localStorage 里），但过滤在服务端做 ——
 * 所以这个模式要作为 `tagMode` 参数随请求发过去（见 {@link toQueryParams}），
 * 服务端才知道这条请求要按哪种模式判标签。
 */
export type TagMode = 'or' | 'and';

/** 一份过滤条件。项目、状态、标签、关键词四者取交集。 */
export interface IdeaFilter {
  /** 当前项目；`null` 表示全部项目。 */
  projectId: string | null;
  /** 状态过滤。 */
  status: StatusFilter;
  /** 选中的标签。 */
  tags: string[];
  /** 标签匹配模式。 */
  tagMode: TagMode;
  /**
   * 关键词过滤。
   *
   * 过滤在服务端做：这个词作为 `q` 参数发给后端，由全文索引判定命中
   * （见 {@link toQueryParams}）。
   *
   * 空串 = 不过滤。字段是可选的：「不带关键词的过滤条件」也是一份
   * 合法的过滤条件。
   */
  keyword?: string;
}

/**
 * 关键词匹配：对**正文**做子串匹配，**忽略大小写**。
 *
 * ⚠️ 这是**近似**判断，只用于 SSE 增量条目（别的设备刚记的那条要不要
 * 立刻显示）。列表本身的过滤在服务端做（见 {@link toQueryParams}）。
 *
 * 两边的口径不完全一样，差别在两个方向都有：
 * - 服务端按「词元相邻」判（中文等价于子串、英文是词级）。正文「咖 啡」
 *   服务端命中（空格只是分隔符），这里的前端子串**不**命中 → 显示层
 *   不再按关键词过滤一遍，否则会把服务端判定命中的条目误杀；
 * - 英文上服务端是词级（搜 `hel` 搜不到 `hello`），这里的前端子串**能**
 *   命中 → 这种条目会先出现，下一次重拉时被服务端的权威结果收回。
 *
 * 为什么忽略大小写：想法是随手记的，写「Idea」和写「idea」指的是同一件事
 * （标签比较也是大小写不敏感的，见 {@link matchesFilter}）。用户敲了
 * 「idea」却搜不到「Idea」那条，会以为搜索坏了。
 *
 * 为什么只搜正文：想法**没有标题**了，正文就是用户在这台应用里写的
 * 全部内容，搜正文已经覆盖了「我写过什么」。
 *
 * 关键词两侧的空白先 trim 掉：只敲了一个空格不该变成「一条都搜不到」。
 *
 * @param keyword 关键词；trim 后为空 = 不过滤，一律命中。
 */
export function matchesKeyword(idea: Idea, keyword: string): boolean {
  const needle = keyword.trim().toLowerCase();
  if (!needle) {
    return true;
  }
  return idea.content.toLowerCase().includes(needle);
}

/**
 * 判断一条想法是否命中过滤条件。
 *
 * 用途：SSE 增量条目要不要进列表（`applyEvent` 靠它）。
 * 列表本身的过滤在服务端做 —— 这个判断是**近似**的，权威结果是重拉的结果。
 *
 * 标签比较**大小写不敏感**：标签是自由文本，写「Idea」和写「idea」
 * 指的是同一件事（后端写入时也按大小写不敏感去重）。
 */
export function matchesFilter(idea: Idea, filter: IdeaFilter): boolean {
  if (filter.projectId && idea.projectId !== filter.projectId) {
    return false;
  }

  if (filter.status === 'open' && idea.done) {
    return false;
  }
  if (filter.status === 'done' && !idea.done) {
    return false;
  }

  // 关键词（正文子串匹配，忽略大小写）。空 = 不过滤。
  if (!matchesKeyword(idea, filter.keyword ?? '')) {
    return false;
  }

  if (filter.tags.length === 0) {
    return true;
  }

  const ideaTags = new Set(idea.tags.map((tag) => tag.toLowerCase()));
  const selected = filter.tags.map((tag) => tag.toLowerCase());

  return filter.tagMode === 'and'
    ? selected.every((tag) => ideaTags.has(tag))
    : selected.some((tag) => ideaTags.has(tag));
}

/**
 * 当前有没有过滤条件生效。
 *
 * 「清除」按钮出不出现看它：没有过滤条件时那个按钮是多余的（点了也不会变）。
 *
 * @param filter 当前过滤条件。
 */
export function hasActiveFilter(filter: IdeaFilter): boolean {
  return filter.status !== 'all' || filter.tags.length > 0 || Boolean(filter.keyword?.trim());
}

/**
 * 过滤条件的**值**指纹：两份条件是不是同一份。
 *
 * 不能比对象身份：过滤条件是普通对象，谁都能重新造一个（`{ ...filter }`
 * 就是新的）。身份一变就当成「条件变了」的话，一份**内容完全相同**的条件
 * 也会被判成不同 —— 正在等的那一页往回翻就被白白丢掉。
 *
 * 比值才稳：条件真的变了（项目 / 状态 / 标签 / 模式 / 关键词有一项不同）
 * 指纹才变。
 *
 * 标签按**集合**比（先排序）：标签的顺序不影响服务端过滤的结果
 * （AND/OR 都是按集合判的）。
 *
 * @param filter 一份过滤条件。
 * @returns 指纹字符串（两份条件相同则相同）。
 */
export function filterKey(filter: IdeaFilter): string {
  return JSON.stringify([
    filter.projectId ?? null,
    filter.status,
    [...filter.tags].sort(),
    filter.tagMode,
    (filter.keyword ?? '').trim(),
  ]);
}

/**
 * 把过滤条件转成 GET /api/ideas 的查询参数。
 *
 * 过滤在**服务端**做（设计文档 §11.1）：项目、状态、标签、AND-OR 模式、
 * 关键词一次请求拿最终结果，前端不再把范围内的全部想法拉回来在内存里筛。
 *
 * 参数名的口径（与后端的端点一一对应）：
 * - `projectId`：有值才给（`null` = 全部项目）；
 * - `status`：照给（`'all'` 也给，后端认）；
 * - `tags`：逗号连接的标签名，空数组不给；
 * - `tagMode`：照给（`'or'` / `'and'`）；
 * - `q`：**关键词**（键名是 `q`，不是 `keyword`），trim 后为空不给。
 *
 * 没有的值**不出现**在参数里（而不是给空串）：空串会被后端当成
 * 「有一个空的过滤条件」，那是另一回事。
 *
 * @param filter 当前过滤条件。
 * @returns 查询参数；值为 undefined 的键不会出现在结果里。
 */
export function toQueryParams(filter: IdeaFilter): Record<string, string | undefined> {
  const params: Record<string, string | undefined> = {
    status: filter.status,
    tagMode: filter.tagMode,
  };
  if (filter.projectId) {
    params.projectId = filter.projectId;
  }
  if (filter.tags.length > 0) {
    params.tags = filter.tags.join(',');
  }
  const keyword = filter.keyword?.trim();
  if (keyword) {
    params.q = keyword;
  }
  return params;
}

/**
 * 按 `updatedAt` 升序、`id` 升序排序（最老的在顶、最新的在底）。
 *
 * 与后端的列表顺序一致（后端也是 `updated_at ASC, id ASC`），
 * 重新排一遍，保证顺序不依赖调用方的数据来源
 * （SSE 合并进来的条目也要排在正确的位置）。
 *
 * @returns 新数组，入参不动。
 */
export function sortIdeas(ideas: Idea[]): Idea[] {
  return [...ideas].sort((left, right) => {
    const byTime = Date.parse(left.updatedAt) - Date.parse(right.updatedAt);
    if (byTime !== 0) {
      return byTime;
    }
    return left.id < right.id ? -1 : left.id > right.id ? 1 : 0;
  });
}
