<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { ApiError, apiGet, apiSend } from '../api/client';
import type { Idea, Project, Tag } from '../api/types';
import BatchBar from '../components/BatchBar.vue';
import Composer from '../components/Composer.vue';
import FilterBar from '../components/FilterBar.vue';
import MergeDialog from '../components/MergeDialog.vue';
import MessageList from '../components/MessageList.vue';
import { showToast } from '../components/ToastHost.vue';
import { useTagMode } from '../composables/useTagMode';
import { matchesFilter, sortIdeas, type IdeaFilter } from '../lib/filter';
import type { SaveOutcome } from '../lib/save';
import {
  applyLocalUpsert,
  focusIdeaId,
  ideas,
  loadIdeas,
  loading,
  removeLocal,
} from '../stores/ideas';
import {
  currentProjectId,
  loadProjects,
  loadSortMode,
  projects,
  setCurrentProject,
} from '../stores/projects';

/**
 * AND/OR 偏好存在本地（每台设备各自的）。
 *
 * 组合式函数返回的是共享的 ref，所以切换模式后各处立刻一致。
 */
const { tagMode, setTagMode } = useTagMode();

/** 过滤条件。项目、状态、标签三者取交集。 */
const filter = ref<IdeaFilter>({
  projectId: currentProjectId.value,
  status: 'all',
  tags: [],
  tagMode: tagMode.value,
});

/** 全部标签（标签多选下拉的选项）。 */
const allTags = ref<Tag[]>([]);

/** 被勾选的想法 id（多选批量操作用）。 */
const selectedIds = ref<Set<string>>(new Set());

/** 合并对话框是否展开。 */
const mergeOpen = ref(false);

/** 有批量请求在飞：操作条禁用，防重复提交。 */
const batchBusy = ref(false);

/**
 * 有请求在飞的条目 id。
 *
 * 切换完成态、保存正文、保存标签、删除共用这一套保护：
 * 在飞时按钮禁用、重复点击忽略 —— 连点两次会拿同一个旧状态去提交，
 * 等于没切换 —— 用户以为生效了，实际什么也没变。
 */
const busyIds = ref<Set<string>>(new Set());

/** 标签补全缓存：前缀（小写）→ 候选标签。 */
const suggestionCache = reactive<Record<string, string[]>>({});

/** 已经发过补全请求的前缀（同一个前缀不重复发）。 */
const requestedPrefixes = new Set<string>();

/** 是否贴着列表底部（滚动跟随在 F11 接上）。 */
const atBottom = ref(true);

/** 真正生效的过滤条件：模式以 localStorage 里的为准。 */
const activeFilter = computed<IdeaFilter>(() => ({ ...filter.value, tagMode: tagMode.value }));

/**
 * 列表内容。
 *
 * 过滤与排序都在客户端做（`matchesFilter` / `sortIdeas`）：
 * AND/OR 偏好存在各设备自己的 localStorage 里，服务端无从得知每条连接用哪种模式。
 */
const visibleIdeas = computed(() =>
  sortIdeas(ideas.value.filter((idea) => matchesFilter(idea, activeFilter.value))),
);

/** 把后端的错误变成一条提示。 */
function reportError(error: unknown, fallback: string): void {
  const message = error instanceof ApiError ? (error.detail ?? fallback) : fallback;
  showToast(message, 'error');
}

/**
 * 首屏加载。
 *
 * 三件事互不依赖：项目列表、排序方式偏好、全部标签。
 * 排序方式只是个偏好，取不到也不该拖垮整屏 —— 用 `allSettled` 各自兜底，
 * 想法列表**单独**加载，列表挂了不影响别的提示。
 */
async function init(): Promise<void> {
  const [projectsResult, sortModeResult, tagsResult] = await Promise.allSettled([
    loadProjects(),
    loadSortMode(),
    loadTags(),
  ]);
  if (projectsResult.status === 'rejected') {
    reportError(projectsResult.reason, '加载项目失败');
  }
  if (sortModeResult.status === 'rejected') {
    reportError(sortModeResult.reason, '加载排序方式失败');
  }
  if (tagsResult.status === 'rejected') {
    reportError(tagsResult.reason, '加载标签失败');
  }

  const result = await switchProject(currentProjectId.value);
  if (!result.ok) {
    await handleIdeasError(result.error, currentProjectId.value);
  }
}

/** 拉取全部标签。 */
async function loadTags(): Promise<void> {
  allTags.value = await apiGet<Tag[]>('/tags');
}

