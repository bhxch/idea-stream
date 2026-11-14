/**
 * 「Webhook 与投递记录」功能区的 E2E 用例。
 *
 * 覆盖：
 *   - 全局页 /webhooks 与项目级页 /projects/:id/webhooks 都能新建 webhook
 *     （URL、订阅事件、Telegram 模板）；
 *   - 列表展示、编辑、启用/禁用、删除（二次确认）；
 *   - 投递记录列表与「重投」按钮；
 *   - 表单校验：名称/URL 为空、URL 不是 http(s) 绝对地址、一个事件都不选。
 *
 * 约定见 web/e2e/README.md：
 *   - 选择器优先 role/文本/label，其次用代码里已有的 [data-webhook-id] /
 *     [data-delivery-id] 钩子；
 *   - 每个用例前由夹具复位，用例之间互不依赖；
 *   - 不写固定时长的 sleep，等状态（expect 自动重试）。
 *
 * 投递记录怎么造：webhook 投的是一个真实的 HTTP 请求，所以这里起一个
 * **本地接收端**（只回 200）当投递目标 —— 投递成功与否就变得确定，
 * 不用去猜「连不上会重试几次、几秒后变成死信」。
 */
import { createServer } from 'node:http';

import type { Locator, Page } from '@playwright/test';

import { test, expect, type IdeaApi, type Seed } from './fixtures';

// ── 本地投递接收端 ────────────────────────────────────────────────────

/** 一个只回 200 的本地 HTTP 接收端（webhook 投递到它就算成功）。 */
interface Receiver {
  /** 投递目标地址（填进 webhook 的 URL）。 */
  url: string;
  /** 已经收到几次投递。 */
  hits(): number;
  /** 关掉接收端。 */
  close(): Promise<void>;
}

/** 起一个只回 200 的本地接收端。端口由系统分配，避免撞端口。 */
async function startReceiver(): Promise<Receiver> {
  let hits = 0;
  const server = createServer((req, res) => {
    hits += 1;
    req.resume();
    req.on('end', () => {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end('{"ok":true}');
    });
  });
  await new Promise<void>((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve());
  });
  const address = server.address() as { port: number };
  return {
    url: `http://127.0.0.1:${address.port}/hook`,
    hits: () => hits,
    close: () =>
      new Promise<void>((resolve) => {
        server.close(() => resolve());
      }),
  };
}

// ── 小工具 ────────────────────────────────────────────────────────────

/** 打开「新建 webhook」表单。 */
async function openCreateForm(page: Page): Promise<void> {
  await page.getByRole('button', { name: '新建 webhook' }).click();
  await expect(page.getByRole('heading', { name: '新建 webhook' })).toBeVisible();
}

/** 把表单的三个必填项填上（名称/URL/Body 模板）。 */
async function fillForm(
  page: Page,
  values: { name: string; url: string; bodyTemplate: string },
): Promise<void> {
  await page.getByLabel('名称').fill(values.name);
  await page.getByLabel('URL').fill(values.url);
  await page.getByLabel('Body 模板').fill(values.bodyTemplate);
}

/** 列表里那条 webhook（按名字定位）。 */
function webhookItem(page: Page, name: string) {
  return page.locator('[data-webhook-id]').filter({ hasText: name });
}

/** 投递记录里的一行。 */
function deliveryRow(page: Page) {
  return page.locator('[data-delivery-id]').first();
}

/**
 * 重新拉一次投递记录。
 *
 * DeliveryList 只在**挂载**时拉一次列表（DeliveryList.vue:57），
 * 页面上没有「刷新」按钮 —— 重新加载页面再展开面板，才会再拉一次。
 *
 * `.delivery-list` 是 BEM class：面板没有 role/aria 钩子可用来判断它
 * 开没开（里面的行有 [data-delivery-id]，空状态只有一句中文），
 * 只能用 class 认它 —— 没有别的办法。
 */
