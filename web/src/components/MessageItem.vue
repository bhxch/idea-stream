<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { Idea, SearchMatch } from '../api/types';
import { formatRelativeTime } from '../lib/format';
import { flattenLines, highlightLines, tagHitFlags } from '../lib/highlight';
import type { SaveOptions, SaveOutcome } from '../lib/save';
import { firstLine } from '../lib/text';
import TagEditor from './TagEditor.vue';

const props = defineProps<{
  /** 这一条想法。 */
  idea: Idea;
  /** 是否被多选勾中。 */
  selected: boolean;
  /**
   * 这一条是否有请求在飞（比如切换完成态、保存编辑）。
   *
   * 在飞时禁用按钮：连点两次会拿同一个旧状态去提交，等于没切换。
   */
  busy?: boolean;
  /**
   * 这一条要不要高亮（深链定位、合并结果定位）。
   *
   * 高亮由页面层控制时长：定位完 1.5 秒就把这个标志撤掉。
   */
  highlight?: boolean;
  /** 标签补全：父组件提供（请求在页面层发，组件不碰网络）。 */
  suggest: (prefix: string) => string[];
  /**
   * 保存正文：resolve 出保存结果（见 {@link SaveOutcome}）。
   *
   * 用 prop 而不是 emit：编辑框要**等保存结果**才知道是退出编辑态还是留在里面，
   * emit 拿不到父组件处理器的返回值。失败时正文留在编辑框里 —— 用户刚敲的不能丢。
   *
   * 结果要能分出「被在飞保护挡下了」（`'busy'`）：这一版**没发出去**，
   * 调用方得知道要重新入队等在飞结束再补存 —— 混在「失败」里的话那次改动就静默丢了。
   */
  onSaveContent: (id: string, content: string) => Promise<SaveOutcome>;
  /**
   * 保存标签：resolve 出保存结果（三态，同上）。
   *
   * `options.silent`：失败时不弹错误提示（自动补存用 —— 用户只是打开编辑器看看，
   * 没改任何东西，不该看到「保存标签失败」）。
   */
  onSaveTags: (id: string, tags: string[], options?: SaveOptions) => Promise<SaveOutcome>;
}>();

const emit = defineEmits<{
  /** 勾选/取消勾选（多选批量操作用）。 */
  'toggle-select': [id: string];
  /** 切换完成态。 */
  'toggle-done': [id: string];
  /** 请求删除（已经二次确认过）。 */
  'delete': [id: string];
}>();

/** 是否在编辑正文。 */
const editing = ref(false);

/** 编辑中的正文草稿。 */
const draft = ref('');

/**
 * 正文的改动序号：每次改动 +1。
 *
 * 用来判断「攒着的那一版」是不是已经过时：用户连着改正文时，
 * 先发出去的那次保存可能回来得很晚 —— 那时用户又改了，
 * 回来的这一版就不该覆盖更新的那一版。
 */
let contentSeq = 0;

/**
 * 攒着的正文（在飞时被挡下的那一版，等在飞结束再存）。
 *
 * 同一条目上同时只允许一个写操作，所以「保存正文」有可能撞上别的操作
 * （比如用户点「标记完成」）。这时候不能直接把这次保存丢掉 ——
 * 用户点了别处就以为存好了，实际既没落库也没提示。
 *
 * 带上 `seq`：改动序号，用来判断回来的保存结果是不是已经过时
 * （用户又改过了的话，回来的是旧的一版，不能覆盖更新的那一版）。
 */
const pendingContent = ref<{ seq: number; content: string } | null>(null);

/**
 * 攒着的标签（被在飞保护挡下的那一版，等在飞结束再存）。
 *
 * 带上 `silent`：这次补存是「自动」的还是「用户主动」的 ——
 * 自动补存（捡回上次没存上的那版）失败时不弹提示，用户没改任何东西，
 * 不该看到「保存标签失败」。
 *
 * 带上 `seq`：改动序号，用来判断回来的保存结果是不是已经过时
 * （用户又改过了的话，回来的是旧的一版，不能覆盖新的）。
 */