/** 切换项目的结果：成功，还是失败（带原因）。 */
type SwitchResult = { ok: true } | { ok: false; error: unknown };

/**
 * 切到某个项目并拉取它的列表。
 *
 * 成功：列表换成新范围的数据；之前勾的条目不再相关，此时才清空勾选。
 * 失败：项目选择**回滚**到切换前 —— 数据还停在上一个项目，
 * `filter.projectId` 若停在新项目，旧数据会被过滤成空列表，两边必须对齐；
 * 勾选保持原样（项目其实没换，勾掉的东西还在列表里）。
 */
async function switchProject(next: string | null): Promise<SwitchResult> {
  const previous = currentProjectId.value;
  setCurrentProject(next);
  filter.value = { ...filter.value, projectId: next };
  try {
    await loadIdeas(next);
    // 拉到新范围的数据了，之前勾的条目不再属于当前范围，这时才清空。
    selectedIds.value = new Set();
    // 标签补全是按项目给的：换了项目，缓存里的候选就不作数了。
    clearSuggestions();
    return { ok: true };
  } catch (error) {
    setCurrentProject(previous);
    filter.value = { ...filter.value, projectId: previous };
    return { ok: false, error };
  }
}

/**
 * 切换项目：换掉列表范围，之前勾选的条目也不再相关。
 *
 * 拉取失败时把项目选择**回滚**（见 {@link switchProject}）：
 * 数据还停在上一个项目，`filter.projectId` 若停在新项目，
 * 旧数据会被过滤成空列表 —— 两边必须对齐。
 */
async function onProjectChange(event: Event): Promise<void> {
  const select = event.target as HTMLSelectElement;
  const value = select.value;
  const previous = currentProjectId.value;
  const next = value === '' ? null : value;
  const result = await switchProject(next);
  if (result.ok) {
    return;
  }
  // 状态回滚了，下拉的 DOM 值也得回去：绑定值前后一样时 Vue 不会重设它，
  // 不手动改的话下拉会停在用户刚选的那个项目上，与状态对不上。
  select.value = previous ?? '';
  if (isTooManyIdeas(result.error)) {
    // 切「全部项目」会一次要全库，超了上限 —— 这不是普通的失败，
    // 改切最近有活动的项目（补救在 recoverFromTooMany 里）。
    await recoverFromTooMany(result.error, next);
    return;
  }
  reportError(result.error, '加载想法失败');
}

/**
 * 加载想法列表失败时的分派。
 *
 * 「全部项目」一次要拉回全库，会撞上服务端的单次返回上限（`too_many_ideas`）——
 * 这不是普通的失败，得给一条能照着做的提示。
 */
async function handleIdeasError(error: unknown, failedProjectId: string | null): Promise<void> {
  if (isTooManyIdeas(error)) {
    await recoverFromTooMany(error, failedProjectId);
    return;
  }
  reportError(error, '加载想法失败');
}

/** 是不是「命中数超过单次返回上限」。 */
function isTooManyIdeas(error: unknown): error is ApiError {
  return error instanceof ApiError && error.code === 'too_many_ideas';
}

/**
 * 「命中数超过单次上限」的提示 + 补救。
 *
 * 补救是**自动切到最近有活动的项目**：数据仍然完整可用，想看全库再自己切回去。
 * 提示按实际发生的事给 —— 切过去了就说「已切到」，没切成（没有别的项目可切）
 * 就别让用户去做应用没能替他们做的事。
 *
 * @param failedProjectId 刚才拉取失败的项目（`null` 表示「全部项目」）。
 *   它就是「最近有活动的项目」时不切了：再切一次是同样的失败，只多一条提示。
 */
async function recoverFromTooMany(error: ApiError, failedProjectId: string | null): Promise<void> {
  const count = parseHitCount(error.detail);
  const target = mostRecentProject();
  if (!target || target.id === failedProjectId || target.id === currentProjectId.value) {
    // 没有别的项目可切：保持当前项目（列表和下拉仍然对得上），只给一条提示。
    showToast(`${tooManyMessage(count)}，仍显示当前范围的想法`, 'error');
    return;
  }
  const result = await switchProject(target.id);
  if (!result.ok) {
    const detail = result.error instanceof ApiError ? result.error.detail : null;
    showToast(
      `${tooManyMessage(count)}，切到最近有活动的项目也失败了${detail ? `：${detail}` : ''}`,
      'error',
    );
    return;
  }
  showToast(`${tooManyMessage(count)}，已切到最近有活动的项目「${target.name}」`, 'error');
}

