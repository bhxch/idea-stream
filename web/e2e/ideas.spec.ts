/**
 * 收件箱（/）主流程的 E2E 用例。
 *
 * 覆盖：新建想法、编辑正文、标记完成/取消完成、加/改/删标签、删除进回收站、
 * 多选批量（条数 / 完成 / 加标签 / 删除）、合并、按项目/按标签/按状态筛选、
 * 清除筛选、关键词搜索（大小写不敏感）、一键「清除」过滤条件、深链定位、
 * SSE 实时同步。
 *
 * 约定见 web/e2e/README.md：
 * - 从 ./fixtures 导入 test/expect（复位在 beforeEach 里自动发生，用例之间互不依赖）；
 * - 不用固定时长等待，一律等状态（toBeVisible / toHaveCount / toHaveText）；
 * - 选择器优先 role / 文本 / label，其次是应用**已有**的钩子
 *   （data-idea-id、aria-label="选择 …" 等）——想法条目上还没有 data-testid
 *   （项目侧栏与窄屏的项目下拉上有了，见 projects.spec.ts）。
 */
import { test, expect, clickHoverOnly } from './fixtures';
import type { Locator, Page } from '@playwright/test';

// 复位由公共夹具的自动夹具统一负责（见 web/e2e/fixtures.ts 的 resetBeforeEach）：
// 每个用例开始前都会复位，用例之间互不依赖。

// ── 定位辅助 ─────────────────────────────────────────────────────────────

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

/** 想法的正文区域（点它进入编辑态）。 */
function ideaBody(page: Page, id: string): Locator {
  // 正文区带 title="点击编辑"（MessageItem 渲染的），用它定位比 class 稳。
  return ideaItem(page, id).getByTitle('点击编辑');
}

/** 批量操作条（勾选想法后出现的那个条）。 */
function batchBar(page: Page): Locator {
  return page.getByRole('group', { name: '批量操作' });
}

/** 状态过滤那一组按钮（全部 / 未完成 / 已完成）。 */
function statusFilter(page: Page): Locator {
  return page.getByRole('group', { name: '状态过滤' });
}

/** 项目侧栏（宽屏下那一栏）。 */
function projectSidebar(page: Page): Locator {
  return page.locator('nav[aria-label="项目"]');
}

/**
 * 打开某条想法的标签编辑器，返回标签输入框。
 *
 * 收起编辑器时如果还有「没存上的改动」，应用会弹一个 window.confirm
 * （MessageItem 的 closeTagEdit）—— 用例里统一接受它，别让对话框把流程卡住。
 */
async function openTagEditor(page: Page, id: string): Promise<Locator> {
  page.on('dialog', (dialog) => {
    void dialog.accept();
  });
  const item = ideaItem(page, id);
  // 🏷 在桌面上是「悬停才显形」的控件：先悬停到条目上，它才显形、才点得到。
  await clickHoverOnly(item, '🏷 标签');
  const input = item.getByPlaceholder('加标签…');
  await expect(input).toBeVisible();
  return input;
}

/** 等标签保存请求（PUT /api/ideas/:id/tags）落地。 */
function tagsSaved(page: Page, id: string): Promise<unknown> {
  return page.waitForResponse(
    (response) =>
      response.url().includes(`/api/ideas/${id}/tags`) &&
      response.request().method() === 'PUT',
  );
}

// ── 新建 / 编辑 ──────────────────────────────────────────────────────────

test('新建想法：提交后列表出现、刷新后还在、内容与提交一致', async ({ page, api }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '想法' })).toBeVisible();

  // 两条正文：想法没有标题了，整段（含首行）都当正文 —— 两段都要能看到。
  const content = '第一行：今天想到一个点子\n第二行：再补一句细节';
  await page.getByPlaceholder('记一条想法…').fill(content);
  await page.getByRole('button', { name: '记下' }).click();

  // 提交后立刻出现在列表里。
  const item = page.locator('article').filter({ hasText: '今天想到一个点子' });
  await expect(item).toHaveCount(1);
  // 想法没有标题了：正文是**全文**，首行不再被摘走当标题。
  await expect(item).toContainText('第一行：今天想到一个点子');
  await expect(item.getByTitle('点击编辑')).toHaveText('第一行：今天想到一个点子\n第二行：再补一句细节');

  // 内容与提交一致（落库了，不只是界面上的）。
  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].content).toBe(content);

  // 刷新后还在。
  await page.reload();
  const reloaded = page.locator('article').filter({ hasText: '今天想到一个点子' });
  await expect(reloaded).toHaveCount(1);
  await expect(reloaded).toContainText('第一行：今天想到一个点子');
  await expect(reloaded.getByTitle('点击编辑')).toHaveText('第一行：今天想到一个点子\n第二行：再补一句细节');
});

