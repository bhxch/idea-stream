<script setup lang="ts">
import PageNav from '../components/PageNav.vue';
import WebhookManager from '../components/WebhookManager.vue';
</script>

<template>
  <main class="page">
    <PageNav />

    <h1>Webhook</h1>
    <p class="webhooks-page__hint">
      全局 webhook 收所有项目的事件（事件的所属项目在回收站里时那一条会跳过）。
    </p>

    <WebhookManager scope="global" title="全局 webhook" />
  </main>
</template>

<style scoped>
.webhooks-page__hint {
  color: var(--muted);
  font-size: 13px;
}
</style>
