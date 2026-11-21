# Idea Stream 前端

Idea 想法流的 Web 前端。Vue 3 + TypeScript + Vite 7，无 UI 框架、无状态管理库，
构建产物直接落进后端的 `server/IdeaApp/wwwroot/`，由 ASP.NET Core 同源托管。

## 技术栈

| 用途 | 选型 |
|---|---|
| 框架 | Vue 3.5（`<script setup>` + Composition API） |
| 语言 | TypeScript 5.9（`strict`） |
| 构建 | Vite 7 |
| 路由 | vue-router 4（history 模式） |
| 状态 | 组合式函数 + `ref`（`src/stores/`），没有引入 Pinia |
| 测试 | Vitest 3 + jsdom + `@vue/test-utils`；端到端用 Playwright |
| 类型检查 | `vue-tsc`（应用与 e2e 各一份 tsconfig，见下） |
| 依赖管理 | npm（`package-lock.json` 提交进仓库） |

## 目录结构

```
web/
  index.html            Vite 的入口 HTML（模板）
  vite.config.ts        构建配置：产物落点、dev proxy、vitest 环境
  package.json          scripts 与依赖
  tsconfig.json         应用侧 TS 配置
  tsconfig.node.json    vite.config.ts 等 Node 侧文件的 TS 配置
  e2e/
    tsconfig.json       e2e 侧 TS 配置（node 类型；应用那份是纯浏览器的）
    fixtures.ts         公共夹具（复位、造数据、API 封装）与类型
    *.spec.ts           端到端用例（Playwright）
  src/
    main.ts             应用入口：createApp + router + 样式
    App.vue             根组件：路由出口（RouterView）+ 全局提示条（ToastHost）
    router.ts           路由表（history 模式）
    api/                后端调用层
      client.ts         fetch 封装、ApiError、路径归一
      types.ts          DTO 类型（与后端契约对应）
      events.ts         SSE (/api/events) 订阅
    stores/             全局状态（ref 组合式）
      ideas.ts          想法列表
      projects.ts       项目列表
      settings.ts       设置项
    components/         可复用组件（列表项、编辑器、对话框、筛选栏……）
    pages/              路由级页面
      HomePage.vue         /                    收件箱（SSE 订阅在这里）
      ProjectsPage.vue     /projects            项目管理
      TrashPage.vue        /trash               回收站
      WebhooksPage.vue     /webhooks            webhook 管理
      ProjectWebhooksPage.vue  /projects/:id/webhooks
    composables/        可复用的组合式函数（深链、滚动跟随、标签模式……）
    lib/                纯函数工具（过滤、合并、排序、格式化、错误处理）
    styles/             全局样式（theme.css 变量 + base.css 基础样式）
```

## 常用命令

### `npm run dev` —— 开发服务器

```bash
cd web
npm ci        # 首次
npm run dev
```

开发时前端在 `http://127.0.0.1:5173`，**与后端不同源**。`vite.config.ts` 里配了
dev proxy，把 `/api` 转发到 `http://127.0.0.1:5080`：

```ts
server: {
  proxy: {
    '/api': { target: 'http://127.0.0.1:5080', changeOrigin: true },
  },
},
```

所以**必须先把后端起起来**，否则页面上所有数据请求都会失败：

```bash
IDEA_Database__Path=/tmp/idea-dev/idea.db dotnet run --project server/IdeaApp
```

### `npm run build` —— 生产构建

```bash
cd web
npm run build
```

产物落在 **`../server/IdeaApp/wwwroot/`**（`vite.config.ts` 的 `build.outDir`），
由后端同源托管：

```
server/IdeaApp/wwwroot/
  index.html
  assets/index-<hash>.js
  assets/index-<hash>.css
```

> ⚠️ **`server/IdeaApp/wwwroot/` 被 `.gitignore` 排除**，构建产物**不进仓库**。
> 新克隆的仓库里这个目录不存在 —— 要先 `npm run build` 才有产物。
> 后端启动时会自己 `Directory.CreateDirectory` 建出这个目录，
> 但目录是空的，页面会是白屏。

`build.emptyOutDir` 为 `true`，每次构建会先清空该目录，别往里手工放文件。

### `npm run preview` —— 预览构建产物

```bash
cd web
npm run build
npm run preview
```

用 Vite 的静态服务器预览刚构建出来的产物（默认 `http://localhost:4173/`）。

`/api` 照样通：Vite 的 `preview.proxy` 默认就是 `server.proxy`，
所以它和 `npm run dev` 一样把 `/api` 转发到 `http://127.0.0.1:5080` ——
**后端仍然要起着**。深链（`/projects` 等）由 Vite 自己的 SPA 兜底处理。

它比 `npm run dev` 少一层开发期处理（HMR、快速刷新），
适合「构建产物对不对」的验收。

### `npm test` —— 单元测试

```bash
cd web
npm test
```

Vitest 跑 `src/**/*.spec.ts`。测试环境全局是 **jsdom**（`vite.config.ts` 的
`test.environment`）—— vue-router 在模块求值时就要解构 `window`，
用 node 环境时只要有 spec import 了 `router.ts` 就会在收集阶段报
"window is not defined"，所以这里全局设死，不靠单文件 pragma。

### `npm run typecheck` —— 类型检查

```bash
cd web
npm run typecheck
```

`vue-tsc --noEmit`，只检查不产出。改完 `.vue` 或 `.ts` 建议先跑一遍。

它跑**两遍**，各用一份 tsconfig：

- **应用**（`tsconfig.json`）：`types` 只有 `vite/client` —— 纯浏览器，
  没有 node 的全局（`process`、`Buffer`…）。这是故意的：让「在浏览器里
  用了 node API」这类错在编译期就红。
- **e2e**（`e2e/tsconfig.json`）：`types` 是 `node` —— 它确实要起进程、
  读文件、发 HTTP。两份分开，**应用那边不会因为 e2e 要用 node 就拿到
  node 全局**。

e2e 那份还带来两个好处：

- e2e 的 **近 6900 行** TypeScript 从此也在类型检查里 ——
  以前只有运行时才会暴露类型错误。
- e2e 的类型**直接用应用的那份**（`import type`，纯类型导入，运行时不
  产生依赖）。早先 e2e 自己手抄了一份，两边各自独立、编译器看不出来
  漂移；改成类型导入后，「e2e 用的字段在应用里不存在」与「两边形状对不上」
  都会在编译期红。

## 与后端的接线

- 前端所有业务请求走 `/api` 前缀（`src/api/client.ts` 的 `API_BASE`）。
  开发时由 Vite proxy 转发，生产时同源直连后端。
- 前端用 **history 模式**（`/projects`、`/trash`、`/webhooks`、
  `/projects/:id/webhooks` 都是真实路径）。后端靠
  `app.MapFallbackToFile("index.html")` 兜底未匹配的 GET，
  深链直接访问才不会 404。
- 静态文件由 `app.UseStaticFiles()` 提供 —— **它必须在 `MapFallbackToFile` 之前**，
  否则 `assets/` 下的 JS/CSS 全 404，页面白屏。
- 后端只绑回环地址，对外走 nginx（见根 README「部署」一节）。
  nginx 把 `/api/events`（SSE）单独一段关缓冲，其余路径透传给应用。