const pendingTags = ref<{ seq: number; tags: string[]; silent: boolean } | null>(null);

/** 编辑框本身（进入编辑态后把焦点放进去）。 */
const editor = ref<HTMLTextAreaElement | null>(null);

/** 是否在编辑标签。 */
const tagEditing = ref(false);

/** 标签草稿：编辑器里正在显示的内容。 */
const tagDraft = ref<string[]>([]);

/**
 * 服务端当前值（本地记一份）。
 *
 * 不直接读 `props.idea.tags`：父组件把服务端返回的想法覆盖本地要等下一次渲染，
 * 刚存上的那一版短时间内对不上 —— 那时候会被误判成「还有未保存的改动」。
 */
const serverTags = ref<string[]>([]);

/**
 * 还没落库的那一版标签（null = 没有）。
 *
 * 收起编辑器时**不销毁**用户敲的东西：这一版留在这儿，下次打开标签编辑器
 * 会捡回来（并再存一次），用户不用重敲一遍。
 *
 * 只在「收起的那一刻」写：编辑器开着的时候草稿本来就在 {@link tagDraft} 里，
 * 不需要在这里存第二份。
 */
const unsavedTags = ref<string[] | null>(null);

/**
 * 标签集合的规范形式：去空白、丢空串、按大小写不敏感去重、再排序。
 *
 * 跟后端的口径一致（`NormalizeTags` 大小写不敏感去重，读回来按名称排序）：
 * 服务端存的是「Python」、用户写的是「python」，在服务端这是同一个标签 ——
 * 不按同一口径比较的话，用户刚存好就被问「还有未保存的改动吗」。
 */
function normalizeTags(tags: readonly string[]): string[] {
  const names = new Set<string>();
  for (const tag of tags) {
    const name = tag.trim().toLowerCase();
    if (name) {
      names.add(name);
    }
  }
  return [...names].sort();
}

/**
 * 两批标签是不是同一批（顺序、大小写、首尾空白都不算）。
 *
 * 必须按集合比，不能比数组：服务端存的是规范化的结果（去首尾空白、
 * 大小写不敏感去重、按名称排序），用户草稿是按敲入顺序排的 ——
 * 刚存好就比数组的话，每次都会被误判成「还有未保存的改动」。
 */
function sameTags(left: readonly string[], right: readonly string[]): boolean {
  const a = normalizeTags(left);
  const b = normalizeTags(right);
  return a.length === b.length && a.every((name, index) => name === b[index]);
}

/** 父组件把服务端返回的想法覆盖本地时，同步一下「服务端当前值」。 */
watch(
  () => props.idea.tags,
  (tags) => {
    serverTags.value = [...tags];
  },
  { immediate: true },
);

/** 删除的二次确认。 */
const confirmingDelete = ref(false);

/**
 * 某个字段的命中位置。
 *
 * `matches` 只在**带关键词**的响应里有（不带关键词时后端整个省略这个键），
 * SSE 推来的增量条目也没有 —— 没有命中位置就是不高亮，渲染结果跟以前一样。
 */
function matchesOf(field: SearchMatch['field']): SearchMatch[] {
  return (props.idea.matches ?? []).filter((match) => match.field === field);
}

/**
 * 正文的片段（命中的那几段渲染成 `<mark>`）。
 *
 * 正文是**全文**（含首行）：想法没有标题了，界面上也就没有「标题层」——
 * 同一句话在一条卡片里出现两次、首行被吃掉，两种都不行。
 *
 * 命中的位置由服务端给（相对**完整原文**的字符区间），所以先按整段原文切。
 */
const bodySegments = computed(() =>
  flattenLines(highlightLines(props.idea.content, matchesOf('content'))),
);

/**
 * 每枚标签 chip 要不要标记（关键词打在标签名上时，chips 是它的渲染面）。
 *
 * 「project」字段的命中在卡片上没有渲染面 —— 卡片不显示项目名，
 * 那个字段留在 matches 里是为了接口自洽（设计文档 §12.3）。
 */
