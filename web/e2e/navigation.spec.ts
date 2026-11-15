/**
 * 外壳、导航与深链。
 *
 * 覆盖四块：
 *   1. 外壳与 PageNav —— 管理页之间的导航条（四个链接、当前项高亮与 aria-current、
 *      回首页的「想法」入口、点击后的路由与内容）；
 *   2. 深链 —— 五个路由直接访问 / 刷新不 404，浏览器前进后退，未知路由的兜底；
 *   3. ToastHost —— 成功/失败提示与加载态、空状态文案；
 *   4. 加载态与空状态的关系 —— 加载中不闪空状态。
 *
 * 约定见 web/e2e/README.md：夹具（复位、造数据）从 './fixtures' 导入，
 * 选择器优先 role/文本，其次已有的无障碍钩子（nav[aria-label="管理"] 等），
 * 不用固定时长的 sleep。
 */
import type { Page, Response } from '@playwright/test';

import { expect, INBOX_ID, INBOX_NAME, test } from './fixtures';

/** 管理页导航条（三个管理页都有；主页面没有）。 */
const NAV_SELECTOR = 'nav[aria-label="管理"]';

/** 取管理页导航条。 */
function nav(page: Page) {
  return page.locator(NAV_SELECTOR);
}

/** 导航条里的某个链接（按可访问名取，避免同名元素）。 */
function navLink(page: Page, name: string) {
  return nav(page).getByRole('link', { name, exact: true });
}

/**
 * 应用挂起来了吗？
 *
 * ToastHost 是 App.vue 里**无条件**渲染的常驻组件，它在 DOM 里就说明
 * Vue 挂上了 —— 比等某个业务元素更普适（有的页面本来就没有想法、
 * 没有 webhook，等那些元素会一直等到超时）。
 */
async function appMounted(page: Page): Promise<boolean> {
  try {
    await expect(page.getByRole('status')).toBeAttached({ timeout: 3000 });
    return true;
  } catch {
    return false;
  }
}

/**
 * 加载一次页面，并确认应用真的挂起来了；没挂上就再加载一次。
 *
 * 为什么要有「再加载一次」：别的阶段在跑 `rebuild.sh frontend`（或
 * `start-server.sh`）时，vite 会先把 wwwroot **清空**再写新产物
 * （web/vite.config.ts 的 `emptyOutDir: true`，产物目录就是后端的
 * wwwroot，后端同源托管它）。那个窗口里（实测约 30ms：600 次采样里
 * 6 次）后端对页面地址回 **404 空 body**，浏览器停在错误页；或者
 * HTML 是新的而 JS 产物还没写完 → JS 404 → 页面一片白。
 * 这两种情况下页面永远等不到应用挂起来（截图就是白的，页面上什么都没有）。
 *
 * 再加载一次拿到的是新产物，页面就正常了。应用自己坏了的话再加载也起不来，
 * 用例照样红 —— 这个兜底不会把真 bug 盖住。
 *
 * 返回「应用挂上了那一次」的响应（深链用例要断言状态码与正文）。
 */
async function loadUntilMounted(
  page: Page,
  load: () => Promise<Response | null>,
): Promise<Response | null> {
  let response = await load();
  if (await appMounted(page)) {
    return response;
  }
  response = await load();
  await expect(page.getByRole('status')).toBeAttached({ timeout: 10_000 });
  return response;
}

/** 打开页面并等应用挂起来。 */
async function openApp(page: Page, path: string): Promise<Response | null> {
  return loadUntilMounted(page, () => page.goto(path));
}

/** 重载当前页面并等应用挂起来。 */
async function reloadApp(page: Page): Promise<Response | null> {
  return loadUntilMounted(page, () => page.reload());
}

// ── 导航条本身 ────────────────────────────────────────────────────────────

for (const path of ['/projects', '/trash', '/webhooks']) {
  test(`导航条在 ${path} 上渲染，四个入口都在`, async ({ page }) => {
    await openApp(page, path);

    await expect(nav(page)).toBeVisible();
    // 四个入口：回想法列表的「想法」+ 三个管理页。
    await expect(nav(page).getByRole('link')).toHaveCount(4);
    // 顺序也钉住：第一个是回首页的那个「想法」。
    await expect(nav(page).getByRole('link')).toHaveText(['想法', '项目', '回收站', 'Webhook']);
  });
}

