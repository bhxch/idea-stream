/**
 * 「Webhook 表单里的『启用』复选框」的 E2E 用例。
 *
 * 补的是 web/e2e/COVERAGE.md §2.4 里标着的那条漏项：
 * **表单里的「启用」复选框（编辑时改启用状态）** —— `WebhookForm.vue:326-338`。
 *
 * 与已有的「列表上的启用/禁用按钮」（webhooks.spec.ts:255）**不是同一条路径**：
 *   - 列表按钮：`WebhookManager.vue:83` 的 toggleEnabled，只 PATCH 一个字段
 *     （`{enabled}`）；
 *   - 表单复选框：`WebhookForm.vue:192-202` 的 onSave，把**整份配置**一起 PATCH
 *     出去，`enabled` 只是其中一个字段。
 *
 * 所以这里既断言**用户看得见的结果**（列表徽标「已启用」/「已禁用」），
 * 也断言**表单这条路发出去的请求体里确实带着 enabled**（两条路径的差别就在这）。
 *
 * 约定见 web/e2e/README.md：
 *   - 从 ./fixtures 导入 test/expect（复位每个用例前自动发生）；
 *   - 用公共夹具造数据（api/seed），不手写 fetch；
 *   - 不写固定时长的 sleep，等状态（expect 自动重试）。
 */
import type { Locator, Page } from '@playwright/test';

import { test, expect } from './fixtures';

// ── 小工具 ────────────────────────────────────────────────────────────

/** 列表里那条 webhook（按 id 定位；data-webhook-id 是代码里已有的钩子）。 */
function webhookItem(page: Page, id: string): Locator {
  return page.locator(`[data-webhook-id="${id}"]`);
}

/**
 * 表单里的「启用」复选框。
 *
 * 页面上只有这一个可访问名是「启用」的复选框（订阅事件那一栏的复选框名字是
 * 「想法新建 idea.created」这种，「清除密钥」那个是「清除已设的密钥」），
 * 所以用 role + 可访问名定位，不用 BEM class。
 */
function enabledCheckbox(page: Page): Locator {
  return page.getByRole('checkbox', { name: '启用', exact: true });
}

/** 打开某条 webhook 的编辑表单，返回「启用」复选框。 */
async function openEditForm(page: Page, item: Locator): Promise<Locator> {
  await item.getByRole('button', { name: '编辑' }).click();
  await expect(page.getByRole('heading', { name: '编辑 webhook' })).toBeVisible();
  return enabledCheckbox(page);
}

/**
 * 在编辑表单里把「启用」设成指定状态并保存。
 *
 * 保存后表单会关掉（WebhookManager 的 onSaved），所以等标题消失。
 */
async function saveEnabled(page: Page, checkbox: Locator, enabled: boolean): Promise<void> {
  if (enabled) {
    await checkbox.check();
  } else {
    await checkbox.uncheck();
  }
  await page.getByRole('button', { name: '保存' }).click();
  await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);
}

// ── 用例 ──────────────────────────────────────────────────────────────

