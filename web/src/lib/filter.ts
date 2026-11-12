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

/** 一份过滤条件。项目、状态、标签三者取交集。 */
export interface IdeaFilter {
  /** 当前项目；`null` 表示全部项目。 */
  projectId: string | null;
  /** 状态过滤。 */
  status: StatusFilter;
  /** 选中的标签。 */
  tags: string[];
  /** 标签匹配模式。 */
  tagMode: TagMode;
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
