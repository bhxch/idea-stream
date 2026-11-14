/**
 * 「Secret 留空 = 不改」（后端三态里的 `null`）的 E2E 用例。
 *
 * 覆盖的语义（见 web/src/components/WebhookForm.vue:126-134 的 `secretValue()`）：
 *   - 编辑一个**已有密钥**的 webhook 时，Secret 输入框留空 = **不改**；
 *   - 留空发出去的是 `null`（后端据此保留原密钥），不是空串（空串 = 清空）；
 *   - 只填空格也算留空（判空口径是 trim）；
 *   - 对照：填了新密钥 = 替换（签名跟着换）；勾「清除已设的密钥」= 空串（清空）。
 *
 * 为什么不能只看徽标：对外只有 `hasSecret` 布尔值，「还是已设密钥」既可能是
 * 没改，也可能是换成了另一个密钥。所以这里除了看徽标，还：
 *   1. 看保存请求里 `secret` 到底是 `null` 还是 `""`（三态的哪一个）；
 *   2. 用**投递签名**钉住密钥的值：接收端拿收到的签名头与自己算的 HMAC 比对，
 *      密钥没换 → 签名还是老密钥算出来的。
 *
 * 约定见 web/e2e/README.md：每个用例前由夹具复位；不写固定时长的 sleep；
 * 选择器优先 role / 文本 / label。
 */
import { createHmac } from 'node:crypto';
import { createServer } from 'node:http';

import type { Page } from '@playwright/test';

import { test, expect } from './fixtures';

// 复位由公共夹具的自动夹具统一负责（见 web/e2e/fixtures.ts 的 resetBeforeEach）：
// 每个用例开始前都会复位，用例之间互不依赖。

// ── 本地投递接收端 ────────────────────────────────────────────────────

/** 收到的一次投递。 */
interface Delivery {
  /** 原始请求体（签名就是对它算的）。 */
  body: Buffer;
  /** 签名头；没配密钥时这个头不存在。 */
  signature: string | undefined;
}

/** 一个只回 200 的本地 HTTP 接收端，把收到的请求记下来。 */
interface Receiver {
  /** 投递目标地址（填进 webhook 的 URL）。 */
  url: string;
  /** 已经收到的投递。 */
  deliveries(): Delivery[];
  /** 关掉接收端。 */
  close(): Promise<void>;
}

