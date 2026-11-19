<script setup lang="ts">
import { computed, ref } from 'vue';
import type { Tag } from '../api/types';
import { useDropdown } from '../composables/useDropdown';
import { hasActiveFilter, type IdeaFilter, type StatusFilter } from '../lib/filter';
import TagFilter from './TagFilter.vue';

const props = defineProps<{
  /** 当前过滤条件。 */
  filter: IdeaFilter;
  /** 全部标签（标签多选下拉的选项）。 */
  allTags: Tag[];
}>();

const emit = defineEmits<{
  /** 过滤条件变了（整体替换，调用方自己决定要不要存）。 */
  'update:filter': [filter: IdeaFilter];
}>();

/** 状态三态：全部 / 未完成 / 已完成。 */
const STATUS_OPTIONS: { value: StatusFilter; label: string }[] = [
  { value: 'all', label: '全部' },
  { value: 'open', label: '未完成' },
  { value: 'done', label: '已完成' },
];

/**
 * 状态下拉的根节点：用来判断「点的是不是组件内部」。
 *
 * 绑在状态组那个容器上（触发器与面板都在它里面）——
 * 点面板里的项不该把面板关掉。
 */
const statusRoot = ref<HTMLElement | null>(null);

/**
 * 状态下拉（窄屏形态）：点外面收起 / Esc 收起。
 *
 * 与 TagFilter 用的是同一个 {@link useDropdown} —— 这套逻辑原先在三个组件里
 * 各抄了一份，再抄第四份就是四处各改各的（漏一处就是一个组件关不掉面板）。
 */
const { open: statusOpen } = useDropdown(statusRoot);

/** 搜索框本身（窄屏顶栏的 🔍 要聚焦它）。 */
const searchInput = ref<HTMLInputElement | null>(null);

/** 当前关键词（没给这个字段时按空处理 = 不过滤）。 */
const keyword = computed(() => props.filter.keyword ?? '');

/** 有没有过滤条件生效（决定「清除」按钮出不出现）。 */
const filtered = computed(() => hasActiveFilter(props.filter));

/** 当前状态的文字（窄屏的下拉触发器上显示它）。 */
const currentStatusLabel = computed(
  () => STATUS_OPTIONS.find((option) => option.value === props.filter.status)?.label ?? '全部',
);

function setStatus(status: StatusFilter): void {
  emit('update:filter', { ...props.filter, status });
}

/** 选状态下拉里的一项：改状态，顺手收起面板。 */
function pickStatus(status: StatusFilter): void {
  setStatus(status);
  statusOpen.value = false;
}

/**
 * 关键词变了：输入即报上去。
 *
 * 过滤在**服务端**做（设计文档 §11.1）：这里只负责把输入报上去，
 * 防抖在「重拉」的入口（HomePage）—— 放在这里的话输入框的手感会变迟钝。
 */
function onKeywordInput(event: Event): void {
  const target = event.target;
  if (target instanceof HTMLInputElement) {
    emit('update:filter', { ...props.filter, keyword: target.value });
  }
}

/**
 * 一键清除：状态、标签、关键词一起清掉。
 *
 * 之前想清掉筛选只能点开标签下拉取消勾选、再点状态组的「全部」——
 * 界面上没有一个能一次清空的入口。
 *
 * 项目不在这里清：它不是「过滤条件」，是列表的范围（由侧栏/Tab 选）。
 */
function clearFilters(): void {
  emit('update:filter', { ...props.filter, status: 'all', tags: [], keyword: '' });
}

defineExpose({
  /** 把焦点放进搜索框（窄屏顶栏的 🔍 用它）。 */
  focusSearch: () => searchInput.value?.focus(),
});
</script>

