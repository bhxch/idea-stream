<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { apiSend } from '../api/client';
import type { Project } from '../api/types';
import PageNav from '../components/PageNav.vue';
import { showToast } from '../components/ToastHost.vue';
import { reportError } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';
import { reorderToTarget } from '../lib/reorder';
import {
  loadProjects,
  loadSortMode,
  projectSortMode,
  projects,
  saveSortMode,
} from '../stores/projects';
import type { ProjectSortMode } from '../stores/projects';

/** 新建项目的输入框。 */
const newName = ref('');

/** 正在改名的项目 id（`null` 表示没在改名）。 */
const editingId = ref<string | null>(null);

/** 改名输入框里的草稿。 */
const renameDraft = ref('');

/** 等着确认删除的项目 id（`null` 表示没在确认）。 */
const confirmingId = ref<string | null>(null);

/** 正在被拖动的项目 id。 */
const draggingId = ref<string | null>(null);

/** 排序方式切换请求在飞（防重复点击）。 */
const switchingMode = ref(false);

/**
 * 能不能拖。
 *
 * 只有「手动排序」模式下顺序才由用户定 —— 「最近活动」模式下顺序是后端按
 * 最近活动时间算的，拖了也没有意义。
 */
function canDrag(): boolean {
  return projectSortMode.value === 'manual';
}

/** 首屏：项目列表与排序方式。两件事互不依赖，各自兜底。 */
async function init(): Promise<void> {
  const [projectsResult, sortModeResult] = await Promise.allSettled([
    loadProjects(),
    loadSortMode(),
  ]);
  if (projectsResult.status === 'rejected') {
    reportError(projectsResult.reason, '加载项目失败');
  }
  if (sortModeResult.status === 'rejected') {
    reportError(sortModeResult.reason, '加载排序方式失败');
  }
}

/** 新建项目。 */
async function onCreate(): Promise<void> {
  const name = newName.value.trim();
  if (!name) {
    showToast('项目名称不能为空', 'error');
    return;
  }
  try {
    await apiSend<Project>('POST', '/projects', { name });
    newName.value = '';
    // 重新拉一次：后端给的顺序（尤其是手动排序下的 sort_order）才是真的。
    await loadProjects();
  } catch (error) {
    reportError(error, '新建项目失败');
  }
}

/** 进入改名态。 */
function startRename(project: Project): void {
  editingId.value = project.id;
  renameDraft.value = project.name;
}

/** 放弃改名。 */
function cancelRename(): void {
  editingId.value = null;
  renameDraft.value = '';
}

/** 保存改名。 */
async function onSaveRename(project: Project): Promise<void> {
  const name = renameDraft.value.trim();
  if (!name) {
    showToast('项目名称不能为空', 'error');
    return;
  }
  try {
    const updated = await apiSend<Project>('PATCH', `/projects/${project.id}`, { name });
    // 用后端返回的项目覆盖本地（名字是后端规范化过的）。
    projects.value = projects.value.map((item) => (item.id === updated.id ? updated : item));
    cancelRename();
  } catch (error) {
    reportError(error, '改名失败');
  }
}

/** 问一句再删（与主页面上删想法的交互一致）。 */
function askDelete(project: Project): void {
  confirmingId.value = project.id;
}

/** 不删了。 */
function cancelDelete(): void {
  confirmingId.value = null;
}

/**
 * 确认删除（项目进回收站，它下面的想法也一起进）。
 *
 * 失败时确认条**留着**：内置收件箱删不掉（后端返回 400 `inbox_not_deletable`），
 * 用户看到提示后还能取消或再试。
 */
async function onDelete(project: Project): Promise<void> {
  try {
    await apiSend<void>('DELETE', `/projects/${project.id}`);
    confirmingId.value = null;
    await loadProjects();
  } catch (error) {
    reportError(error, '删除项目失败');
  }
}

