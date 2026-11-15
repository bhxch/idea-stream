<script setup lang="ts">
import { computed } from 'vue';
import type { Tag } from '../api/types';
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

/** 当前关键词（没给这个字段时按空处理 = 不过滤）。 */
const keyword = computed(() => props.filter.keyword ?? '');

/** 有没有过滤条件生效（决定「清除」按钮出不出现）。 */
const filtered = computed(() => hasActiveFilter(props.filter));

function setStatus(status: StatusFilter): void {
  emit('update:filter', { ...props.filter, status });
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
</script>

<template>
  <div class="filter-bar">
    <div class="filter-bar__status" role="group" aria-label="状态过滤">
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
      关键词过滤：过滤在服务端做（设计文档 §11.1），这里只负责把输入报上去
      （防抖在「重拉」的入口）。空串 = 不过滤。
    -->
    <input
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

.filter-bar__status {
  display: flex;
  gap: 4px;
}

.filter-bar__status-button.is-active {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--accent-fg);
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
</style>
