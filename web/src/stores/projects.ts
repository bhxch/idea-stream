import { ref } from 'vue';
import { apiGet } from '../api/client';
import type { Project } from '../api/types';
import { SETTING_KEYS, loadSettings, saveSetting, settings } from './settings';

/** 项目排序方式：`recent` 按最近活动，`manual` 按用户拖出来的顺序。 */
export type ProjectSortMode = 'recent' | 'manual';

/**
 * 项目列表。
 *
 * 列表的顺序**由后端保证**：后端按当前排序方式返回（`recent` 按最近活动在前，
 * `manual` 按 `sortOrder`），前端不重排。
 */
export const projects = ref<Project[]>([]);

/** 当前选中的项目；`null` 表示「全部项目」。 */
export const currentProjectId = ref<string | null>(null);

/** 当前的项目排序方式（存在后端，跨设备共享）。 */
export const projectSortMode = ref<ProjectSortMode>('recent');

/** 拉取项目列表。 */
export async function loadProjects(): Promise<void> {
  projects.value = await apiGet<Project[]>('/projects');
}

/** 切换当前项目。`null` 表示「全部项目」。 */
export function setCurrentProject(id: string | null): void {
  currentProjectId.value = id;
}

/**
 * 读取项目排序方式。
 *
 * 排序方式与其它设置项一起存在后端的设置表里，所以这里复用设置表的读取。
 */
export async function loadSortMode(): Promise<void> {
  await loadSettings();
  projectSortMode.value =
    settings.value[SETTING_KEYS.projectSortMode] === 'manual' ? 'manual' : 'recent';
}

/**
 * 保存项目排序方式。
 *
 * 只改本地不够 —— 排序方式存在后端，换台设备也要一样。
 */
export async function saveSortMode(mode: ProjectSortMode): Promise<void> {
  await saveSetting(SETTING_KEYS.projectSortMode, mode);
  projectSortMode.value = mode;
}