const tagHits = computed(() => tagHitFlags(props.idea.tags, matchesOf('tags')));

/** 这条想法的短文本（正文首行，截断到约 40 字）—— 勾选框的可访问名称用它。 */
const accessibleName = computed(() => firstLine(props.idea.content));

/** 进入编辑态。 */
function startEdit(): void {
  draft.value = props.idea.content;
  editing.value = true;
  void focusEditor();
}

/** 退出编辑态，不保存。 */
function cancelEdit(): void {
  editing.value = false;
  draft.value = '';
  // 攒着的那一版也不存了：用户明确说了不要。
  pendingContent.value = null;
}

/**
 * 存正文。
 *
 * 在飞时（同一条目上别的写操作在跑）**不能直接丢掉这次保存**：
 * 用户点走了就以为存好了，实际既没落库也没有任何提示。
 * 所以先把草稿攒着（`pendingContent`），等在飞自动补存。
 */
async function saveEdit(): Promise<void> {
  if (!editing.value) {
    return;
  }
  if (props.busy) {
    // 内容没变就没什么要补存的（省掉一次没必要的请求）。
    if (draft.value !== props.idea.content) {
      pendingContent.value = { seq: ++contentSeq, content: draft.value };
    }
    return;
  }
  if (draft.value === props.idea.content) {
    // 一点没改：没什么要存的，直接收起编辑态（别为一次空改动发请求）。
    editing.value = false;
    draft.value = '';
    return;
  }
  const content = draft.value;
  const seq = ++contentSeq;
  const outcome = await saveContent(content, seq);
  if (outcome === 'busy') {
    // 被在飞保护挡下了：这次压根没发出去 —— 攒着，等在飞结束再补存。
    // （期间用户又改过、已经攒了更新的那一版的话，更新的那版留着 —— 它才是用户要的。）
    if (!pendingContent.value || pendingContent.value.seq < seq) {
      pendingContent.value = { seq, content };
    }
  }
}

/**
 * 真的发一次保存。
 *
 * @param seq 这一版正文的序号（用来判断回来的结果是不是已经过时）。
 * @returns 保存结果。`'busy'` = 被在飞保护挡下，**这次压根没发出去** ——
 *   调用方要把它重新入队，等在飞结束再补存。
 *   （不能当失败处理：失败有提示，用户知道没存上；被挡下若也当失败，
 *   这一版改动就既没落库也没提示 —— 静默丢。）
 */
async function saveContent(content: string, seq: number): Promise<SaveOutcome> {
  const outcome = await props
    .onSaveContent(props.idea.id, content)
    .catch(() => 'failed' as SaveOutcome);
  if (outcome === 'saved') {
    // 存的还是当前草稿才收起编辑态（期间用户又改过就别动他的新草稿）。
    if (draft.value === content) {
      editing.value = false;
      draft.value = '';
    }
    // 攒着的那一版不比这次新 → 已被这次覆盖，清掉（更新的那一版留着）。
    if (pendingContent.value && pendingContent.value.seq <= seq) {
      pendingContent.value = null;
    }
  }
  return outcome;
}

/** 把焦点放进编辑框（进来就能接着改）。 */
async function focusEditor(): Promise<void> {
  await nextTick();
  editor.value?.focus();
}

/** 编辑框里的按键：Ctrl+Enter 保存、Esc 取消。 */
function onEditorKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    void saveEdit();
    return;
  }
  if (event.key === 'Escape') {
    event.preventDefault();
    cancelEdit();
  }
}

