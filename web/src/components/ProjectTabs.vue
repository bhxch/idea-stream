<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { Project } from '../api/types';
import type { ProjectSortMode } from '../stores/projects';

/**
 * 窄屏（<1024px）的项目 Tab 条。
 *
 * 宽屏用 {@link ProjectSidebar} 显示同一份数据 —— 两个组件渲染的是同一批项目，
 * 页面层用 CSS 媒体查询让它们互斥显示（同一份数据渲染两次，断点两侧各露一个）。
 */
const props = defineProps<{
  /** 项目列表（顺序由后端按当前排序方式给，前端不重排）。 */
  projects: Project[];
  /** 当前选中的项目；`null` 表示「全部项目」。 */
  currentId: string | null;
  /** 当前的项目排序方式（Tab 条上只作提示，不在这里切换）。 */
  mode: ProjectSortMode;
}>();

const emit = defineEmits<{
  /** 选了某个项目（`null` 表示「全部项目」）。 */
  'select': [id: string | null];
  /** 要新建项目。 */
  'new-project': [];
  /** 要打开项目的更多操作（改名、删除等）。 */
  'open-menu': [];
}>();

/** 「全部」的条数：各项目条数之和。 */
const totalCount = computed(() => props.projects.reduce((sum, project) => sum + project.ideaCount, 0));

/** Tab 条的滚动容器（横向溢出时右边缘加渐隐）。 */
const listEl = ref<HTMLElement | null>(null);

/** 右边还有 Tab 没露出来。 */
const scrollable = ref(false);

/** 已经滑到最右：右边没有内容可提示，不用渐隐。 */
const atEnd = ref(true);

/**
 * 量一下 Tab 条横向溢出了没有。
 *
 * 溢出了才加渐隐：没溢出时也渐隐，最后一个胶囊的右边框会被抹掉，
 * 反而像是边框画坏了。
 */
function measureOverflow(): void {
  const el = listEl.value;
  if (!el) {
    scrollable.value = false;
    atEnd.value = true;
    return;
  }
  scrollable.value = el.scrollWidth - el.clientWidth > 1;
  atEnd.value = el.scrollLeft + el.clientWidth >= el.scrollWidth - 1;
}

/** 该渐隐的时候才渐隐。 */
const fadeRight = computed(() => scrollable.value && !atEnd.value);

let observer: ResizeObserver | null = null;

onMounted(() => {
  measureOverflow();
  const el = listEl.value;
  if (!el) {
    return;
  }
  el.addEventListener('scroll', measureOverflow);
  window.addEventListener('resize', measureOverflow);
  // 容器宽度变了（不一定是窗口变了）：重量一次。
  // jsdom 之类没有 ResizeObserver 的环境里跳过。
  if (typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(measureOverflow);
    observer.observe(el);
  }
});

onBeforeUnmount(() => {
  listEl.value?.removeEventListener('scroll', measureOverflow);
  window.removeEventListener('resize', measureOverflow);
  observer?.disconnect();
  observer = null;
});

// 项目改名、条数变了，Tab 的宽度跟着变：重量一次。
watch(
  () => props.projects,
  () => {
    void nextTick(measureOverflow);
  },
  { deep: true },
);
</script>

<template>
  <nav class="project-tabs" :title="mode === 'recent' ? '按最近活动排序' : '按手动顺序排序'">
    <!-- 项目多的时候横向滑动，不换行：换行会把内容区挤没。 -->
    <ul ref="listEl" class="project-tabs__list" :class="{ 'is-scrollable': fadeRight }">
      <li>
        <button
          class="project-tab"
          :class="{ 'is-active': currentId === null }"
          type="button"
          data-project-id="all"
          @click="emit('select', null)"
        >
          <span class="project-tab__name">全部</span>
          <span class="project-tab__count">{{ totalCount }}</span>
        </button>
      </li>

      <li v-for="project in projects" :key="project.id">
        <button
          class="project-tab"
          :class="{ 'is-active': currentId === project.id }"
          type="button"
          :data-project-id="project.id"
          @click="emit('select', project.id)"
        >
          <span class="project-tab__name">{{ project.name }}</span>
          <span class="project-tab__count">{{ project.ideaCount }}</span>
        </button>
      </li>
    </ul>

    <div class="project-tabs__actions">
      <button class="project-tabs__new" type="button" title="新建项目" @click="emit('new-project')">
        ＋
      </button>
      <button class="project-tabs__menu" type="button" title="项目的更多操作" @click="emit('open-menu')">
        ⋯
      </button>
    </div>
  </nav>
</template>

<style scoped>
.project-tabs {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.project-tabs__list {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1;
  min-width: 0;
  margin: 0;
  padding: 0 0 4px;
  overflow-x: auto;
  list-style: none;
  /* 横向滑动时别让最后一项贴边被切掉。 */
  scrollbar-width: thin;
}

/*
 * 右边还有 Tab 时，右边缘渐隐。
 *
 * 之前是硬切：最后一个胶囊的圆角描边被直直切断，跟「＋」按钮的左边框连成一片，
 * 看着像边框画坏了。渐隐之后是「右边还有内容，滑过去看」的意思。
 */
.project-tabs__list.is-scrollable {
  -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 16px), transparent 100%);
  mask-image: linear-gradient(to right, #000 calc(100% - 16px), transparent 100%);
}

.project-tab {
  display: flex;
  align-items: baseline;
  gap: 4px;
  flex: none;
  padding: 3px 10px;
  border-radius: 999px;
  white-space: nowrap;
}

.project-tab.is-active {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--accent-fg);
}

.project-tab__name {
  overflow-wrap: anywhere;
}

.project-tab__count {
  color: var(--muted);
  font-size: 12px;
}

.project-tab.is-active .project-tab__count {
  color: var(--accent-fg);
}

.project-tabs__actions {
  display: flex;
  flex: none;
  gap: 4px;
}

.project-tabs__new,
.project-tabs__menu {
  padding: 2px 8px;
  color: var(--muted);
}

/*
 * 手机上点按目标不小于 32×32。
 *
 * 这两个是纯符号按钮（＋ / ⋯），只有一行字高（30px），手指点不中 ——
 * 窄屏上把内边距加高一点。
 */
@media (max-width: 640px) {
  .project-tabs__new,
  .project-tabs__menu {
    padding: 4px 10px;
  }
}
</style>
