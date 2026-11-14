/**
 * 「Webhook 的自定义 Header 行」的 E2E 用例。
 *
 * 覆盖的是 WebhookForm.vue 里那块「自定义 Header」字段集：
 *   - 加一行：`addHeader`（`web/src/components/WebhookForm.vue:136-139`）
 *   - 删一行：`removeHeader`（`:141-144`）
 *   - 键值的组装：`buildHeaders`（`:98-109`）——
 *     键去首尾空白、**空键的行整个忽略**、值原样保留（不 trim）、
 *     同名键后一行覆盖前一行；
 *   - 编辑时按已存的配置预填（`:57-59`）。
 *
 * 断言分两路：
 *   - **接口**：Header 的**键名不遮蔽**（`WebhookMasking` 只把值换成 `***`，
 *     见 `server/IdeaApp/Infrastructure/WebhookMasking.cs:31-35`），
 *     所以键集合可以直接断言。
 *   - **界面**：Header 的**值**要验往返只能走界面 —— 页面拉列表时带
 *     `?unmask=true`（`web/src/components/WebhookManager.vue:43-52`），
 *     重新打开编辑表单看到的就是后端存的值。
 *
 * 约定见 web/e2e/README.md：夹具复位、不写固定时长的 sleep、等状态。
 */
import type { Page } from '@playwright/test';

import { test, expect, type IdeaApi } from './fixtures';

// ── 小工具 ────────────────────────────────────────────────────────────

/** 「自定义 Header」那个字段集（fieldset + legend）。 */
function headerGroup(page: Page) {
  return page.getByRole('group', { name: '自定义 Header' });
}

/** 字段集里所有 Header 名输入框。 */
function headerKeyInputs(page: Page) {
  return headerGroup(page).getByLabel('Header 名', { exact: true });
}

/** 字段集里所有 Header 值输入框。 */
function headerValueInputs(page: Page) {
  return headerGroup(page).getByLabel('Header 值', { exact: true });
}

/** 第 index 行（从 0 起）的 Header 名输入框。 */
function headerKeyInput(page: Page, index = 0) {
  return headerKeyInputs(page).nth(index);
}

/** 第 index 行（从 0 起）的 Header 值输入框。 */
function headerValueInput(page: Page, index = 0) {
  return headerValueInputs(page).nth(index);
}

/** 点「加一行」。 */
async function addHeaderRow(page: Page): Promise<void> {
  await headerGroup(page).getByRole('button', { name: '加一行' }).click();
}

/** 点第 index 行（从 0 起）的「×」（删掉这一行 Header）。 */
async function removeHeaderRow(page: Page, index: number): Promise<void> {
  await headerGroup(page).getByRole('button', { name: '删除这一行 Header' }).nth(index).click();
}

/**
 * 表单里当前的 Header 行，形如 `["X-Token=abc123", ...]`（按行序排序）。
 *
 * 用「键=值」的字符串而不是对象：断言只看**内容**，不绑顺序
 * （后端存的是字典，行的先后不该成为用例的约束）。
 */
async function headerRows(page: Page): Promise<string[]> {
  const keys = headerKeyInputs(page);
  const values = headerValueInputs(page);
  const count = await keys.count();
  const rows: string[] = [];
  for (let index = 0; index < count; index += 1) {
    rows.push(`${await keys.nth(index).inputValue()}=${await values.nth(index).inputValue()}`);
  }
  return rows.sort();
}

/** 找到键名等于 key 的那一行的序号（找不到返回 -1）。 */
async function headerRowIndex(page: Page, key: string): Promise<number> {
  const keys = headerKeyInputs(page);
  const count = await keys.count();
  for (let index = 0; index < count; index += 1) {
    if ((await keys.nth(index).inputValue()) === key) {
      return index;
    }
  }
  return -1;
}

/** 打开「新建 webhook」表单。 */
async function openCreateForm(page: Page): Promise<void> {
  await page.getByRole('button', { name: '新建 webhook' }).click();
  await expect(page.getByRole('heading', { name: '新建 webhook' })).toBeVisible();
}

/** 打开某个 webhook 的编辑表单。 */
async function openEditForm(page: Page, name: string): Promise<void> {
  await page
    .locator('[data-webhook-id]')
    .filter({ hasText: name })
    .getByRole('button', { name: '编辑' })
    .click();
  await expect(page.getByRole('heading', { name: '编辑 webhook' })).toBeVisible();
}