<template>
  <div class="filter-bar">
    <div ref="statusRoot" class="filter-bar__status" role="group" aria-label="状态过滤">
      <!--
        宽屏形态：三个分段按钮。
        ⚠️ 这个 role="group" 的容器**一路都要显示**（窄屏时它包着那个下拉触发器）：
        布局 e2e 的关键元素检查找的就是这个 group（getByRole('group', { name: '状态过滤' })），
        容器整个 display:none 的话，窄屏那一档必红 —— 隐藏的是它**里面**的分段按钮。
      -->
      <div class="filter-bar__status-segments">
        <button
          v-for="option in STATUS_OPTIONS"
          :key="option.value"
          class="filter-bar__status-button"
          type="button"
          :aria-pressed="filter.status === option.value"
          :class="{ 'is-active': filter.status === option.value }"
          @click="setStatus(option.value)"
        >
          {{ option.label }}
        </button>
      </div>

      <!--
        窄屏形态：状态三态收成一个下拉（面板样式照 TagFilter 的面板）。
        宽屏下这个按钮不显示（display:none），形态切换全靠 CSS。
      -->
      <button
        class="filter-bar__status-trigger"
        type="button"
        :aria-expanded="statusOpen"
        aria-haspopup="true"
        @click="statusOpen = !statusOpen"
      >
        {{ currentStatusLabel }}<span class="filter-bar__status-caret" aria-hidden="true">▾</span>
      </button>

      <div v-if="statusOpen" class="filter-bar__status-panel">
        <button
          v-for="option in STATUS_OPTIONS"
          :key="option.value"
          class="filter-bar__status-option"
          type="button"
          :aria-pressed="filter.status === option.value"
          @click="pickStatus(option.value)"
        >
          {{ option.label }}
        </button>
      </div>
    </div>

    <!--
      关键词过滤：过滤在服务端做（设计文档 §11.1），这里只负责把输入报上去
      （防抖在「重拉」的入口）。空串 = 不过滤。
    -->
    <input
      ref="searchInput"
      class="filter-bar__search"
      type="search"
      aria-label="搜索想法"
      placeholder="搜索想法…"
      :value="keyword"
      @input="onKeywordInput"
    />

    <TagFilter
      :all-tags="allTags"
      :selected="filter.tags"
      :mode="filter.tagMode"
      @update:selected="emit('update:filter', { ...filter, tags: $event })"
      @update:mode="emit('update:filter', { ...filter, tagMode: $event })"
    />

    <button
      v-if="filtered"
      class="filter-bar__clear"
      type="button"
      @click="clearFilters"
    >
      清除
    </button>
  </div>
</template>

<style scoped>
.filter-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

/*
 * 状态组容器：一路显示。
 * 宽屏时它包着三个分段按钮，窄屏时它包着那个下拉触发器 ——
 * 窄屏形态不能把这个容器整个藏起来（e2e 的关键元素检查找的就是它）。
 */
.filter-bar__status {
  position: relative;
  display: flex;
  gap: 4px;
}

/* 宽屏：三个分段按钮。 */
.filter-bar__status-segments {
  display: flex;
  gap: 4px;
}

.filter-bar__status-button.is-active {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--accent-fg);
}

/* 窄屏才出现的下拉触发器（形态切换全靠 CSS，两套形态都在 DOM 里）。 */
.filter-bar__status-trigger {
  display: none;
  align-items: center;
  gap: 4px;
}

.filter-bar__status-caret {
  color: var(--muted);
  font-size: 11px;
}

/*
 * 状态下拉的面板：浮在触发器下面的一层。
 *
 * 样式照 TagFilter 的面板（绝对定位、边框、底色、阴影用同一套），
 * 只有宽度窄一点：这里只有三个短选项，不必留那么宽。
 */
.filter-bar__status-panel {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 20;
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 120px;
  max-height: 260px;
  overflow-y: auto;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.filter-bar__status-option {
  display: block;
  width: 100%;
  padding: 4px 8px;
  border: none;
  border-radius: 6px;
  text-align: left;
  white-space: nowrap;
}

.filter-bar__status-option:hover {
  background: var(--chip-bg);
}

.filter-bar__search {
  width: 160px;
  /* 窄屏上别把过滤栏撑出横向滚动条（输入框有固有的最小宽度）。 */
  min-width: 0;
  max-width: 100%;
}

.filter-bar__clear {
  color: var(--muted);
}

/*
 * 窄屏（≤767px）：状态三态收成一个下拉，搜索框撑开剩余宽度。
 *
 * ⚠️ 断点说明：这份规格里有两套断点并存 ——
 * 「窄屏」在 §D7 里指没有侧栏的那一档（<1024px，过滤行的**位置**用它），
 * 而触摸目标与两列流用的是 767/768px（§D9、§6.10，这里用它）。
 */
@media (max-width: 767px) {
  .filter-bar__status-segments {
    display: none;
  }

  .filter-bar__status-trigger {
    display: inline-flex;
    /* 触摸目标不小于 32×32（§6.10：新加的下拉触发器同样适用）。 */
    min-width: 32px;
    min-height: 32px;
  }

  .filter-bar__search {
    flex: 1 1 auto;
    width: auto;
  }
}
</style>
