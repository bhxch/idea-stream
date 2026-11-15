/**
 * 服务端搜索与往回翻历史的 E2E 用例。
 *
 * 覆盖（搜索改造到服务端之后的行为）：
 * - 搜索范围不止正文：标题、标签名、项目名都能搜到；
 * - 命中带高亮（`<mark>`）；
 * - 英文是**词级**（搜 `hel` 搜不到 `hello`，搜 `hello` 才搜得到）；
 * - 空格分出的多段之间是 **AND**（两段都出现才命中，不必相邻）；
 * - 往回翻历史：打开页面停在最下边、接近顶部自动加载更旧的一页。
 *
 * 约定见 web/e2e/README.md：
 * - 从 ./fixtures 导入 test/expect（复位在 beforeEach 里自动发生，用例之间互不依赖）；
 * - 不用固定时长等待，一律等状态（toBeVisible / toHaveCount / toBeInViewport）；
 * - 选择器优先 role / 文本，其次是应用**已有**的钩子
 *   （data-idea-id、title="点击编辑"）—— 滚动容器没有可访问名，只能用 BEM class。
 */
import { test, expect } from './fixtures';
import type { Locator, Page } from '@playwright/test';

// ⚠️ 上面那行**必须**从 './fixtures' 导入 test —— 不是从 '@playwright/test'。
// 复位是夹具里的**自动夹具**（{ auto: true }，见 web/e2e/fixtures.ts）：
// 从 '@playwright/test' 导入的 test 用不到那个自动夹具，每例前的复位就不生效，
// 用例之间会串味（表现是「单独跑这个文件全过、全量跑就红」）。

// ── 定位辅助 ─────────────────────────────────────────────────────────────

/**
 * 过滤栏里的关键词输入框。
 *
 * 过滤栏的搜索框是 `type="search"`，无障碍角色是 searchbox，
 * 名字来自 aria-label="搜索想法"（应用自带的钩子）。
 */
function keywordSearch(page: Page): Locator {
  return page.getByRole('searchbox', { name: '搜索想法' });
}

/**
 * 某一条想法的容器元素。
 *
 * 用 `data-idea-id` 限定到具体那一条：列表里有多条想法时，
 * 按角色/文字定位会一次匹配到多条（strict mode 报错），或者匹配错条。
 * 这个属性是应用自己渲染的（web/src/components/MessageItem.vue）。
 */
function ideaItem(page: Page, id: string): Locator {
  return page.locator(`article[data-idea-id="${id}"]`);
}

/**
 * 消息流的滚动容器。
 *
 * 用 BEM class 定位：它是纯布局容器，没有 role / aria-label 可用
 * （web/src/pages/HomePage.vue 的 `.home__scroll`）。
 */
function scrollContainer(page: Page): Locator {
  return page.locator('.home__scroll');
}

// ── 搜索 ─────────────────────────────────────────────────────────────────

test('搜索：能搜到标题里的词', async ({ page, seed }) => {
  // 关键词只在**标题**里，正文里没有 —— 命中靠的是标题那一列。
  await seed.idea('正文里没有那个词', { title: '采购咖啡机滤芯' });
  await seed.idea('另一条想法');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  await keywordSearch(page).fill('咖啡机');

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '采购咖啡机滤芯' })).toHaveCount(1);
});

test('搜索：能搜到标签名', async ({ page, seed, api }) => {
  // 关键词只在**标签**里 —— 正文与标题都不含它。
  await seed.idea('想法甲', { tags: ['咖啡'] });
  await seed.idea('想法乙');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  await keywordSearch(page).fill('咖啡');

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '想法甲' })).toHaveCount(1);

  // 服务端自己就在过滤（不是前端拿全量再筛一遍）：标签名也在搜索范围内。
  const hits = await api.listIdeas({ q: '咖啡' });
  expect(hits).toHaveLength(1);
  expect(hits[0].tags).toEqual(['咖啡']);
});

test('搜索：能搜到项目名', async ({ page, seed }) => {
  // 关键词只在**项目名**里 —— 正文、标题、标签都不含它。
  const project = await seed.project('工作复盘');
  await seed.idea('想法甲', { projectId: project.id });
  await seed.idea('想法乙');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  await keywordSearch(page).fill('工作复盘');

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '想法甲' })).toHaveCount(1);
});

