/**
 * 标签过滤的 AND / OR 模式切换（任一 ↔ 全部）的 E2E 用例。
 *
 * 补的是覆盖审计里的 P0-1（web/e2e/COVERAGE.md:199-203）：
 * - 模式按钮本身（web/src/components/TagFilter.vue:90-101，`toggleMode` 在 :37-39）
 *   从来没被点过：默认文案、切换后的文案、切换后的提示（title）都没被钉住；
 * - AND 分支的匹配语义（web/src/lib/filter.ts:54-56：必须**同时**命中全部选中标签）
 *   一条用例都没走过：ideas.spec.ts:527 只覆盖了默认的 OR。
 * - 顺带钉住模式是**本机偏好**（web/src/composables/useTagMode.ts:13-31）：
 *   切到 AND 后刷新，模式仍是 AND。
 *
 * 约定见 web/e2e/README.md：
 * - 从 ./fixtures 导入 test/expect（复位在 beforeEach 里自动发生，用例之间互不依赖）；
 * - 不用固定时长等待，一律等状态（toBeVisible / toHaveCount / toHaveText）；
 * - 选择器优先 role / 文本，其次是应用已有的钩子（data-idea-id 等）。
 */
import { test, expect, type Seed } from './fixtures';
import type { Locator, Page } from '@playwright/test';

// 复位由公共夹具的自动夹具统一负责（见 web/e2e/fixtures.ts 的 resetBeforeEach）：
// 每个用例开始前都会复位，用例之间互不依赖。

// ── 定位辅助 ─────────────────────────────────────────────────────────────

/**
 * 标签下拉的触发按钮。
 *
 * 文案随选中标签数变：「标签」/「标签（2）」，所以用正则把两种都算上。
 * 用 `^` 锚住开头，避免撞上条目上那个「🏷 标签」按钮（它的无障碍名以 🏷 开头）。
 */
function tagTrigger(page: Page): Locator {
  return page.getByRole('button', { name: /^标签(（\d+）)?$/ });
}

/**
 * AND/OR 模式切换按钮。
 *
 * 文案随模式变：「任一（OR）」/「全部（AND）」（TagFilter.vue:100）。
 * 用正则同时匹配两种状态，这样切换前后都用同一个定位器。
 */
function modeButton(page: Page): Locator {
  return page.getByRole('button', { name: /任一（OR）|全部（AND）/ });
}

/** 某一条想法的容器元素（用 data-idea-id 精确定位，列表里有多条时不会撞）。 */
function ideaItem(page: Page, id: string): Locator {
  return page.locator(`article[data-idea-id="${id}"]`);
}

/** 打开标签下拉面板，等面板里的复选框出现。 */
async function openTagPanel(page: Page): Promise<void> {
  await tagTrigger(page).click();
  await expect(page.getByRole('checkbox', { name: '标签甲' })).toBeVisible();
}

/** 勾一个标签（面板要已经打开）。 */
async function checkTag(page: Page, name: string): Promise<void> {
  await page.getByRole('checkbox', { name }).check();
}

/**
 * 造一组用来测 AND/OR 的想法。
 *
 * 甲：两个标签都有；乙：只有标签甲；丙：只有标签乙；丁：一个标签都没有。
 * 正文里**不含**标签名 —— 条目上的勾选框（aria-label="选择 <正文首行>"）与标签
 * 面板里的勾选框都是 checkbox，名字撞了就定位不到后者（ideas.spec.ts:528-529）。
 */
async function seedTagIdeas(seed: Seed): Promise<{
  both: string;
  onlyA: string;
  onlyB: string;
  none: string;
}> {
  const both = await seed.idea('想法甲', { tags: ['标签甲', '标签乙'] });
  const onlyA = await seed.idea('想法乙', { tags: ['标签甲'] });
  const onlyB = await seed.idea('想法丙', { tags: ['标签乙'] });
  const none = await seed.idea('想法丁');
  return { both: both.id, onlyA: onlyA.id, onlyB: onlyB.id, none: none.id };
}

// ── 模式按钮本身 ─────────────────────────────────────────────────────────

test('模式按钮：默认是「任一（OR）」，提示写明命中任一即显示', async ({ page, seed }) => {
  await seed.idea('想法甲', { tags: ['标签甲'] });
  await page.goto('/');

  const mode = modeButton(page);
  await expect(mode).toBeVisible();
  await expect(mode).toHaveText('任一（OR）');
  // title 是给用户看的提示（TagFilter.vue:93-97），随模式变。
  await expect(mode).toHaveAttribute('title', '当前：命中任意一个选中标签即显示');
});

test('模式按钮：切一次变「全部（AND）」，再切一次变回「任一（OR）」', async ({ page, seed }) => {
  await seed.idea('想法甲', { tags: ['标签甲'] });
  await page.goto('/');

  await modeButton(page).click();
  await expect(modeButton(page)).toHaveText('全部（AND）');
  await expect(modeButton(page)).toHaveAttribute('title', '当前：必须同时命中全部选中标签才显示');

  await modeButton(page).click();
  await expect(modeButton(page)).toHaveText('任一（OR）');
  await expect(modeButton(page)).toHaveAttribute('title', '当前：命中任意一个选中标签即显示');
});

// ── AND / OR 的匹配语义 ─────────────────────────────────────────────────

