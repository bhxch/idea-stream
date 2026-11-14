/**
 * Secret「清除已设的密钥」勾选项（含与新填值的互斥）的 E2E 用例。
 *
 * 覆盖的功能点：
 * - `web/src/components/WebhookForm.vue:351-354`：「清除已设的密钥」勾选框 ——
 *   只有**编辑一个已设密钥的 webhook** 时才出现（新建时没有，没密钥的也没有）；
 * - `web/src/components/WebhookForm.vue:86-96`：勾选框与 Secret 输入框**互斥** ——
 *   勾「清除」→ 输入框里的内容被清掉；输入框里填了**非空白**的内容 → 勾选自动取消；
 *   只填了空白 → 勾选**不**被取消（判空口径与保存时一致，两边都用 trim）；
 * - `web/src/components/WebhookForm.vue:126-134`：`secretValue()` 的三态 ——
 *   `null` = 不改、`""` = 清空、非空 = 换成新密钥。
 *
 * 后端那一侧的三态见 `server/IdeaApp/Application/WebhookService.cs:106-127`
 * （Update：null 不改、"" 清空、非空替换）与 `:303-309`（NormalizeSecret：去首尾空白）。
 *
 * 最后一条用例用**投递签名**（`X-Idea-Signature`）证明密钥的真正取值：
 * 密钥明文只进不出，页面上只能看到「已设密钥 / 无密钥」这个布尔值，
 * 但接收端能用同一个密钥重算签名 —— 于是「换成新密钥」「清空密钥」都能被验证到。
 *
 * 约定见 web/e2e/README.md：
 * - 从 ./fixtures 导入 test/expect（复位在 beforeEach 里自动发生，用例之间互不依赖）；
 * - 不用固定时长等待，一律等状态（toBeVisible / toHaveCount / expect.poll）；
 * - 选择器优先 role + 可访问名，其次是可见文字与代码里已有的 [data-webhook-id] 钩子。
 */
import { createHmac } from 'node:crypto';
import { createServer } from 'node:http';

import type { Locator, Page } from '@playwright/test';

import { test, expect, type IdeaApi } from './fixtures';

// ── 复位兜底 ─────────────────────────────────────────────────────────────

/**
 * 每个用例开始前，保证库里没有残留的 webhook。
 *
 * 为什么要自己兜一层：公共夹具（web/e2e/fixtures.ts）里那个复位钩子写在
 * **夹具模块**里。Playwright 把顶层 `test.beforeEach` 挂在「模块体执行时正在加载的
 * 那个 spec 文件」的套件上，而模块体在一个进程里只跑一次 —— 于是同一次
 * `playwright test` 里，排在第二个及以后的 spec 文件拿不到那个钩子。
 * （夹具的钩子跑过的时候库已经是干净的，所以这里先查、脏了才复位。）
 */
test.beforeEach(async ({ api }) => {
  let dirty = (await api.listWebhooks()).length > 0;
  if (!dirty) {
    for (const project of await api.listProjects()) {
      if ((await api.listWebhooks(project.id)).length > 0) {
        dirty = true;
        break;
      }
    }
  }
  if (dirty) {
    await api.reset();
  }
});

// ── 定位辅助 ─────────────────────────────────────────────────────────────

/** 列表里那条 webhook（按名字定位；[data-webhook-id] 是 WebhookManager.vue:146 的钩子）。 */
function webhookItem(page: Page, name: string): Locator {
  return page.locator('[data-webhook-id]').filter({ hasText: name });
}

/** 「清除已设的密钥」勾选框。 */
function clearCheckbox(page: Page): Locator {
  return page.getByRole('checkbox', { name: '清除已设的密钥' });
}

/** Secret 输入框（label 文字就是「Secret」）。 */
function secretInput(page: Page): Locator {
  return page.getByLabel('Secret');
}

/** 打开某个 webhook 的编辑表单。 */
async function openEditForm(page: Page, webhookId: string): Promise<void> {
  await page
    .locator(`[data-webhook-id="${webhookId}"]`)
    .getByRole('button', { name: '编辑' })
    .click();
  await expect(page.getByRole('heading', { name: '编辑 webhook' })).toBeVisible();
}