/** 进入标签编辑态。 */
function startTagEdit(): void {
  // 上次收起时还没落库的那一版：捡回来（用户不用重敲一遍）。
  const restored = unsavedTags.value;
  // 服务端那边已经存上了（别处改的）就不用捡了。
  const pending = restored !== null && !sameTags(restored, serverTags.value);
  tagDraft.value = pending ? [...restored] : [...serverTags.value];
  tagEditing.value = true;
  if (pending) {
    // 捡回来的那版还没落库：再存一次（上次多半是网络断了）。
    // **静默**：用户只是打开编辑器看看、没改任何东西 ——
    // 失败不该弹「保存标签失败」，那会让人莫名其妙（他没保存过）。
    // （`unsavedTags` 不在这里清：存上了由 saveTags 清；没存上就留着，下次再捡。）
    void onTagsChange(tagDraft.value, { silent: true });
  }
}

/**
 * 收起标签编辑器。
 *
 * 草稿跟服务端当前值不一致 = 还有没存上的改动（多半是上一次保存失败了）。
 * 这时要**再问一句**才收起：保存失败只是一闪而过的提示，用户过会儿就忘了
 * 刚才有东西没存上，直接收起就把那次改动静默丢了 —— 那是真丢，不是丢了个显示。
 *
 * 这一句不是为了拦住用户，是为了**不静默地丢东西**：
 * - 用户不点头 → 不收起，草稿原样留着（可以直接再存一次）；
 * - 用户点了「确定」→ 收起，但那份没存上的改动**不扔**：记在 {@link unsavedTags} 里，
 *   下次打开标签编辑器会捡回来（并再存一次）。
 */
function closeTagEdit(): void {
  if (!sameTags(tagDraft.value, serverTags.value)) {
    const confirmed = window.confirm(
      '还有未保存的改动，确定收起？改动会保留，重新打开标签时会再存一次。',
    );
    if (!confirmed) {
      // 用户不点头：不收起，草稿还在（编辑器留着，可以直接再存一次）。
      return;
    }
    unsavedTags.value = [...tagDraft.value];
  }
  tagEditing.value = false;
  tagDraft.value = [];
}

/** 点「标签」：开着就收起，没开就打开。 */
function toggleTagEdit(): void {
  if (tagEditing.value) {
    closeTagEdit();
    return;
  }
  startTagEdit();
}

/**
 * 标签改动的序号：每次改动 +1。
 *
 * 用来判断「攒着的那一版」是不是已经过时了：用户连着改标签时，
 * 先发出去的那次保存可能回来得很晚 —— 那时用户又改了，
 * 回来的这一版就不该覆盖更新的那一版。
 */
let tagSeq = 0;

/**
 * 已经落到服务端的最新那一版的序号。
 *
 * 两次保存的响应可能**乱序**回来（后发的那次先到）：这时后到的那次是旧的一版，
 * 不能拿它覆盖「服务端当前值」—— 否则界面会显示一个比实际旧的标签集。
 */
let savedTagSeq = 0;

/**
 * 发一次标签保存。
 *
 * @param seq 这一版改动的序号（用来判断回来的结果是不是已经过时）。
 * @returns 保存结果。`'busy'` = 被在飞保护挡下，**这次压根没发出去** ——
 *   调用方要把它重新入队，等在飞结束再补存（不能当失败处理：
 *   失败有提示，用户知道没存上；被挡下若也当失败，这次改动就静默没了）。
 */
async function saveTags(
  tags: string[],
  options: SaveOptions,
  seq: number,
): Promise<SaveOutcome> {
  const outcome = await props
    .onSaveTags(props.idea.id, tags, options)
    .catch(() => 'failed' as SaveOutcome);
  if (outcome === 'saved') {
    // 乱序回来的旧一版不覆盖新的（后发的那次先到了）。
    if (seq >= savedTagSeq) {
      savedTagSeq = seq;
      serverTags.value = [...tags];
      // 存上了，之前攒着的那一版（没落库的）就没意义了。
      unsavedTags.value = null;
    }
    // 攒着的那一版不比这次新 → 已被这次覆盖，清掉（更新的那一版留着）。
    if (pendingTags.value && pendingTags.value.seq <= seq) {
      pendingTags.value = null;
    }
  }
  return outcome;
}

