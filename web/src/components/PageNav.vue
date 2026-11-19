<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';

const route = useRoute();

/**
 * 当前在哪个页面。
 *
 * 高亮自己算，不靠 `router-link-active`：那个是**前缀匹配**，
 * 项目级 webhook 页（/projects/:id/webhooks）会把「项目」也点亮，
 * 而「Webhook」反倒不亮 —— 与用户以为的正好相反。
 *
 * `aria-current` 也跟着高亮走（两处同一个判据）：
 * vue-router 只在「完全匹配」时给链接加 `aria-current="page"`，
 * 项目级 webhook 页上它一个都不加 —— 读屏软件就不知道当前在哪一页了。
 */
const isHome = computed(() => route.path === '/');
const isProjects = computed(() => route.path === '/projects');
const isTrash = computed(() => route.path === '/trash');
/** 项目级 webhook 页也算 webhook 页。 */
const isWebhooks = computed(() => route.path.endsWith('/webhooks'));
</script>

<template>
  <!--
    管理页之间的导航。

    这几个页面（项目管理 / 回收站 / webhook）之间没有别的入口，
    少了这排链接就只有一个页面能被别人找到。

    第一个入口是「想法」—— 回主列表页（/）。
    管理页之间跳得再顺，也得有一条路回想法列表：
    不然进到 /projects、/trash、/webhooks 之后，
    整页上就没有任何链接指回想法列表，只能靠浏览器后退。
  -->
  <nav class="page-nav" aria-label="管理">
    <RouterLink
      class="page-nav__link page-nav__home"
      :class="{ 'is-active': isHome }"
      :aria-current="isHome ? 'page' : undefined"
      to="/"
    >
      想法
    </RouterLink>
    <RouterLink
      class="page-nav__link"
      :class="{ 'is-active': isProjects }"
      :aria-current="isProjects ? 'page' : undefined"
      to="/projects"
    >
      项目
    </RouterLink>
    <RouterLink
      class="page-nav__link"
      :class="{ 'is-active': isTrash }"
      :aria-current="isTrash ? 'page' : undefined"
      to="/trash"
    >
      回收站
    </RouterLink>
    <RouterLink
      class="page-nav__link"
      :class="{ 'is-active': isWebhooks }"
      :aria-current="isWebhooks ? 'page' : undefined"
      to="/webhooks"
    >
      Webhook
    </RouterLink>
  </nav>
</template>

<style scoped>
.page-nav {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 16px;
  font-size: 13px;
}

.page-nav__link {
  color: var(--muted);
  text-decoration: none;
}

.page-nav__link:hover {
  color: var(--accent);
}

.page-nav__link.is-active {
  color: var(--accent);
  font-weight: 600;
}

/*
 * 手机上点按目标不小于 32×32。
 *
 * 这几个入口是行内文字链接，只有一行字高（13px 字 × 1.6 行高 = 20.8px），
 * 手指点不中。窄屏上把它们撑成 32px 高的小方块，文字居中。
 *
 * 断点取 767px（不是 640px）：触摸目标的 32×32 规则跟两列断点对齐 ——
 * 原来的 640px 会让 641–767px 那一段既按手机渲染又不满足触摸目标。
 */
@media (max-width: 767px) {
  .page-nav {
    gap: 4px;
  }

  .page-nav__link {
    display: inline-flex;
    align-items: center;
    min-height: 32px;
    padding: 4px 8px;
  }
}
</style>