/** 从服务端的说明里取出命中条数（「命中 37 条想法，超过单次返回上限 2000，…」）。 */
function parseHitCount(detail: string | undefined): number | null {
  const matched = /命中\s*(\d+)\s*条/.exec(detail ?? '');
  return matched ? Number(matched[1]) : null;
}

/** 「超过单次上限」的提示语（带条数，条数取不到就不带）。 */
function tooManyMessage(count: number | null): string {
  return count === null ? '条目太多，超过单次上限' : `共 ${count} 条超过单次上限`;
}

/**
 * 最近有活动的项目。
 *
 * 按 `lastActivityAt` 挑（不依赖项目排序方式 —— 那只是列表的排列）。
 * 挑不到就是没有：一个想法都没有时切过去也是空的。
 */
function mostRecentProject(): Project | undefined {
  return projects.value
    .map((project) => ({
      project,
      activity: project.lastActivityAt ? Date.parse(project.lastActivityAt) : Number.NEGATIVE_INFINITY,
    }))
    .sort((left, right) => right.activity - left.activity)[0]?.project;
}

/** 过滤条件变了。 */
function onFilterUpdate(next: IdeaFilter): void {
  if (next.tagMode !== tagMode.value) {
    // AND/OR 是设备自己的偏好，记到本地。
    setTagMode(next.tagMode);
  }
  filter.value = { ...next, tagMode: tagMode.value };
}

/**
 * 提交一条新想法。
 *
 * 提交的是纯正文（标题留空）；「全部项目」视图下不指定项目，
 * 后端会把它落到内置收件箱。
 *
 * @returns true 表示存好了（输入框据此清空），false 表示失败（内容保留在框里）。
 */
async function onSubmit(content: string): Promise<boolean> {
  try {
    const created = await apiSend<Idea>('POST', '/ideas', {
      projectId: currentProjectId.value,
      content,
      title: null,
      tags: [],
    });
    applyLocalUpsert(created);
    return true;
  } catch (error) {
    reportError(error, '保存失败');
    return false;
  }
}

/**
 * 标记一个条目「有请求在飞」，返回释放它的函数。
 *
 * 释放要重新赋值整个 Set：Vue 的 ref 靠引用变化才知道内容变了。
 */
function markBusy(id: string): () => void {
  busyIds.value = new Set(busyIds.value).add(id);
  return () => {
    const next = new Set(busyIds.value);
    next.delete(id);
    busyIds.value = next;
  };
}

/**
 * 切换完成态。
 *
 * 先落库再更新本地：接口返回的想法才是最新的（`updatedAt` 由后端给），
 * 本地不自己猜。失败时本地状态不动，条目还是原来的样子。
 *
 * 请求在飞时忽略重复点击（按钮也是禁用的）：第二次点击会拿同一个旧状态
 * 算出同一个 `done` 值 —— 那不是「切换」，是把第一次的结果又发了一遍。
 */
async function onToggleDone(id: string): Promise<void> {
  if (busyIds.value.has(id)) {
    return;
  }
  const idea = ideas.value.find((item) => item.id === id);
  if (!idea) {
    return;
  }
  const release = markBusy(id);
  try {
    const updated = await apiSend<Idea>('POST', `/ideas/${id}/complete`, { done: !idea.done });
    applyLocalUpsert(updated);
  } catch (error) {
    reportError(error, '更新完成状态失败');
  } finally {
    release();
  }
}

/**
 * 保存正文。
 *
 * 用返回的想法覆盖本地（`updatedAt` 由后端给，本地不猜）。
 *
 * @returns `'saved'` 存好了；`'failed'` 失败（已弹提示，编辑框留着用户的草稿）；
 *   `'busy'` **这次压根没发出去**（被在飞保护挡下了）——
 *   调用方要据此重新入队、等在飞结束再补存。
 *   不能笼统地返回 false：那样调用方分不清「存失败」和「没发出去」，
 *   被挡下的那次改动就会既没落库也没有提示 —— 静默丢。
 */
async function onSaveContent(id: string, content: string): Promise<SaveOutcome> {
  if (busyIds.value.has(id)) {
    return 'busy';
  }
  const release = markBusy(id);
  try {
    const updated = await apiSend<Idea>('PATCH', `/ideas/${id}`, { content });
    applyLocalUpsert(updated);
    return 'saved';
  } catch (error) {
    reportError(error, '保存失败');
    return 'failed';
  } finally {
    release();
  }
}