/** 把表单的三个必填项填上（名称/URL/Body 模板）。 */
async function fillRequired(
  page: Page,
  values: { name: string; url: string; bodyTemplate?: string },
): Promise<void> {
  await page.getByLabel('名称').fill(values.name);
  await page.getByLabel('URL').fill(values.url);
  await page.getByLabel('Body 模板').fill(values.bodyTemplate ?? '{"text":"{{idea.content}}"}');
}

/** 订阅一个事件（一个都不订阅时表单不让保存）。 */
async function subscribeOneEvent(page: Page): Promise<void> {
  await page.getByRole('checkbox', { name: '想法新建' }).check();
}

/** 列表里那条 webhook（按名字定位）。 */
function webhookItem(page: Page, name: string) {
  return page.locator('[data-webhook-id]').filter({ hasText: name });
}

/**
 * 接口里这个 webhook 的 Header 键（排序）。
 *
 * 值是遮蔽版（`***`），键名不遮蔽 —— 键集合在这里断言。
 */
async function headerKeys(api: IdeaApi, name: string): Promise<string[]> {
  const hooks = await api.listWebhooks();
  const hook = hooks.find((item) => item.name === name);
  return Object.keys(hook?.headers ?? {}).sort();
}

// ── 用例 ──────────────────────────────────────────────────────────────

