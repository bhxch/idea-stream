import { ref } from 'vue';
import { apiGet } from '../api/client';
import type { Idea } from '../api/types';

/**
 * 当前作用域下的想法列表。
 *
 * 只装**当前项目**（或全部项目）的未删除想法；删除的项目下的想法不在列表里
 * —— 后端已经过滤掉了。
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

/**
 * 拉取想法列表。
 *
 * 只把项目范围交给后端过滤，**状态与标签过滤在客户端做**：
 * AND/OR 偏好存在每台设备自己的 localStorage 里，服务端无从得知每条连接
 * 当前用哪种模式（设计文档 §10.2）。所以这里把范围内的想法全要回来，
 * 由 `lib/filter.ts` 的 `matchesFilter` 在内存里筛。
 *
 * 列表顺序由后端保证（`updated_at ASC, id ASC`，最老的在顶）。
 *
 * @param projectId 当前项目；`null` 表示全部项目
 */
export async function loadIdeas(projectId: string | null): Promise<void> {
  loading.value = true;
  try {
    ideas.value = await apiGet<Idea[]>('/ideas', { projectId: projectId ?? undefined });
  } finally {
    loading.value = false;
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
