<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { Idea } from '../api/types';
import { firstLine } from '../lib/text';

const props = defineProps<{
  /** 是否展开。 */
  open: boolean;
  /**
   * 要合并的想法。
   *
   * 顺序就是拼接顺序（调用方按列表顺序给，与后端的 `updated_at ASC` 一致）。
   */
  ideas: Idea[];
}>();

const emit = defineEmits<{
  /** 确认合并（带改过的正文）。 */
  'confirm': [content: string];
  /** 取消。 */
  'cancel': [];
}>();

/** 合并后的正文（可编辑）。 */
const content = ref('');

/**
 * 初始正文：各条以空行拼接。
 *
 * 分隔符与后端一致（后端不传 content 时就是这么拼的），
 * 用户在这儿看到的就是合并后大致的样子。
 */
const initialContent = computed(() => props.ideas.map((idea) => idea.content).join('\n\n'));

/**
 * 每次打开都从初始正文重来。
 *
 * 上一次合并的内容不该留到下一次 —— 用户看到的是「这次要合并成什么样」。
 */
watch(
  () => props.open,
  (open) => {
    if (open) {
      content.value = initialContent.value;
    }
  },
  { immediate: true },
);
</script>

<template>
  <div v-if="open" class="merge-dialog" role="dialog" aria-modal="true" aria-label="合并想法">
    <div class="merge-dialog__panel">
      <h2 class="merge-dialog__title">合并 {{ ideas.length }} 条想法</h2>

      <ul class="merge-dialog__preview">
        <li v-for="idea in ideas" :key="idea.id" class="merge-dialog__item">
          <span class="merge-dialog__item-title">{{ firstLine(idea.content) }}</span>
          <pre class="merge-dialog__item-body">{{ idea.content }}</pre>
        </li>
      </ul>

      <label class="merge-dialog__label">
        合并后的正文
        <textarea v-model="content" class="merge-dialog__content" rows="8"></textarea>
      </label>

      <div class="merge-dialog__actions">
        <button class="action action--cancel" type="button" @click="emit('cancel')">取消</button>
        <button class="action action--confirm" type="button" @click="emit('confirm', content)">
          合并成一条
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.merge-dialog {
  position: fixed;
  inset: 0;
  z-index: 40;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  background: rgba(0, 0, 0, 0.35);
}

.merge-dialog__panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: min(640px, 100%);
  max-height: 90vh;
  overflow-y: auto;
  /* 对话框里的滚动**关在对话框里**：滑到头不要再把后面的页面一起带着滚。 */
  overscroll-behavior: contain;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.merge-dialog__title {
  margin: 0;
  font-size: 16px;
}

.merge-dialog__preview {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.merge-dialog__item {
  padding: 6px 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
}

.merge-dialog__item-title {
  color: var(--muted);
  font-size: 12px;
}

.merge-dialog__item-body {
  margin: 4px 0 0;
  font: inherit;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.merge-dialog__label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 13px;
}

.merge-dialog__content {
  width: 100%;
  resize: vertical;
}

.merge-dialog__actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.action--confirm {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--accent-fg);
}
</style>
