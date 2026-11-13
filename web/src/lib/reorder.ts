/**
 * 拖拽重排的落点语义：**顶替目标的位置**。
 *
 * 「拖到谁头上就顶替谁」—— 把 A 拖到 C 上，得到的是 C 让位、A 站到 C 原来的位置，
 * 而不是「插到 C 后面」。项目侧栏与项目管理页共用这一份实现：
 * 两处各写一遍的话，迟早会有一处变成「插到后面」，同一套数据在两个界面里
 * 拖出来的顺序对不上。
 */

/**
 * 把 `sourceId` 挪到 `targetId` 所在的位置上。
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
  if (from < 0 || ids.indexOf(targetId) < 0) {
    return [...ids];
  }
  const next = [...ids];
  next.splice(from, 1);
  // 移除源之后，目标的**新下标**变了 —— 用新下标插入才是「顶替」。
  const target = next.indexOf(targetId);
  if (target < 0) {
    return [...ids];
  }
  next.splice(target, 0, sourceId);
  return next;
}
