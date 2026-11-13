<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ApiError, apiGet, apiSend } from '../api/client';
import { connectEvents, type EventStream } from '../api/events';
import type { Idea, IdeaEvent, Project, Tag } from '../api/types';
import BatchBar from '../components/BatchBar.vue';
import Composer from '../components/Composer.vue';
import FilterBar from '../components/FilterBar.vue';
import MergeDialog from '../components/MergeDialog.vue';
import MessageList from '../components/MessageList.vue';
import ProjectSidebar from '../components/ProjectSidebar.vue';
import ProjectTabs from '../components/ProjectTabs.vue';
import { showToast } from '../components/ToastHost.vue';
import { useDeepLink } from '../composables/useDeepLink';
import { useScrollFollow } from '../composables/useScrollFollow';
import { useTagMode } from '../composables/useTagMode';
import { matchesFilter, sortIdeas, type IdeaFilter } from '../lib/filter';
import { applyEvent, applyResync, findAnchorIndex } from '../lib/merge';
import type { SaveOptions, SaveOutcome } from '../lib/save';
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
  projectSortMode,
  projects,
  saveSortMode,
  setCurrentProject,
} from '../stores/projects';
import type { ProjectSortMode } from '../stores/projects';

/**
 * AND/OR 偏好存在本地（每台设备各自的）。
 *
 * 组合式函数返回的是共享的 ref，所以切换模式后各处立刻一致。
 */
const { tagMode, setTagMode } = useTagMode();

/** 路由（项目管理的入口要用它跳转，深链也从这儿读）。 */
const router = useRouter();

/** 当前路由（深链 `?idea=<id>` 从 query 里读）。 */
const route = useRoute();

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
 *
 * ⚠️ 每次增删都**必须造一个全新的 Set**（见 {@link markBusy}）：
 * Vue 的响应式 Set 只追踪 `has`，不追踪 `add`/`delete` ——
 * 原地改的话整条在飞保护会静默失效（按钮不禁用、补存不再重入队）。
 */
const busyIds = ref<Set<string>>(new Set());

/** 标签补全缓存：前缀（小写）→ 候选标签。 */
const suggestionCache = reactive<Record<string, string[]>>({});

/** 已经发过补全请求的前缀（同一个前缀不重复发）。 */
const requestedPrefixes = new Set<string>();

/** 消息流的滚动容器（滚动跟随与深链定位都指着它）。 */
const scrollContainer = ref<HTMLElement | null>(null);

/**
 * 滚动跟随：贴底时新内容自动跟到底，用户往上翻看时不抢滚动。
 */
const { atBottom, onNewContent, scrollToBottom, pendingCount } = useScrollFollow(scrollContainer);

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

/**
 * **数据集**维度的过滤条件：只有项目维度。
 *
 * 状态与标签的过滤交给显示层（`visibleIdeas`）做。
 *
 * 为什么不能把显示层的过滤条件直接交给事件合并（`applyEvent`）：
 * 合并是作用在**数据集**（`ideas.value`）上的 —— 用显示层的条件合并，
 * 会把「改成不符合当前过滤」的条目从数据集里**彻底删掉**，
 * 用户切回别的过滤条件时那条就找不回来了（得整表重拉才回来）。
 * 这与「过滤在客户端做」的前提冲突。
 */
const datasetFilter = computed<IdeaFilter>(() => ({
  ...activeFilter.value,
  status: 'all',
  tags: [],
}));

/**
 * 深链定位：`?idea=<id>` 打开后滚到那条并高亮。
 *
 * 合并出来的新条目也走这一套（`focusIdeaId`）。
 * 定位发生在 DOM 更新之后（见 `useDeepLink` 的说明）。
 */