test('搜索：结果带高亮', async ({ page, seed }) => {
  // 关键词在**正文**里，标题另给一个 —— 这样高亮落在正文那一段上。
  const idea = await seed.idea('今天买了咖啡机滤芯', { title: '采购清单' });
  await page.goto('/');

  await keywordSearch(page).fill('咖啡');

  // 正文里命中的一段被 <mark> 包起来（服务端给字符区间，前端按区间切片段）。
  const body = ideaItem(page, idea.id).getByTitle('点击编辑');
  await expect(body.locator('mark')).toHaveText('咖啡');
});

test('搜索：英文是词级', async ({ page, seed }) => {
  await seed.idea('hello world');
  await seed.idea('想法乙');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  // 词级：hello 是一个整词，hel 只是它的前缀 —— 搜不到。
  await keywordSearch(page).fill('hel');
  await expect(page.locator('article')).toHaveCount(0);

  // 搜整个词就搜得到。
  await keywordSearch(page).fill('hello');
  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: 'hello world' })).toHaveCount(1);
});

test('搜索：多段之间是 AND', async ({ page, seed }) => {
  // 两段都在正文里，但不相邻。
  await seed.idea('今天泡咖啡，记一下水温');
  // 只有第一段 —— 段与段之间是 AND，所以它不该命中。
  await seed.idea('只说了咖啡');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  await keywordSearch(page).fill('咖啡 水温');

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '今天泡咖啡' })).toHaveCount(1);
});

// ── 往回翻历史 ───────────────────────────────────────────────────────────

test('往回翻：打开页面停在最下边', async ({ page, api }) => {
  // 造够多条，消息流才会超过一屏 —— 内容没溢出的话「停在最下边」是废话。
  const oldest = await api.createIdea({ content: '第 1 条想法\n第二行正文' });
  for (let index = 2; index <= 20; index += 1) {
    await api.createIdea({ content: `第 ${index} 条想法\n第二行正文` });
  }
  const newest = await api.createIdea({ content: '第 21 条想法\n第二行正文' });

  await page.goto('/');

  // 列表按更新时间升序（最老的在顶、最新的在底），打开页面就该看到最新那条。
  await expect(ideaItem(page, newest.id)).toBeVisible();
  await expect(ideaItem(page, newest.id)).toBeInViewport();
  // 最老的那条不在视口里：说明页面真的停在底部，而不是「内容刚好没溢出」。
  await expect(ideaItem(page, oldest.id)).toBeVisible();
  await expect(ideaItem(page, oldest.id)).not.toBeInViewport();
});

test('往回翻：接近顶部自动加载更旧的一页', async ({ page, api }) => {
  // 首屏一页是 101 条（前端按「一页 + 一条」要，好判断还有没有更旧的）。
  // 造 102 条：首屏拿到最新的 101 条，再往回翻一页就能多出更旧的那一条。
  for (let index = 1; index <= 102; index += 1) {
    await api.createIdea({ content: `第 ${index} 条想法` });
  }

  await page.goto('/');
  // 首屏只有一页。
  await expect(page.locator('article')).toHaveCount(101);

  // 服务端的 offset 从**最新**一端数：offset=0 是最新那页，
  // offset=101 是再往前 101 条 —— 正好是剩下那一条。
  const newestPage = await api.listIdeas({ limit: 101, offset: 0 });
  const olderPage = await api.listIdeas({ limit: 101, offset: 101 });
  expect(newestPage).toHaveLength(101);
  expect(olderPage).toHaveLength(1);

  // 往回翻的请求：offset 从**最新**一端数，已经加载了多少条就是多少。
  const older = page.waitForResponse(
    (response) =>
      response.url().includes('/api/ideas?') &&
      response.url().includes('offset=101') &&
      response.request().method() === 'GET',
  );

  // 滚到接近顶部 → 哨兵进入视野 → 自动加载更旧的一页。
  await scrollContainer(page).evaluate((element) => {
    element.scrollTop = 0;
  });

  await older;
  // 更旧的那一条拼到了列表**前面**：列表变长了。
  await expect(page.locator('article')).toHaveCount(102);
});