test('编辑想法：改正文并保存，刷新后还是改后的内容', async ({ page, api, seed }) => {
  const idea = await seed.idea('原来的正文');
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  await expect(item.getByTitle('点击编辑')).toHaveText('原来的正文');

  // 点正文进入编辑态 → 改内容 → Ctrl+Enter 保存。
  await ideaBody(page, idea.id).click();
  const editor = item.locator('textarea');
  await expect(editor).toBeVisible();
  await editor.fill('改过的正文');
  await editor.press('Control+Enter');

  // 保存后退出编辑态，正文换成新的。
  await expect(item.getByTitle('点击编辑')).toHaveText('改过的正文');
  await expect(item.locator('textarea')).toHaveCount(0);

  // 刷新后仍是改后的内容。
  await page.reload();
  await expect(ideaBody(page, idea.id)).toHaveText('改过的正文');

  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].content).toBe('改过的正文');
});

// ── 完成 / 取消完成 ──────────────────────────────────────────────────────

test('标记完成：状态与样式都持久化', async ({ page, api, seed }) => {
  const idea = await seed.idea('要做完的事');
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  await expect(item.getByRole('button', { name: '标记完成', exact: true })).toBeVisible();
  await item.getByRole('button', { name: '标记完成', exact: true }).click();

  // 按钮变成「已完成」，aria-pressed 变成 true。
  await expect(item.getByRole('button', { name: '已完成', exact: true })).toBeVisible();
  await expect(item.getByRole('button', { name: '已完成', exact: true })).toHaveAttribute('aria-pressed', 'true');
  // 「已完成」的样式是靠这个类实现的（MessageItem 的 idea--done）。
  await expect(item).toHaveClass(/idea--done/);

  // 刷新后状态与样式还在。
  await page.reload();
  const reloaded = ideaItem(page, idea.id);
  await expect(reloaded.getByRole('button', { name: '已完成', exact: true })).toBeVisible();
  await expect(reloaded).toHaveClass(/idea--done/);

  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].done).toBe(true);
});

