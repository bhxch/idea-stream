<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { Idea } from '../api/types';
import { formatRelativeTime } from '../lib/format';
import TagEditor from './TagEditor.vue';

const props = defineProps<{
  /** 这一条想法。 */
  idea: Idea;
  /** 是否被多选勾中。 */
  selected: boolean;
  /**
   * 这一条是否有请求在飞（比如切换完成态、保存编辑）。
   *
   * 在飞时禁用按钮：连点两次会拿同一个旧状态去提交，等于没切换。
   */
  busy?: boolean;
  /** 标签补全：父组件提供（请求在页面层发，组件不碰网络）。 */
  suggest: (prefix: string) => string[];
  /**
   * 保存正文：resolve 出 `true` 表示存好了。
   *
   * 用 prop 而不是 emit：编辑框要**等保存结果**才知道是退出编辑态还是留在里面，
   * emit 拿不到父组件处理器的返回值。失败时正文留在编辑框里 —— 用户刚敲的不能丢。
   */
  onSaveContent: (id: string, content: string) => Promise<boolean>;
  /** 保存标签：resolve 出 `true` 表示存好了（失败时留在编辑态）。 */
  onSaveTags: (id: string, tags: string[]) => Promise<boolean>;
}>();

const emit = defineEmits<{
  /** 勾选/取消勾选（多选批量操作用）。 */
  'toggle-select': [id: string];
  /** 切换完成态。 */
  'toggle-done': [id: string];
  /** 请求删除（已经二次确认过）。 */
  'delete': [id: string];
}>();

/** 是否在编辑正文。 */
const editing = ref(false);

/** 编辑中的正文草稿。 */
const draft = ref('');

/** 编辑框本身（进入编辑态后把焦点放进去）。 */
const editor = ref<HTMLTextAreaElement | null>(null);

/** 是否在编辑标签。 */
const tagEditing = ref(false);

/** 标签草稿：保存失败时留着，用户能直接重试。 */
const tagDraft = ref<string[]>([]);

/** 攒着的标签（在飞时被挡下的那一版，等在飞结束再存）。 */
const pendingTags = ref<string[] | null>(null);

/** 删除的二次确认。 */
const confirmingDelete = ref(false);

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

/**
 * 正文（渲染用）。
 *
 * 标题为空时，首行已经当视觉标题了，正文里就不再显示一遍 ——
 * 否则同一句话会在一条条目里出现两次。
 */
const bodyContent = computed(() => {
  if (props.idea.title?.trim()) {
    return props.idea.content;
  }
  const lines = props.idea.content.split('\n');
  const first = lines.findIndex((line) => line.trim().length > 0);
  if (first < 0) {
    return props.idea.content;
  }
  // 首行后面跟的空行是分隔用的，一并去掉：正文紧挨着视觉标题。
  return lines.slice(first + 1).join('\n').replace(/^\n+/, '');
});

/** 进入编辑态。 */
function startEdit(): void {
  draft.value = props.idea.content;
  editing.value = true;
  void focusEditor();
}

/** 退出编辑态，不保存。 */
function cancelEdit(): void {
  editing.value = false;
  draft.value = '';
}

/** 存好了才退出编辑态；失败留在里面，草稿不丢。 */
async function saveEdit(): Promise<void> {
  if (!editing.value || props.busy) {
    return;
  }
  const ok = await props.onSaveContent(props.idea.id, draft.value).catch(() => false);
  if (ok) {
    editing.value = false;
    draft.value = '';
  }
}

/** 把焦点放进编辑框（进来就能接着改）。 */
async function focusEditor(): Promise<void> {
  await nextTick();
  editor.value?.focus();
}

/** 编辑框里的按键：Ctrl+Enter 保存、Esc 取消。 */
function onEditorKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    void saveEdit();
    return;
  }
  if (event.key === 'Escape') {
    event.preventDefault();
    cancelEdit();
  }
}

/** 进入标签编辑态。 */
function startTagEdit(): void {
  tagDraft.value = [...props.idea.tags];
  tagEditing.value = true;
}

