<script setup lang="ts">
import { computed } from 'vue';
import type { Idea } from '../api/types';
import { toRows } from '../lib/rows';
import type { SaveOptions, SaveOutcome } from '../lib/save';
import MessageItem from './MessageItem.vue';

const props = defineProps<{
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
  /**
   * 列表是不是还在加载。
   *
   * 加载中**不渲染空状态**：数据还没到就先说「这里还没有想法。」，
   * 等于凭空告诉用户「你什么都没记过」—— 空列表先闪一次，数据才到。
   */
  loading?: boolean;
  /**
   * 要高亮的条目 id（深链定位或合并结果定位）。
   *
   * `null` = 不高亮任何条目。
   */
  highlightId?: string | null;
  /** 标签补全（页面层提供：请求在页面层发）。 */
  suggest: (prefix: string) => string[];
  /** 保存正文（页面层提供）。 */
  onSaveContent: (id: string, content: string) => Promise<SaveOutcome>;
  /** 保存标签（页面层提供）。 */
  onSaveTags: (id: string, tags: string[], options?: SaveOptions) => Promise<SaveOutcome>;
}>();

const emit = defineEmits<{
  'toggle-select': [id: string];
  'toggle-done': [id: string];
  'delete': [id: string];
}>();

/**
 * 想法切成的「一排两个格子」。
 *
 * 切排在渲染层做（`toRows` 是纯函数）—— 位置序号与左右格由**同一个**序号决定，
 * 「奇数在左、偶数在右」才不会走样。
 */
const rows = computed(() => toRows(props.ideas));
</script>

<template>
  <!--
    列表顺序直接用传进来的数组顺序：顺序由后端保证（updated_at ASC, id ASC），
    组件不重排 —— 要重排请在调用方（HomePage）排好。

    结构是「一排一个容器，每排两个格子」：奇数位进左格、偶数位进右格。
  -->
  <div class="list" :data-at-bottom="atBottom">
    <p v-if="ideas.length === 0 && !loading" class="empty">这里还没有想法。</p>
    <div v-for="(row, rowIndex) in rows" :key="row.left.id" class="row">
      <div class="cell cell--left">
        <MessageItem
          :idea="row.left"
          :position="rowIndex * 2 + 1"
          :selected="selectedIds.has(row.left.id)"
          :busy="busyIds.has(row.left.id)"
          :highlight="row.left.id === highlightId"
          :suggest="suggest"
          :on-save-content="onSaveContent"
          :on-save-tags="onSaveTags"
          @toggle-select="emit('toggle-select', $event)"
          @toggle-done="emit('toggle-done', $event)"
          @delete="emit('delete', $event)"
        />
      </div>
      <div class="cell cell--right">
        <MessageItem
          v-if="row.right"
          :idea="row.right"
          :position="rowIndex * 2 + 2"
          :selected="selectedIds.has(row.right.id)"
          :busy="busyIds.has(row.right.id)"
          :highlight="row.right.id === highlightId"
          :suggest="suggest"
          :on-save-content="onSaveContent"
          :on-save-tags="onSaveTags"
          @toggle-select="emit('toggle-select', $event)"
          @toggle-done="emit('toggle-done', $event)"
          @delete="emit('delete', $event)"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.list {
  display: flex;
  flex-direction: column;
  gap: 9px;
}

/*
 * 一排两个格子。
 *
 * 用 grid 而不是 CSS `columns`：多列在内容增删时会重新平衡各列，整墙重排；
 * 「排」是显式 DOM 容器，追加只影响最后一排。
 */
.row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 9px;
}

/* 不让长内容把格子撑破。 */
.cell {
  min-width: 0;
}

.empty {
  margin: 0;
  padding: 24px 12px;
  color: var(--muted);
  text-align: center;
}

/*
 * 最后一排只有一条时，右格是空的 —— 让它不占位。
 *
 * `:empty` 只匹配**真的没有子元素**的格子（右格里有条目时不匹配）。
 */
.cell--right:empty {
  display: none;
}

/*
 * 窄屏（<768px）是单列。
 *
 * grid 只剩一列之后，两个格子自动落到两行（同一列、上下堆叠），
 * 顺序仍是 1、2、3、4…，每一条想法都还在。
 *
 * ⚠️ 绝对不要把 `.cell--right` 整个藏掉：右格里装着第 2、4、6… 条想法，
 * 藏掉它就是手机上每两条想法里有一条永远不见天。
 */
@media (max-width: 767px) {
  .row {
    grid-template-columns: 1fr;
  }
}
</style>