test('取消完成：状态与样式回到未完成', async ({ page, api, seed }) => {
  const idea = await seed.doneIdea('已经做完的事');
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  await expect(item.getByRole('button', { name: '已完成', exact: true })).toBeVisible();
  await item.getByRole('button', { name: '已完成', exact: true }).click();

  await expect(item.getByRole('button', { name: '标记完成', exact: true })).toBeVisible();
  await expect(item.getByRole('button', { name: '标记完成', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await expect(item).not.toHaveClass(/idea--done/);

  await page.reload();
  const reloaded = ideaItem(page, idea.id);
  await expect(reloaded.getByRole('button', { name: '标记完成', exact: true })).toBeVisible();
  await expect(reloaded).not.toHaveClass(/idea--done/);

  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].done).toBe(false);
});

// ── 标签 ─────────────────────────────────────────────────────────────────

test('加标签：新标签出现在条目上，刷新后仍在', async ({ page, api, seed }) => {
  const idea = await seed.idea('给这条加个标签');
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  const input = await openTagEditor(page, idea.id);
  const saved = tagsSaved(page, idea.id);
  await input.fill('灵感库');
  await input.press('Enter');
  await saved;

  // 收起编辑器后，标签显示在条目的标签位上。
  await item.getByRole('button', { name: '✓ 完成编辑' }).click();
  await expect(item.getByText('灵感库', { exact: true })).toBeVisible();

  await page.reload();
  await expect(ideaItem(page, idea.id).getByText('灵感库', { exact: true })).toBeVisible();

  const ideas = await api.listIdeas();
  expect(ideas[0].tags).toEqual(['灵感库']);
});

test('改标签：换掉旧标签，刷新后仍是新标签', async ({ page, api, seed }) => {
  const idea = await seed.idea('换标签', { tags: ['旧标签'] });
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  await expect(item.getByText('旧标签', { exact: true })).toBeVisible();

  const input = await openTagEditor(page, idea.id);
  const saved = tagsSaved(page, idea.id);
  // 删掉旧标签、加一个新标签（一次保存里改完）。
  await item.getByLabel('删除标签 旧标签').click();
  await input.fill('新标签');
  await input.press('Enter');
  await saved;

  await item.getByRole('button', { name: '✓ 完成编辑' }).click();
  await expect(item.getByText('新标签', { exact: true })).toBeVisible();
  await expect(item.getByText('旧标签', { exact: true })).toHaveCount(0);

  await page.reload();
  await expect(ideaItem(page, idea.id).getByText('新标签', { exact: true })).toBeVisible();
  await expect(ideaItem(page, idea.id).getByText('旧标签', { exact: true })).toHaveCount(0);

  const ideas = await api.listIdeas();
  expect(ideas[0].tags).toEqual(['新标签']);
});

test('删标签：标签从条目上消失，刷新后也不再出现', async ({ page, api, seed }) => {
  const idea = await seed.idea('删标签', { tags: ['要删的标签'] });
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  await expect(item.getByText('要删的标签', { exact: true })).toBeVisible();

  const input = await openTagEditor(page, idea.id);
  const saved = tagsSaved(page, idea.id);
  await item.getByLabel('删除标签 要删的标签').click();
  await saved;

  await item.getByRole('button', { name: '✓ 完成编辑' }).click();
  await expect(item.getByText('要删的标签', { exact: true })).toHaveCount(0);

  await page.reload();
  await expect(ideaItem(page, idea.id).getByText('要删的标签', { exact: true })).toHaveCount(0);

  const ideas = await api.listIdeas();
  expect(ideas[0].tags).toEqual([]);
});

// ── 删除 ─────────────────────────────────────────────────────────────────

test('删除想法：条目从列表消失，并进了回收站', async ({ page, api, seed }) => {
  const idea = await seed.idea('待删除的想法');
  await page.goto('/');

  const item = ideaItem(page, idea.id);
  await expect(item).toBeVisible();

  // 删除要先二次确认。
  // 🗑 在桌面上是「悬停才显形」的控件：先悬停到条目上，它才显形、才点得到。
  await clickHoverOnly(item, '🗑 删除');
  await expect(item.getByText('删除这条？它会进回收站。')).toBeVisible();
  await item.getByRole('button', { name: '确认删除' }).click();

  await expect(item).toHaveCount(0);
  await expect(page.locator('article')).toHaveCount(0);

  // 进了回收站（按内容定位：回收站里可能有复位留下的残渣，不能数总数）。
  const trash = await api.listTrash();
  const entry = trash.find((item) => item.kind === 'idea' && item.idea?.id === idea.id);
  expect(entry, '删除的想法应该出现在回收站里').toBeTruthy();

  // 刷新后列表里确实没有它了。
  await page.reload();
  await expect(page.locator('article')).toHaveCount(0);
});

// ── 多选与批量操作 ───────────────────────────────────────────────────────

test('多选：勾选后批量条出现且条数正确', async ({ page, seed }) => {
  await seed.idea('甲');
  await seed.idea('乙');
  await seed.idea('丙');
  await page.goto('/');

  // 一条都没勾：没有批量条。
  await expect(batchBar(page)).toHaveCount(0);

  await page.getByLabel('选择 甲').check();
  await expect(batchBar(page)).toBeVisible();
  await expect(batchBar(page).getByText('已选 1 条')).toBeVisible();

  await page.getByLabel('选择 丙').check();
  await expect(batchBar(page).getByText('已选 2 条')).toBeVisible();

  // 取消一个：条数跟着减。
  await page.getByLabel('选择 甲').uncheck();
  await expect(batchBar(page).getByText('已选 1 条')).toBeVisible();

  // 全取消：批量条消失。
  await page.getByLabel('选择 丙').uncheck();
  await expect(batchBar(page)).toHaveCount(0);
});

test('批量完成：勾选的几条都被标记完成', async ({ page, api, seed }) => {
  const first = await seed.idea('批量甲');
  const second = await seed.idea('批量乙');
  const untouched = await seed.idea('没勾的丙');
  await page.goto('/');

  await page.getByLabel('选择 批量甲').check();
  await page.getByLabel('选择 批量乙').check();
  await expect(batchBar(page).getByText('已选 2 条')).toBeVisible();

  await batchBar(page).getByRole('button', { name: '标记完成', exact: true }).click();

  await expect(ideaItem(page, first.id).getByRole('button', { name: '已完成', exact: true })).toBeVisible();
  await expect(ideaItem(page, second.id).getByRole('button', { name: '已完成', exact: true })).toBeVisible();
  // 没勾的那条不受影响。
  await expect(ideaItem(page, untouched.id).getByRole('button', { name: '标记完成', exact: true })).toBeVisible();

  const ideas = await api.listIdeas();
  expect(ideas.filter((idea) => idea.done).map((idea) => idea.id).sort()).toEqual(
    [first.id, second.id].sort(),
  );

  // 批量撤销完成：又回到未完成。
  await batchBar(page).getByRole('button', { name: '撤销完成', exact: true }).click();
  await expect(ideaItem(page, first.id).getByRole('button', { name: '标记完成', exact: true })).toBeVisible();
  await expect(ideaItem(page, second.id).getByRole('button', { name: '标记完成', exact: true })).toBeVisible();
  const undone = await api.listIdeas();
  expect(undone.every((idea) => !idea.done)).toBe(true);
});

test('批量加标签：勾选的几条都加上标签', async ({ page, api, seed }) => {
  const first = await seed.idea('批量标签甲');
  const second = await seed.idea('批量标签乙');
  await page.goto('/');

  await page.getByLabel('选择 批量标签甲').check();
  await page.getByLabel('选择 批量标签乙').check();
  await expect(batchBar(page).getByText('已选 2 条')).toBeVisible();

  await batchBar(page).getByRole('button', { name: '加标签', exact: true }).click();
  const input = batchBar(page).getByPlaceholder('加标签…');
  await expect(input).toBeVisible();
  const saved = page.waitForResponse(
    (response) =>
      response.url().includes('/api/ideas/bulk/tags') && response.request().method() === 'POST',
  );
  await input.fill('批量标签');
  await input.press('Enter');
  await batchBar(page).getByRole('button', { name: '加到 2 条' }).click();
  await saved;

  await expect(ideaItem(page, first.id).getByText('批量标签', { exact: true })).toBeVisible();
  await expect(ideaItem(page, second.id).getByText('批量标签', { exact: true })).toBeVisible();

  const ideas = await api.listIdeas();
  for (const idea of ideas) {
    expect(idea.tags).toContain('批量标签');
  }
});

test('批量删除：勾选的几条都从列表消失并进回收站', async ({ page, api, seed }) => {
  const first = await seed.idea('批量删除甲');
  const second = await seed.idea('批量删除乙');
  await seed.idea('留下的丙');
  await page.goto('/');

  await page.getByLabel('选择 批量删除甲').check();
  await page.getByLabel('选择 批量删除乙').check();
  await expect(batchBar(page).getByText('已选 2 条')).toBeVisible();

  await batchBar(page).getByRole('button', { name: '删除', exact: true }).click();
  await expect(batchBar(page).getByText('删除选中的 2 条？它们会进回收站。')).toBeVisible();
  await batchBar(page).getByRole('button', { name: '确认删除' }).click();

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '留下的丙' })).toHaveCount(1);

  const trash = await api.listTrash();
  const trashedIds = trash
    .filter((entry) => entry.kind === 'idea')
    .map((entry) => entry.idea?.id);
  expect(trashedIds).toContain(first.id);
  expect(trashedIds).toContain(second.id);

  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].id).not.toBe(first.id);
});