/**
 * 标签变了：立刻存。**存好了不收起编辑器**，失败留在里面（草稿不丢）。
 *
 * 收起是用户自己的动作（点「✓ 完成编辑」）：编辑标签是**一段连续的会话**
 * —— 输入框 + 自动补全 + 回车确认 + × 删除。中途每次落库就收起的话，
 * 加第二个标签要重新点一次「🏷 标签」，删第二个标签也要重新打开（打开后 × 才存在）。
 *
 * 在飞时（自己的保存，或别的操作）先攒着，等在飞结束再存 ——
 * 用户连按两下回车，第二个标签不能因为快了一点就没了。
 *
 * 存上了就把「服务端当前值」更新成这一版：父组件覆盖 `props.idea` 要等下一次渲染，
 * 那之前收起的判断会拿旧值跟新草稿比，误判成「还有未保存的改动」。
 *
 * @param options.silent 失败时不弹提示（自动补存用：用户没改任何东西）。
 * @returns 保存结果（`'busy'` = 被在飞保护挡下，这次压根没发出去）。
 */
async function onTagsChange(next: string[], options?: SaveOptions): Promise<SaveOutcome> {
  tagDraft.value = next;
  const seq = ++tagSeq;
  const opts: SaveOptions = { silent: options?.silent ?? false };
  if (props.busy) {
    // 在飞：先攒着，等在飞结束再补存。
    pendingTags.value = { seq, tags: next, silent: opts.silent };
    return 'busy';
  }
  const outcome = await saveTags(next, opts, seq);
  if (outcome === 'busy' && (!pendingTags.value || pendingTags.value.seq < seq)) {
    // 被在飞保护挡下了：这次压根没发出去 —— 攒着，等在飞结束再补存。
    // （期间用户又改过的话，攒着的该是更新的那一版。）
    pendingTags.value = { seq, tags: next, silent: opts.silent };
  }
  return outcome;
}

/** 补存进行中（防止 watch 连续触发时并发补存）。 */
let flushing = false;

/** 补存期间又来了一次触发：这一轮结束后再补一轮。 */
let flushAgain = false;

/**
 * 在飞的操作结束了：把刚才没存上的那一版补存。
 *
 * 那次保存被在飞保护挡下了（同一个条目同时只允许一个写操作）。
 *
 * **必须串行**：正文与标签分两次发，不能并发 ——
 * 并发的话第二个请求会被在飞保护挡下（第一个请求刚发出时父组件就把这条
 * 标成在飞了），而那一版改动既没落库也没有任何提示：静默丢。
 */
async function flushPending(): Promise<void> {
  if (flushing) {
    // 已经有一轮在补了：这一轮结束后再补一轮（别并发发两个写）。
    flushAgain = true;
    return;
  }
  flushing = true;
  try {
    do {
      flushAgain = false;
      await flushOnce();
    } while (flushAgain);
  } finally {
    flushing = false;
  }
}

/**
 * 补存一轮：先正文、再标签（串行，见 {@link flushPending}）。
 *
 * 这里**直接调** {@link saveContent} / {@link saveTags}，不走
 * {@link onTagsChange} 的「在飞就先攒着」那条路：
 * 补存就是在飞结束时做的，此刻 `props.busy` 可能还是旧值（父组件刚释放、
 * 子组件还没重渲染），拿它当依据会把该发的请求又攒回去。
 * 真正准的是父组件的守卫 —— 它挡下了就回 `'busy'`，我们再攒回去。
 *
 * 循环着补：补存期间用户可能又改了（新的改动会再攒进来），
 * 一次补完再回头看有没有新的 —— 别让新攒下的那版等到下一次在飞结束。
 */
