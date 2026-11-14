<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';

const route = useRoute();

/**
 * 当前在哪个管理页。
 *
 * 高亮自己算，不靠 `router-link-active`：那个是**前缀匹配**，
 * 项目级 webhook 页（/projects/:id/webhooks）会把「项目」也点亮，
 * 而「Webhook」反倒不亮 —— 与用户以为的正好相反。
 */
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
  -->
  <nav class="page-nav" aria-label="管理">
    <RouterLink class="page-nav__link" :class="{ 'is-active': isProjects }" to="/projects">
      项目
    </RouterLink>
    <RouterLink class="page-nav__link" :class="{ 'is-active': isTrash }" to="/trash">
      回收站
    </RouterLink>
    <RouterLink class="page-nav__link" :class="{ 'is-active': isWebhooks }" to="/webhooks">
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
 * 断点取 640px（手机竖屏的上限）：再宽的屏上留白够，
 * 密排的文字链接更好看，也更好点。
 */
@media (max-width: 640px) {
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
