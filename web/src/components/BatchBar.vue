<script setup lang="ts">
import { ref } from 'vue';
import TagEditor from './TagEditor.vue';

const props = defineProps<{
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
  /**
   * 给选中的条目加标签（带要加的标签）。
   *
   * 第二个参数是**回执**：父组件发完请求后调用它，告诉我成没成
   * （`true` 成功、`false` 失败）。清草稿要等回执 ——
   * 父组件那边是异步发请求的，先清了就等于「失败时用户敲的标签没了」。
   */
  'add-tags': [tags: string[], ack: (ok: boolean) => void];
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

/** 两个标签列表是不是一样的（顺序也要一样）。 */
function sameTags(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((tag, index) => tag === right[index]);
}

/**
 * 把草稿里的标签应用到选中的条目上。
 *
 * 草稿与面板**等父组件回执**再清：请求失败时用户敲的标签还在，
 * 面板还开着，能直接重试 —— 先清了就等于白敲一遍。
 */
function applyTags(): void {
  if (tagDraft.value.length === 0 || props.busy) {
    return;
  }
  const tags = [...tagDraft.value];
  emit('add-tags', tags, (ok: boolean) => {
    // 回执回来时用户可能又改了草稿 —— 那就留着新的那份，别覆盖掉。
    if (ok && sameTags(tagDraft.value, tags)) {
      tagDraft.value = [];
      tagEditing.value = false;
    }
  });
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
        :disabled="tagDraft.length === 0 || busy"
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
/*
 * 操作条**随内容流**排（不再 fixed）。
 *
 * 之前它固定在视口底部中央，会盖住下面的输入框：勾选条目后页面滚到底部，
 * 用户想接着记一条都点不到「记下」。现在它排在消息流与输入框之间 ——
 * 位置还是底部，但不会压住输入框。
 *
 * z-index 只比内容高一点：条目里的标签编辑器下拉（z-index 20）要能盖住它，
 * 反过来被它盖住就看不见候选了。
 */
.batch-bar {
  position: relative;
  z-index: 5;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--panel);
  box-shadow: var(--shadow);
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

/*
 * 手机上点按目标不小于 32×32（与 MessageItem 里的同名规则一致）：
 * 批量操作条上的按钮也是一行字高，手指点不中。
 */
@media (max-width: 640px) {
  .action {
    padding: 6px 10px;
  }
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