const { highlightedId, locateAnchor } = useDeepLink({
  container: scrollContainer,
  anchorIds: () => [focusIdeaId.value, deepLinkId()],
  isVisible: (id) => findAnchorIndex(visibleIdeas.value, id) >= 0,
  watchSources: [focusIdeaId, visibleIdeas, () => route.query.idea],
});

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

  // 打开页面就看到最新的一条（列表是从上往下长的，底部才是当下）。
  // 等一拍再滚：内容这时才刚进 DOM。
  await nextTick();
  // 有深链锚点时**不能**滚到底：那会把刚定位到的那条挤出视野。
  // 定位由深链逻辑负责（它也在 DOM 更新之后才动手）。
  if (!locateAnchor()) {
    scrollToBottom();
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
    await reloadList(() => loadIdeas(next));
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
 * 入口是项目侧栏（宽屏）与项目 Tab（窄屏）—— 两者渲染的是同一份数据。
 *
 * 拉取失败时把项目选择**回滚**（见 {@link switchProject}）：
 * 数据还停在上一个项目，`filter.projectId` 若停在新项目，
 * 旧数据会被过滤成空列表 —— 两边必须对齐。
 */
async function onSelectProject(id: string | null): Promise<void> {
  const previous = currentProjectId.value;
  if (id === previous) {
    return;
  }
  const result = await switchProject(id);
  if (result.ok) {
    return;
  }
  if (isTooManyIdeas(result.error)) {
    // 切「全部项目」会一次要全库，超了上限 —— 这不是普通的失败，
    // 改切最近有活动的项目（补救在 recoverFromTooMany 里）。
    await recoverFromTooMany(result.error, id);
    return;
  }
  reportError(result.error, '加载想法失败');
}

/**
 * 去项目管理页。
 *
 * 新建项目、项目改名/删除这些是**管理**动作，归项目管理页（Task F8）。
 * 这一轮只做布局与接线，所以这两个入口先指向那里。
 */
function onManageProjects(): void {
  void router.push('/projects');
}

/**
 * 拖拽重排项目（只有「手动排序」模式下才拖得动）。
 *
 * 顺序存在后端：重排成功后用返回的项目列表覆盖本地（后端重写了全表的
 * sort_order，返回值才是真的顺序）。
 */
async function onReorderProjects(ids: string[]): Promise<void> {
  try {
    projects.value = await apiSend<Project[]>('POST', '/projects/reorder', { ids });
  } catch (error) {
    reportError(error, '重排项目失败');
    // 顺序没改成功，本地显示的顺序可能已经跟后端对不上了：拉回来。
    // 拉取也可能失败 —— 那就再报一条，别让它变成没人管的 rejection。
    try {
      await loadProjects();
    } catch (reloadError) {
      reportError(reloadError, '加载项目失败');
    }
  }
}

/**
 * 切换项目排序方式。
 *
 * 排序方式存在后端（跨设备共享），所以改完要重新拉一次项目列表 ——
 * 两种方式下后端给的顺序不一样。
 */
async function onChangeSortMode(mode: ProjectSortMode): Promise<void> {
  try {
    await saveSortMode(mode);
    await loadProjects();
  } catch (error) {
    reportError(error, '切换排序方式失败');
  }
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
    const before = visibleIdeas.value.length;
    applyLocalUpsert(created);
    if (visibleIdeas.value.length > before) {
      // 自己刚记的那条：贴底就跟着滚（不然它落在屏幕外），没贴底就只记一笔。
      // 滚动在 DOM 更新之后才做（`onNewContent` 内部会等）。
      void onNewContent();
    }
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
 * @param options.silent 失败时不弹提示（自动补存用：用户没改任何东西，
 *   只是把上次没存上的那版捡回来再存一次，不该看到「保存标签失败」）。
 * @returns `'saved'` / `'failed'` / `'busy'`，同 {@link onSaveContent}。
 */
async function onSaveTags(
  id: string,
  tags: string[],
  options?: SaveOptions,
): Promise<SaveOutcome> {
  if (busyIds.value.has(id)) {
    return 'busy';
  }
  const release = markBusy(id);
  try {
    const updated = await apiSend<Idea>('PUT', `/ideas/${id}/tags`, { tags });
    applyLocalUpsert(updated);
    return 'saved';
  } catch (error) {
    if (!options?.silent) {
      reportError(error, '保存标签失败');
    }
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

/* ------------------------------------------------------------------ *
 * SSE：别的设备改了东西，这里同步过来
 * ------------------------------------------------------------------ */

/** 事件流连接（页面活着的时候一直在）。 */
let stream: EventStream | null = null;

/**
 * 列表正在被整表替换（首屏、切项目、resync）。
 *
 * 替换的这段时间里可能又来了事件 —— 整表替换会把它们抹掉
 * （服务端给的是它读库那一刻的快照），所以先攒着，替换完再补回去。
 */
let reloading = false;

/** 整表替换期间攒着的事件。 */
const queuedEvents: IdeaEvent[] = [];

/**
 * 把一条事件合并进列表。
 *
 * 想法事件走 {@link applyEvent}（按**数据集**的过滤条件决定显不显示 ——
 * 只有项目维度，状态/标签留给显示层）；项目事件归项目列表管（这里只更新项目，不动想法）。
 */
function applyEventToList(event: IdeaEvent): void {
  if (event.type.startsWith('project.')) {
    void onProjectEvent(event);
    return;
  }
  // 想法变了，它所属项目的条数也跟着变：事件快照里没有条数，只能自己加减。
  adjustProjectCount(event.project?.id, countDelta(event));
  const before = visibleIdeas.value.length;
  ideas.value = applyEvent(ideas.value, event, datasetFilter.value);
  if (visibleIdeas.value.length > before) {
    // 有新条目进来了：贴底就跟着滚，没贴底就只记一笔（「↓ 有 N 条新内容」）。
    // 滚动在 DOM 更新之后才做（`onNewContent` 内部会等）。
    void onNewContent();
  }
}

/**
 * 一条想法事件让项目条数变了多少。
 *
 * 合并事件：源条目被**硬删**了（-N），合并出来的是新的一条（+1）。
 */
function countDelta(event: IdeaEvent): number {
  switch (event.type) {
    case 'idea.created':
    case 'idea.restored':
      return 1;
    case 'idea.deleted':
      return -1;
    case 'idea.merged':
      return 1 - (event.mergedFrom?.length ?? 0);
    default:
      return 0;
  }
}

/**
 * 把某个项目的条数调一调（想法增删时用）。
 *
 * 项目的条数与最近活动时间只有列表端点给，事件快照里没有 ——
 * 只能按事件自己加减。
 */
function adjustProjectCount(projectId: string | undefined, delta: number): void {
  if (!projectId || delta === 0) {
    return;
  }
  const index = projects.value.findIndex((project) => project.id === projectId);
  if (index < 0) {
    return;
  }
  const next = [...projects.value];
  const count = Math.max(0, next[index].ideaCount + delta);
  next[index] = { ...next[index], ideaCount: count };
  projects.value = next;
}

/** 收到一条业务事件。 */
function onEvent(event: IdeaEvent): void {
  if (reloading) {
    // 列表马上要被整表换掉：先攒着，换完再补（不然这条变更就丢了）。
    queuedEvents.push(event);
    return;
  }
  applyEventToList(event);
}

/**
 * 整表重拉列表。
 *
 * 重拉期间来的事件不能丢：整表替换会把它们抹掉，所以替换完再补回去。
 *
 * @param load 真正去拉列表的活（首屏、切项目、resync 各带自己的参数）
 */
async function reloadList(load: () => Promise<void>): Promise<void> {
  reloading = true;
  try {
    await load();
  } finally {
    reloading = false;
    // 补回刚才攒着的事件：一条都不丢。
    const queued = queuedEvents.splice(0);
    for (const event of queued) {
      applyEventToList(event);
    }
  }
}

/**
 * 项目事件：改名、重排、新建、删除。
 *
 * 快照里只有 id、名字与排序位置（没有想法计数与最近活动时间），
 * 所以能就地更新的就地更新，剩下的重新拉一次项目列表。
 *
 * 这一层**兜住所有失败**：事件是异步送来的，抛出去就变成没人管的
 * Promise rejection（页面上什么也看不到），失败必须给一条提示。
 */
async function onProjectEvent(event: IdeaEvent): Promise<void> {
  try {
    await applyProjectEvent(event);
  } catch (error) {
    reportError(error, '处理项目变更失败');
  }
}

/** 项目事件的真正处理（失败由 {@link onProjectEvent} 兜底）。 */
async function applyProjectEvent(event: IdeaEvent): Promise<void> {
  const snapshot = event.project;
  if (!snapshot) {
    return;
  }
  if (event.type === 'project.deleted') {
    // 项目进回收站了：它下面的想法也进回收站（后端会另外发 idea.deleted）。
    projects.value = projects.value.filter((project) => project.id !== snapshot.id);
    if (currentProjectId.value === snapshot.id) {
      // 当前就在这个项目里：切回「全部项目」，范围变了要重新拉列表。
      // 切不过去时列表还停在已删项目的旧数据上 —— 得告诉用户，别静默停在错的数据上。
      const result = await switchProject(null);
      if (!result.ok) {
        reportError(result.error, '切换项目失败');
      }
    }
    return;
  }
  const index = projects.value.findIndex((project) => project.id === snapshot.id);
  if (index < 0) {
    // 本地没有这个项目（新建的，或从回收站恢复的）：拉一次完整的列表。
    try {
      await loadProjects();
    } catch (error) {
      reportError(error, '加载项目失败');
    }
    return;
  }
  // 就地更新：名字与排序位置快照里都有。
  const next = [...projects.value];
  next[index] = { ...next[index], name: snapshot.name, sortOrder: snapshot.sortOrder };
  projects.value =
    projectSortMode.value === 'manual'
      ? // 手动排序：顺序就是 sortOrder 排的。
        [...next].sort((left, right) => left.sortOrder - right.sortOrder)
      : // 最近活动排序：快照里没有最近活动时间，排不了，保持原位。
        next;
}

/**
 * 服务端说「你可能漏了事件」：整表重拉一次。
 *
 * 重拉之后**不动滚动位置** —— 用户翻到哪儿还是哪儿。
 */
async function onResync(): Promise<void> {
  try {
    await reloadList(async () => {
      const fresh = await apiGet<Idea[]>('/ideas', { projectId: currentProjectId.value ?? undefined });
      ideas.value = applyResync(ideas.value, fresh);
    });
    // 项目条数是按事件加减的，漏过事件就会漂：顺带拉一次项目列表校准。
    await loadProjects();
  } catch (error) {
    reportError(error, '刷新列表失败');
  }
}

/* ------------------------------------------------------------------ *
 * 深链定位：`?idea=<id>` 打开后滚到那条并高亮
 * ------------------------------------------------------------------ */

/** 深链参数里的条目 id。 */
function deepLinkId(): string | null {
  const value = route.query.idea;
  return typeof value === 'string' && value.length > 0 ? value : null;
}

onMounted(() => {
  void init();
  stream = connectEvents({ onEvent, onResync });
});

onBeforeUnmount(() => {
  // 离开页面就把连接关了：不然事件流会一直往一个没人看的页面上推。
  stream?.close();
  stream = null;
});
</script>

<template>
  <!--
    两版布局：同一份项目数据渲染两次（宽屏侧栏、窄屏 Tab），
    互斥显示由下面的媒体查询决定（断点 1024px）。
  -->
  <main class="home">
    <aside class="home__sidebar">
      <ProjectSidebar
        :projects="projects"
        :current-id="currentProjectId"
        :mode="projectSortMode"
        @select="onSelectProject"
        @new-project="onManageProjects"
        @reorder="onReorderProjects"
        @change-mode="onChangeSortMode"
      />
    </aside>

    <section class="home__content">
      <header class="bar">
        <h1 class="bar__title">想法</h1>
      </header>

      <div class="home__tabs">
        <ProjectTabs
          :projects="projects"
          :current-id="currentProjectId"
          :mode="projectSortMode"
          @select="onSelectProject"
          @new-project="onManageProjects"
          @open-menu="onManageProjects"
        />
      </div>

      <div class="home__filters">
        <FilterBar :filter="activeFilter" :all-tags="allTags" @update:filter="onFilterUpdate" />
      </div>

      <div class="home__body">
        <div ref="scrollContainer" class="home__scroll">
          <p v-if="loading" class="hint">加载中…</p>

          <MessageList
            :ideas="visibleIdeas"
            :selected-ids="selectedIds"
            :busy-ids="busyIds"
            :at-bottom="atBottom"
            :highlight-id="highlightedId"
            :suggest="suggestTags"
            :on-save-content="onSaveContent"
            :on-save-tags="onSaveTags"
            @toggle-select="onToggleSelect"
            @toggle-done="onToggleDone"
            @delete="onDelete"
          />
        </div>

        <!--
          用户往上翻看时来了新内容：不能抢滚动，但要告诉他有几条新的。
        -->
        <button
          v-if="pendingCount > 0"
          class="new-content"
          type="button"
          @click="scrollToBottom"
        >
          ↓ 有 {{ pendingCount }} 条新内容
        </button>

        <!--
          批量操作条**随内容流**排（不 fixed）：固定在底部会盖住下面的输入框，
          页面滚到底部时用户想接着记一条都点不到「记下」。
        -->
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
      </div>

      <Composer :on-submit="onSubmit" />
    </section>

    <MergeDialog
      :open="mergeOpen"
      :ideas="selectedIdeas"
      @confirm="onMergeConfirm"
      @cancel="mergeOpen = false"
    />
  </main>
</template>

<style scoped>
/*
 * 宽度约束：这一轮定成「侧栏 + 内容」两栏，整体居中；
 * 内容栏不写死宽度，但有上限 —— 太宽的话一行字太长，不好读。
 */
.home {
  display: flex;
  gap: 20px;
  height: 100vh;
  /* 移动端浏览器地址栏收起/展开时高度会变，dvh 更准（不支持时用上面的 vh）。 */
  height: 100dvh;
  overflow: hidden;
  justify-content: center;
  padding: 16px;
}

.home__sidebar {
  flex: 0 0 220px;
  min-width: 0;
  min-height: 0;
}

.home__content {
  display: flex;
  flex-direction: column;
  gap: 10px;
  flex: 1 1 auto;
  min-width: 0;
  min-height: 0;
  max-width: 880px;
}

.bar {
  order: 1;
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.bar__title {
  margin: 0;
  font-size: 20px;
}

/* 窄屏：项目收成顶部横向 Tab；宽屏：这一块不显示（见下面的媒体查询）。 */
.home__tabs {
  order: 2;
  min-width: 0;
}

/* 宽屏：过滤栏在消息流上面。 */
.home__filters {
  order: 2;
}

.home__body {
  order: 3;
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 8px;
  flex: 1 1 auto;
  min-height: 0;
}

/* 消息流的滚动容器：只有它滚，标题与输入框始终留在原地。 */
.home__scroll {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
}

/* 「↓ 有 N 条新内容」：浮在消息流底部，不抢滚动。 */
.new-content {
  position: absolute;
  bottom: 8px;
  left: 50%;
  z-index: 6;
  transform: translateX(-50%);
  padding: 4px 12px;
  border-color: var(--accent);
  background: var(--panel);
  box-shadow: var(--shadow);
  font-size: 13px;
  white-space: nowrap;
}

/* 输入框永远在最下面（宽屏也在下面，与窄屏一致）。 */
.composer {
  order: 4;
}

.hint {
  margin: 0;
  color: var(--muted);
}

/* 窄屏（<1024px）：侧栏收成顶部 Tab，过滤栏挪到输入框上面。 */
@media (max-width: 1023px) {
  .home {
    flex-direction: column;
    gap: 10px;
    /* 竖着排的时候 justify-content 管的是竖直方向：内容要从上往下排。 */
    justify-content: flex-start;
    padding: 12px;
  }

  .home__sidebar {
    display: none;
  }

  .home__tabs {
    display: block;
    order: 2;
  }

  .home__body {
    order: 3;
  }

  /* 过滤栏移到输入框上方（窄屏上方的横向空间留给 Tab）。 */
  .home__filters {
    order: 4;
  }

  .composer {
    order: 5;
  }
}

/* 宽屏（≥1024px）：左侧项目栏 + 中间消息流。 */
@media (min-width: 1024px) {
  .home__sidebar {
    display: block;
  }

  .home__tabs {
    display: none;
  }
}
</style>
