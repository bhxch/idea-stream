<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';

const emit = defineEmits<{
  /** 去项目管理页。 */
  manageProjects: [];
  /** 去回收站页。 */
  trash: [];
  /** 去 Webhook 页。 */
  webhooks: [];
}>();

/** 下拉面板是否展开。 */
const open = ref(false);

/** 组件根节点：用来判断「点的是不是组件内部」。 */
const root = ref<HTMLElement | null>(null);

/** 去项目管理页（顺手收起面板）。 */
function goManageProjects(): void {
  emit('manageProjects');
  open.value = false;
}

/** 去回收站页（顺手收起面板）。 */
function goTrash(): void {
  emit('trash');
  open.value = false;
}

/** 去 Webhook 页（顺手收起面板）。 */
function goWebhooks(): void {
  emit('webhooks');
  open.value = false;
}

/**
 * 点组件外面 → 收起面板。
 *
 * 面板是浮在触发器下面的一层，不点它就不消失的话，
 * 用户会以为「点了没反应」，只能刷新页面。
 *
 * 点在组件内部（触发器本身、面板里的项）不算「外面」——
 * 触发器的 click 会冒泡到 document，靠这条判断才不会「刚打开就被关掉」。
 */
function onDocumentClick(event: MouseEvent): void {
  if (!open.value) {
    return;
  }
  const target = event.target;
  if (target instanceof Node && root.value?.contains(target)) {
    return;
  }
  open.value = false;
}

/** Esc → 收起面板。 */
function onDocumentKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    open.value = false;
  }
}

onMounted(() => {
  document.addEventListener('click', onDocumentClick);
  document.addEventListener('keydown', onDocumentKeydown);
});

onBeforeUnmount(() => {
  document.removeEventListener('click', onDocumentClick);
  document.removeEventListener('keydown', onDocumentKeydown);
});
</script>

<template>
  <div ref="root" class="more-menu">
    <button
      class="more-menu__trigger"
      type="button"
      aria-label="更多"
      aria-haspopup="true"
      :aria-expanded="open"
      @click="open = !open"
    >
      ⋯
    </button>

    <div v-if="open" class="more-menu__panel">
      <!--
        主页原来没有管理页导航条（PageNav 只挂在四个管理页上），
        手机上进回收站要点两步：⋯ → 项目管理 → 回收站。
        这里把三个入口直接摆在手边。
      -->
      <button class="more-menu__item" type="button" @click="goManageProjects">项目管理</button>
      <button class="more-menu__item" type="button" @click="goTrash">回收站</button>
      <button class="more-menu__item" type="button" @click="goWebhooks">Webhook</button>
    </div>
  </div>
</template>

<style scoped>
.more-menu {
  position: relative;
}

/*
 * 面板：浮在触发器下面的一层。
 *
 * 做法照 TagFilter 的面板（同一类东西，别写出两套）。
 */
.more-menu__panel {
  position: absolute;
  top: calc(100% + 4px);
  right: 0;
  z-index: 20;
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 140px;
  padding: 8px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  box-shadow: var(--shadow);
}

.more-menu__item {
  display: flex;
  align-items: center;
  width: 100%;
  padding: 4px 8px;
  border: none;
  border-radius: 6px;
  text-align: left;
  white-space: nowrap;
}

.more-menu__item:hover {
  background: var(--chip-bg);
}

/*
 * 手机上点按目标不小于 32×32。
 *
 * 触发器只有一个符号、菜单项只有一行字高，手指都点不中 ——
 * 窄屏上把它们撑到 32px 高。
 */
@media (max-width: 767px) {
  .more-menu__trigger,
  .more-menu__item {
    min-width: 32px;
    min-height: 32px;
  }
}
</style>