async function refreshDeliveries(page: Page, item: Locator): Promise<void> {
  await page.reload();
  await item.getByRole('button', { name: '投递记录' }).click();
  await expect(page.locator('.delivery-list')).toBeVisible();
}

/**
 * 清掉「挂在回收站项目上」的 webhook（孤儿 webhook）。
 *
 * ⚠️ 这是绕开**底座**的一个 bug（web/e2e/fixtures.ts 的 `IdeaApi.reset`）：
 * 复位先删项目、后删 webhook —— 项目一删就不再出现在 `GET /api/projects` 里，
 * `clearWebhooks` 顺着「活动项目」列表去找 webhook 时就找不到它们了，
 * 于是**项目级 webhook 永远删不掉**。
 *
 * 后果不只是库里多几行：这些 webhook 一直是**启用**状态，每次复位都会
 * 给它们发事件，投递全部失败（地址早就没了），而失败的投递会在
 * 1s/5s/30s 的退避里**一直占着投递 worker 的并发槽**（一共 8 个）——
 * 攒多了之后新的投递排不上队，投递记录迟迟不出现。
 *
 * 它们的项目都在回收站里（项目没了，webhook 也就没用了），清掉不影响任何用例。
 */
async function cleanupOrphanWebhooks(api: IdeaApi): Promise<void> {
  for (const entry of await api.listTrash()) {
    if (entry.kind !== 'project' || !entry.project) {
      continue;
    }
    for (const webhook of await api.listWebhooks(entry.project.id)) {
      await api.deleteWebhook(webhook.id);
    }
  }
}

/**
 * 跑一遍「投递记录 + 重投」的完整流程。
 *
 * 用的 webhook 是**项目级**的：项目级 webhook 只收自己项目的事件，
 * 别的项目造出来的想法不会混进这条 webhook 的投递记录里。
 */
async function deliveryFlow(page: Page, api: IdeaApi, seed: Seed): Promise<void> {
  const receiver = await startReceiver();
  try {
    const project = await seed.project('投递项目');
    await page.goto(`/projects/${project.id}/webhooks`);
    await openCreateForm(page);

    await fillForm(page, {
      name: '投递测试',
      url: receiver.url,
      bodyTemplate: '{"text":"{{idea.content}}"}',
    });
    await page.getByRole('checkbox', { name: '想法新建' }).check();
    await page.getByRole('button', { name: '保存' }).click();

    const item = webhookItem(page, '投递测试');
    await expect(item).toBeVisible();
    const webhookId = await item.getAttribute('data-webhook-id');

    // 项目级 webhook 创建后就是启用状态 —— 不用再点「启用」。
    // 造一条想法 → idea.created → 投到接收端。
    await seed.idea('会被投递的想法', { projectId: project.id });

    // 投递记录：等这一条投递落库（重新加载页面才会重新拉列表）。
    await expect(async () => {
      await refreshDeliveries(page, item);
      await expect(deliveryRow(page)).toContainText('idea.created');
      await expect(deliveryRow(page)).toContainText('delivered');
      await expect(deliveryRow(page)).toContainText('尝试 1 次');
    }).toPass({ timeout: 30_000 });

    // 重投：后端真的又发了一次请求。
    const before = receiver.hits();
    const retryResponse = page.waitForResponse(
      (response) =>
        response.url().endsWith('/retry') && response.request().method() === 'POST',
    );
    await deliveryRow(page).getByRole('button', { name: '重投' }).click();
    expect((await retryResponse).status()).toBe(200);
    await expect.poll(() => receiver.hits(), { timeout: 30_000 }).toBeGreaterThan(before);

    // 记录上的尝试次数 +1（重投与自动重试记在同一行上）。
    await expect(async () => {
      await refreshDeliveries(page, item);
      await expect(deliveryRow(page)).toContainText('尝试 2 次');
    }).toPass({ timeout: 30_000 });

    // 走之前把自己造的 webhook 删掉：项目级 webhook 复位删不掉（见
    // cleanupOrphanWebhooks），留在库里会一直往死地址投递。
    if (webhookId) {
      await api.deleteWebhook(webhookId);
    }
  } finally {
    await receiver.close();
  }
}

