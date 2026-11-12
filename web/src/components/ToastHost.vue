<script lang="ts">
import { reactive } from 'vue';

/** 一条提示。 */
export interface Toast {
  id: number;
  message: string;
  kind: 'info' | 'error';
}

/**
 * 提示队列是模块级单例（全局约束：不用 Pinia，状态用模块级单例）。
 * 组件只有一个实例，状态放在模块作用域即可，不用额外的 store 文件。
 */
const toasts = reactive<Toast[]>([]);

let nextId = 1;

/** 弹一条提示。后端的错误提示统一展示 ProblemDetails 的 `detail`。 */
export function showToast(message: string, kind: Toast['kind'] = 'info'): void {
  const id = nextId++;
  toasts.push({ id, message, kind });
  setTimeout(() => {
    const index = toasts.findIndex((toast) => toast.id === id);
    if (index >= 0) {
      toasts.splice(index, 1);
    }
  }, 4000);
}

export default {
  name: 'ToastHost',
  setup() {
    return { toasts };
  },
};
</script>

<template>
  <div class="toast-host" role="status" aria-live="polite">
    <div
      v-for="toast in toasts"
      :key="toast.id"
      class="toast"
      :class="`toast--${toast.kind}`"
    >
      {{ toast.message }}
    </div>
  </div>
</template>

<style scoped>
.toast-host {
  position: fixed;
  right: 16px;
  bottom: 16px;
  z-index: 100;
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: min(320px, calc(100vw - 32px));
}

.toast {
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--panel);
  box-shadow: var(--shadow);
  overflow-wrap: anywhere;
}

.toast--error {
  border-color: var(--danger);
  color: var(--danger);
}
</style>
