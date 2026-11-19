<script setup lang="ts">
import { computed, ref } from 'vue';
import type { Project } from '../api/types';
import { useDropdown } from '../composables/useDropdown';

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

/** 组件根节点：用来判断「点的是不是组件内部」。 */
const root = ref<HTMLElement | null>(null);

/**
 * 下拉面板（展开状态 + 点外面收起 / Esc 收起）。
 *
 * 这段逻辑原先在三个组件里各抄了一份，收敛到 {@link useDropdown} 里。
 */
const { open } = useDropdown(root);

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
  /* 触发器不许比容器宽：名字太长时靠省略号截断，不把顶栏撑破。 */
  max-width: 100%;
}

/*
 * 触发器：项目名可能被截断。
 *
 * 三件要一起：触发器自己是 inline-flex 且不许比容器宽（`max-width: 100%`），
 * 溢出部分由它 `overflow: hidden` 裁掉；名字那一段（.project-dropdown__current）
 * 必须是**块级盒** —— 行内盒上 overflow / text-overflow 不生效，
 * 只写 text-overflow 是白写（名字会一路顶到 🔍 与 ⋯ 上，甚至顶出视口）。
 */
.project-dropdown__trigger {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  overflow: hidden;
}

.project-dropdown__current {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.project-dropdown__caret {
  /* 箭头不许被挤掉：它是「这里能下拉」的提示。 */
  flex: none;
  color: var(--muted);
  font-size: 11px;
}

/*
 * 面板：浮在触发器下面的一层。
 *
 * 面板样式照 TagFilter 的面板（CSS 层面）：绝对定位、边框、底色、阴影用同一套。
 * （「点外面收起 / Esc 收起」那段 JS 逻辑与 TagFilter 逐字相同 ——
 *  Task 13 会连同 TagFilter 一起收敛成 web/src/composables/useDropdown.ts。）
 */
.project-dropdown__panel {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 20;
  display: flex;
  flex-direction: column;
  gap: 4px;
  /*
   * 面板宽度的上限：min-width 给它一个「至少这么宽」的底线，
   * 而它自己的宽度是「内容宽」与「能放下的宽度」里小的那个 ——
   * 项目名很长时这一项会换行（见 .project-dropdown__item），
   * 所以面板不会一路撑到视口外面去。
   */
  min-width: 180px;
  max-width: calc(100vw - 24px);
  max-height: 260px;
  overflow-y: auto;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.project-dropdown__item {
  /*
   * 项目名不截断，而是**换行**：面板里要能看清完整的名字
   * （触发器上那个名字被截断了，这里是它唯一的全文出口）。
   * `overflow-wrap: anywhere` 让没有空格的长串也能断行。
   */
  display: block;
  width: 100%;
  padding: 4px 8px;
  border: none;
  border-radius: 6px;
  text-align: left;
  overflow-wrap: anywhere;
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
