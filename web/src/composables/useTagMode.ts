import { ref, type Ref } from 'vue';
import type { TagMode } from '../lib/filter';

/**
 * AND/OR 偏好的存放位置。
 *
 * 存在 localStorage 里 → **只在这台设备上生效，跨设备不共享**
 * （项目排序方式那种要跨设备一致的东西存在后端的设置表里）。
 */
const STORAGE_KEY = 'idea.tagMode';

/** 读出本地存的模式；没存或存了非法值都回落 `or`。 */
function readStoredMode(): TagMode {
  try {
    return globalThis.localStorage?.getItem(STORAGE_KEY) === 'and' ? 'and' : 'or';
  } catch {
    // 隐私模式下 localStorage 可能直接抛异常；偏好丢了不影响功能。
    return 'or';
  }
}

/** 写回本地。写失败（配额满、无痕模式）也不能影响功能。 */
function writeStoredMode(mode: TagMode): void {
  try {
    globalThis.localStorage?.setItem(STORAGE_KEY, mode);
  } catch {
    // 忽略：偏好存不下不影响功能。
    // 但下次读是以 localStorage 为准的，写失败等于这次选择没存住，
    // 模式会回到存着的那个值（没存过就是 or）。
  }
}

/**
 * 模块级单例：所有用这个组合式函数的地方共用同一个模式。
 *
 * 不用 Pinia，状态放模块作用域即可（与 ToastHost 的做法一致）。
 */
const tagMode = ref<TagMode>('or');

/** 切换模式并记到本地。 */
function setTagMode(mode: TagMode): void {
  tagMode.value = mode;
  writeStoredMode(mode);
}

/**
 * 拿到 AND/OR 模式。
 *
 * 返回的是**共享的** ref，所以任何一处调用 `setTagMode`，其它地方立刻看到变化。
 * 每次调用都以 localStorage 为准重新读一次：本地存储才是这个偏好的唯一来源。
 *
 * 只在**本页面内**同步：没有监听 `storage` 事件，别的标签页改了设置这里不会跟着变
 * （单页应用不需要跨标签页同步）。
 */
export function useTagMode(): { tagMode: Ref<TagMode>; setTagMode: (mode: TagMode) => void } {
  tagMode.value = readStoredMode();
  return { tagMode, setTagMode };
}
