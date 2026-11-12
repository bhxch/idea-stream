<script setup lang="ts">
import type { Idea } from '../api/types';
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
}>();

const emit = defineEmits<{
  'toggle-select': [id: string];
  'toggle-done': [id: string];
  'edit': [id: string];
  'delete': [id: string];
  'edit-tags': [id: string];
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
      @toggle-select="emit('toggle-select', $event)"
      @toggle-done="emit('toggle-done', $event)"
      @edit="emit('edit', $event)"
      @delete="emit('delete', $event)"
      @edit-tags="emit('edit-tags', $event)"
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
