<script setup lang="ts">
import { ref } from 'vue';

const emit = defineEmits<{
  /** 提交一条正文（已去掉首尾空白）。 */
  submit: [content: string];
}>();

const text = ref('');
const input = ref<HTMLTextAreaElement | null>(null);

/** 提交当前内容；空内容直接忽略。 */
function submit(): void {
  const content = text.value.trim();
  if (!content) {
    return;
  }
  emit('submit', content);
  text.value = '';
  // 提交完焦点回到输入框：连续记录不用重新点。
  input.value?.focus();
}

/** Enter 提交，Shift+Enter 换行。 */
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    submit();
  }
}
</script>

<template>
  <form class="composer" @submit.prevent="submit">
    <textarea
      ref="input"
      v-model="text"
      class="input"
      rows="3"
      placeholder="记一条想法…（Enter 提交，Shift+Enter 换行）"
      @keydown="onKeydown"
    ></textarea>
    <button class="send" type="submit" :disabled="text.trim().length === 0">记下</button>
  </form>
</template>

<style scoped>
.composer {
  display: flex;
  align-items: flex-end;
  gap: 8px;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
}

.input {
  flex: 1;
  resize: vertical;
  border: none;
  background: transparent;
}

.input:focus {
  outline: none;
}

.send {
  flex: none;
  padding: 6px 14px;
  border-color: var(--accent);
  background: var(--accent);
  color: var(--accent-fg);
}

.send:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>