// ── 合并 ─────────────────────────────────────────────────────────────────

test('合并：选多条合并，结果内容正确且原条目消失', async ({ page, api, seed }) => {
  const first = await seed.idea('想法甲');
  const second = await seed.idea('想法乙');
  await page.goto('/');

  await page.getByLabel('选择 想法甲').check();
  await page.getByLabel('选择 想法乙').check();
  await expect(batchBar(page).getByText('已选 2 条')).toBeVisible();

  await batchBar(page).getByRole('button', { name: '合并', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: '合并想法' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('heading', { name: '合并 2 条想法' })).toBeVisible();
  // 预览里有这两条。预览行现在取的是**正文首行**（想法没有标题了），
  // 与下面 <pre> 里的正文文本一模一样 —— 按文本定位会命中 2 个元素，
  // 所以直接定位预览行本身（.merge-dialog__item-title）。
  await expect(
    dialog.locator('.merge-dialog__item-title').filter({ hasText: '想法甲' }),
  ).toHaveText('想法甲');
  await expect(
    dialog.locator('.merge-dialog__item-title').filter({ hasText: '想法乙' }),
  ).toHaveText('想法乙');

  // 合并后的正文：按列表顺序（updated_at 升序）以空行拼接。
  const ordered = (await api.listIdeas()).filter(
    (idea) => idea.id === first.id || idea.id === second.id,
  );
  const expected = [...ordered].sort((left, right) => {
    const byTime = Date.parse(left.updatedAt) - Date.parse(right.updatedAt);
    return byTime !== 0 ? byTime : left.id < right.id ? -1 : 1;
  });
  const expectedContent = expected.map((idea) => idea.content).join('\n\n');
  await expect(dialog.getByLabel('合并后的正文')).toHaveValue(expectedContent);

  await dialog.getByRole('button', { name: '合并成一条' }).click();

  // 列表里只剩合并出来的那一条。
  await expect(page.locator('article')).toHaveCount(1);
  await expect(dialog).toHaveCount(0);

  // 原条目没了（合并是硬删），结果内容正确。
  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].id).not.toBe(first.id);
  expect(ideas[0].id).not.toBe(second.id);
  expect(ideas[0].content).toBe(expectedContent);

  // 结果条目上能看到**完整的**合并正文（想法没有标题了，只有正文这一段）。
  const merged = ideaItem(page, ideas[0].id);
  await expect(merged).toContainText(expectedContent);
});

