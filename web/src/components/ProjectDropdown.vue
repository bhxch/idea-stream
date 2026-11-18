<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { Project } from '../api/types';

const props = defineProps<{
  /** 项目列表（顺序由后端给，前端不重排）。 */
  projects: Project[];
  /** 当前选中的项目；null 表示「全部项目」。 */
  currentId: string | null;
}>();

const emit = defineEmits<{
  /** 选了某个项目（null = 全部项目）。 */
  select: [id: string | null];
}>();

/** 下拉面板是否展开。 */
const open = ref(false);

/** 组件根节点：用来判断「点的是不是组件内部」。 */
const root = ref<HTMLElement | null>(null);

/**
 * 当前项目的名字。
 *
 * 找不到（项目刚被删掉、或列表还没拉到）就当「全部项目」——
 * 触发器上总得有个名字，不能空着。
 */
const currentName = computed(
  () => props.projects.find((project) => project.id === props.currentId)?.name ?? '全部项目',
);

/** 选一个项目：报上去，顺手收起面板。 */
function choose(id: string | null): void {
  emit('select', id);
  open.value = false;
}

/**
 * 点组件外面 → 收起面板。
 *
 * 面板是浮在触发器下面的一层，不点它就不消失的话，
 * 用户会以为「点了没反应」，只能刷新页面。
 *
 * 点在组件内部（触发器本身、面板里的项）不算「外面」——
 * 触发器的 click 会冒泡到 document，靠这条判断才不会「刚打开就被关掉」。
 */
function onDocumentClick(event: MouseEvent): void {
  if (!open.value) {
    return;
  }
  const target = event.target;
  if (target instanceof Node && root.value?.contains(target)) {
    return;
  }
  open.value = false;
}

/** Esc → 收起面板。 */
function onDocumentKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    open.value = false;
  }
}

onMounted(() => {
  document.addEventListener('click', onDocumentClick);
  document.addEventListener('keydown', onDocumentKeydown);
});

onBeforeUnmount(() => {
  document.removeEventListener('click', onDocumentClick);
  document.removeEventListener('keydown', onDocumentKeydown);
});
</script>

<template>
  <div ref="root" class="project-dropdown">
    <button
      class="project-dropdown__trigger"
      type="button"
      :aria-expanded="open"
      aria-haspopup="true"
      @click="open = !open"
    >
      <span class="project-dropdown__current">{{ currentName }}</span>
      <span class="project-dropdown__caret" aria-hidden="true">▾</span>
    </button>

    <div v-if="open" class="project-dropdown__panel">
      <!-- 第一项永远是「全部项目」：不选项目也是一种选择，得能选回去。 -->
      <button
        class="project-dropdown__item"
        type="button"
        data-project-id="all"
        @click="choose(null)"
      >
        全部项目
      </button>

      <button
        v-for="project in projects"
        :key="project.id"
        class="project-dropdown__item"
        type="button"
        :data-project-id="project.id"
        @click="choose(project.id)"
      >
        {{ project.name }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.project-dropdown {
  position: relative;
}

.project-dropdown__current {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.project-dropdown__caret {
  color: var(--muted);
  font-size: 11px;
}

/*
 * 面板：浮在触发器下面的一层。
 *
 * 做法照 TagFilter 的面板（同一类东西，别写出两套）。
 */
.project-dropdown__panel {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 20;
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 180px;
  max-height: 260px;
  overflow-y: auto;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.project-dropdown__item {
  display: flex;
  align-items: center;
  width: 100%;
  padding: 4px 8px;
  border: none;
  border-radius: 6px;
  text-align: left;
  white-space: nowrap;
}

.project-dropdown__item:hover {
  background: var(--chip-bg);
}

/*
 * 手机上点按目标不小于 32×32。
 *
 * 触发器只有一行字高，手指点不中 —— 窄屏上把它撑到 32px 高。
 */
@media (max-width: 767px) {
  .project-dropdown__trigger {
    min-width: 32px;
    min-height: 32px;
  }
}
</style>