test.describe('Webhook 的自定义 Header 行', () => {
  test('新建：加一行 Header，填上键值，保存后这个 Header 存下来了', async ({ page, api }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    // 表单刚打开时一行 Header 都没有。
    await expect(headerKeyInputs(page)).toHaveCount(0);

    await addHeaderRow(page);
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await headerKeyInput(page, 0).fill('X-Token');
    await headerValueInput(page, 0).fill('abc123');

    await fillRequired(page, { name: '带 Header 的 webhook', url: 'https://example.com/hook' });
    await subscribeOneEvent(page);
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '带 Header 的 webhook')).toBeVisible();

    // 接口：键名不遮蔽，键集合直接断言。
    await expect.poll(() => headerKeys(api, '带 Header 的 webhook')).toEqual(['X-Token']);

    // 界面：重新打开编辑表单，看到的是后端存的**原值**。
    await openEditForm(page, '带 Header 的 webhook');
    await expect.poll(() => headerRows(page)).toEqual(['X-Token=abc123']);
  });

  test('新建：加多行 Header，每一行的键值都存下来了', async ({ page, api }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await addHeaderRow(page);
    await headerKeyInput(page, 0).fill('X-Token');
    await headerValueInput(page, 0).fill('abc123');
    await addHeaderRow(page);
    await headerKeyInput(page, 1).fill('Authorization');
    await headerValueInput(page, 1).fill('Bearer xyz');
    await expect(headerKeyInputs(page)).toHaveCount(2);

    await fillRequired(page, { name: '两个 Header', url: 'https://example.com/hook' });
    await subscribeOneEvent(page);
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '两个 Header')).toBeVisible();
    await expect.poll(() => headerKeys(api, '两个 Header')).toEqual(['Authorization', 'X-Token']);

    await openEditForm(page, '两个 Header');
    await expect(headerKeyInputs(page)).toHaveCount(2);
    await expect.poll(() => headerRows(page)).toEqual([
      'Authorization=Bearer xyz',
      'X-Token=abc123',
    ]);
  });

  test('新建：删掉一行 Header（×），保存后只剩剩下那行', async ({ page, api }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await addHeaderRow(page);
    await headerKeyInput(page, 0).fill('X-Token');
    await headerValueInput(page, 0).fill('abc123');
    await addHeaderRow(page);
    await headerKeyInput(page, 1).fill('Authorization');
    await headerValueInput(page, 1).fill('Bearer xyz');

    // 删第一行：× 按钮的可访问名是「删除这一行 Header」。
    await removeHeaderRow(page, 0);
    await expect(headerKeyInputs(page)).toHaveCount(1);
    // 剩下的那行的键值没被动过。
    await expect.poll(() => headerRows(page)).toEqual(['Authorization=Bearer xyz']);

    await fillRequired(page, { name: '删了一行的 webhook', url: 'https://example.com/hook' });
    await subscribeOneEvent(page);
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '删了一行的 webhook')).toBeVisible();
    // 被删掉的那行没进后端。
    await expect.poll(() => headerKeys(api, '删了一行的 webhook')).toEqual(['Authorization']);

    await openEditForm(page, '删了一行的 webhook');
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['Authorization=Bearer xyz']);
  });

  test('新建：键名留空的那行被忽略（不存进后端）', async ({ page, api }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    // 第一行只填了值，键名空着。
    await addHeaderRow(page);
    await headerValueInput(page, 0).fill('没有键名的值');
    await addHeaderRow(page);
    await headerKeyInput(page, 1).fill('X-Token');
    await headerValueInput(page, 1).fill('abc123');
    await expect(headerKeyInputs(page)).toHaveCount(2);

    await fillRequired(page, { name: '空键名的 webhook', url: 'https://example.com/hook' });
    await subscribeOneEvent(page);
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '空键名的 webhook')).toBeVisible();
    // 只有有键名的那条存下来了 —— 空键的行整个被忽略（连值都不带）。
    await expect.poll(() => headerKeys(api, '空键名的 webhook')).toEqual(['X-Token']);

    await openEditForm(page, '空键名的 webhook');
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['X-Token=abc123']);
  });

  test('新建：键名首尾空白被去掉，值原样保留', async ({ page, api }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await addHeaderRow(page);
    await headerKeyInput(page, 0).fill('  X-Token  ');
    await headerValueInput(page, 0).fill('  abc123  ');

    await fillRequired(page, { name: '带空白的 Header', url: 'https://example.com/hook' });
    await subscribeOneEvent(page);
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '带空白的 Header')).toBeVisible();
    // 键名 trim 过。
    await expect.poll(() => headerKeys(api, '带空白的 Header')).toEqual(['X-Token']);

    await openEditForm(page, '带空白的 Header');
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['X-Token=  abc123  ']);
  });

  test('新建：两行用同一个键名，后一行覆盖前一行', async ({ page, api }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await addHeaderRow(page);
    await headerKeyInput(page, 0).fill('X-Token');
    await headerValueInput(page, 0).fill('第一个');
    await addHeaderRow(page);
    await headerKeyInput(page, 1).fill('X-Token');
    await headerValueInput(page, 1).fill('第二个');

    await fillRequired(page, { name: '重复键名的 webhook', url: 'https://example.com/hook' });
    await subscribeOneEvent(page);
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '重复键名的 webhook')).toBeVisible();
    // 同一个键名只有一个 —— 后一行把前一行盖掉了。
    await expect.poll(() => headerKeys(api, '重复键名的 webhook')).toEqual(['X-Token']);

    await openEditForm(page, '重复键名的 webhook');
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['X-Token=第二个']);
  });

  test('编辑：已存的 Header 按行预填；删掉一行保存后那个 Header 被删掉', async ({ page, api, seed }) => {
    await seed.webhook({
      name: '带 Header 的旧 webhook',
      url: 'https://example.com/old',
      headers: { 'X-Token': 'abc123', Authorization: 'Bearer xyz' },
    });

    await page.goto('/webhooks');
    await openEditForm(page, '带 Header 的旧 webhook');

    // 预填：两行，键值都是**原值**（页面拉列表时带 ?unmask=true）。
    await expect(headerKeyInputs(page)).toHaveCount(2);
    await expect.poll(() => headerRows(page)).toEqual([
      'Authorization=Bearer xyz',
      'X-Token=abc123',
    ]);

    // 删掉 X-Token 那一行。
    const index = await headerRowIndex(page, 'X-Token');
    expect(index).toBeGreaterThanOrEqual(0);
    await removeHeaderRow(page, index);
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['Authorization=Bearer xyz']);

    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);
    // 保存走的是**全量替换** headers：被删掉的那个 Header 没了。
    await expect.poll(() => headerKeys(api, '带 Header 的旧 webhook')).toEqual(['Authorization']);

    await openEditForm(page, '带 Header 的旧 webhook');
    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['Authorization=Bearer xyz']);
  });

  test('编辑：加一行新 Header，保存后加上去了', async ({ page, api, seed }) => {
    await seed.webhook({
      name: '要加 Header 的 webhook',
      url: 'https://example.com/old',
      headers: { 'X-Token': 'abc123' },
    });

    await page.goto('/webhooks');
    await openEditForm(page, '要加 Header 的 webhook');

    await expect(headerKeyInputs(page)).toHaveCount(1);
    await expect.poll(() => headerRows(page)).toEqual(['X-Token=abc123']);

    await addHeaderRow(page);
    await expect(headerKeyInputs(page)).toHaveCount(2);
    await headerKeyInput(page, 1).fill('X-Extra');
    await headerValueInput(page, 1).fill('extra');

    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);
    // 老的那个没被覆盖掉，新的加上了。
    await expect.poll(() => headerKeys(api, '要加 Header 的 webhook')).toEqual([
      'X-Extra',
      'X-Token',
    ]);

    await openEditForm(page, '要加 Header 的 webhook');
    await expect(headerKeyInputs(page)).toHaveCount(2);
    await expect.poll(() => headerRows(page)).toEqual(['X-Extra=extra', 'X-Token=abc123']);
  });
});
