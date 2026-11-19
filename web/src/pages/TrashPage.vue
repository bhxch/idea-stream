<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { apiGet, apiSend } from '../api/client';
import type { Idea, Project, TrashEntry } from '../api/types';
import PageNav from '../components/PageNav.vue';
import { showToast } from '../components/ToastHost.vue';
import { reportError } from '../lib/errors';
import { formatRelativeTime } from '../lib/format';
import { loadProjects, projects } from '../stores/projects';

/**
 * 回收站里的条目。
 *
 * 两类互补、互不重叠（spec §10.5）：
 * - 容器（`kind === 'project'`）：已删的项目，下挂它下面仍在回收站里的想法；
 * - 顶层（`kind === 'idea'`）：被单独删掉的想法（它的项目还活着）。
 */
const entries = ref<TrashEntry[]>([]);

/** 展开着的容器（项目 id 集合）。 */
const expanded = ref<Set<string>>(new Set());

/** 正在恢复的条目 id（`null` = 没有）。恢复请求在飞时禁用按钮。 */
const busyId = ref<string | null>(null);

/** 拉一次回收站。 */
async function load(): Promise<void> {
  try {
    entries.value = await apiGet<TrashEntry[]>('/trash');
  } catch (error) {
    reportError(error, '加载回收站失败');
  }
}

/**
 * 顶层条目原来属于哪个项目（项目名）。
 *
 * 顶层条目 = 被单独删掉的想法，它的项目**还活着**（后端把「项目也被删了」
 * 的想法归到容器条目下，见 server/IdeaApp/Application/TrashService.cs），
 * 所以项目名从项目列表里查得到。查不到（项目列表没拉到）就返回 null ——
 * 那时页面上只是少一行说明，其余照常。
 */
function originProjectName(projectId: string): string | null {
  return projects.value.find((project) => project.id === projectId)?.name ?? null;
}

/** 容器是不是展开着的。 */
function isExpanded(projectId: string): boolean {
  return expanded.value.has(projectId);
}

/** 展开/收起容器。 */
function toggle(projectId: string): void {
  // 每次都造新 Set：Vue 的响应式 Set 只追踪 `has`，原地改会静默失效。
  const next = new Set(expanded.value);
  if (next.has(projectId)) {
    next.delete(projectId);
  } else {
    next.add(projectId);
  }
  expanded.value = next;
}

/**
 * 恢复一个项目（连带恢复「因删项目而删」的想法）。
 *
 * 恢复完重新拉一次列表：恢复过的条目不该还留在回收站里。
 */
async function restoreProject(projectId: string): Promise<void> {
  if (busyId.value) {
    return;
  }
  busyId.value = projectId;
  try {
    await apiSend<Project>('POST', `/projects/${projectId}/restore`);
    showToast('项目已恢复', 'info');
    await load();
  } catch (error) {
    reportError(error, '恢复项目失败');
  } finally {
    busyId.value = null;
  }
}

/**
 * 恢复一条想法。
 *
 * 容器里的想法也能单独恢复（项目仍在回收站时恢复是有可见效果的：
 * 它从容器里消失、回到所属项目的列表）。
 */
async function restoreIdea(ideaId: string): Promise<void> {
  if (busyId.value) {
    return;
  }
  busyId.value = ideaId;
  try {
    await apiSend<Idea>('POST', `/ideas/${ideaId}/restore`);
    showToast('想法已恢复', 'info');
    await load();
  } catch (error) {
    reportError(error, '恢复想法失败');
  } finally {
    busyId.value = null;
  }
}

onMounted(() => {
  void load();
  // 项目列表：顶层条目要显示「原来属于哪个项目」，得靠它把 projectId 翻成项目名。
  // 拉不到也不影响回收站本身 —— 只是那行说明不显示。
  void loadProjects().catch((error) => reportError(error, '加载项目失败'));
});
</script>

