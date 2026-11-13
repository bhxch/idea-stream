<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { apiGet, apiSend } from '../api/client';
import type { Idea, Project, TrashEntry } from '../api/types';
import PageNav from '../components/PageNav.vue';
import { reportError } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';

/**
 * 回收站里的条目。
 *
 * 两类互补、互不重叠（spec §10.5）：
 * - 容器（`kind === 'project'`）：已删的项目，下挂它下面仍在回收站里的想法；
 * - 顶层（`kind === 'idea'`）：被单独删掉的想法（它的项目还活着）。
 */
const entries = ref<TrashEntry[]>([]);

/** 展开着的容器（项目 id 集合）。 */
const expanded = ref<Set<string>>(new Set());

/** 正在恢复的条目 id（`null` = 没有）。恢复请求在飞时禁用按钮。 */
const busyId = ref<string | null>(null);

/** 拉一次回收站。 */
async function load(): Promise<void> {
  try {
    entries.value = await apiGet<TrashEntry[]>('/trash');
  } catch (error) {
    reportError(error, '加载回收站失败');
  }
}

/** 容器是不是展开着的。 */
function isExpanded(projectId: string): boolean {
  return expanded.value.has(projectId);
}

/** 展开/收起容器。 */
function toggle(projectId: string): void {
  // 每次都造新 Set：Vue 的响应式 Set 只追踪 `has`，原地改会静默失效。
  const next = new Set(expanded.value);
  if (next.has(projectId)) {
    next.delete(projectId);
  } else {
    next.add(projectId);
  }
  expanded.value = next;
}

/**
 * 恢复一个项目（连带恢复「因删项目而删」的想法）。
 *
 * 恢复完重新拉一次列表：恢复过的条目不该还留在回收站里。
 */
async function restoreProject(projectId: string): Promise<void> {
  if (busyId.value) {
    return;
  }
  busyId.value = projectId;
  try {
    await apiSend<Project>('POST', `/projects/${projectId}/restore`);
    await load();
  } catch (error) {
    reportError(error, '恢复项目失败');
  } finally {
    busyId.value = null;
  }
}

/**
 * 恢复一条想法。
 *
 * 容器里的想法也能单独恢复（项目仍在回收站时恢复是有可见效果的：
 * 它从容器里消失、回到所属项目的列表）。
 */
async function restoreIdea(ideaId: string): Promise<void> {
  if (busyId.value) {
    return;
  }
  busyId.value = ideaId;
  try {
    await apiSend<Idea>('POST', `/ideas/${ideaId}/restore`);
    await load();
  } catch (error) {
    reportError(error, '恢复想法失败');
  } finally {
    busyId.value = null;
  }
}

onMounted(() => {
  void load();
});
</script>

<template>
  <main class="page">
    <PageNav />

    <h1>回收站</h1>
    <p class="trash__hint">回收站里的条目到期自动清理；在那之前可以恢复。</p>

    <ul class="trash__list">
      <li
        v-for="entry in entries"
        :key="`${entry.kind}:${entry.project?.id ?? entry.idea?.id}`"
        class="trash__entry"
        :class="entry.kind === 'project' ? 'trash__entry--container' : 'trash__entry--idea'"
      >
        <!-- 容器：已删的项目，下挂它下面仍在回收站里的想法。 -->
        <template v-if="entry.kind === 'project' && entry.project">
          <div class="trash__row">
            <button class="trash__toggle" type="button" @click="toggle(entry.project.id)">
              {{ isExpanded(entry.project.id) ? '收起' : '展开' }}
            </button>
            <span class="trash__name">{{ entry.project.name }}</span>
            <!--
              条数取 ideas.length，**不是** project.ideaCount：
              ideaCount 统计的是未删想法，已删项目恒为 0。
            -->
            <span class="trash__count">{{ entry.ideas.length }} 条想法</span>
            <span class="trash__time">{{ formatRelativeTime(entry.project.updatedAt) }}</span>
            <button
              class="trash__restore"
              type="button"
              :disabled="busyId !== null"
              @click="restoreProject(entry.project.id)"
            >
              恢复项目
            </button>
          </div>

          <ul v-if="isExpanded(entry.project.id)" class="trash__children">
            <li v-for="idea in entry.ideas" :key="idea.id" class="trash__child">
              <span class="trash__child-body">{{ idea.content }}</span>
              <span class="trash__time">{{ formatRelativeTime(idea.updatedAt) }}</span>
              <button
                class="trash__restore-idea"
                type="button"
                :disabled="busyId !== null"
                @click="restoreIdea(idea.id)"
              >
                恢复
              </button>
            </li>
          </ul>
        </template>

        <!-- 顶层：被单独删掉的想法（它的项目还活着）。 -->
        <template v-else-if="entry.idea">
          <div class="trash__row">
            <span class="trash__body">{{ entry.idea.content }}</span>
            <span class="trash__time">{{ formatRelativeTime(entry.idea.updatedAt) }}</span>
            <button
              class="trash__restore"
              type="button"
              :disabled="busyId !== null"
              @click="restoreIdea(entry.idea.id)"
            >
              恢复
            </button>
          </div>
        </template>
      </li>
    </ul>

    <p v-if="entries.length === 0" class="trash__empty">回收站是空的。</p>
  </main>
</template>

<style scoped>
.trash__hint,
.trash__empty {
  color: var(--muted);
  font-size: 13px;
}

.trash__list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 12px 0 0;
  padding: 0;
  list-style: none;
}

.trash__entry {
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.trash__row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex-wrap: wrap;
}

.trash__name {
  flex: 1 1 auto;
  min-width: 0;
  overflow-wrap: anywhere;
}

.trash__body,
.trash__child-body {
  flex: 1 1 auto;
  min-width: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.trash__count,
.trash__time {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.trash__children {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 8px 0 0;
  padding: 8px 0 0 12px;
  border-top: 1px solid var(--border);
  list-style: none;
}

.trash__child {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}

.trash__child-body {
  font-size: 13px;
}
</style>
