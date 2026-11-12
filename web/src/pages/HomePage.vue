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

/**
 * 首屏加载。
 *
 * 三件事互不依赖：项目列表、排序方式偏好、全部标签。
 * 排序方式只是个偏好，取不到也不该拖垮整屏 —— 用 `allSettled` 各自兜底，
 * 想法列表**单独**加载，列表挂了不影响别的提示。
 */
async function init(): Promise<void> {
  const [projectsResult, sortModeResult, tagsResult] = await Promise.allSettled([
    loadProjects(),
    loadSortMode(),
    loadTags(),
  ]);
  if (projectsResult.status === 'rejected') {
    reportError(projectsResult.reason, '加载项目失败');
  }
  if (sortModeResult.status === 'rejected') {
    reportError(sortModeResult.reason, '加载排序方式失败');
  }
  if (tagsResult.status === 'rejected') {
    reportError(tagsResult.reason, '加载标签失败');
  }

  try {
    await loadIdeas(currentProjectId.value);
  } catch (error) {
    handleIdeasError(error);
  }
}

/** 拉取全部标签。 */
async function loadTags(): Promise<void> {
  allTags.value = await apiGet<Tag[]>('/tags');
}

/**
 * 切换项目：换掉列表范围，之前勾选的条目也不再相关。
 *
 * 拉取失败时把项目选择**回滚**：数据还停在上一个项目，
 * `filter.projectId` 若停在新项目，旧数据会被过滤成空列表 —— 两边必须对齐。
 */
async function onProjectChange(event: Event): Promise<void> {
  const select = event.target as HTMLSelectElement;
  const value = select.value;
  const previous = currentProjectId.value;
  const next = value === '' ? null : value;
  setCurrentProject(next);
  filter.value = { ...filter.value, projectId: next };
  selectedIds.value = new Set();
  try {
    await loadIdeas(next);
  } catch (error) {
    if (isTooManyIdeas(error)) {
      // 切「全部项目」会一次要全库，超了上限 —— 改切最近有活动的项目，不回滚。
      reportTooManyIdeas(error);
      return;
    }
    setCurrentProject(previous);
    filter.value = { ...filter.value, projectId: previous };
    // 状态回滚了，下拉的 DOM 值也得回去：绑定值前后一样时 Vue 不会重设它，
    // 不手动改的话下拉会停在用户刚选的那个项目上，与状态对不上。
    select.value = previous ?? '';
    reportError(error, '加载想法失败');
  }
}

/**
 * 加载想法列表失败时的分派。
 *
 * 「全部项目」一次要拉回全库，会撞上服务端的单次返回上限（`too_many_ideas`）——
 * 这不是普通的失败，得给一条能照着做的提示。
 */
function handleIdeasError(error: unknown): void {
  if (isTooManyIdeas(error)) {
    reportTooManyIdeas(error);
    return;
  }
  reportError(error, '加载想法失败');
}

/** 是不是「命中数超过单次返回上限」。 */
function isTooManyIdeas(error: unknown): error is ApiError {
  return error instanceof ApiError && error.code === 'too_many_ideas';
}

/**
 * 「命中数超过单次上限」的提示 + 补救。
 *
 * 提示里带上条数，用户才知道自己有多少条；然后**自动切到最近有活动的项目**：
 * 数据仍然完整可用，想看全库再自己切回去。
 */
function reportTooManyIdeas(error: ApiError): void {
  const count = parseHitCount(error.detail);
  showToast(
    count === null ? '条目太多，超过单次上限，请先选一个项目' : `共 ${count} 条超过单次上限，请先选一个项目`,
    'error',
  );
  void switchToMostRecentProject();
}

/** 从服务端的说明里取出命中条数（「命中 37 条想法，超过单次返回上限 2000，…」）。 */
function parseHitCount(detail: string | undefined): number | null {
  const matched = /命中\s*(\d+)\s*条/.exec(detail ?? '');
  return matched ? Number(matched[1]) : null;
}

/**
 * 切到最近有活动的项目。
 *
 * 按 `lastActivityAt` 挑（不依赖项目排序方式 —— 那只是列表的排列）。
 * 挑不到就什么都不做：一条想法都没有时切过去也是空的。
 */
async function switchToMostRecentProject(): Promise<void> {
  const ranked = projects.value
    .map((project) => ({
      project,
      activity: project.lastActivityAt ? Date.parse(project.lastActivityAt) : Number.NEGATIVE_INFINITY,
    }))
    .sort((left, right) => right.activity - left.activity);
  const target = ranked[0]?.project;
  if (!target) {
    return;
  }
  setCurrentProject(target.id);
  filter.value = { ...filter.value, projectId: target.id };
  try {
    await loadIdeas(target.id);
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
 *
 * @returns true 表示存好了（输入框据此清空），false 表示失败（内容保留在框里）。
 */
async function onSubmit(content: string): Promise<boolean> {
  try {
    const created = await apiSend<Idea>('POST', '/ideas', {
      projectId: currentProjectId.value,
      content,
      title: null,
      tags: [],
    });
    applyLocalUpsert(created);
    return true;
  } catch (error) {
    reportError(error, '保存失败');
    return false;
  }
}

/**
 * 切换完成态。
 *
 * 先落库再更新本地：接口返回的想法才是最新的（`updatedAt` 由后端给），
 * 本地不自己猜。失败时本地状态不动，条目还是原来的样子。
 */
async function onToggleDone(id: string): Promise<void> {
  const idea = ideas.value.find((item) => item.id === id);
  if (!idea) {
    return;
  }
  try {
    const updated = await apiSend<Idea>('POST', `/ideas/${id}/complete`, { done: !idea.done });
    applyLocalUpsert(updated);
  } catch (error) {
    reportError(error, '更新完成状态失败');
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
      @toggle-done="onToggleDone"
    />

    <Composer :on-submit="onSubmit" />
  </main>
</template>

<style scoped>
.home {
  display: flex;
  flex-direction: column;
  gap: 12px;
  /* 宽度约束留给 F7 的两版布局（侧栏 + 内容），这里不写死居中容器。 */
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