test.describe('Webhook 表单里的「启用」复选框', () => {
  test('编辑：勾上「启用」保存后，列表徽标变成「已启用」', async ({ page, api, seed }) => {
    // 全局作用域的 webhook 创建出来就是禁用的（后端按作用域定默认值）。
    const webhook = await seed.webhook({ name: '表单启用' });

    await page.goto('/webhooks');
    const item = webhookItem(page, webhook.id);
    await expect(item).toContainText('已禁用');

    const checkbox = await openEditForm(page, item);
    await expect(checkbox).not.toBeChecked();

    await saveEnabled(page, checkbox, true);

    await expect(item).toContainText('已启用');
    // 服务端的状态也跟着改了（不只是页面上的字变了）。
    const saved = (await api.listWebhooks()).find((w) => w.id === webhook.id);
    expect(saved?.enabled).toBe(true);

    // 走之前删掉：别留一个启用着的全局 webhook 在库里一直往死地址投递。
    await api.deleteWebhook(webhook.id);
  });

  test('编辑：取消勾选「启用」保存后，列表徽标变成「已禁用」', async ({ page, api, seed }) => {
    const webhook = await seed.webhook({ name: '表单禁用' });

    await page.goto('/webhooks');
    const item = webhookItem(page, webhook.id);

    // 先用表单把它启用（下面要测的是「取消勾选」这半边）。
    await saveEnabled(page, await openEditForm(page, item), true);
    await expect(item).toContainText('已启用');

    // 再打开编辑表单：复选框的初值跟着当前状态走（勾上）。
    const checkbox = await openEditForm(page, item);
    await expect(checkbox).toBeChecked();

    await saveEnabled(page, checkbox, false);

    await expect(item).toContainText('已禁用');
    const saved = (await api.listWebhooks()).find((w) => w.id === webhook.id);
    expect(saved?.enabled).toBe(false);
  });

  test('编辑表单：「启用」复选框的初值 = 这条 webhook 当前的启用状态', async ({
    page,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '初值跟随' });

    await page.goto('/webhooks');
    const item = webhookItem(page, webhook.id);

    // 当前是禁用的 → 编辑表单里的复选框不勾。
    await expect(await openEditForm(page, item)).not.toBeChecked();
    // 关掉表单（重新加载页面，编辑态是组件内部的 state，刷新就没了）。
    await page.reload();

    // 启用之后（这里用列表上的按钮改状态），编辑表单里的复选框就该是勾上的。
    await item.getByRole('button', { name: '启用', exact: true }).click();
    await expect(item).toContainText('已启用');
    await expect(await openEditForm(page, item)).toBeChecked();
  });

  test('只动「启用」开关：名称、URL、订阅事件都不被改掉', async ({ page, api, seed }) => {
    const webhook = await seed.webhook({
      name: '只改开关',
      url: 'https://example.com/keep-me',
      events: ['idea.created', 'project.created'],
    });

    await page.goto('/webhooks');
    const item = webhookItem(page, webhook.id);
    const checkbox = await openEditForm(page, item);

    // 表单带出来的是原来的配置（订阅事件也是勾着的）。
    await expect(page.getByLabel('名称')).toHaveValue('只改开关');
    await expect(page.getByLabel('URL')).toHaveValue('https://example.com/keep-me');
    await expect(page.getByRole('checkbox', { name: '想法新建' })).toBeChecked();
    await expect(page.getByRole('checkbox', { name: '项目新建' })).toBeChecked();

    await saveEnabled(page, checkbox, true);

    // 列表上其它东西原封不动。
    await expect(item).toContainText('只改开关');
    await expect(item).toContainText('https://example.com/keep-me');
    await expect(item).toContainText('idea.created');
    await expect(item).toContainText('project.created');

    await api.deleteWebhook(webhook.id);
  });

  test('表单保存时把 enabled 一起写进 PATCH 请求体（与列表按钮那条路径不同）', async ({
    page,
    api,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '请求体' });

    await page.goto('/webhooks');
    const item = webhookItem(page, webhook.id);
    const checkbox = await openEditForm(page, item);

    const patched = page.waitForResponse(
      (response) =>
        response.url().includes(`/api/webhooks/${webhook.id}`) &&
        response.request().method() === 'PATCH',
    );
    await saveEnabled(page, checkbox, true);
    const response = await patched;

    expect(response.status()).toBe(200);
    const body = JSON.parse(response.request().postData() ?? '{}') as Record<string, unknown>;
    // 表单这条路把整份配置一起发出去，enabled 是其中一个字段。
    expect(body.enabled).toBe(true);
    expect(body.name).toBe('请求体');

    await api.deleteWebhook(webhook.id);
  });

  test('新建表单：「启用」复选框是锁着的，全局作用域下初值不勾', async ({ page }) => {
    await page.goto('/webhooks');
    await page.getByRole('button', { name: '新建 webhook' }).click();
    await expect(page.getByRole('heading', { name: '新建 webhook' })).toBeVisible();

    const checkbox = enabledCheckbox(page);
    await expect(checkbox).toBeDisabled();
    await expect(checkbox).not.toBeChecked();
    // 页面上写明了为什么锁着。
    await expect(page.getByText('新建时由作用域决定默认值')).toBeVisible();
  });

  test('新建表单：项目作用域下「启用」复选框也是锁着的，但初值是勾上的', async ({
    page,
    seed,
  }) => {
    const project = await seed.project('作用域项目');

    await page.goto(`/projects/${project.id}/webhooks`);
    await page.getByRole('button', { name: '新建 webhook' }).click();
    await expect(page.getByRole('heading', { name: '新建 webhook' })).toBeVisible();

    const checkbox = enabledCheckbox(page);
    await expect(checkbox).toBeDisabled();
    await expect(checkbox).toBeChecked();
  });

  test('项目级 webhook：编辑表单里也能改启用状态', async ({ page, api, seed }) => {
    const project = await seed.project('项目开关');
    // 项目作用域的 webhook 创建出来就是启用的（后端按作用域定默认值）。
    const webhook = await seed.webhook({
      scope: 'project',
      projectId: project.id,
      name: '项目钩子',
    });

    await page.goto(`/projects/${project.id}/webhooks`);
    const item = webhookItem(page, webhook.id);
    await expect(item).toContainText('已启用');

    const checkbox = await openEditForm(page, item);
    await expect(checkbox).toBeChecked();

    await saveEnabled(page, checkbox, false);

    await expect(item).toContainText('已禁用');
    const saved = (await api.listWebhooks(project.id)).find((w) => w.id === webhook.id);
    expect(saved?.enabled).toBe(false);

    // 项目级 webhook 复位删不掉（见 webhooks.spec.ts 里 cleanupOrphanWebhooks 的说明），
    // 自己删掉，免得它留在库里一直往死地址投递。
    await api.deleteWebhook(webhook.id);
  });
});
