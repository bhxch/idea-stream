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
    environment: 'node',
  },
});
