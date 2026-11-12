import type { Idea, IdeaEvent } from '../api/types';
import { matchesFilter, sortIdeas, type IdeaFilter } from './filter';

/**
 * SSE 事件合并进列表的规则。
 *
 * 都是纯函数：入参是「当前列表 + 一条事件」，返回「合并后的新列表」——
 * 不就地改入参，调用方拿到新数组直接替换即可。
 *
 * 三条贯穿始终的规矩：
 *
 * 1. **先按过滤条件判断要不要显示**。不符合当前过滤的想法不能塞进消息流 ——
 *    否则用户会在「未完成」视图里看到一条「已完成」的条目。
 * 2. **不符合过滤时只做移除**。一条想法可能本来在列表里（用户看着它），
 *    后来被改成了不符合过滤的样子（标记完成、换标签）—— 那时要把它**拿掉**，
 *    不是留着。
 * 3. **按 id 去重**。自己发起的操作已经乐观插进列表了，广播回来的同一条事件
 *    不该让列表里出现两条（用 Map 去重）。
 */

/**
 * 按 id 去重。
 *
 * 同一个 id 出现多次时保留**最后**那一条（后到的数据是最新的）。
 */
function dedupe(list: Idea[]): Idea[] {
  const byId = new Map<string, Idea>();
  for (const idea of list) {
    byId.set(idea.id, idea);
  }
  return [...byId.values()];
}

/**
 * 插入或替换一条想法；已有的 id 会被挪到**末尾**（最新的排在最后）。
 *
 * @returns 新数组，入参不动。
 */
function upsertAtEnd(list: Idea[], idea: Idea): Idea[] {
  const byId = new Map<string, Idea>();
  for (const item of list) {
    if (item.id !== idea.id) {
      byId.set(item.id, item);
    }
  }
  byId.set(idea.id, idea);
  return [...byId.values()];
}

/**
 * 把一条 SSE 事件合并进列表。
 *
 * @param ideas 当前列表（顺序由调用方保证与后端一致：`updated_at ASC, id ASC`）
 * @param e 收到的事件
 * @param f 当前过滤条件（不符合的想法不显示）
 * @returns 合并后的新列表；入参不动。
 */
export function applyEvent(ideas: Idea[], e: IdeaEvent, f: IdeaFilter): Idea[] {
  switch (e.type) {
    case 'idea.created':
    case 'idea.restored': {
      const idea = e.idea;
      if (!idea) {
        return ideas;
      }
      // 不符合过滤 → 不插入（但也别动列表里已有的其它条目）。
      if (!matchesFilter(idea, f)) {
        return ideas;
      }
      // 新条目是最新的，排在最后。
      return upsertAtEnd(ideas, idea);
    }

    case 'idea.updated': {
      const idea = e.idea;
      if (!idea) {
        return ideas;
      }
      if (!matchesFilter(idea, f)) {
        // 改完之后不符合过滤了：原来在列表里的那条要拿掉。
        return ideas.filter((item) => item.id !== idea.id);
      }
      // 换成本次事件的版本，并按 updatedAt 重排（改了 updatedAt 的那条排到最后）。
      return sortIdeas(upsertAtEnd(ideas, idea));
    }

    case 'idea.deleted': {
      const id = e.idea?.id;
      if (!id) {
        return ideas;
      }
      return ideas.filter((item) => item.id !== id);
    }

    case 'idea.merged': {
      // 源条目被硬删了：先从列表里去掉。
      const sources = e.mergedFrom ?? [];
      let next = ideas.filter((item) => !sources.includes(item.id));
      const merged = e.idea;
      if (merged && matchesFilter(merged, f)) {
        // 合并结果是新的一条，排在最后。
        next = upsertAtEnd(next, merged);
      }
      return next;
    }

    case 'project.created':
    case 'project.updated':
    case 'project.deleted':
      // 项目变更（改名、重排、删除）由项目 store 处理，想法列表不动。
      return ideas;

    default:
      return ideas;
  }
}

/**
 * 整表替换：服务端说「你可能漏了事件，重新拉一次列表」。
 *
 * @param fresh 重新拉到的完整列表
 * @returns 新列表；入参不动。
 */
export function applyResync(ideas: Idea[], fresh: Idea[]): Idea[] {
  // ideas 不参与计算：整表替换，旧列表整个丢掉。
  void ideas;
  return dedupe(fresh);
}

/**
 * 找到要定位的那条想法在列表里的下标。
 *
 * 用来做深链定位（`?idea=<id>`）与合并结果的滚动定位。
 *
 * @param id 目标 id；`undefined`（没带深链参数）直接返回 -1
 * @returns 下标；找不到返回 -1（调用方据此静默忽略，不报错）
 */
export function findAnchorIndex(ideas: Idea[], id: string | undefined): number {
  if (!id) {
    return -1;
  }
  return ideas.findIndex((idea) => idea.id === id);
}