/** 开始拖动。 */
function onDragStart(project: Project, event: DragEvent): void {
  if (!canDrag()) {
    event.preventDefault();
    return;
  }
  draggingId.value = project.id;
  // dataTransfer 在部分环境里没有（jsdom 就没有），取不到不影响拖拽：
  // 拖动中的 id 记在 draggingId 里。
  event.dataTransfer?.setData('text/plain', project.id);
}

/**
 * 拖到某个项目上：放下时按落点重排。
 *
 * 落点语义见 {@link reorderToTarget} —— 项目侧栏用的是同一份实现，
 * 同一个动作在两个界面里结果必须一样。
 */
async function onDrop(target: Project, event: DragEvent): Promise<void> {
  event.preventDefault();
  const sourceId = draggingId.value ?? event.dataTransfer?.getData('text/plain') ?? '';
  draggingId.value = null;
  if (!canDrag() || !sourceId || sourceId === target.id) {
    return;
  }
  const ids = projects.value.map((project) => project.id);
  const next = reorderToTarget(ids, sourceId, target.id);
  if (next.every((id, index) => id === ids[index])) {
    // 顺序没变：不用发请求（后端会白白重写一遍 sort_order）。
    return;
  }
  try {
    // 用后端返回的列表覆盖本地：重写全表 sort_order 之后，返回的顺序才是真的。
    projects.value = await apiSend<Project[]>('POST', '/projects/reorder', { ids: next });
  } catch (error) {
    reportError(error, '重排项目失败');
    // 拉回来：本地显示的顺序可能已经跟后端对不上了。
    try {
      await loadProjects();
    } catch (reloadError) {
      reportError(reloadError, '加载项目失败');
    }
  }
}

/** 拖完了（不管落在哪）。 */
function onDragEnd(): void {
  draggingId.value = null;
}

/**
 * 切换排序方式。
 *
 * **只调设置**：初始化 `sort_order` 是后端的事 ——
 * `ProjectService.SetSortMode` 切到 `manual` 时会自己用当前显示顺序
 * （此刻 `List()` 给出的顺序，也就是用户正看到的顺序）重排一遍，
 * 再写设置，所以切过去后顺序与切换前一致，不会跳位。
 *
 * 前端**不要**自己再发一次 `POST /projects/reorder`：它用的是本地缓存的
 * 项目列表，别的设备新建的项目不在里面，而后端的 `Reorder` 规定
 * 「没被列出的接在后面」—— 那会把顺序真的改坏。
 */
async function onChangeMode(next: ProjectSortMode): Promise<void> {
  if (next === projectSortMode.value || switchingMode.value) {
    return;
  }
  switchingMode.value = true;
  try {
    await saveSortMode(next);
    // 两种方式下后端给的顺序不一样：重新拉一次。
    await loadProjects();
  } catch (error) {
    reportError(error, '切换排序方式失败');
  } finally {
    switchingMode.value = false;
  }
}

onMounted(() => {
  void init();
});
</script>

