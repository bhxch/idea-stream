import { createRouter, createWebHistory } from 'vue-router';
import HomePage from './pages/HomePage.vue';
import ProjectWebhooksPage from './pages/ProjectWebhooksPage.vue';
import ProjectsPage from './pages/ProjectsPage.vue';
import TrashPage from './pages/TrashPage.vue';
import WebhooksPage from './pages/WebhooksPage.vue';

/**
 * 路由表。
 *
 * 用 history 模式，后端要用 `MapFallbackToFile("index.html")` 兜底深链
 * （Task 12 接线），否则直接访问 /projects、/trash 这类地址会 404。
 */
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: HomePage },
    { path: '/projects', name: 'projects', component: ProjectsPage },
    { path: '/trash', name: 'trash', component: TrashPage },
    { path: '/webhooks', name: 'webhooks', component: WebhooksPage },
    { path: '/projects/:id/webhooks', name: 'project-webhooks', component: ProjectWebhooksPage },
  ],
});
