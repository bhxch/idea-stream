/**
 * 冒烟用例：底座本身是通的。
 *
 * 它验三件事：
 *   1. /api/health 正常（后端活着）；
 *   2. 前端产物确实被后端同源托管（/ 返回 index.html 而不是 404）；
 *   3. Vue 应用真的挂载起来了（标题栏的「想法」是渲染出来的，不是静态 HTML）。
 *
 * 这条用例红了 = 底座坏了，别写别的用例 —— 先把它修绿。
 */
import { test, expect } from './fixtures';

test('冒烟：/api/health 正常，首页标题栏出现「想法」', async ({ page, api }) => {
  const health = await api.health();
  expect(health.status).toBe('ok');

  await page.goto('/');

  // 标题栏的「想法」是 HomePage 渲染出来的 —— 它出现就说明前端起活了。
  await expect(page.getByRole('heading', { name: '想法' })).toBeVisible();
  await expect(page).toHaveTitle('Idea 记事本');
});