test('合并：改过对话框里的正文，合并结果用改后的内容', async ({ page, api, seed }) => {
  await seed.idea('想法甲');
  await seed.idea('想法乙');
  await page.goto('/');

  await page.getByLabel('选择 想法甲').check();
  await page.getByLabel('选择 想法乙').check();
  await batchBar(page).getByRole('button', { name: '合并', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: '合并想法' });
  await expect(dialog).toBeVisible();

  // 改一改合并后的正文再确认。
  const content = dialog.getByLabel('合并后的正文');
  await content.fill('我自己整理过的正文');
  await dialog.getByRole('button', { name: '合并成一条' }).click();

  await expect(dialog).toHaveCount(0);
  await expect(page.locator('article')).toHaveCount(1);

  const ideas = await api.listIdeas();
  expect(ideas).toHaveLength(1);
  expect(ideas[0].content).toBe('我自己整理过的正文');
  await expect(ideaItem(page, ideas[0].id)).toContainText('我自己整理过的正文');
});

// ── 筛选 ─────────────────────────────────────────────────────────────────

test('筛选-按项目：只显示选中项目的想法', async ({ page, seed }) => {
  const project = await seed.project('测试项目');
  await seed.idea('收件箱里的想法');
  await seed.idea('项目里的想法', { projectId: project.id });
  await page.goto('/');

  // 默认「全部项目」：两条都在。
  await expect(page.locator('article')).toHaveCount(2);

  await projectSidebar(page).getByRole('button', { name: '测试项目' }).click();
  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '项目里的想法' })).toHaveCount(1);

  // 回到「全部项目」：两条又都在。
  await projectSidebar(page).getByRole('button', { name: '全部项目' }).click();
  await expect(page.locator('article')).toHaveCount(2);
});

test('筛选-按标签：只显示带该标签的想法', async ({ page, seed }) => {
  // 条目的正文刻意**不含**标签名：条目上的勾选框（aria-label="选择 <正文首行>"）
  // 与标签过滤里的勾选框都是 checkbox，名字撞了就没法只定位到后者。
  await seed.idea('想法甲', { tags: ['灵感库'] });
  await seed.idea('想法乙', { tags: ['待办清单'] });
  await seed.idea('想法丙');
  await page.goto('/');

  await expect(page.locator('article')).toHaveCount(3);

  // 打开标签下拉，勾一个标签。
  await page.getByRole('button', { name: '标签', exact: true }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).check();

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '想法甲' })).toHaveCount(1);

  // 再勾一个（默认 OR 模式）：命中任一标签即显示。
  await page.getByRole('checkbox', { name: '待办清单' }).check();
  await expect(page.locator('article')).toHaveCount(2);
});

