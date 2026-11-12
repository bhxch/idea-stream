<script setup lang="ts">
import { computed } from 'vue';
import type { Idea } from '../api/types';
import { formatRelativeTime } from '../lib/format';

const props = defineProps<{
  /** 这一条想法。 */
  idea: Idea;
  /** 是否被多选勾中。 */
  selected: boolean;
}>();

const emit = defineEmits<{
  /** 勾选/取消勾选（多选批量操作用）。 */
  'toggle-select': [id: string];
  /** 切换完成态。 */
  'toggle-done': [id: string];
  /** 请求编辑正文。 */
  'edit': [id: string];
  /** 请求删除。 */
  'delete': [id: string];
  /** 请求编辑标签。 */
  'edit-tags': [id: string];
}>();

/**
 * 视觉标题。
 *
 * 标题是可选的（可以只记正文），为空时拿正文首行顶上，
 * 否则列表里一排没有标题的条目会很难认。
 */
const visualTitle = computed(() => {
  const title = props.idea.title?.trim();
  if (title) {
    return title;
  }
  const firstLine = props.idea.content.split('\n').find((line) => line.trim().length > 0);
  return firstLine?.trim() ?? '';
});
</script>

<template>
  <article class="idea" :class="{ 'idea--done': idea.done, 'idea--selected': selected }">
    <div class="head">
      <input
        class="select"
        type="checkbox"
        :checked="selected"
        :aria-label="`选择 ${visualTitle}`"
        @change="emit('toggle-select', idea.id)"
      />
      <button
        class="done"
        type="button"
        :aria-pressed="idea.done"
        @click="emit('toggle-done', idea.id)"
      >
        {{ idea.done ? '已完成' : '标记完成' }}
      </button>
      <h2 class="title">{{ visualTitle }}</h2>
      <time class="time">{{ formatRelativeTime(idea.updatedAt) }}</time>
    </div>

    <!-- 正文一律按纯文本渲染（插值），绝不用 v-html：内容是用户随手写的，
         里面可能有 <b>、& 之类的字符，按 HTML 解析会被吞掉或变成注入。 -->
    <div class="body">{{ idea.content }}</div>

    <ul v-if="idea.tags.length > 0" class="tags">
      <li v-for="tag in idea.tags" :key="tag" class="chip">{{ tag }}</li>
    </ul>
  </article>
</template>

<style scoped>
.idea {
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.idea--selected {
  border-color: var(--accent);
}

.idea--done .title,
.idea--done .body {
  color: var(--muted);
  text-decoration: line-through;
}

.head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.title {
  flex: 1;
  margin: 0;
  font-size: 15px;
  overflow-wrap: anywhere;
}

.time {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.body {
  margin-top: 6px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 8px 0 0;
  padding: 0;
  list-style: none;
}

.chip {
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--chip-bg);
  font-size: 12px;
}
</style>