/**
 * 保存标签（覆盖式：给的就是全部标签）。
 *
 * @returns `'saved'` / `'failed'` / `'busy'`，同 {@link onSaveContent}。
 */
async function onSaveTags(id: string, tags: string[]): Promise<SaveOutcome> {
  if (busyIds.value.has(id)) {
    return 'busy';
  }
  const release = markBusy(id);
  try {
    const updated = await apiSend<Idea>('PUT', `/ideas/${id}/tags`, { tags });
    applyLocalUpsert(updated);
    return 'saved';
  } catch (error) {
    reportError(error, '保存标签失败');
    return 'failed';
  } finally {
    release();
  }
}

/**
 * 删除一条想法（进回收站）。
 *
 * 删除成功才把它从本地列表里去掉：失败时条目还在，用户还能重试。
 */
async function onDelete(id: string): Promise<void> {
  if (busyIds.value.has(id)) {
    return;
  }
  const release = markBusy(id);
  try {
    await apiSend<void>('DELETE', `/ideas/${id}`);
    removeLocal(id);
  } catch (error) {
    reportError(error, '删除失败');
  } finally {
    release();
  }
}

/**
 * 标签补全：同步给当前已知的候选，同时在后台补一次请求。
 *
 * 补全结果是异步的（要发请求），而组件要的是同步的候选列表：
 * 结果写进 `suggestionCache`（响应式），组件那边会跟着重画。
 */
function suggestTags(prefix: string): string[] {
  const key = prefix.trim().toLowerCase();
  if (!key) {
    return [];
  }
  const cached = suggestionCache[key];
  if (cached) {
    return cached;
  }
  if (!requestedPrefixes.has(key)) {
    requestedPrefixes.add(key);
    void fetchSuggestions(key, prefix.trim());
  }
  return [];
}

/** 拉一次标签补全，结果写进缓存。 */
async function fetchSuggestions(key: string, prefix: string): Promise<void> {
  try {
    const list = await apiGet<string[]>('/tags/suggest', {
      projectId: currentProjectId.value ?? undefined,
      prefix,
    });
    suggestionCache[key] = list;
  } catch (error) {
    // 取不到就当没有建议，并且允许下次重试。
    requestedPrefixes.delete(key);
    reportError(error, '获取标签建议失败');
  }
}

/** 清掉标签补全缓存（换项目时调用：候选是按项目给的）。 */
function clearSuggestions(): void {
  for (const key of Object.keys(suggestionCache)) {
    delete suggestionCache[key];
  }
  requestedPrefixes.clear();
}

/** 勾选/取消勾选一条想法。 */
function onToggleSelect(id: string): void {
  const next = new Set(selectedIds.value);
  if (next.has(id)) {
    next.delete(id);
  } else {
    next.add(id);
  }
  selectedIds.value = next;
}

/**
 * 勾选中的想法，按列表顺序。
 *
 * 顺序与后端合并时的拼接顺序一致（`updated_at ASC, id ASC`），
 * 所以合并对话框里看到的拼接结果就是后端会拼出来的那份。
 */
const selectedIdeas = computed(() =>
  sortIdeas(ideas.value.filter((idea) => selectedIds.value.has(idea.id))),
);

/** 批量标记完成 / 撤销完成。 */
async function onBulkComplete(done: boolean): Promise<void> {
  const ids = [...selectedIds.value];
  if (ids.length === 0 || batchBusy.value) {
    return;
  }
  batchBusy.value = true;
  try {
    const updated = await apiSend<Idea[]>('POST', '/ideas/bulk/complete', { ids, done });
    updated.forEach((idea) => applyLocalUpsert(idea));
  } catch (error) {
    reportError(error, done ? '批量标记完成失败' : '批量撤销完成失败');
  } finally {
    batchBusy.value = false;
  }
}

/**
 * 批量加标签。
 *
 * 并集式追加（`replace: false`）：原有的标签留着，不是覆盖。
 *
 * @param tags 要加的标签。
 * @param ack 回执：告诉操作条成没成（操作条等回执才清草稿 ——
 *   失败时用户敲的标签得留着，能直接重试）。
 */
