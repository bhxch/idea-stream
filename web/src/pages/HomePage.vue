<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ApiError, apiGet, apiSend } from '../api/client';
import type { Idea, Tag } from '../api/types';
import Composer from '../components/Composer.vue';
import FilterBar from '../components/FilterBar.vue';
import MessageList from '../components/MessageList.vue';
import { showToast } from '../components/ToastHost.vue';
import { useTagMode } from '../composables/useTagMode';
import { matchesFilter, sortIdeas, type IdeaFilter } from '../lib/filter';
import { applyLocalUpsert, ideas, loadIdeas, loading } from '../stores/ideas';
import {
  currentProjectId,
  loadProjects,
  loadSortMode,
  projects,
  setCurrentProject,
} from '../stores/projects';

/**
 * AND/OR 偏好存在本地（每台设备各自的）。
 *
 * 组合式函数返回的是共享的 ref，所以切换模式后各处立刻一致。
 */
const { tagMode, setTagMode } = useTagMode();

/** 过滤条件。项目、状态、标签三者取交集。 */
const filter = ref<IdeaFilter>({
  projectId: currentProjectId.value,
  status: 'all',
  tags: [],
  tagMode: tagMode.value,
});

/** 全部标签（标签多选下拉的选项）。 */
const allTags = ref<Tag[]>([]);

/** 被勾选的想法 id（多选批量操作在 F6 接上）。 */
const selectedIds = ref<Set<string>>(new Set());

/** 是否贴着列表底部（滚动跟随在 F11 接上）。 */
const atBottom = ref(true);

/** 真正生效的过滤条件：模式以 localStorage 里的为准。 */
const activeFilter = computed<IdeaFilter>(() => ({ ...filter.value, tagMode: tagMode.value }));

/**
 * 列表内容。
 *
 * 过滤与排序都在客户端做（`matchesFilter` / `sortIdeas`）：
 * AND/OR 偏好存在各设备自己的 localStorage 里，服务端无从得知每条连接用哪种模式。
 */
const visibleIdeas = computed(() =>
  sortIdeas(ideas.value.filter((idea) => matchesFilter(idea, activeFilter.value))),
);

/** 把后端的错误变成一条提示。 */
function reportError(error: unknown, fallback: string): void {
  const message = error instanceof ApiError ? (error.detail ?? fallback) : fallback;
  showToast(message, 'error');
}

/** 首屏加载：项目列表 + 排序方式 + 全部标签 + 当前项目的想法。 */
async function init(): Promise<void> {
  try {
    await Promise.all([loadProjects(), loadSortMode(), loadTags()]);
    await loadIdeas(currentProjectId.value);
  } catch (error) {
    reportError(error, '加载失败');
  }
}

/** 拉取全部标签。 */
async function loadTags(): Promise<void> {
  allTags.value = await apiGet<Tag[]>('/tags');
}

/** 切换项目：换掉列表范围，之前勾选的条目也不再相关。 */
async function onProjectChange(event: Event): Promise<void> {
  const value = (event.target as HTMLSelectElement).value;
  setCurrentProject(value === '' ? null : value);
  filter.value = { ...filter.value, projectId: currentProjectId.value };
  selectedIds.value = new Set();
  try {
    await loadIdeas(currentProjectId.value);
  } catch (error) {
    reportError(error, '加载想法失败');
  }
}

/** 过滤条件变了。 */
function onFilterUpdate(next: IdeaFilter): void {
  if (next.tagMode !== tagMode.value) {
    // AND/OR 是设备自己的偏好，记到本地。
    setTagMode(next.tagMode);
  }
  filter.value = { ...next, tagMode: tagMode.value };
}

/**
 * 提交一条新想法。
 *
 * 提交的是纯正文（标题留空）；「全部项目」视图下不指定项目，
 * 后端会把它落到内置收件箱。
 */
async function onSubmit(content: string): Promise<void> {
  try {
    const created = await apiSend<Idea>('POST', '/ideas', {
      projectId: currentProjectId.value,
      content,
      title: null,
      tags: [],
    });
    applyLocalUpsert(created);
  } catch (error) {
    reportError(error, '保存失败');
  }
}

/** 勾选/取消勾选一条想法。 */
function onToggleSelect(id: string): void {
  const next = new Set(selectedIds.value);
  if (next.has(id)) {
    next.delete(id);
  } else {
    next.add(id);
  }
  selectedIds.value = next;
}

onMounted(() => {
  void init();
});
</script>

<template>
  <main class="home">
    <header class="bar">
      <h1 class="bar__title">想法</h1>
      <label class="bar__project">
        项目
        <select class="bar__select" :value="currentProjectId ?? ''" @change="onProjectChange">
          <option value="">全部项目</option>
          <option v-for="project in projects" :key="project.id" :value="project.id">
            {{ project.name }}（{{ project.ideaCount }}）
          </option>
        </select>
      </label>
    </header>

    <FilterBar :filter="activeFilter" :all-tags="allTags" @update:filter="onFilterUpdate" />

    <p v-if="loading" class="hint">加载中…</p>

    <MessageList
      :ideas="visibleIdeas"
      :selected-ids="selectedIds"
      :at-bottom="atBottom"
      @toggle-select="onToggleSelect"
    />

    <Composer @submit="onSubmit" />
  </main>
</template>

<style scoped>
.home {
  display: flex;
  flex-direction: column;
  gap: 12px;
  max-width: 960px;
  margin: 0 auto;
  padding: 24px 16px;
}

.bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.bar__title {
  margin: 0;
  font-size: 20px;
}

.bar__project {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 13px;
}

.bar__select {
  min-width: 160px;
}

.hint {
  margin: 0;
  color: var(--muted);
}
</style>
