import { ref } from 'vue';
import { apiGet, apiSend } from '../api/client';

/**
 * 后端设置项的键。
 *
 * 目前只有项目排序方式是可写的（后端对不可写的键返回 400 `setting_key_not_writable`）。
 */
export const SETTING_KEYS = {
  projectSortMode: 'project.sort_mode',
} as const;

/**
 * 后端返回的设置是**扁平字典**：`{ "project.sort_mode": "recent" }`。
 *
 * 设置存后端，所以跨设备共享（与存在 localStorage 的 AND/OR 偏好相反）。
 */
export const settings = ref<Record<string, string>>({});

/** 读取全部设置项。 */
export async function loadSettings(): Promise<void> {
  settings.value = await apiGet<Record<string, string>>('/settings');
}

/**
 * 写一个设置项。
 *
 * 后端返回的是**写完之后**的整张表，直接整体覆盖本地缓存，
 * 免得本地留着旧值与服务端不一致。
 */
export async function saveSetting(key: string, value: string): Promise<void> {
  settings.value = await apiSend<Record<string, string>>('PUT', `/settings/${key}`, { value });
}
