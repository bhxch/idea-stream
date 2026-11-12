<script setup lang="ts">
import { ref } from 'vue';
import TagEditor from './TagEditor.vue';

defineProps<{
  /** 勾选中的条数。 */
  count: number;
  /** 标签补全（页面层提供：请求在页面层发）。 */
  suggest: (prefix: string) => string[];
  /** 有批量请求在飞：按钮禁用，防重复提交。 */
  busy?: boolean;
}>();

const emit = defineEmits<{
  /** 批量标记完成。 */
  'complete': [];
  /** 批量撤销完成。 */
  'undo-complete': [];
  /** 合并选中的条目。 */
  'merge': [];
  /** 删除选中的条目。 */
  'delete': [];
  /** 给选中的条目加标签（带要加的标签）。 */
  'add-tags': [tags: string[]];
  /** 取消勾选。 */
  'clear': [];
}>();

/** 标签编辑面板是否展开。 */
const tagEditing = ref(false);

/** 待添加的标签：选好了点「加到 N 条」才发请求。 */
const tagDraft = ref<string[]>([]);

/** 删除的二次确认。 */
const confirmingDelete = ref(false);

/** 标签变了：先记在草稿里。 */
function onTagsChange(next: string[]): void {
  tagDraft.value = next;
}

/** 把草稿里的标签应用到选中的条目上。 */
function applyTags(): void {
  if (tagDraft.value.length === 0) {
    return;
  }
  emit('add-tags', tagDraft.value);
  tagDraft.value = [];
  tagEditing.value = false;
}

/** 点「删除」先问一句，别真删。 */
function requestDelete(): void {
  confirmingDelete.value = true;
}

/** 不删了。 */
function cancelDelete(): void {
  confirmingDelete.value = false;
}

/** 确认删除。 */
function confirmDelete(): void {
  confirmingDelete.value = false;
  emit('delete');
}
</script>

<template>
  <div class="batch-bar" role="group" aria-label="批量操作">
    <span class="count">已选 {{ count }} 条</span>

    <button class="action action--complete" type="button" :disabled="busy" @click="emit('complete')">
      标记完成
    </button>
    <button
      class="action action--undo-complete"
      type="button"
      :disabled="busy"
      @click="emit('undo-complete')"
    >
      撤销完成
    </button>
    <button
      class="action action--merge"
      type="button"
      :disabled="busy || count < 2"
      title="合并至少需要两条想法"
      @click="emit('merge')"
    >
      合并
    </button>
    <button
      class="action action--add-tags"
      type="button"
      :disabled="busy"
      @click="tagEditing = !tagEditing"
    >
      加标签
    </button>
    <button
      class="action action--delete"
      type="button"
      :disabled="busy"
      @click="requestDelete"
    >
      删除
    </button>
    <button class="action action--clear" type="button" :disabled="busy" @click="emit('clear')">
      取消选择
    </button>

    <div v-if="tagEditing" class="panel">
      <TagEditor v-model:value="tagDraft" :suggest="suggest" @update:value="onTagsChange" />
      <button
        class="action action--apply-tags"
        type="button"
        :disabled="tagDraft.length === 0"
        @click="applyTags"
      >
        加到 {{ count }} 条
      </button>
    </div>

    <div v-if="confirmingDelete" class="panel panel--danger">
      <span class="panel__text">删除选中的 {{ count }} 条？它们会进回收站。</span>
      <button class="action action--delete confirm__delete" type="button" @click="confirmDelete">
        确认删除
      </button>
      <button class="action confirm__cancel" type="button" @click="cancelDelete">取消</button>
    </div>
  </div>
</template>

<style scoped>
.batch-bar {
  position: fixed;
  left: 50%;
  bottom: 16px;
  z-index: 30;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  max-width: calc(100vw - 32px);
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--panel);
  box-shadow: var(--shadow);
  transform: translateX(-50%);
}

.count {
  color: var(--muted);
  font-size: 13px;
  white-space: nowrap;
}

.action {
  padding: 3px 10px;
  font-size: 13px;
}

.action:disabled {
  opacity: 0.5;
  cursor: default;
}

.action--delete {
  color: var(--danger);
}

.panel {
  display: flex;
  align-items: flex-end;
  gap: 8px;
  flex-basis: 100%;
  padding-top: 8px;
  border-top: 1px solid var(--border);
}

.panel--danger {
  align-items: center;
}

.panel__text {
  flex: 1;
  font-size: 13px;
}
</style>
