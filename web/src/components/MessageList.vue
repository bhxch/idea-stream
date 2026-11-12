<script setup lang="ts">
import type { Idea } from '../api/types';
import type { SaveOutcome } from '../lib/save';
import MessageItem from './MessageItem.vue';

defineProps<{
  /** 要显示的想法，按数组顺序渲染。 */
  ideas: Idea[];
  /** 被勾选的 id 集合。 */
  selectedIds: Set<string>;
  /**
   * 请求在飞的条目 id 集合。
   *
   * 在飞的那条要禁用它的按钮：连点两次会拿同一个旧状态去提交，等于没切换。
   */
  busyIds: Set<string>;
  /** 当前是否贴着列表底部（滚动跟随用，F11 消费）。 */
  atBottom: boolean;
  /** 标签补全（页面层提供：请求在页面层发）。 */
  suggest: (prefix: string) => string[];
  /** 保存正文（页面层提供）。 */
  onSaveContent: (id: string, content: string) => Promise<SaveOutcome>;
  /** 保存标签（页面层提供）。 */
  onSaveTags: (id: string, tags: string[]) => Promise<SaveOutcome>;
}>();

const emit = defineEmits<{
  'toggle-select': [id: string];
  'toggle-done': [id: string];
  'delete': [id: string];
}>();
</script>

<template>
  <!--
    列表顺序直接用传进来的数组顺序：顺序由后端保证（updated_at ASC, id ASC），
    组件不重排 —— 要重排请在调用方（HomePage）排好。
  -->
  <div class="list" :data-at-bottom="atBottom">
    <p v-if="ideas.length === 0" class="empty">这里还没有想法。</p>
    <MessageItem
      v-for="idea in ideas"
      :key="idea.id"
      :idea="idea"
      :selected="selectedIds.has(idea.id)"
      :busy="busyIds.has(idea.id)"
      :suggest="suggest"
      :on-save-content="onSaveContent"
      :on-save-tags="onSaveTags"
      @toggle-select="emit('toggle-select', $event)"
      @toggle-done="emit('toggle-done', $event)"
      @delete="emit('delete', $event)"
    />
  </div>
</template>

<style scoped>
.list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.empty {
  margin: 0;
  padding: 24px 12px;
  color: var(--muted);
  text-align: center;
}
</style>