async function flushOnce(): Promise<void> {
  for (;;) {
    const content = pendingContent.value;
    if (content !== null) {
      pendingContent.value = null;
      // 内容跟服务端已经一致了就别再存一次（刚那次保存其实已经存上了）。
      if (editing.value && content.content !== props.idea.content) {
        const outcome = await saveContent(content.content, content.seq);
        if (outcome === 'busy') {
          // 被在飞保护挡下了：这一版还在，放回去等下一次在飞结束再补存。
          // （期间攒了更新的那一版的话，那一版才是用户要的 —— 别覆盖它。）
          if (pendingContent.value === null) {
            pendingContent.value = content;
          }
          // 别在这儿死循环：等下一次在飞结束（父组件会再翻转 busy）再补。
          return;
        }
      }
      continue;
    }
    // 正文这次结束了（无论成功失败），再看标签有没有攒着。
    const tags = pendingTags.value;
    if (tags !== null) {
      pendingTags.value = null;
      const outcome = await saveTags(tags.tags, { silent: tags.silent }, tags.seq);
      if (outcome === 'busy') {
        if (pendingTags.value === null) {
          pendingTags.value = tags;
        }
        return;
      }
      continue;
    }
    return;
  }
}

watch(
  () => props.busy,
  (busy) => {
    if (busy) {
      return;
    }
    void flushPending();
  },
);

/** 点「删除」：先问一句，别真删。 */
function requestDelete(): void {
  confirmingDelete.value = true;
}

/** 不删了。 */
function cancelDelete(): void {
  confirmingDelete.value = false;
}

/** 确认删除。 */
function confirmDelete(): void {
  confirmingDelete.value = false;
  emit('delete', props.idea.id);
}
</script>

<template>
  <article
    class="idea"
    :class="{ 'idea--done': idea.done, 'idea--selected': selected, highlight }"
    :data-idea-id="idea.id"
  >
    <div class="head">
      <input
        class="select"
        type="checkbox"
        :checked="selected"
        :aria-label="`选择 ${accessibleName}`"
        @change="emit('toggle-select', idea.id)"
      />
      <button
        class="done"
        type="button"
        :disabled="busy"
        :aria-pressed="idea.done"
        @click="emit('toggle-done', idea.id)"
      >
        {{ idea.done ? '已完成' : '标记完成' }}
      </button>
      <time class="time">{{ formatRelativeTime(idea.updatedAt) }}</time>
    </div>

    <!-- 正文一律按纯文本渲染（插值），绝不用 v-html：内容是用户随手写的，
         里面可能有 <b>、& 之类的字符，按 HTML 解析会被吞掉或变成注入。
         搜索命中时按区间切成片段，命中的那几段包 <mark>。 -->
    <textarea
      v-if="editing"
      ref="editor"
      v-model="draft"
      class="editor"
      rows="4"
      :disabled="busy"
      @blur="saveEdit"
      @keydown="onEditorKeydown"
    ></textarea>
    <div v-else class="body" title="点击编辑" @click="startEdit">
      <template v-for="(segment, index) in bodySegments" :key="index">
        <mark v-if="segment.marked">{{ segment.text }}</mark>
        <template v-else>{{ segment.text }}</template>
      </template>
    </div>

    <TagEditor
      v-if="tagEditing"
      v-model:value="tagDraft"
      :suggest="suggest"
      @update:value="onTagsChange"
    />
    <ul v-else-if="idea.tags.length > 0" class="tags">
      <!-- 关键词打在标签名上时，命中的 chip 也包 <mark>（与正文同一套样式）。
           按「整枚 chip」标，不做片段级 —— 标签很短，整枚看得清。 -->
      <li v-for="(tag, index) in idea.tags" :key="tag" class="chip">
        <mark v-if="tagHits[index]">{{ tag }}</mark>
        <template v-else>{{ tag }}</template>
      </li>
    </ul>

    <div class="actions">
      <button
        class="action action--tags"
        type="button"
        :disabled="busy"
        @click="toggleTagEdit"
      >
        {{ tagEditing ? '✓ 完成编辑' : '🏷 标签' }}
      </button>
      <button
        class="action action--delete"
        type="button"
        :disabled="busy"
        @click="requestDelete"
      >
        🗑 删除
      </button>
    </div>

    <div v-if="confirmingDelete" class="confirm">
      <span class="confirm__text">删除这条？它会进回收站。</span>
      <!-- 在飞时禁用：删除请求被在飞保护挡下的话，用户点了会「没反应」
           （父组件的守卫是静默返回的）。按钮禁用本身就是可见反馈。 -->
      <button class="action confirm__delete" type="button" :disabled="busy" @click="confirmDelete">
        确认删除
      </button>
      <button class="action confirm__cancel" type="button" @click="cancelDelete">取消</button>
    </div>
  </article>