// ── 全局页 /webhooks ─────────────────────────────────────────────────

test.describe('全局页 /webhooks', () => {
  test('空状态：没有 webhook 时有提示与「新建 webhook」入口', async ({ page }) => {
    await page.goto('/webhooks');

    await expect(page.getByRole('heading', { level: 1, name: 'Webhook' })).toBeVisible();
    await expect(page.getByRole('heading', { name: '全局 webhook' })).toBeVisible();
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
    await expect(page.getByRole('button', { name: '新建 webhook' })).toBeVisible();
  });

  test('新建：名称/URL/Body 模板/订阅事件都填上，保存后列表里能看到', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await fillForm(page, {
      name: '我的 webhook',
      url: 'https://example.com/hook',
      bodyTemplate: '{"text":"{{idea.content}}"}',
    });
    // 勾一个事件（事件名在页面上是中文标签 + 事件类型）。
    await page.getByRole('checkbox', { name: '想法新建' }).check();

    await page.getByRole('button', { name: '保存' }).click();

    const item = webhookItem(page, '我的 webhook');
    await expect(item).toBeVisible();
    await expect(item).toContainText('https://example.com/hook');
    await expect(item).toContainText('idea.created');
    // 全局作用域的 webhook 创建后是禁用的（后端按作用域定默认值）。
    await expect(item).toContainText('已禁用');
    await expect(item).toContainText('无密钥');
    // 表单保存后关掉了。
    await expect(page.getByRole('heading', { name: '新建 webhook' })).toHaveCount(0);
  });

  test('新建：Telegram 模板按钮把 URL 与 Body 模板预填好', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await page.getByRole('button', { name: '添加 Telegram webhook' }).click();

    await expect(page.getByLabel('URL')).toHaveValue(
      'https://api.telegram.org/bot<BOT_TOKEN>/sendMessage',
    );
    await expect(page.getByLabel('Body 模板')).toHaveValue(/message_thread_id/);

    await page.getByLabel('名称').fill('Telegram 钩子');
    // 至少要订阅一个事件，否则表单不给保存（Telegram 模板不带事件，得自己勾）。
    await page.getByRole('checkbox', { name: '想法新建' }).check();
    await page.getByRole('button', { name: '保存' }).click();

    const item = webhookItem(page, 'Telegram 钩子');
    await expect(item).toBeVisible();
    await expect(item).toContainText('api.telegram.org');
  });

  test('启用/禁用：点「启用」变成已启用，再点「禁用」变回已禁用', async ({ page, seed }) => {
    const webhook = await seed.webhook({ name: '开关测试' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);
    await expect(item).toContainText('已禁用');

    await item.getByRole('button', { name: '启用', exact: true }).click();
    await expect(item).toContainText('已启用');

    await item.getByRole('button', { name: '禁用', exact: true }).click();
    await expect(item).toContainText('已禁用');
  });

  test('表单校验：名称为空给出错误提示，也不保存', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    // 名称留空，只填 URL 与 Body 模板。
    await page.getByLabel('URL').fill('https://example.com/hook');
    await page.getByLabel('Body 模板').fill('{"text":"x"}');
    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('status')).toContainText('webhook 名称不能为空');
    // 没保存成功：列表还是空的。
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });

  test('表单校验：URL 为空给出错误提示，也不保存', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await page.getByLabel('名称').fill('没填 URL 的 webhook');
    await page.getByLabel('Body 模板').fill('{"text":"x"}');
    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('status')).toContainText('webhook 的 URL 不能为空');
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });

  test('表单校验：URL 不是 http(s) 绝对地址时给出错误提示，也不保存', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await fillForm(page, {
      name: 'URL 不合法的 webhook',
      url: 'not-a-url',
      bodyTemplate: '{"text":"x"}',
    });
    // 至少要订阅一个事件，否则表单先报「没订阅事件」，请求根本发不出去，
    // URL 非法这条错误就轮不到后端说了。
    await page.getByRole('checkbox', { name: '想法新建' }).check();
    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('status')).toContainText('http(s) 绝对地址');
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });

  test('表单校验：一个事件都不选时给出错误提示（不该静默存成不订阅任何事件）', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await fillForm(page, {
      name: '没订阅事件的 webhook',
      url: 'https://example.com/hook',
      bodyTemplate: '{"text":"x"}',
    });
    // 订阅事件一个都不勾。
    await page.getByRole('button', { name: '保存' }).click();

    await expect(page.getByRole('status')).toContainText('事件');
    // 也不该保存成功（存下来的 webhook 会变成「不订阅任何事件」）。
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });

  test('编辑：改了名称与 URL，保存后列表跟着变', async ({ page, seed }) => {
    const webhook = await seed.webhook({ name: '旧名字', url: 'https://example.com/old' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);
    await item.getByRole('button', { name: '编辑' }).click();

    // 表单里是这个 webhook 原来的配置。
    await expect(page.getByLabel('名称')).toHaveValue('旧名字');
    await expect(page.getByLabel('URL')).toHaveValue('https://example.com/old');

    await page.getByLabel('名称').fill('新名字');
    await page.getByLabel('URL').fill('https://example.com/new');
    await page.getByRole('button', { name: '保存' }).click();

    await expect(webhookItem(page, '新名字')).toBeVisible();
    await expect(item).toContainText('https://example.com/new');
    await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);
  });

  test('删除：要二次确认，取消不删，确认后从列表里消失', async ({ page, seed }) => {
    const webhook = await seed.webhook({ name: '待删除' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);

    await item.getByRole('button', { name: '删除', exact: true }).click();
    await expect(
      page.getByText('删除这个 webhook？它的配置和投递记录会一起没了。'),
    ).toBeVisible();

    // 取消：什么也不该发生。
    await item.getByRole('button', { name: '取消', exact: true }).click();
    await expect(item).toBeVisible();

    await item.getByRole('button', { name: '删除', exact: true }).click();
    await item.getByRole('button', { name: '确认删除' }).click();

    await expect(item).toHaveCount(0);
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });

  test('Secret：新建时填了密钥，列表里显示「已设密钥」', async ({ page }) => {
    await page.goto('/webhooks');
    await openCreateForm(page);

    await fillForm(page, {
      name: '带密钥的 webhook',
      url: 'https://example.com/hook',
      bodyTemplate: '{"text":"x"}',
    });
    await page.getByLabel('Secret').fill('s3cret');
    // 至少要订阅一个事件，否则表单不给保存。
    await page.getByRole('checkbox', { name: '想法新建' }).check();
    await page.getByRole('button', { name: '保存' }).click();

    const item = webhookItem(page, '带密钥的 webhook');
    await expect(item).toBeVisible();
    await expect(item).toContainText('已设密钥');
    // 密钥明文不出现在列表里。
    await expect(item).not.toContainText('s3cret');
  });

  test('投递记录：没有记录时是空状态', async ({ page, seed }) => {
    const webhook = await seed.webhook({ name: '没有投递记录' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);
    await item.getByRole('button', { name: '投递记录' }).click();

    await expect(page.getByText('还没有投递记录。')).toBeVisible();
  });

  test('投递记录：事件触发后列出记录；点「重投」会真的再投一次', async ({
    page,
    api,
    seed,
  }) => {
    // 投递走后台队列（排队 + 重试），慢的时候要等一会儿。
    test.setTimeout(120_000);

    // 先把孤儿 webhook 清掉（见 cleanupOrphanWebhooks 的说明）：
    // 它们会把投递队列占满，不清掉的话这一条永远等不到投递记录。
    await cleanupOrphanWebhooks(api);

    // ⚠️ E2E 服务与库是**共享**的：别的分区用例跑起来时，它们的复位会把所有
    // 项目删掉（复位会删掉它们）。所以整段流程允许重来几轮 ——
    // 每轮从头来一遍（接收端 → 项目 → webhook → 想法 → 投递记录）。
    // 失败的信息不会丢：三轮都失败时抛最后一轮的错。
    let lastError: unknown;
    for (let round = 0; round < 3; round += 1) {
      try {
        await deliveryFlow(page, api, seed);
        lastError = undefined;
        break;
      } catch (error) {
        lastError = error;
      }
    }
    if (lastError) {
      throw lastError;
    }
  });
});

