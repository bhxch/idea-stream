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
</style>