/** 关掉表单（点「取消」，不保存）。 */
async function closeForm(page: Page): Promise<void> {
  await page.getByRole('button', { name: '取消', exact: true }).click();
  await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);
}

/** 服务端那条 webhook 的 hasSecret（密钥只进不出，只能看这个布尔值）。 */
async function hasSecretOnServer(api: IdeaApi, webhookId: string): Promise<boolean | undefined> {
  const list = await api.listWebhooks();
  return list.find((webhook) => webhook.id === webhookId)?.hasSecret;
}

// ── 本地投递接收端（用来验签名的真实取值） ──────────────────────────────

/** 一次投递：请求正文与签名头。 */
interface Delivery {
  /** 请求正文（渲染后的 Body 模板）。 */
  body: string;
  /** `X-Idea-Signature` 头；没有密钥时这个头压根不存在。 */
  signature: string | undefined;
}

/** 一个只回 200 的本地 HTTP 接收端，并把收到的请求记下来。 */
interface Receiver {
  /** 投递目标地址（填进 webhook 的 URL）。 */
  url: string;
  /** 已经收到的投递（按收到的顺序）。 */
  deliveries(): Delivery[];
  /** 关掉接收端。 */
  close(): Promise<void>;
}

/** 起一个只回 200 的本地接收端。端口由系统分配，避免撞端口。 */
async function startReceiver(): Promise<Receiver> {
  const received: Delivery[] = [];
  const server = createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => {
      const header = req.headers['x-idea-signature'];
      received.push({
        body: Buffer.concat(chunks).toString('utf8'),
        signature: typeof header === 'string' ? header : undefined,
      });
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
    deliveries: () => [...received],
    close: () =>
      new Promise<void>((resolve) => {
        server.close(() => resolve());
      }),
  };
}

/**
 * 按正文里的想法内容算出 HMAC 签名。
 *
 * 与后端 `server/IdeaApp/Infrastructure/HmacSigner.cs:26-35` 对齐：
 * `sha256=` + 小写十六进制的 HMAC-SHA256(密钥, 正文)。
 */
function sign(secret: string, payload: string): string {
  return `sha256=${createHmac('sha256', secret).update(payload, 'utf8').digest('hex')}`;
}

/**
 * 等到「正文里含这段内容」的那次投递到达接收端。
 *
 * ⚠️ 内容要用 **ASCII**：后端渲染 Body 模板时走 `Utf8JsonWriter`，非 ASCII 会被
 * 转义成 `\uXXXX`（如「第一条想法」→「\u7B2C\u4E00...」），正文里就找不到原文了。
 */
async function waitForDelivery(receiver: Receiver, content: string): Promise<Delivery> {
  let found: Delivery | undefined;
  await expect
    .poll(
      () => {
        found = receiver.deliveries().find((delivery) => delivery.body.includes(content));
        return found !== undefined;
      },
      { timeout: 60_000, message: `等一次投递（正文含「${content}」）` },
    )
    .toBe(true);
  return found as Delivery;
}