/** 收起标签编辑器（改动都已经存过了）。 */
function cancelTagEdit(): void {
  tagEditing.value = false;
  tagDraft.value = [];
}

/** 点「标签」：开着就收起，没开就打开。 */
function toggleTagEdit(): void {
  if (tagEditing.value) {
    cancelTagEdit();
    return;
  }
  startTagEdit();
}

/**
 * 标签变了：立刻存。存好了收起编辑器，失败留在里面（草稿不丢）。
 *
 * 在飞时（自己的保存，或别的操作）先攒着，等在飞结束再存 ——
 * 用户连按两下回车，第二个标签不能因为快了一点就没了。
 */
async function onTagsChange(next: string[]): Promise<void> {
  tagDraft.value = next;
  if (props.busy) {
    pendingTags.value = next;
    return;
  }
  const ok = await props.onSaveTags(props.idea.id, next).catch(() => false);
  if (ok) {
    tagEditing.value = false;
    tagDraft.value = [];
  }
}

/**
 * 在飞的操作结束了：把刚才没存上的那一版补存。
 *
 * 那次保存被在飞保护挡下了（同一个条目同时只允许一个写操作）。
 */
watch(
  () => props.busy,
  (busy) => {
    if (busy || !pendingTags.value) {
      return;
    }
    const pending = pendingTags.value;
    pendingTags.value = null;
    void onTagsChange(pending);
  },
);

/** 点「删除」：先问一句，别真删。 */
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
  emit('delete', props.idea.id);
}
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
        :disabled="busy"
        :aria-pressed="idea.done"
        @click="emit('toggle-done', idea.id)"
      >
        {{ idea.done ? '已完成' : '标记完成' }}
      </button>
      <h2 v-if="!editing" class="title">{{ visualTitle }}</h2>
      <time class="time">{{ formatRelativeTime(idea.updatedAt) }}</time>
    </div>

    <!-- 正文一律按纯文本渲染（插值），绝不用 v-html：内容是用户随手写的，
         里面可能有 <b>、& 之类的字符，按 HTML 解析会被吞掉或变成注入。 -->
    <textarea
      v-if="editing"
      ref="editor"
      v-model="draft"
      class="editor"
      rows="4"
      :disabled="busy"
      @blur="saveEdit"
      @keydown="onEditorKeydown"
    ></textarea>
    <div v-else class="body" title="点击编辑" @click="startEdit">{{ bodyContent }}</div>

    <TagEditor
      v-if="tagEditing"
      v-model:value="tagDraft"
      :suggest="suggest"
      @update:value="onTagsChange"
    />
    <ul v-else-if="idea.tags.length > 0" class="tags">
      <li v-for="tag in idea.tags" :key="tag" class="chip">{{ tag }}</li>
    </ul>

    <div class="actions">
      <button
        class="action action--tags"
        type="button"
        :disabled="busy"
        @click="toggleTagEdit"
      >
        {{ tagEditing ? '✓ 完成' : '🏷 标签' }}
      </button>
      <button
        class="action action--delete"
        type="button"
        :disabled="busy"
        @click="requestDelete"
      >
        🗑 删除
      </button>
    </div>

    <div v-if="confirmingDelete" class="confirm">
      <span class="confirm__text">删除这条？它会进回收站。</span>
      <button class="action confirm__delete" type="button" @click="confirmDelete">确认删除</button>
      <button class="action confirm__cancel" type="button" @click="cancelDelete">取消</button>
    </div>
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

/* 请求在飞：按钮不可点，别让它看起来还能按。 */
.done:disabled,
.action:disabled {
  opacity: 0.5;
  cursor: default;
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
  min-height: 1.6em;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  cursor: text;
}

.editor {
  display: block;
  width: 100%;
  margin-top: 6px;
  resize: vertical;
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

.actions {
  display: flex;
  gap: 6px;
  margin-top: 8px;
}

.action {
  padding: 2px 8px;
  font-size: 12px;
}

.action--delete {
  color: var(--danger);
}

.confirm {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 6px 8px;
  border: 1px solid var(--danger);
  border-radius: 6px;
}

.confirm__text {
  flex: 1;
  font-size: 12px;
}
</style>
