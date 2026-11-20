<script setup lang="ts">
import { ref } from 'vue';

const props = defineProps<{
  /**
   * 提交回调：resolve 出 `true` 表示存好了，`false` 表示失败。
   *
   * 为什么用 prop 而不是 emit：输入框要**等提交结果**才能决定是清空还是保留，
   * 而 `emit` 拿不到父组件处理器的返回值（父组件的 `onSubmit` 是异步的）。
   * 失败时输入框保留原内容 —— 用户刚敲的正文不能因为一次网络失败就没了。
   */
  onSubmit: (content: string) => Promise<boolean>;
}>();

const text = ref('');
const input = ref<HTMLTextAreaElement | null>(null);

/** 提交请求在飞：连按 Enter 别连发好几条。 */
const submitting = ref(false);

/**
 * 提交当前内容；空内容与重复提交直接忽略。
 *
 * **成功之后**才清空输入框并把焦点放回去；失败时内容原样留着，
 * 用户改完可以直接再提交，不用重新敲一遍。
 */
async function submit(): Promise<void> {
  const content = text.value.trim();
  if (!content || submitting.value) {
    return;
  }
  submitting.value = true;
  try {
    // 父组件负责弹错误提示（失败时它返回 false），这里只管输入框的清空与保留。
    const ok = await props.onSubmit(content).catch(() => false);
    // 提交期间用户又改了内容就别清：那是他新写的，不该被这次提交抹掉。
    if (ok && text.value.trim() === content) {
      text.value = '';
    }
    // 提交完焦点回到输入框：连续记录不用重新点。
    input.value?.focus();
  } finally {
    submitting.value = false;
  }
}

/** Enter 提交，Shift+Enter 换行。 */
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    void submit();
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
      aria-label="记一条想法"
      placeholder="记一条想法…（Enter 提交，Shift+Enter 换行）"
      @keydown="onKeydown"
    ></textarea>
    <button class="send" type="submit" :disabled="text.trim().length === 0 || submitting">
      记下
    </button>
  </form>
</template>

<style scoped>
/*
 * 输入区只是一行布局：输入框 + 「记下」按钮。
 *
 * 边框**只有一层**，就在输入框自己身上（.input）。
 * 原来这里（.composer）与输入框各画一层同色边框，两者之间只隔 8px 内边距，
 * 视觉上是「框里套框」—— 两层同色圆角框套在一起，谁也说不清哪层是输入框。
 *
 * 边框放在输入框上还有第二个好处：浏览器画的原生缩放手柄
 * （textarea 右下角那两道斜线）落在输入框自己的右下角，读得出是「拖这里放大」。
 * 边框若只留在 .composer 上，手柄就悬在输入框与按钮之间的空当里，
 * 像按钮旁残留的坏字符。
 */
.composer {
  display: flex;
  align-items: flex-end;
  gap: 8px;
}

/*
 * 输入框：唯一的一层边框在这里。
 *
 * 边框与底色跟全局表单控件一致（见 styles/base.css），
 * 这里再写一遍是为了让「就这一层边框」在组件里看得见、也测得到。
 */
.input {
  flex: 1;
  resize: vertical;
  border: 1px solid var(--border);
}

/*
 * 焦点圈：**不要**在这里把 outline 去掉。
 *
 * 之前这里有一条 `.input:focus { outline: none }` —— 它把全局的
 * `:focus-visible { outline: 2px solid var(--accent) }` 盖掉了
 * （scoped 选择器带属性限定，权重比全局那条高）：
 * 键盘 Tab 到输入框上时**一点焦点痕迹都没有**。
 * 输入框自己有边框，焦点圈加在边框外面，两层不打架。
 */

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