test('筛选-按状态：未完成与已完成分别只显示对应的', async ({ page, seed }) => {
  await seed.idea('还没做的');
  await seed.doneIdea('已经做完了');
  await page.goto('/');

  await expect(page.locator('article')).toHaveCount(2);

  await statusFilter(page).getByRole('button', { name: '未完成', exact: true }).click();
  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '还没做的' })).toHaveCount(1);

  await statusFilter(page).getByRole('button', { name: '已完成', exact: true }).click();
  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '已经做完了' })).toHaveCount(1);

  await statusFilter(page).getByRole('button', { name: '全部', exact: true }).click();
  await expect(page.locator('article')).toHaveCount(2);
});

test('清除筛选：取消标签与状态筛选后列表恢复全部', async ({ page, seed }) => {
  await seed.idea('想法甲', { tags: ['灵感库'] });
  await seed.idea('想法乙');
  await page.goto('/');

  await expect(page.locator('article')).toHaveCount(2);

  // 加上两个筛选条件。
  await page.getByRole('button', { name: '标签', exact: true }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).check();
  await expect(page.locator('article')).toHaveCount(1);

  await statusFilter(page).getByRole('button', { name: '已完成', exact: true }).click();
  await expect(page.locator('article')).toHaveCount(0);

  // 清除：状态回到「全部」，标签取消勾选。
  await statusFilter(page).getByRole('button', { name: '全部', exact: true }).click();
  await page.getByRole('button', { name: '标签（1）' }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).uncheck();

  await expect(page.locator('article')).toHaveCount(2);
  await expect(page.getByRole('button', { name: '标签', exact: true })).toBeVisible();
});

// ── 关键词搜索（过滤栏里的搜索框） ──────────────────────────────────────────

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
 * 过滤栏里的「清除」按钮。
 *
 * 名字就是「清除」两个字（exact 匹配，免得撞上别的含「清除」的名字）。
 * 没有过滤条件生效时它不出现 —— 所以用 count 断言「它在不在」，
 * 用 toBeVisible 断言「它在」。
 */
function clearFiltersButton(page: Page): Locator {
  return page.getByRole('button', { name: '清除', exact: true });
}

test('搜索：输入关键词后只显示正文含该词的想法', async ({ page, seed }) => {
  await seed.idea('做苹果派的想法');
  await seed.idea('做香蕉派的想法');
  await seed.idea('今天天气不错');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(3);

  await keywordSearch(page).fill('苹果');

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: '做苹果派的想法' })).toHaveCount(1);
});

test('搜索：关键词大小写不敏感', async ({ page, seed }) => {
  await seed.idea('Weather today');
  await seed.idea('别的想法');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  // 搜小写，命中的是大写开头的那一条。
  await keywordSearch(page).fill('weather');

  await expect(page.locator('article')).toHaveCount(1);
  await expect(page.locator('article').filter({ hasText: 'Weather today' })).toHaveCount(1);
});

test('搜索：搜不到时显示空状态', async ({ page, seed }) => {
  await seed.idea('有的想法');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(1);

  await keywordSearch(page).fill('这个词库里没有');

  await expect(page.locator('article')).toHaveCount(0);
  await expect(page.getByText('这里还没有想法。')).toBeVisible();
});

test('搜索：清空输入后列表恢复全部', async ({ page, seed }) => {
  await seed.idea('第一条想法');
  await seed.idea('第二条想法');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  await keywordSearch(page).fill('第一条');
  await expect(page.locator('article')).toHaveCount(1);

  await keywordSearch(page).fill('');
  await expect(page.locator('article')).toHaveCount(2);
});

// ── 「清除」按钮 ───────────────────────────────────────────────────────────

test('清除按钮：没有过滤条件时不出现', async ({ page, seed }) => {
  await seed.idea('一条想法');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(1);

  await expect(clearFiltersButton(page)).toHaveCount(0);
});

test('清除按钮：按状态过滤时出现', async ({ page, seed }) => {
  await seed.idea('一条想法');
  await page.goto('/');

  await statusFilter(page).getByRole('button', { name: '未完成', exact: true }).click();

  await expect(clearFiltersButton(page)).toBeVisible();
});

test('清除按钮：按标签过滤时出现', async ({ page, seed }) => {
  // 条目的正文刻意**不含**标签名：想法条目上的勾选框
  // （aria-label="选择 <正文首行>"）与标签过滤里的勾选框都是 checkbox，
  // 正文里带上标签名就会撞名（strict mode 报错）。
  await seed.idea('想法甲', { tags: ['灵感库'] });
  await page.goto('/');

  await page.getByRole('button', { name: '标签', exact: true }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).check();

  await expect(clearFiltersButton(page)).toBeVisible();
});