test('导航条的四个链接指向 /、/projects、/trash、/webhooks', async ({ page }) => {
  await openApp(page, '/projects');

  // 「想法」是回主列表页的入口（管理页之间跳得再顺，也得有一条路回想法列表）。
  await expect(navLink(page, '想法')).toHaveAttribute('href', '/');
  await expect(navLink(page, '项目')).toHaveAttribute('href', '/projects');
  await expect(navLink(page, '回收站')).toHaveAttribute('href', '/trash');
  await expect(navLink(page, 'Webhook')).toHaveAttribute('href', '/webhooks');
});

test('主页面没有管理页导航条（外壳不同）', async ({ page }) => {
  await openApp(page, '/');

  await expect(nav(page)).toHaveCount(0);

  // 主页面自己有**两套**项目导航，用不同的 aria-label 区分开：
  //   - 宽屏侧栏：aria-label="项目"；
  //   - 窄屏 Tab 条：aria-label="项目切换"。
  // 两套渲染的是同一批项目，靠 CSS 媒体查询互斥显示（断点两侧各露一个），
  // 所以**窄屏那套此刻也在 DOM 里**，只是被 display:none 藏起来了。
  const sidebar = page.locator('nav[aria-label="项目"]');
  const tabs = page.locator('nav[aria-label="项目切换"]');

  await expect(sidebar).toHaveCount(1);
  await expect(tabs).toHaveCount(1);
  // 宽屏（默认视口 1280×720）显示的是侧栏，Tab 条收着。
  await expect(sidebar).toBeVisible();
  await expect(tabs).toBeHidden();
});

// ── 当前项高亮 ────────────────────────────────────────────────────────────

for (const item of [
  { path: '/projects', label: '项目' },
  { path: '/trash', label: '回收站' },
  { path: '/webhooks', label: 'Webhook' },
]) {
  test(`当前页在导航条上被点亮：${item.path}`, async ({ page }) => {
    await openApp(page, item.path);

    // vue-router 给「完全匹配」的链接加 aria-current="page" —— 这是无障碍钩子，
    // 比看 class 稳。
    const current = nav(page).locator('a[aria-current="page"]');
    await expect(current).toHaveCount(1);
    await expect(current).toHaveText(item.label);
  });
}

test('项目级 webhook 页点亮的是「Webhook」，不是「项目」', async ({ page }) => {
  // 用内置收件箱（不会被复位删掉）—— 用例之间不共享任何数据。
  await openApp(page, `/projects/${INBOX_ID}/webhooks`);

  // 高亮是 PageNav 自己算的（PageNav.vue:14-17 是故意绕开 router-link-active
  // 的前缀匹配：项目级 webhook 页会把「项目」也点亮，与用户以为的正好相反）。
  await expect(navLink(page, 'Webhook')).toHaveClass(/is-active/);
  await expect(navLink(page, '项目')).not.toHaveClass(/is-active/);
  await expect(navLink(page, '回收站')).not.toHaveClass(/is-active/);

  // aria-current 跟着同一个判据走（vue-router 只在「完全匹配」时加它，
  // 这个路由上它一个都不加 —— 读屏软件就不知道当前在哪一页了）。
  await expect(navLink(page, 'Webhook')).toHaveAttribute('aria-current', 'page');
  await expect(navLink(page, '项目')).not.toHaveAttribute('aria-current', 'page');
  await expect(navLink(page, '回收站')).not.toHaveAttribute('aria-current', 'page');
  await expect(navLink(page, '想法')).not.toHaveAttribute('aria-current', 'page');
});

test('当前项目：侧栏项与 Tab 项都带 aria-current="page"', async ({ page, seed }) => {
  const project = await seed.project('当前项目');
  await openApp(page, '/');

  const sidebarItem = (id: string) => page.locator(`nav[aria-label="项目"] [data-project-id="${id}"]`);
  const tabsItem = (id: string) => page.locator(`nav[aria-label="项目切换"] [data-project-id="${id}"]`);

  // 默认选中「全部项目」：两套导航里的「全部」都带 aria-current。
  await expect(sidebarItem('all')).toHaveAttribute('aria-current', 'page');
  await expect(tabsItem('all')).toHaveAttribute('aria-current', 'page');

  // 切到那个项目：侧栏项与 Tab 项都带上，「全部」那两项都摘掉。
  await sidebarItem(project.id).click();

  await expect(sidebarItem(project.id)).toHaveAttribute('aria-current', 'page');
  await expect(tabsItem(project.id)).toHaveAttribute('aria-current', 'page');
  await expect(sidebarItem('all')).not.toHaveAttribute('aria-current', 'page');
  await expect(tabsItem('all')).not.toHaveAttribute('aria-current', 'page');

  // 两套导航的项目名也用不同的 data-testid 区分开
  // （project-sidebar-name-<id> / project-tabs-name-<id>）——
  // 两个元素都渲染着，光看文字分不出谁是谁。
  await expect(page.getByTestId(`project-sidebar-name-${project.id}`)).toHaveCount(1);
  await expect(page.getByTestId(`project-tabs-name-${project.id}`)).toHaveCount(1);
});

