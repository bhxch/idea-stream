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

/** 渐隐带宽度（px）：只淡「右边被内容顶出去」的那一小段。 */
const fadeWidth = ref(0);

/** 渐隐带宽度上限（px）：再宽就盖住太多东西了。 */
const MAX_FADE_WIDTH = 16;

/**
 * 元素里的文本节点，从右往左（文档顺序的倒序）。
 */
function textNodesFromRight(el: HTMLElement): Text[] {
  if (typeof document.createTreeWalker !== 'function') {
    return [];
  }
  const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
  const nodes: Text[] = [];
  let node = walker.nextNode();
  while (node) {
    nodes.push(node as Text);
    node = walker.nextNode();
  }
  return nodes.reverse();
}

/**
 * 量渐隐带该有多宽（px）。
 *
 * 原则：完整露出来的字一个字都不淡。
 * 淡过的字对比度会掉 —— 实测把还看得清的那个「0」从 5.3:1 淡到 3.5:1，
 * 低于 AA 对正文文字要的 4.5:1。固定 16px 的带子在只溢出 9px 时，
 * 会把整块还在的字盖进去。
 *
 * 做法：从右往左找最后一个「完整露出」的字，
 * 渐隐带只盖住它右边的空隙，以及被切掉一半的那几个字。
 */
function measureFadeWidth(el: HTMLElement): number {
  const box = el.getBoundingClientRect();
  if (box.width === 0 && box.height === 0) {
    // 量不到（没有排版引擎的环境，比如 jsdom）：宁可不渐隐。
    return 0;
  }
  if (typeof document.createRange !== 'function') {
    return 0;
  }
  const range = document.createRange();
  if (typeof range.getBoundingClientRect !== 'function') {
    // jsdom 没有排版接口。
    return 0;
  }
  const right = box.right;
  for (const node of textNodesFromRight(el)) {
    const text = node.textContent ?? '';
    if (!text.trim()) {
      continue; // 空白文本节点
    }
    // 整段都在可视区右边（还没滚进来）或左边（滚过去了）：整段跳过，
    // 免得为一个滚不到的地方逐字去量（滚动时每次都要重量）。
    // 量不到（没有排版引擎的环境）就不跳 —— 交给下面逐字去量。
    const owner = node.parentElement?.getBoundingClientRect();
    if (owner && (owner.width || owner.height) && (owner.left >= right || owner.right <= box.left)) {
      continue;
    }
    for (let i = text.length - 1; i >= 0; i--) {
      range.setStart(node, i);
      range.setEnd(node, i + 1);
      const rect = range.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) {
        continue; // 空格之类，没有字形
      }
      if (rect.right > right) {
        continue; // 被切掉了（或者还没滚进来）：往前找
      }
      if (rect.right <= box.left) {
        return 0; // 已经滚出可视区了
      }
      return Math.min(MAX_FADE_WIDTH, Math.max(0, right - rect.right));
    }
  }
  return 0;
}

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
    fadeWidth.value = 0;
    return;
  }
  scrollable.value = el.scrollWidth - el.clientWidth > 1;
  atEnd.value = el.scrollLeft + el.clientWidth >= el.scrollWidth - 1;
  fadeWidth.value = scrollable.value ? measureFadeWidth(el) : 0;
}

/** 该渐隐的时候才渐隐。 */
const fadeRight = computed(() => scrollable.value && !atEnd.value);

/** 真的有一段要淡（量出来宽度了）才加遮罩：宽度 0 的遮罩会把整条内容淡没了。 */
const fading = computed(() => fadeRight.value && fadeWidth.value > 0);

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
  <nav class="project-tabs" aria-label="项目" :title="mode === 'recent' ? '按最近活动排序' : '按手动顺序排序'">
    <!--
      项目多的时候横向滑动，不换行：换行会把内容区挤没。
      is-scrollable：右边还有 Tab 没露出来；is-fading：右边缘真的在渐隐
      （渐隐带多宽由 JS 量出来，--tabs-fade）。
    -->
    <ul
      ref="listEl"
      class="project-tabs__list"
      :class="{ 'is-scrollable': fadeRight, 'is-fading': fading }"
      :style="{ '--tabs-fade': `${fadeWidth}px` }"
    >
      <li>
        <button
          class="project-tab"
          :class="{ 'is-active': currentId === null }"
          type="button"
          :aria-current="currentId === null ? 'page' : undefined"
          data-project-id="all"
          @click="emit('select', null)"
        >
          <span class="project-tab__name" data-testid="project-name-all">全部</span>
          <span class="project-tab__count">{{ totalCount }}</span>
        </button>
      </li>

      <li v-for="project in projects" :key="project.id">
        <button
          class="project-tab"
          :class="{ 'is-active': currentId === project.id }"
          type="button"
          :aria-current="currentId === project.id ? 'page' : undefined"
          :data-project-id="project.id"
          @click="emit('select', project.id)"
        >
          <span class="project-tab__name" :data-testid="`project-name-${project.id}`">{{ project.name }}</span>
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
 *
 * 渐隐带多宽由 JS 量出来（--tabs-fade），不是写死的 16px：
 * 写死 16px 时，只横向溢出 9px 也会淡掉 16px ——
 * 里面可能整块都是还看得清的字，淡完对比度掉到 3.5:1（AA 要 4.5:1）。
 * 现在只淡「右边被内容顶出去」的那一小段：空隙 + 被切掉一半的字。
 *
 * 宽度是 0 时不加遮罩：0 宽的渐变等于把整条内容淡没了。
 */
.project-tabs__list.is-fading {
  -webkit-mask-image: linear-gradient(to right, #000 calc(100% - var(--tabs-fade, 16px)), transparent 100%);
  mask-image: linear-gradient(to right, #000 calc(100% - var(--tabs-fade, 16px)), transparent 100%);
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
