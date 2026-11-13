import { ApiError } from '../api/client';
import { showToast } from '../components/ToastHost.vue';

/**
 * 把一次失败变成一条给用户看的提示。
 *
 * 放在这里而不是各页面各写一份：三个管理页与主页面的失败处理完全一样，
 * 复制四遍的话，改一次错误文案要改四个地方，漏掉一个就会出现
 * 「有的失败有提示、有的失败静默」的情况。
 *
 * @param error 捕获到的异常。
 * @param fallback 兜底文案：后端没给说明（或者根本不是 `ApiError`）时用它。
 */
export function reportError(error: unknown, fallback: string): void {
  const message = error instanceof ApiError ? (error.detail ?? fallback) : fallback;
  showToast(message, 'error');
}