// ── 回首页的入口 ───────────────────────────────────────────────────────────

for (const path of ['/projects', '/trash', '/webhooks']) {
  test(`从 ${path} 点「想法」回到首页，想法列表还在`, async ({ page, seed }) => {
    const idea = await seed.idea('回到首页要看得见的想法');
    await openApp(page, path);

    await navLink(page, '想法').click();

    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('heading', { name: '想法', exact: true })).toBeVisible();
    await expect(page.locator(`article[data-idea-id="${idea.id}"]`)).toBeVisible();
  });
}

// ── 点击导航：路由与内容对应 ──────────────────────────────────────────────

for (const item of [
  { from: '/trash', label: '项目', path: '/projects', heading: '项目管理' },
  { from: '/projects', label: '回收站', path: '/trash', heading: '回收站' },
  { from: '/projects', label: 'Webhook', path: '/webhooks', heading: 'Webhook' },
]) {
  test(`点击「${item.label}」跳到 ${item.path} 并显示对应页面`, async ({ page }) => {
    await openApp(page, item.from);

    await navLink(page, item.label).click();

    await expect(page).toHaveURL(new RegExp(`${item.path}$`));
    await expect(page.getByRole('heading', { name: item.heading, exact: true })).toBeVisible();
  });
}

test('主页面上的「新建项目」入口能到项目管理页', async ({ page }) => {
  await openApp(page, '/');

  // 侧栏的「＋」按钮（title="新建项目"；可访问名是符号本身）。
  await page.locator('nav[aria-label="项目"]').getByRole('button', { name: '＋' }).click();

  await expect(page).toHaveURL(/\/projects$/);
  await expect(page.getByRole('heading', { name: '项目管理', exact: true })).toBeVisible();
});

// ── 五个路由直接访问（深链刷新不 404） ────────────────────────────────────

for (const route of [
  { path: '/', heading: '想法' },
  { path: '/projects', heading: '项目管理' },
  { path: '/trash', heading: '回收站' },
  { path: '/webhooks', heading: 'Webhook' },
]) {
  test(`深链直接访问 ${route.path}：返回 index.html，应用正常挂载`, async ({ page }) => {
    const response = await openApp(page, route.path);

    expect(response?.status()).toBe(200);
    expect(response?.headers()['content-type']).toContain('text/html');
    // 返回的是 index.html 本体（应用挂载点在里面）。
    expect(await response?.text()).toContain('<div id="app">');
    await expect(page.locator('#app')).toBeAttached();
    // 应用真的挂起来了：页面自己的标题渲染出来了。
    await expect(page.getByRole('heading', { name: route.heading, exact: true })).toBeVisible();
    await expect(page).toHaveTitle('Idea 记事本');
  });
}

test('深链直接访问项目级 webhook 页：返回 index.html，应用正常挂载', async ({ page }) => {
    const response = await openApp(page, `/projects/${INBOX_ID}/webhooks`);

  expect(response?.status()).toBe(200);
  expect(response?.headers()['content-type']).toContain('text/html');
  expect(await response?.text()).toContain('<div id="app">');
  await expect(page.getByRole('heading', { name: /项目 Webhook/ })).toBeVisible();
  // 项目名取自项目列表，标题里带项目名。
  await expect(page.getByRole('heading', { name: `项目 Webhook · ${INBOX_NAME}` })).toBeVisible();
});

test('深链刷新不 404：刷新后还是同一个页面', async ({ page }) => {
  await openApp(page, `/projects/${INBOX_ID}/webhooks`);
  await expect(page.getByRole('heading', { name: /项目 Webhook/ })).toBeVisible();

  const response = await reloadApp(page);

  expect(response?.status()).toBe(200);
  await expect(page).toHaveURL(new RegExp(`/projects/${INBOX_ID}/webhooks$`));
  await expect(page.getByRole('heading', { name: /项目 Webhook/ })).toBeVisible();
});