/** 起一个只回 200 的本地接收端。端口由系统分配，避免撞端口。 */
async function startReceiver(): Promise<Receiver> {
  const deliveries: Delivery[] = [];
  const server = createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => {
      deliveries.push({
        body: Buffer.concat(chunks),
        signature: req.headers['x-idea-signature'] as string | undefined,
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
    deliveries: () => deliveries,
    close: () =>
      new Promise<void>((resolve) => {
        server.close(() => resolve());
      }),
  };
}

/**
 * 后端给投递签名的方式（对齐 server/IdeaApp/Infrastructure/HmacSigner.cs：
 * `sha256=` + 小写十六进制的 HMAC-SHA256，正文就是发出去的请求体）。
 */
function signatureOf(secret: string, body: Buffer): string {
  return `sha256=${createHmac('sha256', secret).update(body).digest('hex')}`;
}

// ── 小工具 ────────────────────────────────────────────────────────────

/** 列表里那条 webhook（按名字定位）。 */
function webhookItem(page: Page, name: string) {
  return page.locator('[data-webhook-id]').filter({ hasText: name });
}

/** 打开某条 webhook 的编辑表单。 */
async function openEditForm(page: Page, item: ReturnType<typeof webhookItem>): Promise<void> {
  await item.getByRole('button', { name: '编辑' }).click();
  await expect(page.getByRole('heading', { name: '编辑 webhook' })).toBeVisible();
}

/**
 * 点「保存」，并把这次 PATCH 的**请求体**抓回来。
 *
 * 请求体是三态的唯一证据：`null` = 不改、`""` = 清空、非空 = 替换。
 * 响应体里没有 secret 字段（密钥只进不出），所以要看请求体。
 */
async function saveAndCapturePatch(
  page: Page,
  webhookId: string,
): Promise<Record<string, unknown>> {
  const patch = page.waitForResponse(
    (response) =>
      response.request().method() === 'PATCH' &&
      new URL(response.url()).pathname === `/api/webhooks/${webhookId}`,
  );
  await page.getByRole('button', { name: '保存' }).click();
  const response = await patch;
  return (response.request().postDataJSON() ?? {}) as Record<string, unknown>;
}

/**
 * 等一次正文里带指定标记的投递到达接收端。
 *
 * 标记要用 **ASCII**：后端渲染出来的 JSON 会把非 ASCII 字符转义成
 * `\uXXXX`（实测中文正文在请求体里是 `"\u624B\u5DE5..."`），
 * 拿中文去 `includes` 永远匹配不上。
 */
async function waitForDelivery(receiver: Receiver, marker: string): Promise<Delivery> {
  await expect
    .poll(
      () => receiver.deliveries().filter((d) => d.body.toString('utf8').includes(marker)).length,
      { timeout: 30_000, intervals: [200, 500, 1000] },
    )
    .toBeGreaterThan(0);
  const found = receiver.deliveries().find((d) => d.body.toString('utf8').includes(marker));
  if (!found) {
    throw new Error(`没找到带「${marker}」的投递`);
  }
  return found;
}

// ── 用例 ──────────────────────────────────────────────────────────────

test.describe('Secret 留空 = 不改', () => {
  test('编辑时 Secret 留空：已有密钥保留（发出去的是 null，不是空串）', async ({
    page,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '留空不改', secret: 's3cret' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);
    await expect(item).toContainText('已设密钥');

    await openEditForm(page, item);
    const secretInput = page.getByLabel('Secret');
    // 密钥明文不回传：编辑时输入框是空的，placeholder 写明了「留空表示不改」。
    await expect(secretInput).toHaveValue('');
    await expect(secretInput).toHaveAttribute('placeholder', '留空表示不改');

    // 只改名称，Secret 一律不碰。
    await page.getByLabel('名称').fill('留空不改（已改名）');
    const payload = await saveAndCapturePatch(page, webhook.id);

    // 三态里的 null：后端据此**保留**原密钥（不是清空）。
    expect(Object.prototype.hasOwnProperty.call(payload, 'secret')).toBe(true);
    expect(payload.secret).toBeNull();

    // 密钥还在：徽标仍是「已设密钥」。
    await expect(page.locator(`[data-webhook-id="${webhook.id}"]`)).toContainText('已设密钥');
    await expect(page.locator(`[data-webhook-id="${webhook.id}"]`)).toContainText(
      '留空不改（已改名）',
    );
  });

  test('编辑时 Secret 只填了空格：trim 之后为空，同样是「不改」', async ({ page, seed }) => {
    const webhook = await seed.webhook({ name: '只填空格', secret: 's3cret' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);
    await expect(item).toContainText('已设密钥');

    await openEditForm(page, item);
    // 只填了一个空格：判空口径是 trim，所以这仍然是「留空」。
    await page.getByLabel('Secret').fill('   ');
    await page.getByLabel('名称').fill('只填空格（已改名）');
    const payload = await saveAndCapturePatch(page, webhook.id);

    expect(payload.secret).toBeNull();
    await expect(page.locator(`[data-webhook-id="${webhook.id}"]`)).toContainText('已设密钥');
  });

  test('对照：勾「清除已设的密钥」发出去的是空串（清空），与留空不是一回事', async ({
    page,
    seed,
  }) => {
    const webhook = await seed.webhook({ name: '清除对照', secret: 's3cret' });

    await page.goto('/webhooks');
    const item = page.locator(`[data-webhook-id="${webhook.id}"]`);
    await expect(item).toContainText('已设密钥');

    await openEditForm(page, item);
    await page.getByRole('checkbox', { name: '清除已设的密钥' }).check();
    const payload = await saveAndCapturePatch(page, webhook.id);

    // 三态里的空串：后端据此**清空**密钥。
    expect(payload.secret).toBe('');
    await expect(page.locator(`[data-webhook-id="${webhook.id}"]`)).toContainText('无密钥');
  });

  test('编辑留空后：投递仍用原来的密钥签名（换成新密钥才换签名）', async ({
    page,
    api,
    seed,
  }) => {
    // 投递走后台队列（排队 + 重试），慢的时候要等一会儿。
    test.setTimeout(120_000);

    const receiver = await startReceiver();
    let webhookId: string | null = null;
    try {
      // 1. 通过页面新建一个带密钥的 webhook（URL 指向本地接收端）。
      await page.goto('/webhooks');
      await page.getByRole('button', { name: '新建 webhook' }).click();
      await page.getByLabel('名称').fill('签名保持');
      await page.getByLabel('URL').fill(receiver.url);
      await page.getByLabel('Body 模板').fill('{"text":"{{idea.content}}"}');
      await page.getByLabel('Secret').fill('s3cret');
      await page.getByRole('checkbox', { name: '想法新建' }).check();
      await page.getByRole('button', { name: '保存' }).click();

      const item = webhookItem(page, '签名保持');
      await expect(item).toBeVisible();
      webhookId = await item.getAttribute('data-webhook-id');

      // 全局 webhook 创建后是禁用的：不启用就不会有投递。
      await item.getByRole('button', { name: '启用', exact: true }).click();
      await expect(item).toContainText('已启用');

      // 2. 造一条想法 → 第一次投递：签名是 s3cret 算的（顺带证明签名口径对得上）。
      //    想法正文用 ASCII 标记：渲染出来的 JSON 会把中文转义成 \uXXXX。
      await seed.idea('e2e-marker-one');
      const first = await waitForDelivery(receiver, 'e2e-marker-one');
      expect(first.signature).toBe(signatureOf('s3cret', first.body));

      // 3. 编辑时 Secret 留空（只改名称）→ 密钥不该变。
      await openEditForm(page, item);
      await page.getByLabel('名称').fill('签名保持（改过名）');
      await page.getByRole('button', { name: '保存' }).click();
      await expect(page.locator(`[data-webhook-id="${webhookId}"]`)).toContainText('已设密钥');

      await seed.idea('e2e-marker-two');
      const second = await waitForDelivery(receiver, 'e2e-marker-two');
      // 密钥没被清掉：签名还是老密钥算出来的（而不是「换成了别的密钥」）。
      expect(second.signature).toBe(signatureOf('s3cret', second.body));
      expect(second.signature).not.toBe(signatureOf('别的密钥', second.body));

      // 4. 编辑时填了新密钥 → 这次签名才跟着换（说明上面那步确实是「没改」）。
      await openEditForm(page, item);
      await page.getByLabel('Secret').fill('newsecret');
      await page.getByRole('button', { name: '保存' }).click();
      await expect(page.locator(`[data-webhook-id="${webhookId}"]`)).toContainText('已设密钥');

      await seed.idea('e2e-marker-three');
      const third = await waitForDelivery(receiver, 'e2e-marker-three');
      expect(third.signature).toBe(signatureOf('newsecret', third.body));
      expect(third.signature).not.toBe(signatureOf('s3cret', third.body));
    } finally {
      // 走之前删掉：webhook 还开着，留着会一直往已经关掉的接收端投递。
      if (webhookId) {
        await api.deleteWebhook(webhookId);
      }
      await receiver.close();
    }
  });
});
