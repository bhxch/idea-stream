import type { Idea } from '../api/types';

/** 状态过滤：全部 / 未完成 / 已完成。 */
export type StatusFilter = 'all' | 'open' | 'done';

/**
 * 标签匹配模式。
 *
 * `or`：想法的标签与选中标签有交集即命中；
 * `and`：选中标签全部命中才命中。
 *
 * 这是**每台设备各自的偏好**（存在 localStorage 里），后端无从得知
 * 每条连接当前用哪种模式，所以过滤在客户端做（设计文档 §10.2）。
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
   * 关键词过滤：正文里含有这个子串才显示（见 {@link matchesKeyword}）。
   *
   * 空串 = 不过滤。字段是可选的：「不带关键词的过滤条件」也是一份
   * 合法的过滤条件（比如只看数据集维度的那个，见 HomePage 的 datasetFilter）。
   */
  keyword?: string;
}

/**
 * 关键词匹配：对**正文**做子串匹配，**忽略大小写**。
 *
 * 为什么忽略大小写：想法是随手记的，写「Idea」和写「idea」指的是同一件事
 * （标签比较也是大小写不敏感的，见 {@link matchesFilter}）。用户敲了
 * 「idea」却搜不到「Idea」那条，会以为搜索坏了。
 *
 * 为什么只搜正文：正文就是用户在这台应用里写的全部内容（录入框只提交正文，
 * 标题留空），搜正文已经覆盖了「我写过什么」。标题字段平时是空的
 * （只有走 API 建的条目与合并结果才可能有标题），暂不纳入。
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
 * 按 `updatedAt` 升序、`id` 升序排序（最老的在顶、最新的在底）。
 *
 * 与后端的列表顺序一致（后端也是 `updated_at ASC, id ASC`），
 * 客户端过滤之后重新排一遍，保证顺序不依赖调用方的数据来源。
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