test('项目级 webhook 深链指向不存在的项目时页面仍能打开', async ({ page }) => {
    await openApp(page, '/projects/no-such-project/webhooks');

  // 找不到项目时标题退回「项目 Webhook」（不带项目名），页面不崩。
  await expect(page.getByRole('heading', { name: '项目 Webhook', exact: true })).toBeVisible();
});

// ── 浏览器前进后退 ────────────────────────────────────────────────────────

test('浏览器后退与前进：路由与内容对应', async ({ page }) => {
  await openApp(page, '/projects');
  await expect(page.getByRole('heading', { name: '项目管理', exact: true })).toBeVisible();

  await navLink(page, '回收站').click();
  await expect(page).toHaveURL(/\/trash$/);
  await expect(page.getByRole('heading', { name: '回收站', exact: true })).toBeVisible();

  await page.goBack();
  await expect(page).toHaveURL(/\/projects$/);
  await expect(page.getByRole('heading', { name: '项目管理', exact: true })).toBeVisible();

  await page.goForward();
  await expect(page).toHaveURL(/\/trash$/);
  await expect(page.getByRole('heading', { name: '回收站', exact: true })).toBeVisible();
});

test('浏览器后退回到主页面：内容还是想法列表', async ({ page }) => {
  await openApp(page, '/');
  await expect(page.getByRole('heading', { name: '想法', exact: true })).toBeVisible();

  await page.locator('nav[aria-label="项目"]').getByRole('button', { name: '＋' }).click();
  await expect(page).toHaveURL(/\/projects$/);

  await page.goBack();

  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole('heading', { name: '想法', exact: true })).toBeVisible();
});

// ── 未知路由的兜底 ────────────────────────────────────────────────────────

test('未知路径由后端兜底成 index.html（不是 404）', async ({ page }) => {
    const response = await openApp(page, '/no-such-page');

  expect(response?.status()).toBe(200);
  expect(response?.headers()['content-type']).toContain('text/html');
  expect(await response?.text()).toContain('<div id="app">');
});

test('未知路由有兜底：显示首页而不是白屏', async ({ page }) => {
  await openApp(page, '/no-such-page');

  // 路由表里有 catch-all（web/src/router.ts 的 fallback 条目），未知地址
  // 回首页 —— 以前没有这条路由时会渲染空的 RouterView，用户看到白屏。
  // 这里钉住「要有兜底」这条期望。
  await expect(page.getByRole('heading', { name: '想法', exact: true })).toBeVisible();
});

// ── ToastHost ─────────────────────────────────────────────────────────────

/**
 * 把项目管理页要的两个接口固定成成功响应。
 *
 * 为什么要打桩：页面的加载请求一旦失败，应用会弹一条错误提示 →
 * 「没有消息时是空的」这条断言就被一条无关的提示顶掉了。
 * 这里只关心 ToastHost 自己的行为。
 */
async function stubProjectsPage(page: Page): Promise<void> {
  await page.route('**/api/projects**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/settings**', (route) => route.fulfill({ json: {} }));
}

test('ToastHost 常驻在页面上，没有消息时是空的', async ({ page }) => {
  await stubProjectsPage(page);
  await openApp(page, '/projects');

  const host = page.getByRole('status');
  await expect(host).toBeAttached();
  await expect(host).toHaveText('');
});

test('失败时 ToastHost 弹出错误提示', async ({ page }) => {
  await stubProjectsPage(page);
  await openApp(page, '/projects');

  // 不填名字就点「新建项目」—— 应用给出错误提示（ProjectsPage.vue:63-67）。
  await page.getByRole('button', { name: '新建项目' }).click();

  await expect(page.getByRole('status')).toContainText('项目名称不能为空');
  // 错误提示与普通提示只差一个 class（ToastHost.vue:45），没有别的无障碍钩子，
  // 只能看 class。
  await expect(page.locator('[role="status"] .toast')).toHaveClass(/toast--error/);
});