test('切到「全部（AND）」：只显示同时命中全部选中标签的想法', async ({ page, seed }) => {
  const ids = await seedTagIdeas(seed);
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(4);

  await openTagPanel(page);

  // OR（默认）：勾一个 → 命中该标签的都在。
  await checkTag(page, '标签甲');
  await expect(page.locator('article')).toHaveCount(2);
  await expect(ideaItem(page, ids.both)).toBeVisible();
  await expect(ideaItem(page, ids.onlyA)).toBeVisible();

  // OR：再勾一个 → 命中任一标签的都在（甲、乙、丙）。
  await checkTag(page, '标签乙');
  await expect(page.locator('article')).toHaveCount(3);
  await expect(ideaItem(page, ids.onlyB)).toBeVisible();

  // 切到 AND：必须**同时**命中两个标签，只剩甲。
  await modeButton(page).click();
  await expect(modeButton(page)).toHaveText('全部（AND）');
  await expect(page.locator('article')).toHaveCount(1);
  await expect(ideaItem(page, ids.both)).toBeVisible();
  await expect(ideaItem(page, ids.onlyA)).toHaveCount(0);
  await expect(ideaItem(page, ids.onlyB)).toHaveCount(0);
  await expect(ideaItem(page, ids.none)).toHaveCount(0);
});

test('切回「任一（OR）」：又回到命中任一选中标签的想法', async ({ page, seed }) => {
  const ids = await seedTagIdeas(seed);
  await page.goto('/');

  await openTagPanel(page);
  await checkTag(page, '标签甲');
  await checkTag(page, '标签乙');
  await expect(page.locator('article')).toHaveCount(3);

  // 切到 AND → 只剩同时命中两个标签的甲。
  await modeButton(page).click();
  await expect(page.locator('article')).toHaveCount(1);
  await expect(ideaItem(page, ids.both)).toBeVisible();

  // 切回 OR → 又回到命中任一标签的三条。
  await modeButton(page).click();
  await expect(modeButton(page)).toHaveText('任一（OR）');
  await expect(page.locator('article')).toHaveCount(3);
  await expect(ideaItem(page, ids.both)).toBeVisible();
  await expect(ideaItem(page, ids.onlyA)).toBeVisible();
  await expect(ideaItem(page, ids.onlyB)).toBeVisible();
  await expect(ideaItem(page, ids.none)).toHaveCount(0);
});

test('只勾一个标签时：AND 与 OR 的结果一样（全部＝那一个）', async ({ page, seed }) => {
  const ids = await seedTagIdeas(seed);
  await page.goto('/');

  await openTagPanel(page);
  await checkTag(page, '标签甲');
  await expect(page.locator('article')).toHaveCount(2);

  await modeButton(page).click();
  await expect(modeButton(page)).toHaveText('全部（AND）');
  await expect(page.locator('article')).toHaveCount(2);
  await expect(ideaItem(page, ids.both)).toBeVisible();
  await expect(ideaItem(page, ids.onlyA)).toBeVisible();
  await expect(ideaItem(page, ids.onlyB)).toHaveCount(0);
});

// ── 模式是本机偏好 ───────────────────────────────────────────────────────

test('模式写进本地：刷新后仍是「全部（AND）」，过滤照旧按 AND 走', async ({ page, seed }) => {
  const ids = await seedTagIdeas(seed);
  await page.goto('/');

  await openTagPanel(page);
  await checkTag(page, '标签甲');
  await checkTag(page, '标签乙');
  await modeButton(page).click();
  await expect(page.locator('article')).toHaveCount(1);

  // 刷新：模式从本地偏好里读回来，仍是 AND（筛选条件本身不落盘，标签要重新勾）。
  await page.reload();
  await expect(modeButton(page)).toHaveText('全部（AND）');
  // 没勾标签 → 四条都在（标签筛选不落盘）。
  await expect(page.locator('article')).toHaveCount(4);

  await openTagPanel(page);
  await checkTag(page, '标签甲');
  await checkTag(page, '标签乙');
  await expect(page.locator('article')).toHaveCount(1);
  await expect(ideaItem(page, ids.both)).toBeVisible();
  await expect(ideaItem(page, ids.onlyA)).toHaveCount(0);
  await expect(ideaItem(page, ids.onlyB)).toHaveCount(0);
});

// ── 交互细节 ─────────────────────────────────────────────────────────────

test('切换模式不动已选标签：计数与勾选状态都保持', async ({ page, seed }) => {
  await seedTagIdeas(seed);
  await page.goto('/');

  await openTagPanel(page);
  await checkTag(page, '标签甲');
  await expect(tagTrigger(page)).toHaveText('标签（1）');

  await modeButton(page).click();

  // 选中的标签没被清掉：计数还是 1，复选框还是勾着的。
  await expect(tagTrigger(page)).toHaveText('标签（1）');
  await expect(page.getByRole('checkbox', { name: '标签甲' })).toBeChecked();
});

test('标签面板收起时也能切换模式', async ({ page, seed }) => {
  const ids = await seedTagIdeas(seed);
  await page.goto('/');

  await openTagPanel(page);
  await checkTag(page, '标签甲');
  await expect(page.locator('article')).toHaveCount(2);

  // Esc 收起面板（TagFilter.vue:62-66）。
  await page.keyboard.press('Escape');
  await expect(page.getByRole('checkbox', { name: '标签甲' })).toHaveCount(0);

  // 面板收着也能切模式，过滤照旧生效。
  await modeButton(page).click();
  await expect(modeButton(page)).toHaveText('全部（AND）');
  await expect(page.locator('article')).toHaveCount(2);
  await expect(ideaItem(page, ids.both)).toBeVisible();
  await expect(ideaItem(page, ids.onlyA)).toBeVisible();
  await expect(ideaItem(page, ids.onlyB)).toHaveCount(0);
});
