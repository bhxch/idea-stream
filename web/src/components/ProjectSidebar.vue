<script setup lang="ts">
import { computed, ref } from 'vue';
import type { Project } from '../api/types';
import { formatRelativeTime } from '../lib/format';
import { reorderToTarget } from '../lib/reorder';
import type { ProjectSortMode } from '../stores/projects';

/**
 * 宽屏（≥1024px）的项目侧栏。
 *
 * 窄屏（<1024px）用顶栏里的 {@link ProjectDropdown} 显示同一份数据 ——
 * 两个组件渲染的是同一批项目，页面层用 CSS 媒体查询让它们互斥显示。
 */
const props = defineProps<{
  /** 项目列表（顺序由后端按当前排序方式给，前端不重排）。 */
  projects: Project[];
  /** 当前选中的项目；`null` 表示「全部项目」。 */
  currentId: string | null;
  /** 当前的项目排序方式。 */
  mode: ProjectSortMode;
}>();

const emit = defineEmits<{
  /** 选了某个项目（`null` 表示「全部项目」）。 */
  'select': [id: string | null];
  /** 要新建项目。 */
  'new-project': [];
  /** 拖拽重排完成，新的项目 id 顺序。 */
  'reorder': [ids: string[]];
  /** 切换项目排序方式。 */
  'change-mode': [mode: ProjectSortMode];
}>();

/** 「全部项目」的条数：各项目条数之和。 */
const totalCount = computed(() => props.projects.reduce((sum, project) => sum + project.ideaCount, 0));

/** 正在被拖动的项目 id。 */
const draggingId = ref<string | null>(null);

/**
 * 能不能拖。
 *
 * 只有「手动排序」模式下顺序才由用户定 —— 「最近活动」模式下顺序是后端按
 * 最近活动时间算的，拖了也没有意义（后端会按新顺序重写 sort_order，但列表
 * 仍按最近活动排）。
 */
function canDrag(): boolean {
  return props.mode === 'manual';
}

/** 开始拖动。 */
function onDragStart(id: string, event: DragEvent): void {
  if (!canDrag()) {
    event.preventDefault();
    return;
  }
  draggingId.value = id;
  // dataTransfer 在部分环境里没有（jsdom 就没有），取不到不影响拖拽：
  // 拖动中的 id 记在 draggingId 里。
  event.dataTransfer?.setData('text/plain', id);
}

/**
 * 拖到某个项目上：放下时按落点重排。
 *
 * 落点语义见 {@link reorderToTarget} —— 项目管理页用的是同一份实现。
 */
function onDrop(targetId: string, event: DragEvent): void {
  event.preventDefault();
  const sourceId = draggingId.value ?? event.dataTransfer?.getData('text/plain') ?? '';
  draggingId.value = null;
  if (!canDrag() || !sourceId || sourceId === targetId) {
    return;
  }
  const ids = props.projects.map((project) => project.id);
  const next = reorderToTarget(ids, sourceId, targetId);
  if (next.length !== ids.length || next.every((id, index) => id === ids[index])) {
    // 顺序没变：不用发请求（后端会白白重写一遍 sort_order）。
    return;
  }
  emit('reorder', next);
}

/** 拖完了（不管落在哪）。 */
function onDragEnd(): void {
  draggingId.value = null;
}

/** 切换排序方式。 */
function toggleMode(): void {
  emit('change-mode', props.mode === 'recent' ? 'manual' : 'recent');
}
</script>

<template>
  <nav class="project-sidebar" aria-label="项目">
    <div class="project-sidebar__head">
      <h2 class="project-sidebar__title">项目</h2>
      <button
        class="project-sidebar__mode"
        type="button"
        :title="mode === 'recent' ? '当前：按最近活动排序' : '当前：按手动顺序排序'"
        @click="toggleMode"
      >
        {{ mode === 'recent' ? '最近活动' : '手动排序' }}
      </button>
      <button class="project-sidebar__new" type="button" title="新建项目" @click="emit('new-project')">
        ＋
      </button>
    </div>

    <ul class="project-sidebar__list">
      <li>
        <button
          class="project-sidebar__item"
          :class="{ 'is-active': currentId === null }"
          type="button"
          :aria-current="currentId === null ? 'page' : undefined"
          data-project-id="all"
          @click="emit('select', null)"
        >
          <span class="project-sidebar__name" data-testid="project-sidebar-name-all">全部项目</span>
          <span class="project-sidebar__count">{{ totalCount }}</span>
        </button>
      </li>

      <li
        v-for="project in projects"
        :key="project.id"
        class="project-sidebar__row"
        :class="{ 'is-dragging': draggingId === project.id }"
      >
        <button
          class="project-sidebar__item"
          :class="{ 'is-active': currentId === project.id, 'is-draggable': canDrag() }"
          type="button"
          :aria-current="currentId === project.id ? 'page' : undefined"
          :draggable="canDrag()"
          :data-project-id="project.id"
          @dragstart="onDragStart(project.id, $event)"
          @dragover.prevent
          @drop="onDrop(project.id, $event)"
          @dragend="onDragEnd"
          @click="emit('select', project.id)"
        >
          <span class="project-sidebar__name" :data-testid="`project-sidebar-name-${project.id}`">{{ project.name }}</span>
          <span class="project-sidebar__count">{{ project.ideaCount }}</span>
          <span v-if="project.lastActivityAt" class="project-sidebar__activity">
            {{ formatRelativeTime(project.lastActivityAt) }}
          </span>
        </button>
      </li>
    </ul>
  </nav>
</template>

<style scoped>
.project-sidebar {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  overflow-y: auto;
}

.project-sidebar__head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.project-sidebar__title {
  flex: 1;
  margin: 0;
  font-size: 15px;
}

.project-sidebar__mode,
.project-sidebar__new {
  padding: 1px 8px;
  font-size: 12px;
  color: var(--muted);
}

.project-sidebar__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.project-sidebar__row.is-dragging {
  opacity: 0.5;
}

.project-sidebar__item {
  display: flex;
  align-items: baseline;
  gap: 6px;
  width: 100%;
  padding: 4px 8px;
  border: none;
  border-radius: 6px;
  text-align: left;
}

.project-sidebar__item:hover {
  background: var(--chip-bg);
}

.project-sidebar__item.is-active {
  background: var(--accent);
  color: var(--accent-fg);
}

.project-sidebar__name {
  flex: 1;
  overflow-wrap: anywhere;
}

.project-sidebar__count {
  color: var(--muted);
  font-size: 12px;
  /* 等宽数字：条数是用户判断「这个项目里有什么」的依据，要能上下对齐着比。 */
  font-variant-numeric: tabular-nums;
}

.project-sidebar__item.is-active .project-sidebar__count,
.project-sidebar__item.is-active .project-sidebar__activity {
  color: var(--accent-fg);
}

.project-sidebar__activity {
  color: var(--muted);
  font-size: 11px;
  white-space: nowrap;
}
</style>