async function onBulkAddTags(tags: string[], ack: (ok: boolean) => void): Promise<void> {
  const ids = [...selectedIds.value];
  if (ids.length === 0 || tags.length === 0 || batchBusy.value) {
    ack(false);
    return;
  }
  batchBusy.value = true;
  let ok = false;
  try {
    const updated = await apiSend<Idea[]>('POST', '/ideas/bulk/tags', {
      ids,
      tags,
      replace: false,
    });
    updated.forEach((idea) => applyLocalUpsert(idea));
    ok = true;
  } catch (error) {
    reportError(error, '批量加标签失败');
  } finally {
    batchBusy.value = false;
  }
  ack(ok);
}

/**
 * 批量删除（进回收站）。
 *
 * 后端返回被软删的那些想法：按返回的移除本地列表，
 * 删掉的条目不在列表里了，勾选也就没意义。
 */
async function onBulkDelete(): Promise<void> {
  const ids = [...selectedIds.value];
  if (ids.length === 0 || batchBusy.value) {
    return;
  }
  batchBusy.value = true;
  try {
    const deleted = await apiSend<Idea[]>('POST', '/ideas/bulk/delete', { ids });
    deleted.forEach((idea) => removeLocal(idea.id));
    selectedIds.value = new Set();
  } catch (error) {
    reportError(error, '批量删除失败');
  } finally {
    batchBusy.value = false;
  }
}

/**
 * 合并：源条目硬删，生成一条全新的想法。
 *
 * 合并结果出现在列表最下面（它是最新的），用户多半没看见 ——
 * 把它的 id 记进 `focusIdeaId`，F11 拿它滚动定位过去。
 */
async function onMergeConfirm(content: string): Promise<void> {
  // 用 `selectedIdeas` 的顺序（列表顺序），不是勾选顺序 ——
  // 合并对话框里的预览就是这个顺序，两个对不上会误导排查。
  const ids = selectedIdeas.value.map((idea) => idea.id);
  if (ids.length < 2 || batchBusy.value) {
    return;
  }
  batchBusy.value = true;
  try {
    const merged = await apiSend<Idea>('POST', '/ideas/merge', { ids, content });
    ids.forEach((id) => removeLocal(id));
    applyLocalUpsert(merged);
    selectedIds.value = new Set();
    mergeOpen.value = false;
    focusIdeaId.value = merged.id;
  } catch (error) {
    reportError(error, '合并失败');
  } finally {
    batchBusy.value = false;
  }
}

/** 取消勾选。 */
function onClearSelection(): void {
  selectedIds.value = new Set();
}

onMounted(() => {
  void init();
});
</script>

<template>
  <main class="home">
    <header class="bar">
      <h1 class="bar__title">想法</h1>
      <label class="bar__project">
        项目
        <select class="bar__select" :value="currentProjectId ?? ''" @change="onProjectChange">
          <option value="">全部项目</option>
          <option v-for="project in projects" :key="project.id" :value="project.id">
            {{ project.name }}（{{ project.ideaCount }}）
          </option>
        </select>
      </label>
    </header>

    <FilterBar :filter="activeFilter" :all-tags="allTags" @update:filter="onFilterUpdate" />

    <p v-if="loading" class="hint">加载中…</p>

    <MessageList
      :ideas="visibleIdeas"
      :selected-ids="selectedIds"
      :busy-ids="busyIds"
      :at-bottom="atBottom"
      :suggest="suggestTags"
      :on-save-content="onSaveContent"
      :on-save-tags="onSaveTags"
      @toggle-select="onToggleSelect"
      @toggle-done="onToggleDone"
      @delete="onDelete"
    />

    <Composer :on-submit="onSubmit" />

    <BatchBar
      v-if="selectedIds.size > 0"
      :count="selectedIds.size"
      :busy="batchBusy"
      :suggest="suggestTags"
      @complete="onBulkComplete(true)"
      @undo-complete="onBulkComplete(false)"
      @merge="mergeOpen = true"
      @delete="onBulkDelete"
      @add-tags="onBulkAddTags"
      @clear="onClearSelection"
    />

    <MergeDialog
      :open="mergeOpen"
      :ideas="selectedIdeas"
      @confirm="onMergeConfirm"
      @cancel="mergeOpen = false"
    />
  </main>
</template>

<style scoped>
.home {
  display: flex;
  flex-direction: column;
  gap: 12px;
  /* 宽度约束留给 F7 的两版布局（侧栏 + 内容），这里不写死居中容器。 */
  padding: 24px 16px;
}

.bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.bar__title {
  margin: 0;
  font-size: 20px;
}

.bar__project {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 13px;
}

.bar__select {
  min-width: 160px;
}

.hint {
  margin: 0;
  color: var(--muted);
}
</style>