// ── 项目级页 /projects/:id/webhooks ──────────────────────────────────

test.describe('项目级页 /projects/:id/webhooks', () => {
  test('页面能直接打开，标题里带项目名', async ({ page, seed }) => {
    const project = await seed.project('我的项目');

    await page.goto(`/projects/${project.id}/webhooks`);

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('项目 Webhook · 我的项目');
    await expect(page.getByRole('heading', { name: '项目级 webhook' })).toBeVisible();
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });

  test('新建：项目级 webhook 保存后就是已启用', async ({ page, api, seed }) => {
    const project = await seed.project('我的项目');

    await page.goto(`/projects/${project.id}/webhooks`);
    await openCreateForm(page);

    await fillForm(page, {
      name: '项目级钩子',
      url: 'https://example.com/project-hook',
      bodyTemplate: '{"text":"{{idea.content}}"}',
    });
    await page.getByRole('checkbox', { name: '想法新建' }).check();
    await page.getByRole('button', { name: '保存' }).click();

    const item = webhookItem(page, '项目级钩子');
    await expect(item).toBeVisible();
    await expect(item).toContainText('https://example.com/project-hook');
    await expect(item).toContainText('idea.created');
    // 项目作用域的 webhook 创建后默认启用（与全局相反）。
    await expect(item).toContainText('已启用');

    // 走之前删掉：项目级 webhook 复位删不掉（见 cleanupOrphanWebhooks）。
    const webhookId = await item.getAttribute('data-webhook-id');
    if (webhookId) {
      await api.deleteWebhook(webhookId);
    }
  });

  test('作用域隔离：全局的只出现在全局页，项目的只出现在项目页', async ({
    page,
    api,
    seed,
  }) => {
    const project = await seed.project('隔离项目');
    await seed.webhook({ name: '全局钩子' });
    const projectHook = await seed.webhook({
      name: '项目钩子',
      scope: 'project',
      projectId: project.id,
    });

    await page.goto('/webhooks');
    await expect(webhookItem(page, '全局钩子')).toBeVisible();
    await expect(webhookItem(page, '项目钩子')).toHaveCount(0);

    await page.goto(`/projects/${project.id}/webhooks`);
    await expect(webhookItem(page, '项目钩子')).toBeVisible();
    await expect(webhookItem(page, '全局钩子')).toHaveCount(0);

    // 项目级 webhook 复位删不掉（见 cleanupOrphanWebhooks），自己删掉。
    await api.deleteWebhook(projectHook.id);
  });

  test('删除：项目级 webhook 也能删掉', async ({ page, seed }) => {
    const project = await seed.project('删除项目');
    const webhook = await seed.webhook({
      name: '待删的项目钩子',
      scope: 'project',
      projectId: project.id,
    });

    await page.goto(`/projects/${project.id}/webhooks`);
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);

    await item.getByRole('button', { name: '删除', exact: true }).click();
    await item.getByRole('button', { name: '确认删除' }).click();

    await expect(item).toHaveCount(0);
    await expect(page.getByText('还没有 webhook。')).toBeVisible();
  });
});