/**
 * 清掉「挂在回收站项目上」的 webhook（孤儿 webhook）。
 *
 * 它们一直是**启用**状态，投递全部失败（地址早就没了），失败的投递会在
 * 1s/5s/30s 的退避里一直占着投递 worker 的并发槽（一共 8 个）—— 攒多了
 * 新的投递排不上队，投递记录迟迟不出现。
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

// ── 用例 ─────────────────────────────────────────────────────────────────

test.describe('Secret：清除已设的密钥（全局页 /webhooks）', () => {
  test('「清除已设的密钥」只在编辑一个已设密钥的 webhook 时出现', async ({ page, seed }) => {
    const withSecret = await seed.webhook({ name: '有密钥的钩子', secret: 'e2e-secret' });
    const withoutSecret = await seed.webhook({ name: '没密钥的钩子' });

    await page.goto('/webhooks');
    // 列表徽标先对得上（WebhookManager.vue:160）。
    await expect(webhookItem(page, '有密钥的钩子')).toContainText('已设密钥');
    await expect(webhookItem(page, '没密钥的钩子')).toContainText('无密钥');

    // 编辑「有密钥的」→ 勾选框在，且默认不勾。
    await openEditForm(page, withSecret.id);
    await expect(clearCheckbox(page)).toBeVisible();
    await expect(clearCheckbox(page)).not.toBeChecked();
    await closeForm(page);

    // 编辑「没密钥的」→ 勾选框压根不存在（没东西可清）。
    await openEditForm(page, withoutSecret.id);
    await expect(clearCheckbox(page)).toHaveCount(0);
    await closeForm(page);

    // 新建 → 勾选框也不存在（新建时还没有「已设的密钥」）。
    await page.getByRole('button', { name: '新建 webhook' }).click();
    await expect(page.getByRole('heading', { name: '新建 webhook' })).toBeVisible();
    await expect(clearCheckbox(page)).toHaveCount(0);
  });

  test('勾上「清除已设的密钥」会把 Secret 输入框里已有的内容清掉（互斥方向一）', async ({
    page,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '互斥一', secret: '旧密钥' });

    await page.goto('/webhooks');
    await openEditForm(page, webhook.id);

    // 先在输入框里填点东西。
    await secretInput(page).fill('新填的值');
    await expect(secretInput(page)).toHaveValue('新填的值');

    // 勾「清除」→ 输入框里的内容被清掉（两者互斥，不能同时成立）。
    await clearCheckbox(page).check();
    await expect(clearCheckbox(page)).toBeChecked();
    await expect(secretInput(page)).toHaveValue('');
  });

  test('填了新密钥 → 「清除已设的密钥」自动取消勾选（互斥方向二）', async ({ page, seed }) => {
    const webhook = await seed.webhook({ name: '互斥二', secret: '旧密钥' });

    await page.goto('/webhooks');
    await openEditForm(page, webhook.id);

    await clearCheckbox(page).check();
    await expect(secretInput(page)).toHaveValue('');

    // 又填了新密钥 → 「清除」自动取消勾选，否则新填的会被静默丢掉。
    await secretInput(page).fill('新密钥');
    await expect(clearCheckbox(page)).not.toBeChecked();
    await expect(secretInput(page)).toHaveValue('新密钥');
  });

  test('只填了空白时「清除已设的密钥」的勾选不被取消（判空口径与保存时一致）', async ({
    page,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '只填空白', secret: '旧密钥' });

    await page.goto('/webhooks');
    await openEditForm(page, webhook.id);

    await clearCheckbox(page).check();
    // 只输入一个空格：这不是「填了内容」，勾选不该被静默取消 ——
    // 取消之后保存时又因 trim 后为空而发 null（= 不改），用户明确要的「清除」
    // 就被丢掉了，两个控件都没起作用。
    await secretInput(page).fill(' ');
    await expect(clearCheckbox(page)).toBeChecked();

    // 保存 → 明确要的「清除」生效（发的是空串，不是 null）。
    await page.getByRole('button', { name: '保存' }).click();
    await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);
    await expect(webhookItem(page, '只填空白')).toContainText('无密钥');
  });

  test('勾上「清除已设的密钥」保存后，密钥真的被清掉了', async ({ page, api, seed }) => {
    const webhook = await seed.webhook({ name: '要清除的钩子', secret: '要被清掉的密钥' });

    await page.goto('/webhooks');
    await expect(webhookItem(page, '要清除的钩子')).toContainText('已设密钥');

    await openEditForm(page, webhook.id);
    await clearCheckbox(page).check();
    await page.getByRole('button', { name: '保存' }).click();
    await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);

    // 列表徽标从「已设密钥」变成「无密钥」。
    const item = webhookItem(page, '要清除的钩子');
    await expect(item).toContainText('无密钥');
    await expect(item).not.toContainText('已设密钥');
    // 服务端也是真的没了（不只是页面上的显示）。
    expect(await hasSecretOnServer(api, webhook.id)).toBe(false);
  });

  test('不碰 Secret 直接保存 → 密钥不变（留空是「不改」，不是「清空」）', async ({
    page,
    api,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '留空不改', secret: '原来的密钥' });

    await page.goto('/webhooks');
    await openEditForm(page, webhook.id);

    // 密钥明文不回传：编辑时输入框从空开始。
    await expect(secretInput(page)).toHaveValue('');

    await page.getByLabel('名称').fill('留空不改（改名了）');
    await page.getByRole('button', { name: '保存' }).click();
    await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);

    const item = webhookItem(page, '留空不改（改名了）');
    await expect(item).toContainText('已设密钥');
    expect(await hasSecretOnServer(api, webhook.id)).toBe(true);
  });

  test('项目级页也能清除密钥', async ({ page, api, seed }) => {
    const project = await seed.project('密钥项目');
    const webhook = await seed.webhook({
      name: '项目级钩子',
      scope: 'project',
      projectId: project.id,
      secret: '项目密钥',
    });

    try {
      await page.goto(`/projects/${project.id}/webhooks`);
      await expect(webhookItem(page, '项目级钩子')).toContainText('已设密钥');

      await openEditForm(page, webhook.id);
      await expect(clearCheckbox(page)).toBeVisible();
      await clearCheckbox(page).check();
      await page.getByRole('button', { name: '保存' }).click();
      await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);

      await expect(webhookItem(page, '项目级钩子')).toContainText('无密钥');
      const list = await api.listWebhooks(project.id);
      expect(list.find((item) => item.id === webhook.id)?.hasSecret).toBe(false);
    } finally {
      // 项目级 webhook 复位删不掉（项目一删它就成了孤儿），自己删掉。
      await api.deleteWebhook(webhook.id).catch(() => undefined);
    }
  });
});

test.describe('Secret：密钥的真正取值（投递签名）', () => {
  test('换密钥 → 投递用新密钥签名；清除密钥 → 投递不再带签名头', async ({ page, api, seed }) => {
    // 投递走后台队列（排队 + 重试），慢的时候要等一会儿。
    test.setTimeout(180_000);

    // 孤儿 webhook 会把投递队列占满，不清掉的话这一条可能永远等不到投递。
    await cleanupOrphanWebhooks(api);

    const receiver = await startReceiver();
    let webhookId: string | null = null;
    try {
      await page.goto('/webhooks');
      await page.getByRole('button', { name: '新建 webhook' }).click();
      await expect(page.getByRole('heading', { name: '新建 webhook' })).toBeVisible();

      await page.getByLabel('名称').fill('签名验证');
      await page.getByLabel('URL').fill(receiver.url);
      await page.getByLabel('Body 模板').fill('{"text":"{{idea.content}}"}');
      await page.getByLabel('Secret').fill('第一把密钥');
      await page.getByRole('checkbox', { name: '想法新建' }).check();
      await page.getByRole('button', { name: '保存' }).click();

      const item = webhookItem(page, '签名验证');
      await expect(item).toBeVisible();
      webhookId = await item.getAttribute('data-webhook-id');
      // 全局 webhook 创建后是禁用的 → 启用它才会收事件。
      await item.getByRole('button', { name: '启用', exact: true }).click();
      await expect(item).toContainText('已启用');

      // ① 第一把密钥：投递带签名，且签名就是用「第一把密钥」算的。
      await seed.idea('idea-one');
      const first = await waitForDelivery(receiver, 'idea-one');
      expect(first.signature).toBe(sign('第一把密钥', first.body));

      // ② 换成新密钥：投递的签名改用新密钥算的。
      await openEditForm(page, webhookId as string);
      await secretInput(page).fill('第二把密钥');
      await expect(clearCheckbox(page)).not.toBeChecked();
      await page.getByRole('button', { name: '保存' }).click();
      await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);

      await seed.idea('idea-two');
      const second = await waitForDelivery(receiver, 'idea-two');
      expect(second.signature).toBe(sign('第二把密钥', second.body));
      expect(second.signature).not.toBe(sign('第一把密钥', second.body));

      // ③ 清除密钥：投递不再带签名头（后端只在密钥非空时才签名）。
      await openEditForm(page, webhookId as string);
      await clearCheckbox(page).check();
      await page.getByRole('button', { name: '保存' }).click();
      await expect(page.getByRole('heading', { name: '编辑 webhook' })).toHaveCount(0);

      await seed.idea('idea-three');
      const third = await waitForDelivery(receiver, 'idea-three');
      expect(third.signature).toBeUndefined();
    } finally {
      // 走之前删掉：这个 webhook 是启用的，留着会一直往接收端投递。
      if (webhookId) {
        await api.deleteWebhook(webhookId).catch(() => undefined);
      }
      await receiver.close();
    }
  });
});
