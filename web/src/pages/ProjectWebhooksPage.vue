<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { useRoute } from 'vue-router';
import PageNav from '../components/PageNav.vue';
import WebhookManager from '../components/WebhookManager.vue';
import { reportError } from '../lib/errors';
import { loadProjects, projects } from '../stores/projects';

const route = useRoute();

/** 路由参数里的项目 id。 */
const projectId = computed(() => String(route.params.id ?? ''));

/** 项目名（列表端点才有，这里取来给标题用）。 */
const projectName = computed(
  () => projects.value.find((project) => project.id === projectId.value)?.name ?? '',
);

onMounted(async () => {
  try {
    await loadProjects();
  } catch (error) {
    reportError(error, '加载项目失败');
  }
});
</script>

<template>
  <main class="page">
    <PageNav />

    <h1>{{ projectName ? `项目 Webhook · ${projectName}` : '项目 Webhook' }}</h1>
    <p class="project-webhooks__hint">
      项目级 webhook 只收这个项目的事件；项目进回收站期间它不投递，项目恢复后自动恢复投递。
    </p>

    <WebhookManager scope="project" :project-id="projectId" title="项目级 webhook" />
  </main>
</template>

<style scoped>
.project-webhooks__hint {
  color: var(--muted);
  font-size: 13px;
}
</style>
