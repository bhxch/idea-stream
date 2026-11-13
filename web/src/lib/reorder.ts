/**
 * 拖拽重排的落点语义：**方向性**。
 *
 * - 源在目标**之前**（往下拖）→ 落到目标**之后**；
 * - 源在目标**之后**（往上拖）→ 落到目标**之前**。
 *
 * 四种情况都有效果，没有「拖了等于没动」的死角：
 *
 * | 当前顺序 | 拖动   | 结果      |
 * |----------|--------|-----------|
 * | A B C    | A → B  | B A C     |
 * | A B C    | C → B  | A C B     |
 * | A B C    | A → C  | B C A     |
 * | A B C    | C → A  | C A B     |
 *
 * 项目侧栏与项目管理页共用这一份实现：两处各写一遍的话，迟早会有一处变成
 * 「插到目标前面」，同一套数据在两个界面里拖出来的顺序对不上。
 */

/**
 * 把 `sourceId` 挪到 `targetId` 的位置（按方向决定落在它前面还是后面）。
 *
 * @param ids 当前顺序（项目 id 列表）。
 * @param sourceId 被拖动的那个 id。
 * @param targetId 落点（被顶替的那个 id）。
 * @returns 新的顺序。源与目标相同、或有一个不在 `ids` 里时原样返回。
 */
export function reorderToTarget(
  ids: readonly string[],
  sourceId: string,
  targetId: string,
): string[] {
  if (sourceId === targetId) {
    return [...ids];
  }
  const from = ids.indexOf(sourceId);
  const to = ids.indexOf(targetId);
  if (from < 0 || to < 0) {
    return [...ids];
  }
  const next = [...ids];
  next.splice(from, 1);
  // 移除源之后，目标的**新下标**变了 —— 用新下标算落点。
  const target = next.indexOf(targetId);
  if (target < 0) {
    return [...ids];
  }
  // 往下拖（源原来在目标前面）：落到目标之后；往上拖：落到目标之前。
  const insertAt = from < to ? target + 1 : target;
  next.splice(insertAt, 0, sourceId);
  return next;
}