test('清除按钮：有关键词时出现', async ({ page, seed }) => {
  await seed.idea('一条想法');
  await page.goto('/');

  await keywordSearch(page).fill('一条');

  await expect(clearFiltersButton(page)).toBeVisible();
});

test('清除按钮：一下把状态、标签、关键词一起清掉', async ({ page, seed }) => {
  await seed.idea('带标签的想法', { tags: ['灵感库'] });
  await seed.idea('普通想法');
  await page.goto('/');
  await expect(page.locator('article')).toHaveCount(2);

  // 三个过滤条件同时生效：「已完成」把两条都筛掉了。
  await statusFilter(page).getByRole('button', { name: '已完成', exact: true }).click();
  await page.getByRole('button', { name: '标签', exact: true }).click();
  await page.getByRole('checkbox', { name: '灵感库' }).check();
  await keywordSearch(page).fill('想法');
  await expect(page.locator('article')).toHaveCount(0);
  await expect(clearFiltersButton(page)).toBeVisible();

  await clearFiltersButton(page).click();

  // 状态回到「全部」。
  await expect(statusFilter(page).getByRole('button', { name: '全部', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  // 关键词清空。
  await expect(keywordSearch(page)).toHaveValue('');
  // 标签取消勾选（触发按钮上的计数消失；面板会被这次点击收起，重新打开看勾选状态）。
  await expect(page.getByRole('button', { name: '标签', exact: true })).toBeVisible();
  await page.getByRole('button', { name: '标签', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: '灵感库' })).not.toBeChecked();
  // 列表恢复全部。
  await expect(page.locator('article')).toHaveCount(2);
});

// ── 深链 ─────────────────────────────────────────────────────────────────

test('深链：/?idea=<id> 直达并高亮那一条', async ({ page, seed }) => {
  const idea = await seed.idea('深链指向的想法');
  await seed.idea('别的想法');

  // 高亮只持续 1.5 秒（useDeepLink 的 highlightMs），直接断言 class 会 flaky：
  // 用 MutationObserver 把「曾经被加上 highlight 类」的条目 id 记下来。
  await page.addInitScript(() => {
    const store: string[] = [];
    (window as unknown as { __highlightedIdeaIds: string[] }).__highlightedIdeaIds = store;
    const observer = new MutationObserver((records) => {
      for (const record of records) {
        const element = record.target as HTMLElement;
        if (element.classList?.contains('highlight')) {
          const id = element.dataset?.ideaId;
          if (id) {
            store.push(id);
          }
        }
      }
    });
    const attach = () =>
      observer.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ['class'],
        subtree: true,
      });
    if (document.documentElement) {
      attach();
    } else {
      document.addEventListener('DOMContentLoaded', attach);
    }
  });

  await page.goto(`/?idea=${idea.id}`);

  // 那条在列表里。
  await expect(ideaItem(page, idea.id)).toBeVisible();
  await expect(page.locator('article')).toHaveCount(2);

  // 它被高亮过。
  await expect
    .poll(async () =>
      page.evaluate(() => (window as unknown as { __highlightedIdeaIds: string[] }).__highlightedIdeaIds),
    )
    .toContain(idea.id);
});

// ── SSE ──────────────────────────────────────────────────────────────────

test('SSE：别的上下文新建的想法，当前页面自动出现', async ({ page, api }) => {
  // 等事件流连上：服务端先订阅再发响应头，响应头到了就说明订阅已登记。
  const connected = page.waitForResponse((response) => response.url().includes('/api/events'), {
    timeout: 15_000,
  });
  await page.goto('/');
  await connected;
  await expect(page.getByRole('heading', { name: '想法' })).toBeVisible();

  // api 走的是另一个 HTTP 上下文（不是浏览器页面），等价于「另一台设备」。
  const created = await api.createIdea({ content: '来自另一个上下文的想法' });

  // 页面没有刷新，新条目自己出现了。
  await expect(
    page.locator('article').filter({ hasText: '来自另一个上下文的想法' }),
  ).toHaveCount(1);
  await expect(ideaItem(page, created.id)).toBeVisible();
});
