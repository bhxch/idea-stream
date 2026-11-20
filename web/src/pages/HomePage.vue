<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { ApiError, apiGet, apiSend } from '../api/client';
import { connectEvents, type EventStream } from '../api/events';
import type { Idea, IdeaEvent, Project, Tag } from '../api/types';
import BatchBar from '../components/BatchBar.vue';
import Composer from '../components/Composer.vue';
import FilterBar from '../components/FilterBar.vue';
import MergeDialog from '../components/MergeDialog.vue';
import MessageList from '../components/MessageList.vue';
import MoreMenu from '../components/MoreMenu.vue';
import ProjectDropdown from '../components/ProjectDropdown.vue';
import ProjectSidebar from '../components/ProjectSidebar.vue';
import { showToast } from '../components/ToastHost.vue';
import { useDeepLink } from '../composables/useDeepLink';
import { useHistoryScroll, type ViewportAnchor } from '../composables/useHistoryScroll';
import { useScrollFollow } from '../composables/useScrollFollow';
import { useTagMode } from '../composables/useTagMode';
import { sortIdeas, type IdeaFilter } from '../lib/filter';
import { reportError } from '../lib/errors';
import { applyEvent, findAnchorIndex } from '../lib/merge';
import type { SaveOptions, SaveOutcome } from '../lib/save';
import {
  applyLocalUpsert,
  focusIdeaId,
  ideas,
  loadedLimit,
  loadIdeas,
  loading,
  loadingMore,
  removeLocal,
  scrolledItems,
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

/** 过滤条件。项目、状态、标签、关键词四者取交集。 */
const filter = ref<IdeaFilter>({
  projectId: currentProjectId.value,
  status: 'all',
  tags: [],
  tagMode: tagMode.value,
  keyword: '',
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
 * 过滤栏的组件引用（窄屏顶栏的 🔍 要聚焦它的搜索框）。
 *
 * 类型只写暴露出来的那一个方法 —— `<script setup>` 里拿不到组件的完整类型，
 * 用 `InstanceType<typeof FilterBar>` 反而绕。
 */
const filterBarRef = ref<{ focusSearch: () => void } | null>(null);

/**
 * 滚动跟随：贴底时新内容自动跟到底，用户往上翻看时不抢滚动。
 */
const { atBottom, onNewContent, scrollToBottom, pendingCount } = useScrollFollow(scrollContainer);

/** 真正生效的过滤条件：模式以 localStorage 里的为准。 */
const activeFilter = computed<IdeaFilter>(() => ({ ...filter.value, tagMode: tagMode.value }));

/**
 * 防抖重拉的时长（毫秒）。
 *
 * 搜索框每敲一个键都会改过滤条件，不防抖就是每敲一下打一次后端。
 */
const RELOAD_DEBOUNCE_MS = 250;

/**
 * 待执行的重拉（过滤条件变了）。
 *
 * 过滤条件变了 → 重置到一页 + 滚到底部；
 * SSE 事件 → 保留已加载的深度（见 {@link scheduleResync}）。
 *
 * ⚠️ 两种意图**各用各的计时器**：共用一个的话，SSE 事件会把还没执行的
 * 「条件变化」重拉顶掉 —— 条件变了就该重置到一页、滚到底部，
 * 那个重拉不能被顶掉（顶掉了用户看到的还是旧条件的结果）。
 */
let reloadTimer: ReturnType<typeof setTimeout> | undefined;

/** 待执行的重拉（SSE 事件）。 */
let resyncTimer: ReturnType<typeof setTimeout> | undefined;

/**
 * 换项目时自己已经拉过一遍了，别让 watch 再拉一次。
 *
 * `switchProject` 要**同步**改 `filter.value`（失败时要能回滚）并立刻拉一次；
 * watch 看到条件变了也会排一次防抖重拉 —— 那是同一次拉取，白白发一个请求。
 */
let skipNextReload = false;

/**
 * 首屏还在准备（项目/标签/排序方式都还没到，列表还没开始拉）。
 *
 * store 里的 `loading` 只覆盖「列表请求在飞」那一段；首屏还有一段
 * 「前置请求在飞」的时间 —— 那时候数据集是空的，但**列表确实还没加载完**。
 * 这段时间里不能渲染空状态（见 {@link listLoading}）。
 */
const initialLoading = ref(true);

/**
 * 列表是不是还在加载。
 *
 * 加载中不渲染空状态：数据没到就先闪一句「这里还没有想法。」，
 * 等于凭空告诉用户「你什么都没记过」。
 *
 * 但**列表已经有内容**时不闪「加载中…」：现在列表会在过滤条件变化、
 * SSE 事件到达时重拉（那是后台刷新，不是「首次加载」）—— 每来一个事件
 * 就闪一下「加载中…」是噪声。
 */
const listLoading = computed(
  () => initialLoading.value || (loading.value && ideas.value.length === 0),
);

/**
 * 列表内容。
 *
 * 只排序，**不再过滤**：服务端返回的数组已经是过滤过的（项目 / 状态 / 标签 /
 * AND-OR / 关键词都在服务端过滤，见设计文档 §13.1）。
 *
 * 为什么显示层不再按关键词过滤一遍：前端的子串判断比服务端的「词元相邻」
 * 更**严格** —— 正文「咖 啡」服务端判定命中（空格只是分隔符），前端
 * `includes("咖啡")` 不命中 → 再过滤一遍就把服务端判定命中的条目**误杀**了。
 */
const visibleIdeas = computed(() => sortIdeas(ideas.value));

/**
 * 过滤条件变了 → 防抖后重拉（一次请求拿最终结果）。
 *
 * 过滤在服务端做，所以条件一变就得重新要一份**过滤后的**列表
 * （设计文档 §13.5）。
 */
function scheduleReload(): void {
  cancelReloadTimer();
  reloadTimer = setTimeout(() => {
    reloadTimer = undefined;
    void reloadAndReport(async () => {
      await loadIdeas(activeFilter.value);
      // 条件变了 → 命中集合也变了，之前的浏览深度没有意义 → 滚到底部。
      await nextTick();
      scrollToBottom();
    }, '加载想法失败');
  }, RELOAD_DEBOUNCE_MS);
}

/**
 * SSE 事件到了 → 防抖后重拉（保留已加载的深度）。
 *
 * 深度必须保留：用户往上翻了三页历史，重拉后历史没了的话视口会突然跳回
 * 最新那批（设计文档 §13.5）。
 */
function scheduleResync(): void {
  cancelResyncTimer();
  resyncTimer = setTimeout(() => {
    resyncTimer = undefined;
    void reloadAndReport(async () => {
      // 重拉之前先记住视口在哪：重拉回来的列表顶部会多出更旧的条目
      // （`limit` 按「往上翻过的深度」给，比已加载的条数多），
      // 内容从上面长出来，视口会被推着走。
      const wasAtBottom = atBottom.value;
      const anchor = captureViewportAnchor();
      await loadIdeas(activeFilter.value, loadedLimit(scrolledItems.value));
      await nextTick();
      if (wasAtBottom) {
        // 贴着底部：跟着滚到底（当下就在最下面）。
        scrollToBottom();
        return;
      }
      // 在上翻：把视口钉回原来那条（用户没滚动，看到的内容不该变）。
      restoreViewportAnchor(anchor);
    }, '刷新列表失败');
  }, RELOAD_DEBOUNCE_MS);
}

/**
 * 记下视口顶部那条（第一个露出来的条目）。
 *
 * @returns 锚点；列表是空的（或没有条目）时返回 null。
 */
function captureViewportAnchor(): ViewportAnchor | null {
  const container = scrollContainer.value;
  if (!container) {
    return null;
  }
  const containerTop = container.getBoundingClientRect().top;
  for (const element of container.querySelectorAll<HTMLElement>('[data-idea-id]')) {
    const rect = element.getBoundingClientRect();
    if (rect.bottom > containerTop) {
      const id = element.dataset.ideaId;
      if (id) {
        return { id, top: rect.top };
      }
    }
  }
  return null;
}

/**
 * 把视口钉回锚点那条。
 *
 * 钉不回去也没关系（那条已经被过滤掉了 / 不在列表里了）：那种情况下内容
 * 真的变了，不该硬钉。
 */
function restoreViewportAnchor(anchor: ViewportAnchor | null): void {
  const container = scrollContainer.value;
  if (!container || !anchor) {
    return;
  }
  const element = container.querySelector<HTMLElement>(`[data-idea-id="${anchor.id}"]`);
  if (!element) {
    return;
  }
  container.scrollTop += element.getBoundingClientRect().top - anchor.top;
}

/** 撤掉待执行的「条件变化」重拉。 */
function cancelReloadTimer(): void {
  if (reloadTimer !== undefined) {
    clearTimeout(reloadTimer);
    reloadTimer = undefined;
  }
}

/** 撤掉待执行的「SSE 事件」重拉。 */
function cancelResyncTimer(): void {
  if (resyncTimer !== undefined) {
    clearTimeout(resyncTimer);
    resyncTimer = undefined;
  }
}

/** 撤掉待执行的重拉（两种意图都撤）。 */
function cancelReloads(): void {
  cancelReloadTimer();
  cancelResyncTimer();
}

/**
 * 过滤条件变了 → 防抖后重拉。
 *
 * `activeFilter` 是 computed，每次求值都返回**新对象**：条件一变它的身份
 * 就变，watch 会触发。AND/OR 模式也在里面（它存在本机 localStorage 里，
 * 但过滤在服务端做，模式也得告诉服务端）。
 */
watch(activeFilter, () => {
  // 换项目时自己已经拉过了（见 {@link switchProject}），别再拉一次。
  if (skipNextReload) {
    skipNextReload = false;
    return;
  }
  scheduleReload();
});

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

/**
 * 首屏加载。
 *
 * 三件事互不依赖：项目列表、排序方式偏好、全部标签。
 * 排序方式只是个偏好，取不到也不该拖垮整屏 —— 用 `allSettled` 各自兜底，
 * 想法列表**单独**加载，列表挂了不影响别的提示。
 */
async function init(): Promise<void> {
  try {
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
  } finally {
    // 首屏的准备做完了：不管成没成，都不再算「加载中」。
    initialLoading.value = false;
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
  // 下面自己就要拉一次：告诉 watch 别再排一次（见 skipNextReload）。
  skipNextReload = true;
  filter.value = { ...filter.value, projectId: next };
  try {
    // 过滤条件全部带上（过滤在服务端做）：换项目只是范围变了，
    // 状态 / 标签 / 关键词得跟着走。
    await reloadList(() => loadIdeas({ ...activeFilter.value, projectId: next }));
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
    // 命中数超过单次返回上限 —— 这不是普通的失败，
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

/** 去回收站。 */
function goTrash(): void {
  void router.push('/trash');
}

/** 去 Webhook 页。 */
function goWebhooks(): void {
  void router.push('/webhooks');
}

/**
 * 窄屏顶栏的 🔍：滚回列表顶部并聚焦过滤行里的搜索框。
 *
 * ⚠️ **不引入第二套搜索状态** —— 聚焦的是过滤行里那个输入框，
 * 它绑的就是同一份 `filter.keyword`。
 */
function focusSearch(): void {
  void nextTick();
  // `scrollTo` 在 jsdom 里没有（浏览器才有），可选调用：没有就跳过，
  // 别让「滚回顶部」这件事把「聚焦搜索框」一起带崩。
  scrollContainer.value?.scrollTo?.({ top: 0 });
  filterBarRef.value?.focusSearch();
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
 * `too_many_ideas`（命中数超过单次返回上限）**只在没给 `limit` 时**才会出现 ——
 * 前端现在每次都带 `limit`，所以这条路径基本走不到；留着是兜底
 * （万一哪个入口漏发了 `limit`），别把它当成常事。
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
 * 提交的就是正文（想法没有标题了）；「全部项目」视图下不指定项目，
 * 后端会把它落到内置收件箱。
 *
 * @returns true 表示存好了（输入框据此清空），false 表示失败（内容保留在框里）。
 */
async function onSubmit(content: string): Promise<boolean> {
  try {
    const created = await apiSend<Idea>('POST', '/ideas', {
      projectId: currentProjectId.value,
      content,
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
 * 勾选中**且看得见**的想法，按列表顺序。
 *
 * 「看得见」= 在当前过滤条件下的可见列表里（`visibleIdeas`）。
 *
 * 为什么不是直接从数据集（`ideas.value`）里取：
 * 数据集不再剔除过滤项（F4 的修复），所以里面可能有「用户在视图里看不见、
 * 但还勾着」的条目 —— 比如它在别的设备上被标记完成了，在「未完成」视图里消失了，
 * 勾选却还留着。那种条目既不该计入批量条数，也不该被批量操作处理
 * （用户根本没看见它，却会被批量标记完成/删除）。
 *
 * 顺序与后端合并时的拼接顺序一致（`updated_at ASC, id ASC`），
 * 所以合并对话框里看到的拼接结果就是后端会拼出来的那份。
 */
const selectedIdeas = computed(() =>
  visibleIdeas.value.filter((idea) => selectedIds.value.has(idea.id)),
);

/**
 * 批量操作真正要处理的 id。
 *
 * 口径与批量条数一致：**看得见且勾选**的那些（见 {@link selectedIdeas}）。
 */
function selectedVisibleIds(): string[] {
  return selectedIdeas.value.map((idea) => idea.id);
}

/** 批量标记完成 / 撤销完成。 */
async function onBulkComplete(done: boolean): Promise<void> {
  const ids = selectedVisibleIds();
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
  const ids = selectedVisibleIds();
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
  const ids = selectedVisibleIds();
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
 * 往回翻历史：接近顶部就预加载更旧的一页
 * ------------------------------------------------------------------ */

/**
 * 往回翻的哨兵（消息流最上面的那个）。
 *
 * 往回翻的方向是**上**：看板本来就是聊天软件式的（打开页面停在最下边，
 * 底部是当下，往上才是历史），所以哨兵放在列表顶部 —— 它进入视野就说明
 * 用户快翻到顶了。
 */
const historySentinel = ref<HTMLElement | null>(null);

/**
 * 往回翻历史的无限滚动（接近顶部就预加载更旧的一页，prepend 后把视口钉回原处）。
 *
 * 这一块（哨兵观察、防重复、过期丢弃、视口修正）与页面其余部分没有共享状态，
 * 整块搬进了 composable —— HomePage 里只留两个 ref 与两个生命周期入口。
 *
 * ⚠️ 视口补偿有**两套**（规格 §6.5）：composable 里那套是高度差补偿
 * （偶数 prepend 时精确）；这里再把锚点那套传进去 —— 到底那一页是奇数、
 * 边界排换边时高度差补偿不精确，锚点校正按条目 id 把视口钉回原处。
 */
const history = useHistoryScroll({
  container: scrollContainer,
  sentinel: historySentinel,
  filter: () => activeFilter.value,
  reportError,
  captureAnchor: captureViewportAnchor,
  restoreAnchor: restoreViewportAnchor,
});

/* ------------------------------------------------------------------ *
 * SSE：别的设备改了东西，这里同步过来
 * ------------------------------------------------------------------ */

/** 事件流连接（页面活着的时候一直在）。 */
let stream: EventStream | null = null;

/**
 * 还在飞的整表替换数（首屏、切项目、resync、SSE 重拉都算）。
 *
 * 替换的这段时间里可能又来了事件 —— 整表替换会把它们抹掉
 * （服务端给的是它读库那一刻的快照），所以先攒着，**全部**替换完再补回去。
 *
 * ⚠️ 必须是计数器而不是布尔：条件变化与 SSE 各有一个重拉入口（各自的
 * 防抖计时器，只撤自己那路），两发同时在飞是可能的 —— 先结束的那发把
 * 布尔置回 false，窗口里来的事件就会被合并进列表、然后被仍在飞的那发
 * 整表替换抹掉（该条目短暂消失，下一个事件才把它带回来）。数到零才算结束。
 */
let reloadingCount = 0;

/** 整表替换期间攒着的事件。 */
const queuedEvents: IdeaEvent[] = [];

/**
 * 把一条事件合并进列表。
 *
 * 想法事件走 {@link applyEvent}（按**当前**过滤条件决定显不显示 ——
 * 项目 / 状态 / 标签 / AND-OR / 关键词都算）；项目事件归项目列表管
 * （这里只更新项目，不动想法）。
 */
function applyEventToList(event: IdeaEvent): void {
  if (event.type.startsWith('project.')) {
    void onProjectEvent(event);
    return;
  }
  // 想法变了，它所属项目的条数也跟着变：事件快照里没有条数，只能自己加减。
  adjustProjectCount(event.project?.id, countDelta(event));
  const before = visibleIdeas.value.length;
  ideas.value = applyEvent(ideas.value, event, activeFilter.value);
  if (visibleIdeas.value.length > before) {
    // 有新条目进来了：贴底就跟着滚，没贴底就只记一笔（「↓ 有 N 条新内容」）。
    // 滚动在 DOM 更新之后才做（`onNewContent` 内部会等）。
    void onNewContent();
  }
  // 增量条目是用**近似**判断决定显不显示的（前端子串 vs 服务端词元相邻）——
  // 防抖后重拉一次，用服务端的权威结果覆盖本地（设计文档 §13.5）。
  scheduleResync();
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
  if (reloadingCount > 0) {
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
  reloadingCount += 1;
  try {
    await load();
  } finally {
    reloadingCount -= 1;
    if (reloadingCount === 0) {
      // 最后一发也结束了才补事件：还有别的发在飞的话，它们结束时一样会
      // 整表替换 —— 现在补进去也会被抹掉。攒着更稳，一条都不会丢。
      const queued = queuedEvents.splice(0);
      for (const event of queued) {
        applyEventToList(event);
      }
    }
  }
}

/**
 * 整表重拉 + 失败提示。
 *
 * 失败必须给一条提示：不然就是一次没人管的 Promise rejection —— 页面上什么
 * 也看不到，用户以为列表是最新的，其实停在了旧数据上。
 *
 * @param load 真正去拉列表的活。
 * @param message 失败时给用户的说明。
 */
async function reloadAndReport(load: () => Promise<void>, message: string): Promise<void> {
  try {
    await reloadList(load);
  } catch (error) {
    reportError(error, message);
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
 * 重拉之后**不动滚动位置** —— 用户翻到哪儿还是哪儿（所以要保留已加载的深度）。
 */
async function onResync(): Promise<void> {
  try {
    // 带上全部过滤条件（过滤在服务端做），并保留已加载的深度：
    // 重拉把翻过的历史抹掉的话，视口会突然跳回最新那批。
    await reloadList(() => loadIdeas(activeFilter.value, loadedLimit(scrolledItems.value)));
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
  // 往回翻的哨兵：接近顶部就预加载更旧的一页。
  history.observe();
});

onBeforeUnmount(() => {
  // 页面没了：在飞的往回翻请求回来之后就别再写 store 了（store 是模块级的，
  // 下一个页面还在用它，那一页没人看）—— composable 里的 disposed 标志管这个。
  history.dispose();
  // 离开页面就把连接关了：不然事件流会一直往一个没人看的页面上推。
  stream?.close();
  stream = null;
  // 待执行的重拉也别做了：页面都没了，重拉回来的数据没人看。
  cancelReloads();
});
</script>

<template>
  <!--
    两版布局：同一份项目数据渲染两次（宽屏侧栏、窄屏顶栏的项目下拉），
    互斥显示由下面的媒体查询决定（断点 1024px）。
  -->
  <main id="main-content" tabindex="-1" class="home">
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
      <header class="home__topbar">
        <h1 class="home__topbar-title">想法</h1>

        <!--
          窄屏的项目切换：顶栏里挤不下第二条 Tab 行，收成一个下拉。
          宽屏用侧栏，这里不重复（媒体查询互斥显示）。
        -->
        <div class="home__topbar-project">
          <ProjectDropdown
            :projects="projects"
            :current-id="currentProjectId"
            @select="onSelectProject"
          />
        </div>

        <div class="home__topbar-actions">
          <!-- 宽屏的搜索框在过滤行里，这个只在窄屏出现（不引第二套搜索状态）。 -->
          <button
            class="home__topbar-search"
            type="button"
            aria-label="搜索"
            @click="focusSearch"
          >
            🔍
          </button>
          <MoreMenu
            @manage-projects="onManageProjects"
            @trash="goTrash"
            @webhooks="goWebhooks"
          />
        </div>
      </header>

      <div class="home__body">
        <div ref="scrollContainer" class="home__scroll">
          <!--
            过滤行：宽屏钉在列表上方（sticky），窄屏随内容滚走。
            它过滤的是下面这片内容，放在列表上方才说得通（规格 §D7）。
          -->
          <div class="home__filters">
            <FilterBar
              ref="filterBarRef"
              :filter="activeFilter"
              :all-tags="allTags"
              @update:filter="onFilterUpdate"
            />
          </div>

          <p v-if="listLoading" class="hint">加载中…</p>

          <!--
            往回翻的哨兵：它在消息流的最**上面**（往回翻的方向是上）。
            接近顶部时它进入视野 → 预加载更旧的一页。
          -->
          <div ref="historySentinel" class="home__history-sentinel" aria-hidden="true"></div>

          <!-- 正在往回翻：给个提示（不然用户以为页面没反应）。 -->
          <p v-if="loadingMore" class="hint">加载更早的…</p>

          <MessageList
            :ideas="visibleIdeas"
            :loading="listLoading"
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

          下面那个活区（.sr-only）负责**报**这件事：按钮是条件渲染的，
          它冒出来时屏幕阅读器未必会念 —— 活区一直挂在 DOM 里，
          内容一变就会被念出来（异步更新要有 aria-live）。
        -->
        <p class="sr-only" role="status" aria-live="polite">
          <template v-if="pendingCount > 0">有 {{ pendingCount }} 条新内容</template>
        </p>
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

          条数与批量操作的口径一致：**看得见且勾选**的那些
          （看不见的勾选不算数，见 selectedIdeas）。
        -->
        <BatchBar
          v-if="selectedIdeas.length > 0"
          :count="selectedIdeas.length"
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
 * 宽度约束：这一轮改成「侧栏 + 铺满的主区」两栏。
 *
 * 之前整体 `justify-content: center`、内容栏还卡着 `max-width: 880px` ——
 * 1920px 的屏上左右各空约 400px，一屏能看到的条数一点没变。
 * 现在侧栏贴左边缘，主区铺满剩下的宽度。
 */
.home {
  display: flex;
  gap: 10px;
  height: 100vh;
  /* 移动端浏览器地址栏收起/展开时高度会变，dvh 更准（不支持时用上面的 vh）。 */
  height: 100dvh;
  overflow: hidden;
  padding: 0;
}

.home__sidebar {
  flex: 0 0 220px;
  min-width: 0;
  min-height: 0;
  /*
   * 侧栏**这一列**贴着窗口左边缘（`.home` 的 padding 归零了），
   * 但里面的内容别贴边：
   *   - 上：10px —— 标题那一行别顶到窗口上边缘，与主区顶栏对齐；
   *   - 左：8px —— 「项目」标题与选中项的高亮条别画在屏幕物理边缘上
   *     （看着像被裁掉；上下留白、左右不留就不对称了）。
   */
  padding: 10px 0 0 8px;
}

.home__content {
  display: flex;
  flex-direction: column;
  gap: 10px;
  flex: 1 1 auto;
  min-width: 0;
  min-height: 0;
  /* 主区四周留 12px（规格 §8.1）：侧栏贴左边缘，主区不贴右边缘。 */
  padding: 0 12px 12px;
}

/*
 * 顶栏：一行，钉在主区最上面。
 *
 * 左边是标题，窄屏再跟一个项目下拉，右边是动作区。
 */
.home__topbar {
  order: 1;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 0;
}

.home__topbar-title {
  margin: 0;
  font-size: 20px;
  /* 标题不参与压缩：空间不够时压项目名，不压标题。 */
  flex: none;
}

/* 窄屏才显示项目下拉（宽屏用侧栏，不重复）：见下面的媒体查询。 */
.home__topbar-project {
  display: none;
  /*
   * min-width: 0 —— 这一格允许被压窄（否则项目名很长时它顶不动别人，
   * 整个顶栏会撑出视口）；压窄之后由组件里的省略号截断名字。
   */
  min-width: 0;
}

.home__topbar-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
  /* 动作区不参与压缩：🔍 与 ⋯ 必须一直点得到。 */
  flex: none;
}

/* 宽屏的搜索框在过滤行里，这个只在窄屏出现。 */
.home__topbar-search {
  display: none;
  align-items: center;
  justify-content: center;
}

/*
 * 过滤行：宽屏钉在列表顶部，窄屏随内容滚走。
 *
 * 它在滚动容器（.home__scroll）里：宽屏用 sticky 把它钉在列表上方，
 * 窄屏用 static 让它随内容滚走 —— 钉死的每一行都是从内容里扣的钱
 * （规格 §D7）。
 */
.home__filters {
  position: sticky;
  top: 0;
  z-index: 5;
  padding: 6px 0;
  /* 钉住时背景要是不透明，滚上来的卡片会从它下面透出来。 */
  background: var(--bg);
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

/*
  往回翻的哨兵：不占地方，但要是个**看得见的**元素 ——
  零高度的块在浏览器里不好判定相交，1px 高的块最稳。
*/
.home__history-sentinel {
  height: 1px;
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

/* 窄屏（<1024px）：侧栏收成顶栏里的项目下拉，过滤行挪到列表顶部。 */
@media (max-width: 1023px) {
  .home {
    flex-direction: column;
    gap: 10px;
    /* 竖着排的时候 justify-content 管的是竖直方向：内容要从上往下排。 */
    justify-content: flex-start;
    /* 四周的留白由主区自己给（.home__content 的 12px），这里不再加一层。 */
    padding: 0;
  }

  .home__sidebar {
    display: none;
  }

  /* 窄屏的项目切换收成顶栏里的下拉（宽屏用侧栏，不重复）。 */
  .home__topbar-project {
    display: block;
  }

  /* 窄屏的 🔍：宽屏的搜索框在过滤行里，这个是给窄屏用的。 */
  .home__topbar-search {
    display: inline-flex;
  }

  .home__body {
    order: 3;
  }

  /*
    过滤行随内容滚走（不钉死）。
    ⚠️ 这里用的是 1023px（跟侧栏的断点一致：「窄屏」= 没有侧栏的那一档），
    过滤栏**形态**的切换（状态三态收成下拉）也是 1023px（见 FilterBar.vue），
    而**触摸目标**（点按不小于 32×32）跟两列流、悬停显形走 767/768px ——
    这两套断点并存是规格本身的样子（§D7 与 §D9/§6.10）。
  */
  .home__filters {
    position: static;
    background: none;
    padding: 0;
  }

  .composer {
    order: 5;
  }
}

/* 宽屏（≥1024px）：左侧项目栏 + 铺满的消息流。 */
@media (min-width: 1024px) {
  .home__sidebar {
    display: block;
  }
}

/*
 * 手机上点按目标不小于 32×32（规格 §6.10）。
 *
 * 顶栏的 🔍 只有一个符号，手指点不中 —— 窄屏上把它撑到 32px 高。
 * （下拉触发器与菜单项的触摸目标在各自组件里。）
 */
@media (max-width: 767px) {
  .home__topbar-search {
    min-width: 32px;
    min-height: 32px;
  }
}
</style>
