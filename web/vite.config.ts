import vue from '@vitejs/plugin-vue';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [vue()],
  base: '/',
  build: {
    // 产物直接落到后端的 wwwroot，由 ASP.NET Core 同源托管（Task 12 接线）。
    outDir: '../server/IdeaApp/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // 开发时前端与后端不同源，用 Vite 自带的 proxy 把 /api 转发到后端。
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5080',
        changeOrigin: true,
      },
    },
  },
  test: {
    // 全局 jsdom：vue-router 在模块求值时就解构 window（用 history 模式），
    // 环境是 node 时只要有 spec import 了 router.ts 就会在收集阶段报
    // "window is not defined"。单文件 pragma 容易漏，这里全局设死。
    environment: 'jsdom',
  },
});