<template>
  <main class="page">
    <PageNav />

    <header class="projects__head">
      <h1>项目管理</h1>
      <div class="projects__modes" role="group" aria-label="排序方式">
        <button
          class="projects__mode projects__mode--recent"
          :class="{ 'is-active': projectSortMode === 'recent' }"
          type="button"
          :disabled="switchingMode"
          @click="onChangeMode('recent')"
        >
          最近活动
        </button>
        <button
          class="projects__mode projects__mode--manual"
          :class="{ 'is-active': projectSortMode === 'manual' }"
          type="button"
          :disabled="switchingMode"
          @click="onChangeMode('manual')"
        >
          手动排序
        </button>
      </div>
    </header>

    <form class="projects__new" @submit.prevent="onCreate">
      <input
        class="projects__new-input"
        v-model="newName"
        type="text"
        placeholder="新项目名称"
        aria-label="新项目名称"
      />
      <button class="projects__new-submit" type="submit">新建项目</button>
    </form>

    <p class="projects__hint">
      手动排序模式下可以直接拖动项目：往下拖落到目标的后面，往上拖落到目标的前面。
    </p>

    <ul class="projects__list">
      <li
        v-for="project in projects"
        :key="project.id"
        class="projects__item"
        :class="{ 'is-dragging': draggingId === project.id, 'is-draggable': canDrag() }"
        :draggable="canDrag()"
        :data-project-id="project.id"
        @dragstart="onDragStart(project, $event)"
        @dragover.prevent
        @drop="onDrop(project, $event)"
        @dragend="onDragEnd"
      >
        <div class="projects__row">
          <input
            v-if="editingId === project.id"
            class="projects__rename"
            v-model="renameDraft"
            type="text"
            aria-label="项目名称"
            @keydown.enter="onSaveRename(project)"
          />
          <span v-else class="projects__name">{{ project.name }}</span>
          <span class="projects__count">{{ project.ideaCount }} 条想法</span>
          <span v-if="project.lastActivityAt" class="projects__activity">
            {{ formatRelativeTime(project.lastActivityAt) }}
          </span>
          <!--
            两个操作按钮是一组：窄屏换行时不能被拆开（「改名」留在上一行、
            「删除」孤零零掉到下一行左下角）。
          -->
          <div class="projects__actions">
            <button
              class="projects__edit"
              type="button"
              :disabled="editingId !== null && editingId !== project.id"
              @click="startRename(project)"
            >
              改名
            </button>
            <button class="projects__delete" type="button" @click="askDelete(project)">删除</button>
          </div>
        </div>

        <div v-if="editingId === project.id" class="projects__bar">
          <button class="projects__save-rename" type="button" @click="onSaveRename(project)">
            保存
          </button>
          <button class="projects__cancel-rename" type="button" @click="cancelRename">取消</button>
        </div>

        <div v-if="confirmingId === project.id" class="projects__bar projects__bar--danger">
          <span class="projects__confirm-text">删除后进回收站（它下面的想法也一起进）。</span>
          <button class="projects__confirm-yes" type="button" @click="onDelete(project)">
            确认删除
          </button>
          <button class="projects__confirm-no" type="button" @click="cancelDelete">取消</button>
        </div>
      </li>
    </ul>

    <p v-if="projects.length === 0" class="projects__empty">还没有项目。</p>
  </main>
</template>

<style scoped>
.projects__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.projects__head h1 {
  margin: 0;
  font-size: 20px;
}

.projects__modes {
  display: flex;
  gap: 6px;
}

.projects__mode.is-active {
  border-color: var(--accent);
  color: var(--accent);
}

.projects__new {
  display: flex;
  gap: 8px;
  margin-top: 12px;
}

.projects__new-input {
  flex: 1 1 auto;
  min-width: 0;
}

.projects__hint,
.projects__empty {
  color: var(--muted);
  font-size: 13px;
}

.projects__list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 12px 0 0;
  padding: 0;
  list-style: none;
}

.projects__item {
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.projects__item.is-draggable {
  cursor: grab;
}

.projects__item.is-dragging {
  opacity: 0.5;
}

.projects__row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex-wrap: wrap;
}

/*
 * 「改名」「删除」一组：换行时一起走、靠右。
 *
 * 之前两个按钮是行里的两个独立元素，窄屏下会被 flex 拆到两行 ——
 * 「删除」这个破坏性按钮孤零零落在条目左下角。
 */
.projects__actions {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex: none;
  margin-left: auto;
}

.projects__name {
  flex: 1 1 auto;
  min-width: 0;
  overflow-wrap: anywhere;
}

.projects__rename {
  flex: 1 1 auto;
  min-width: 0;
}

.projects__count,
.projects__activity {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.projects__delete {
  color: var(--danger);
}

.projects__bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
}

.projects__bar--danger {
  padding: 6px 8px;
  border: 1px solid var(--danger);
  border-radius: 6px;
}

.projects__confirm-text {
  flex: 1;
  font-size: 12px;
}
</style>
