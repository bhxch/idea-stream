<script setup lang="ts">
import type { Tag } from '../api/types';
import type { IdeaFilter, StatusFilter } from '../lib/filter';
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

function setStatus(status: StatusFilter): void {
  emit('update:filter', { ...props.filter, status });
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

    <TagFilter
      :all-tags="allTags"
      :selected="filter.tags"
      :mode="filter.tagMode"
      @update:selected="emit('update:filter', { ...filter, tags: $event })"
      @update:mode="emit('update:filter', { ...filter, tagMode: $event })"
    />
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
</style>
