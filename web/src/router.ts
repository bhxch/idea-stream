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
    // 兜底：未知地址回首页。
    //
    // 后端把未匹配的 GET 兜成 index.html（MapFallbackToFile），所以未知地址
    // 拿到的是 200 + 应用外壳 —— 但路由表里没有对应条目的话，RouterView 会
    // 渲染成空，用户看到的是白屏（只有顶栏底色）。这条 catch-all 让它回首页。
    //
    // 必须放在最后：vue-router 按 routes 数组顺序匹配，前面的路由优先，
    // /projects/:id/webhooks 这类不会被它抢走。
    { path: '/:pathMatch(.*)*', name: 'fallback', redirect: '/' },
  ],
});