<template>
  <!-- 列表页：不套窄容器 —— 列表铺满到右边缘（规格 §D10）。 -->
  <main class="page">
    <PageNav />

    <h1>回收站</h1>
    <p class="trash__hint">
      回收站里的内容到期后会自动清理（保留期由服务端配置决定，页面与 API
      都不提供设置入口）；在那之前可以恢复。
    </p>

    <ul class="trash__list">
      <li
        v-for="entry in entries"
        :key="`${entry.kind}:${entry.project?.id ?? entry.idea?.id}`"
        class="trash__entry"
        :class="entry.kind === 'project' ? 'trash__entry--container' : 'trash__entry--idea'"
      >
        <!-- 容器：已删的项目，下挂它下面仍在回收站里的想法。 -->
        <template v-if="entry.kind === 'project' && entry.project">
          <div class="trash__row">
            <!--
              「展开」与项目名是一组：窄屏换行时不能被拆到两行去
              （拆开就像「展开」跟项目名没关系）。
            -->
            <div class="trash__main">
              <button class="trash__toggle" type="button" @click="toggle(entry.project.id)">
                {{ isExpanded(entry.project.id) ? '收起' : '展开' }}
              </button>
              <span class="trash__name">{{ entry.project.name }}</span>
            </div>
            <!-- 条数与时间是一组补充说明。 -->
            <div class="trash__meta">
              <!--
                条数取 ideas.length，**不是** project.ideaCount：
                ideaCount 统计的是未删想法，已删项目恒为 0。
              -->
              <span class="trash__count">{{ entry.ideas.length }} 条想法</span>
              <span class="trash__time">{{ formatRelativeTime(entry.project.updatedAt) }}</span>
            </div>
            <button
              class="trash__restore"
              type="button"
              :disabled="busyId !== null"
              @click="restoreProject(entry.project.id)"
            >
              恢复项目
            </button>
          </div>

          <ul v-if="isExpanded(entry.project.id)" class="trash__children">
            <li v-for="idea in entry.ideas" :key="idea.id" class="trash__child">
              <div class="trash__main">
                <span class="trash__child-body">{{ idea.content }}</span>
              </div>
              <div class="trash__meta">
                <span class="trash__time">{{ formatRelativeTime(idea.updatedAt) }}</span>
              </div>
              <button
                class="trash__restore-idea"
                type="button"
                :disabled="busyId !== null"
                @click="restoreIdea(idea.id)"
              >
                恢复
              </button>
            </li>
          </ul>
        </template>

        <!-- 顶层：被单独删掉的想法（它的项目还活着）。 -->
        <template v-else-if="entry.idea">
          <div class="trash__row">
            <div class="trash__main">
              <span class="trash__body">{{ entry.idea.content }}</span>
              <!--
                原属项目：条目上要看得见这条想法原来属于哪个项目。
                项目名与说明写在**同一个文本节点**里（不是两个元素）——
                这样「页面里有没有出现这个项目名」这种断言仍然是精确的。
              -->
              <span v-if="originProjectName(entry.idea.projectId)" class="trash__origin">
                原属项目：{{ originProjectName(entry.idea.projectId) }}
              </span>
            </div>
            <div class="trash__meta">
              <span class="trash__time">{{ formatRelativeTime(entry.idea.updatedAt) }}</span>
            </div>
            <button
              class="trash__restore"
              type="button"
              :disabled="busyId !== null"
              @click="restoreIdea(entry.idea.id)"
            >
              恢复
            </button>
          </div>
        </template>
      </li>
    </ul>

    <p v-if="entries.length === 0" class="trash__empty">回收站是空的。</p>
  </main>
</template>

<style scoped>
.trash__hint,
.trash__empty {
  color: var(--muted);
  font-size: 13px;
}

.trash__list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 12px 0 0;
  padding: 0;
  list-style: none;
}

.trash__entry {
  padding: 8px 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

/*
 * 回收站条目行：主体（展开 + 名称，或正文）、补充说明（条数 / 时间）、操作按钮。
 *
 * 三块的分组是为了窄屏换行时不出事故：
 * - 「展开」与项目名同一组，不会被拆到两行（「展开」跟它控制的名字分家了）；
 * - 「恢复」按钮自己占一块，换行时靠右，不会掉到「展开」正下方，
 *   看着像「展开」的下属按钮。
 */
.trash__row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex-wrap: wrap;
}

.trash__main {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex: 1 1 auto;
  min-width: 0;
  flex-wrap: wrap;
}

.trash__meta {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex: none;
}

.trash__restore {
  flex: none;
  /* 换行时靠右：不在「展开」底下杵着。 */
  margin-left: auto;
}

.trash__name {
  /*
   * flex-basis 取 0：项目名再长也不会被 flex 换行拆到「展开」下面去
   * （名字自己折行，「展开」始终跟它同一行）。
   */
  flex: 1 1 0;
  min-width: 0;
  overflow-wrap: anywhere;
}

.trash__body,
.trash__child-body {
  flex: 1 1 auto;
  min-width: 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.trash__count,
.trash__time {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

/* 原属项目（顶层条目）：跟时间一样是补充说明，字小一号。 */
.trash__origin {
  color: var(--muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.trash__children {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 8px 0 0;
  padding: 8px 0 0 12px;
  border-top: 1px solid var(--border);
  list-style: none;
}

.trash__child {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}

.trash__child .trash__restore-idea {
  flex: none;
  margin-left: auto;
}

.trash__child-body {
  font-size: 13px;
}
</style>
