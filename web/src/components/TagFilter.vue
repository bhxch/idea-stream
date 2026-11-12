<script setup lang="ts">
import { ref } from 'vue';
import type { Tag } from '../api/types';
import type { TagMode } from '../lib/filter';

const props = defineProps<{
  /** 全部标签（含每个标签的想法条数）。 */
  allTags: Tag[];
  /** 当前选中的标签。 */
  selected: string[];
  /** 当前匹配模式。 */
  mode: TagMode;
}>();

const emit = defineEmits<{
  /** 选中的标签变了。 */
  'update:selected': [tags: string[]];
  /** 匹配模式变了。 */
  'update:mode': [mode: TagMode];
}>();

/** 下拉面板是否展开。 */
const open = ref(false);

/** 勾选/取消一个标签。 */
function toggleTag(name: string): void {
  const next = props.selected.includes(name)
    ? props.selected.filter((tag) => tag !== name)
    : [...props.selected, name];
  emit('update:selected', next);
}

/** AND/OR 是一个按钮来回切，按钮上显示当前模式。 */
function toggleMode(): void {
  emit('update:mode', props.mode === 'or' ? 'and' : 'or');
}
</script>

<template>
  <div class="tag-filter">
    <button
      class="tag-filter__trigger"
      type="button"
      :aria-expanded="open"
      @click="open = !open"
    >
      标签<span v-if="selected.length > 0">（{{ selected.length }}）</span>
    </button>

    <button
      class="tag-filter__mode"
      type="button"
      :title="
        mode === 'or'
          ? '当前：命中任意一个选中标签即显示'
          : '当前：必须同时命中全部选中标签才显示'
      "
      @click="toggleMode"
    >
      {{ mode === 'or' ? '任一（OR）' : '全部（AND）' }}
    </button>

    <div v-if="open" class="tag-filter__panel">
      <p v-if="allTags.length === 0" class="tag-filter__empty">还没有标签</p>
      <label v-for="tag in allTags" :key="tag.name" class="tag-filter__option">
        <input
          type="checkbox"
          :checked="selected.includes(tag.name)"
          @change="toggleTag(tag.name)"
        />
        <span class="tag-filter__name">{{ tag.name }}</span>
        <span class="tag-filter__count">{{ tag.count }}</span>
      </label>
    </div>
  </div>
</template>

<style scoped>
.tag-filter {
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
}

.tag-filter__panel {
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

.tag-filter__option {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
}

.tag-filter__name {
  flex: 1;
  overflow-wrap: anywhere;
}

.tag-filter__count {
  color: var(--muted);
  font-size: 12px;
}

.tag-filter__empty {
  margin: 0;
  color: var(--muted);
}
</style>
