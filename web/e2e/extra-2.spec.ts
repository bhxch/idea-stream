/**
 * AND/OR 偏好落盘的 E2E 用例。
 *
 * 覆盖：
 * - `web/src/composables/useTagMode.ts:13-31`（读/写 localStorage，含写失败的兜底分支）
 * - `web/src/pages/HomePage.vue:365-371`（切模式时落盘）
 * - `web/src/components/TagFilter.vue:37-39, 90-101`（AND/OR 切换按钮）
 *
 * 约定见 web/e2e/README.md：
 * - 从 ./fixtures 导入 test/expect（复位在 beforeEach 里自动发生，用例之间互不依赖）；
 * - 不用固定时长等待，一律等状态（toBeVisible / toHaveCount / toHaveText）；
 * - 选择器优先 role + 可访问名，其次是可见文字。
 */
import { test, expect } from './fixtures';
import type { Locator, Page } from '@playwright/test';

/** localStorage 里存 AND/OR 偏好的键（见 useTagMode.ts:10）。 */
const STORAGE_KEY = 'idea.tagMode';

// 复位由公共夹具的自动夹具统一负责（见 web/e2e/fixtures.ts 的 resetBeforeEach）：
// 每个用例开始前都会复位，用例之间互不依赖。

// ── 定位辅助 ─────────────────────────────────────────────────────────────

/** AND/OR 切换按钮：当前是 OR 模式时按钮文案是「任一（OR）」。 */
function orModeButton(page: Page): Locator {
  return page.getByRole('button', { name: '任一（OR）' });
}

/** AND/OR 切换按钮：当前是 AND 模式时按钮文案是「全部（AND）」。 */
function andModeButton(page: Page): Locator {
  return page.getByRole('button', { name: '全部（AND）' });
}

/** 读出 localStorage 里存的偏好（没存过就是 null）。 */
function storedMode(page: Page): Promise<string | null> {
  return page.evaluate((key) => window.localStorage.getItem(key), STORAGE_KEY);
}

/** 正文含某段文字的想法条目。 */
function ideaItem(page: Page, text: string): Locator {
  return page.locator('article').filter({ hasText: text });
}

// ── 用例 ─────────────────────────────────────────────────────────────────

test('AND/OR 偏好：切到「全部（AND）」会写进 localStorage，刷新后仍是 AND', async ({ page }) => {
  await page.goto('/');
  await expect(orModeButton(page)).toBeVisible();

  // 默认是 OR（没存过偏好时读出来是 or），点一下切到 AND。
  await orModeButton(page).click();
  await expect(andModeButton(page)).toBeVisible();

  // 偏好落在 localStorage 的 idea.tagMode 上，值是 'and'。
  expect(await storedMode(page)).toBe('and');

  // 刷新后从 localStorage 读回来，还是 AND。
  await page.reload();
  await expect(andModeButton(page)).toBeVisible();
  expect(await storedMode(page)).toBe('and');
});

test('AND/OR 偏好：切回「任一（OR）」也会写进 localStorage，刷新后仍是 OR', async ({ page }) => {
  await page.goto('/');
  await orModeButton(page).click();
  await expect(andModeButton(page)).toBeVisible();

  // 再点一下切回 OR。
  await andModeButton(page).click();
  await expect(orModeButton(page)).toBeVisible();

  // 写的是 'or'（不是删掉这个键）。
  expect(await storedMode(page)).toBe('or');

  // 刷新后还是 OR。
  await page.reload();
  await expect(orModeButton(page)).toBeVisible();
  expect(await storedMode(page)).toBe('or');
});

test('AND/OR 偏好：AND 模式下只显示同时命中两个标签的想法，刷新后偏好仍在（结果照旧）', async ({
  page,
  seed,
}) => {
  await seed.idea('想法甲', { tags: ['灵感库', '待办清单'] });
  await seed.idea('想法乙', { tags: ['灵感库'] });
  await page.goto('/');

  // 默认 OR：两个标签都选中时，命中任一即显示（两条）。
  await page.getByRole('button', { name: '标签', exact: true }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).check();
  await page.getByRole('checkbox', { name: '待办清单' }).check();
  await expect(page.locator('article')).toHaveCount(2);

  // 切到 AND：必须同时命中两个标签才显示（只剩想法甲）。
  await orModeButton(page).click();
  await expect(andModeButton(page)).toBeVisible();
  await expect(page.locator('article')).toHaveCount(1);
  await expect(ideaItem(page, '想法甲')).toHaveCount(1);

  // 刷新：标签选择是页面内的状态（不保留），但 AND 偏好从 localStorage 读回来了。
  await page.reload();
  await expect(andModeButton(page)).toBeVisible();
  await expect(page.locator('article')).toHaveCount(2);

  // 重新勾上两个标签：仍是 AND 的结果（只有同时命中的那条）。
  await page.getByRole('button', { name: '标签', exact: true }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).check();
  await page.getByRole('checkbox', { name: '待办清单' }).check();
  await expect(page.locator('article')).toHaveCount(1);
  await expect(ideaItem(page, '想法甲')).toHaveCount(1);
});

test('AND/OR 偏好：localStorage 写失败（隐私模式）时不报错、界面照常切换，刷新后回落到 OR', async ({
  page,
}) => {
  // 模拟隐私模式 / 配额满：写 localStorage 直接抛异常（useTagMode.ts:24-30 的兜底分支）。
  await page.addInitScript(() => {
    const original = Storage.prototype.setItem;
    Storage.prototype.setItem = function (this: Storage, key: string, value: string): void {
      if (key === 'idea.tagMode') {
        throw new DOMException('写不进去（模拟隐私模式）', 'QuotaExceededError');
      }
      original.call(this, key, value);
    };
  });

  const pageErrors: string[] = [];
  page.on('pageerror', (error) => pageErrors.push(error.message));

  await page.goto('/');
  await expect(orModeButton(page)).toBeVisible();

  // 切换照常生效：模式是共享的 ref，先更新再落盘，落盘失败不影响界面。
  await orModeButton(page).click();
  await expect(andModeButton(page)).toBeVisible();

  // 偏好没存住：localStorage 里还是空的。
  expect(await storedMode(page)).toBeNull();

  // 写失败被兜住了：没有未捕获异常，也没弹出错误提示。
  expect(pageErrors).toEqual([]);
  await expect(page.getByRole('status')).toHaveText('');

  // 刷新后回到默认的 OR（存不住就等于没存过）。
  await page.reload();
  await expect(orModeButton(page)).toBeVisible();
});

test('AND/OR 偏好是设备本地的：不会同步到后端的设置里', async ({ page, api }) => {
  await page.goto('/');
  await orModeButton(page).click();
  await expect(andModeButton(page)).toBeVisible();
  expect(await storedMode(page)).toBe('and');

  // 后端的设置表只放跨设备共享的那类设置（项目排序方式），没有 AND/OR 偏好。
  const settings = await api.getSettings();
  expect(Object.keys(settings)).not.toContain(STORAGE_KEY);
});