test('错误提示过几秒自动消失', async ({ page }) => {
  await stubProjectsPage(page);
  await openApp(page, '/projects');

  await page.getByRole('button', { name: '新建项目' }).click();
  await expect(page.getByText('项目名称不能为空')).toBeVisible();

  // ToastHost 4 秒后自动收掉（ToastHost.vue:23-28）。
  await expect(page.getByText('项目名称不能为空')).toHaveCount(0, { timeout: 10_000 });
  await expect(page.getByRole('status')).toHaveText('');
});

// ── 加载态与空状态 ────────────────────────────────────────────────────────

test('加载态：想法列表加载中显示「加载中…」', async ({ page }) => {
  // 把想法列表的接口按住，好观察加载态 —— 这是唯一能看到「加载中…」的窗口。
  let release = (): void => {};
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route('**/api/ideas**', async (route) => {
    await gate;
    await route.continue();
  });

  await openApp(page, '/');

  await expect(page.getByText('加载中…')).toBeVisible();

  release();
  await expect(page.getByText('加载中…')).toHaveCount(0);
});

test('加载态：加载中不闪空状态（「这里还没有想法。」不与「加载中…」同时出现）', async ({ page }) => {
  // 装一个观察者：只要「加载中…」与「这里还没有想法。」在 DOM 里**同时**出现过，
  // 就记一笔 —— 这样连「闪一帧」也抓得住（只断言某一瞬间看不到是不够的）。
  await page.addInitScript(() => {
    const store = window as unknown as { __loadingAndEmptyCoexisted: number };
    store.__loadingAndEmptyCoexisted = 0;
    const scan = (): void => {
      const texts = Array.from(document.querySelectorAll('p'));
      const loading = texts.some((el) => el.textContent?.trim() === '加载中…');
      const empty = texts.some((el) => el.textContent?.trim() === '这里还没有想法。');
      if (loading && empty) {
        store.__loadingAndEmptyCoexisted += 1;
      }
    };
    const observer = new MutationObserver(() => scan());
    const attach = (): void => {
      if (document.documentElement) {
        observer.observe(document.documentElement, {
          childList: true,
          subtree: true,
          characterData: true,
        });
      }
    };
    if (document.documentElement) {
      attach();
    } else {
      document.addEventListener('DOMContentLoaded', attach);
    }
  });

  // 把想法列表的接口按住，好观察加载态 —— 这是唯一能看到「加载中…」的窗口。
  let release = (): void => {};
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route('**/api/ideas**', async (route) => {
    await gate;
    await route.continue();
  });

  await openApp(page, '/');

  await expect(page.getByText('加载中…')).toBeVisible();
  // 加载中时那条空状态文案不在 DOM 里（不是「存在但看不见」）。
  await expect(page.getByText('这里还没有想法。')).toHaveCount(0);

  release();
  await expect(page.getByText('加载中…')).toHaveCount(0);

  // 整个过程中两者一次都没同时出现过。
  const coexisted = await page.evaluate(
    () => (window as unknown as { __loadingAndEmptyCoexisted: number }).__loadingAndEmptyCoexisted,
  );
  expect(coexisted, '「加载中…」与「这里还没有想法。」同时出现过').toBe(0);
});

test('空状态：没有想法时显示「这里还没有想法。」', async ({ page }) => {
  // 「空」这个状态用桩数据钉住：靠真实数据断言「空」不稳
  // （库里可能有想法 —— 它们默认往收件箱里造）。
  // 这里关心的是文案，不是数据。
  await page.route('**/api/ideas**', (route) => route.fulfill({ json: [] }));

  await openApp(page, '/');

  // 加载完之后才轮到空状态文案。
  await expect(page.getByText('加载中…')).toHaveCount(0);
  await expect(page.getByText('这里还没有想法。')).toBeVisible();
});

test('空状态：没有 webhook 时显示「还没有 webhook。」', async ({ page }) => {
  // 同上：全局 webhook 列表也用桩数据固定成空。
  await page.route('**/api/webhooks**', (route) => route.fulfill({ json: [] }));

  await openApp(page, '/webhooks');

  await expect(page.getByText('还没有 webhook。')).toBeVisible();
});

// ⚠️ 「回收站是空的。」这条没有写用例：它只在**全新数据库**下出现 ——
// 复位清不掉回收站（应用只允许恢复、不允许彻底删除），而拿到全新库要靠
// freshDatabase()，那会删掉本轮的 E2E 库并重启本轮的服务，
// 会打断同时在跑的其它用例（workers=1 只保证单进程内串行，管不了跨进程）。
// 详见本次交付的 gaps。
