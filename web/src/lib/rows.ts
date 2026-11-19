import type { Idea } from '../api/types';

/**
 * 一排两个格子。
 *
 * 奇数位（第 1、3、5…条）进左格，偶数位进右格 —— 编号与左右由**同一个
 * 位置序号**决定，所以「奇数在左、偶数在右」永远成立。
 * 最后一条若只有一条，它在左格，右格留空。
 */
export interface IdeaRow {
  /** 这一排的第一条（位置序号 = 2k+1）。 */
  left: Idea;
  /** 这一排的第二条（位置序号 = 2k+2）；没有时是 null（右格留空）。 */
  right: Idea | null;
}

/**
 * 把想法切成「一排两个格子」。
 *
 * **不重排**：输入的顺序就是渲染的顺序（顺序由后端保证，
 * `updated_at ASC, id ASC`），这里只切排，不动先后。
 *
 * 为什么不让 CSS `columns` 干这件事：CSS 多列在内容增删时会**重新平衡各列**，
 * 一次追加就可能让整墙重排；用「排」做显式 DOM 容器，追加只影响最后一排。
 *
 * @param ideas 已加载并排好序的想法（最老的那条在前）。
 * @returns 一排一个的格子列表；最后一条独占时那一排的右格是 `null`。
 */
export function toRows(ideas: Idea[]): IdeaRow[] {
  const rows: IdeaRow[] = [];
  for (let index = 0; index < ideas.length; index += 2) {
    const right = index + 1 < ideas.length ? ideas[index + 1] : null;
    rows.push({ left: ideas[index], right });
  }
  return rows;
}