</template>

<style scoped>
.idea {
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.idea--selected {
  border-color: var(--accent);
}

/*
 * 深链定位后的高亮：闪一下，告诉用户「找的就是这条」。
 * 页面层 1.5 秒后把这个类撤掉。
 */
.idea.highlight {
  border-color: var(--accent);
  animation: idea-highlight 1.5s ease-out;
}

@keyframes idea-highlight {
  from {
    background: var(--chip-bg);
  }

  to {
    background: var(--panel);
  }
}

.idea--done .body {
  color: var(--muted);
  text-decoration: line-through;
}

/*
 * 卡片头：勾选框、「标记完成」、时间戳并排。
 *
 * 纵向对齐用 baseline（不是 center）：控件高度不一时，center 会把它们
 * 拉到彼此的正中，看着像挂着；baseline 让它们跟文字**首行**齐平。
 */
.head {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

/*
 * 复选框没有文字基线，单独顶对齐。
 *
 * 不这样的话整行的基线由它决定（复选框的基线是它的下边缘），
 * 旁边的按钮与时间戳会被顶到复选框下边缘那条线上，高一截。
 */
.select {
  align-self: flex-start;
}

/* 请求在飞：按钮不可点，别让它看起来还能按。 */
.done:disabled,
.action:disabled {
  opacity: 0.5;
  cursor: default;
}

.time {
  color: var(--muted);
  font-size: 12px;
  white-space: nowrap;
}

.body {
  margin-top: 6px;
  min-height: 1.6em;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  cursor: text;
}

/*
 * 搜索命中的高亮。
 *
 * <mark> 的浏览器默认样式是黄底黑字，在暗色主题里很扎眼 ——
 * 换成主题的强调色，亮暗两套主题下都看得清。
 */
.body mark,
.chip mark {
  background: var(--accent);
  color: var(--accent-fg);
  border-radius: 3px;
}

.editor {
  display: block;
  width: 100%;
  margin-top: 6px;
  resize: vertical;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 8px 0 0;
  padding: 0;
  list-style: none;
}

.chip {
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--chip-bg);
  font-size: 12px;
}

.actions {
  display: flex;
  gap: 6px;
  margin-top: 8px;
}

.action {
  padding: 2px 8px;
  font-size: 12px;
}

/*
 * 手机上点按目标不小于 32×32。
 *
 * - 勾选框：原生复选框只有 13×13，手指根本点不中 ——
 *   窄屏上改成自绘的 32×32 方框（勾在中间），外观与其它输入控件一致。
 * - 小按钮（标签 / 删除）：12px 的字配 2px 内边距只有 25px 高 —— 窄屏上加高。
 */
@media (max-width: 640px) {
  .select {
    appearance: none;
    -webkit-appearance: none;
    display: grid;
    place-content: center;
    width: 32px;
    height: 32px;
    margin: 0;
    padding: 0;
    cursor: pointer;
  }

  .select:checked::after {
    content: '';
    width: 7px;
    height: 13px;
    border: solid var(--accent);
    border-width: 0 2px 2px 0;
    transform: rotate(45deg) translate(-1px, -1px);
  }

  .action {
    padding: 6px 8px;
  }

  /*
   * 窄屏上控件那一行：时间戳靠右（其余控件挨着排）。
   * 标题层没有了，`.head` 不再换行 —— 一行放得下。
   */
  .time {
    margin-left: auto;
  }
}

.action--delete {
  color: var(--danger);
}

.confirm {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 6px 8px;
  border: 1px solid var(--danger);
  border-radius: 6px;
}

.confirm__text {
  flex: 1;
  font-size: 12px;
}
</style>
