<script setup lang="ts">
import { computed, ref, watch } from 'vue';

const props = defineProps<{
  /**
   * 标签补全：按前缀给候选标签。
   *
   * **同步**返回当前已知的候选（父组件拿请求结果做缓存）：
   * 第一次通常还没有结果（请求在飞），父组件把结果写进它的响应式状态后，
   * 这里会跟着重画 —— 补全本来就是异步的，组件不该自己发请求。
   */
  suggest: (prefix: string) => string[];
}>();

/** 已选标签。父组件把新值回传，这里才显示成 chips。 */
const value = defineModel<string[]>('value', { required: true });

/** 输入框里的半截文字（还没确认成标签）。 */
const draft = ref('');

/** 补全候选。 */
const options = ref<string[]>([]);

/** 当前高亮的候选下标；-1 表示没高亮（此时回车添加的是输入的文字本身）。 */
const highlighted = ref(-1);

/** 候选里去掉已经选过的（标签按大小写不敏感去重）。 */
const candidates = computed(() =>
  options.value.filter((option) => !value.value.some((tag) => isSameTag(tag, option))),
);

/** 输入框有字、候选非空时才展开下拉。 */
const showOptions = computed(() => draft.value.trim().length > 0 && candidates.value.length > 0);

/** 两个标签是不是同一个（大小写不敏感，与后端的去重口径一致）。 */
function isSameTag(left: string, right: string): boolean {
  return left.trim().toLowerCase() === right.trim().toLowerCase();
}

/**
 * 候选跟着父组件的缓存走。
 *
 * 父组件是同步给的：请求在飞时先给空数组，结果到了下拉自动补上。
 */
watch(
  () => (draft.value.trim() ? props.suggest(draft.value.trim()) : []),
  (next) => {
    options.value = next;
    highlighted.value = -1;
  },
  { immediate: true },
);

/** 加一个标签（重复的忽略）。 */
function addTag(raw: string): void {
  const name = raw.trim();
  if (!name) {
    return;
  }
  if (value.value.some((tag) => isSameTag(tag, name))) {
    draft.value = '';
    return;
  }
  value.value = [...value.value, name];
  draft.value = '';
  highlighted.value = -1;
}

/** 删掉一个已选标签。 */
function removeTag(name: string): void {
  value.value = value.value.filter((tag) => tag !== name);
}

/** 上下键移动高亮。 */
function moveHighlight(step: number): void {
  if (candidates.value.length === 0) {
    return;
  }
  const next = highlighted.value + step;
  highlighted.value = (next + candidates.value.length) % candidates.value.length;
}

/**
 * 键盘：回车添加、空格/逗号分隔多标签、上下键选候选、Esc 清掉半截文字。
 *
 * 输入法组字中不抢按键：这时候的回车/空格是「选词」，不是「添加标签」。
 */
function onKeydown(event: KeyboardEvent): void {
  if (event.isComposing) {
    return;
  }
  if (event.key === 'Enter') {
    event.preventDefault();
    const candidate = highlighted.value >= 0 ? candidates.value[highlighted.value] : undefined;
    addTag(candidate ?? draft.value);
    return;
  }
  if (event.key === ' ' || event.key === ',' || event.key === '，') {
    event.preventDefault();
    addTag(draft.value);
    return;
  }
  if (event.key === 'Escape') {
    draft.value = '';
    highlighted.value = -1;
    return;
  }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    moveHighlight(event.key === 'ArrowDown' ? 1 : -1);
  }
}
</script>

<template>
  <div class="tag-editor">
    <ul v-if="value.length > 0" class="tag-editor__chips">
      <li v-for="tag in value" :key="tag" class="chip">
        <span class="chip__name">{{ tag }}</span>
        <button
          class="chip__remove"
          type="button"
          :aria-label="`删除标签 ${tag}`"
          @click="removeTag(tag)"
        >
          ×
        </button>
      </li>
    </ul>

    <div class="tag-editor__field">
      <input
        v-model="draft"
        class="tag-editor__input"
        type="text"
        placeholder="加标签…"
        @keydown="onKeydown"
      />

      <ul v-if="showOptions" class="tag-editor__options">
        <li v-for="(option, index) in candidates" :key="option" class="tag-editor__option">
          <button
            class="option"
            type="button"
            :class="{ 'option--active': index === highlighted }"
            @click="addTag(option)"
          >
            {{ option }}
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.tag-editor {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 8px;
}

.tag-editor__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--chip-bg);
  font-size: 12px;
}

.chip__remove {
  padding: 0 2px;
  border: none;
  background: none;
  color: var(--muted);
  line-height: 1;
}

.tag-editor__field {
  position: relative;
}

.tag-editor__input {
  width: 100%;
}

.tag-editor__options {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 20;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 160px;
  max-height: 220px;
  overflow-y: auto;
  margin: 0;
  padding: 4px;
  list-style: none;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.option {
  width: 100%;
  border: none;
  background: none;
  text-align: left;
}

.option--active {
  background: var(--chip-bg);
}
</style>
