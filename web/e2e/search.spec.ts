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

test('搜索：中文是词元相邻（标点不挡路 / 中间隔一个字就不算）', async ({ page, seed }) => {
  // 「咖啡，水温」：标点被跳过，词元仍然**相邻** → 命中。
  await seed.idea('咖啡，水温');
  // 「咖啡的水温」：中间隔了一个「的」→ 不相邻 → 不命中。
  await seed.idea('咖啡的水温');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  await keywordSearch(page).fill('咖啡水温');

  // 只剩标点被跳过的那一条：子串匹配做不到这一点（子串里「咖啡水温」压根不是
  // 「咖啡，水温」的子串），所以这条用例能把「词元相邻」与「子串」分开。
  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '咖啡，水温' })).toHaveCount(1);
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
  // id 留着：光看**条数**分不出「从最新一端数」与「从最老一端数」—— 两种口径下
  // offset=0 都返回 101 条、offset=101 都返回 1 条。要看返回的是**哪一条**。
  const ids: string[] = [];
  for (let index = 1; index <= 102; index += 1) {
    const idea = await api.createIdea({ content: `第 ${index} 条想法` });
    ids.push(idea.id);
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
  // 更旧的那一页就是**最老**的那一条（不是最新那一条）。
  expect(olderPage[0].id).toBe(ids[0]);
  // 最新那页里不能混进最老的那一条。
  expect(newestPage.some((idea) => idea.id === ids[0])).toBe(false);

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
  // 翻完之后，最老的那一条排在列表**第一**（列表按更新时间升序，最老的在顶）。
  //
  // ⚠️ 这一条**断不了「更旧的一页拼在前面还是后面」**：显示层按 updated_at
  // 重排过一遍，两种拼法排出来的顺序与滚动几何**完全一样** ——
  // 实测把 `HomePage.vue` 的 prepend 改成 append，这条用例照样全绿。
  // prepend 的语义由单测钉住：`web/src/pages/HomePage.spec.ts`
  // 「更旧的一页拼到 store 数组的前面（不是后面）」—— 断言的正是**没排序**的那一份。
  await expect(page.locator('article').first()).toHaveAttribute('data-idea-id', ids[0]);

  // 往回翻之后**视口没有跳走**：用户原来看着的那一条还在视口顶上
  // （新加载的内容落在视口**上方**，滚动修正把视口钉回原处）。
  // 滚动修正要是没做，视口会被新内容顶下去 —— 用户看着的那条就跑了。
  const containerBox = await scrollContainer(page).boundingBox();
  const secondBox = await page.locator('article').nth(1).boundingBox();
  expect(containerBox).not.toBeNull();
  expect(secondBox).not.toBeNull();
  if (containerBox && secondBox) {
    // 差一两个像素是取整（滚动容器上沿上面还有 1px 的哨兵）。
    expect(secondBox.y).toBeLessThanOrEqual(containerBox.y + 5);
  }
});

test('往回翻到顶：一条不漏、一条不重（超过两页才看得出来）', async ({ page, api }) => {
  // 造 250 条 = 三页（首屏 101 + 往回翻 101 + 最后 48）。
  //
  // **超过两页**才看得出来：服务端这一页的**最老**那一条是「多要的那条」，
  // 客户端要是把它丢了，丢的正好是紧挨着已加载区域的那一条 ——
  // 两页以内时它落在列表的边界上（看不出来），三页时它就夹在列表中间，
  // 列表从此**缺一条**，而下一轮请求拿列表长度当 offset，那个洞补不上。
  const ids: string[] = [];
  for (let index = 1; index <= 250; index += 1) {
    const idea = await api.createIdea({ content: `第 ${index} 条想法` });
    ids.push(idea.id);
  }

  await page.goto('/');
  // 首屏：一页 + 一条（多要的那条用来判断还有没有更旧的）。
  await expect(page.locator('article')).toHaveCount(101);

  // 往回翻到顶：每一轮滚到顶部触发一次预加载，等这一页拼进列表。
  // 最多 5 轮 —— 250 条最多翻两轮（101 + 101 + 48）。
  for (let round = 0; round < 5; round += 1) {
    const before = await page.locator('article').count();
    if (before >= ids.length) {
      break;
    }
    await scrollContainer(page).evaluate((element) => {
      element.scrollTop = 0;
    });
    await expect
      .poll(async () => page.locator('article').count(), { timeout: 15_000 })
      .toBeGreaterThan(before);
  }

  // 一条不漏：列表里的 id 集合 = 库里的 id 集合。
  const shown = await page.locator('article[data-idea-id]').evaluateAll((nodes) =>
    nodes.map((node) => node.getAttribute('data-idea-id') ?? ''),
  );
  expect(shown).toHaveLength(ids.length);
  expect([...shown].sort()).toEqual([...ids].sort());
  // 一条不重。
  expect(new Set(shown).size).toBe(ids.length);
});
