/**
 * 回收站（/trash）的端到端用例。
 *
 * 覆盖四件事：
 *   1. 删掉的想法/项目进回收站，条目上看得出原属项目；
 *   2. 恢复：条目回到原项目；
 *   3. 彻底删除：应用**不提供**这个操作（到期自动清理是唯一出口），
 *      这里把「没有彻底删除按钮」钉住；
 *   4. 回收站为空时的空状态。
 *
 * 约定见 web/e2e/README.md：
 *   - 夹具在每个用例前复位，所以用例之间互不依赖；
 *   - 断言回收站时**按内容定位**，不要断言条目总数 —— 复位清不掉残渣
 *     （每个被删项目会留一条 `e2e-reset` 的想法在容器里）。
 *
 * ⚠️ 关于重试：底座现在是「一轮 = 一个端口 + 一个库」
 * （web/e2e/.tmp/run-<端口>/e2e.db），一轮内部 `workers: 1`，
 * 两轮并发跑也各用各的库 —— 跨轮次的数据串扰已经没有了。
 * 这里保留两次重试当**兜底**：真出问题时表现是夹具的复位自己报
 * 400 `not_in_trash` 或 404 `idea_not_found`，用例在 beforeEach 就挂掉；
 * 重试能把这种**环境层面的偶发**扛过去，而真正的断言失败重试了也还是失败。
 */
import type { Page } from '@playwright/test';

import { test, expect, clickHoverOnly, type IdeaApi } from './fixtures';

/**
 * 回收站里的一个条目（按内容定位）。
 *
 * ⚠️ 条目本身没有可访问名，也没有 data-testid（回收站页面上还没有），
 * 只能退回 BEM class —— 这是 README 里说的「最后手段」。用 hasText 限定内容，
 * 所以 class 改名只会让这条用例需要改，不会误伤别的条目。
 */
function trashEntry(page: Page, text: string) {
  return page.locator('.trash__entry').filter({ hasText: text });
}

/**
 * 这一轮的唯一标记。
 *
 * 复位会在回收站里留下**同名项目的残渣容器**（每个被删的项目留一条 `e2e-reset`
 * 的想法在容器里），而残渣清不掉 —— 上一轮跑过的项目名会一直躺在回收站里。
 * 项目名带上这个标记，两轮之间就不会撞名（撞了就是「定位到 2 个元素」）。
 */
function uniqueTag(): string {
  return `${Date.now().toString(36).slice(-5)}${Math.random().toString(36).slice(2, 5)}`;
}

/**
 * 把回收站里的东西**全恢复**回来 → 回收站空了。
 *
 * 应用没有「彻底删除」，回收站只进不出：复位（fixtures.ts）清不掉它 ——
 * 复位自己还会留下残渣。所以想断言「回收站是空的」，只能把里面的东西恢复回来。
 *
 * ⚠️ 用 `freshDatabase()`（删库重建 + 重启服务）也能达到同样的效果，但那是
 * 整库级别的动作 —— 别的用例正在跑的话会把它们的数据一起抹掉。恢复回收站
 * 影响面小得多：只是把条目搬回活动状态。
 */
async function emptyTrash(api: IdeaApi): Promise<void> {
  const trash = await api.listTrash();
  // 先恢复想法：容器的项目还没恢复时，恢复想法是允许的。
  for (const entry of trash) {
    if (entry.kind === 'idea' && entry.idea) {
      await api.restoreIdea(entry.idea.id);
    } else if (entry.kind === 'project') {
      for (const idea of entry.ideas) {
        await api.restoreIdea(idea.id);
      }
    }
  }
  // 再恢复项目：容器没了，条目也就没了。
  for (const entry of trash) {
    if (entry.kind === 'project' && entry.project) {
      await api.restoreProject(entry.project.id);
    }
  }
}

/**
 * 在主页面上把某条想法删掉（走「🗑 删除」→「确认删除」两步）。
 *
 * 删完等条目从 DOM 里消失 —— 那是「真的删掉了」的可见证据。
 */
async function deleteIdeaInUi(page: Page, ideaId: string): Promise<void> {
  const item = page.locator(`[data-idea-id="${ideaId}"]`);
  // 🗑 在桌面上是「悬停才显形」的控件：先悬停到条目上，它才显形、才点得到。
  await clickHoverOnly(item, '🗑 删除');
  await item.getByRole('button', { name: '确认删除' }).click();
  await expect(item).toHaveCount(0);
}

/** 在项目管理页上把某个项目删掉（走「删除」→「确认删除」两步）。 */
async function deleteProjectInUi(page: Page, projectId: string): Promise<void> {
  const row = page.locator(`[data-project-id="${projectId}"]`);
  await row.getByRole('button', { name: '删除', exact: true }).click();
  await row.getByRole('button', { name: '确认删除' }).click();
  await expect(row).toHaveCount(0);
}

test.describe('回收站 /trash', () => {
  // 环境层面的偶发（别的分区同时在跑）靠重试扛过去，见文件头。
  test.describe.configure({ retries: 2 });

  // ── 删除 → 回收站 ──────────────────────────────────────────────────────

  test('删除的想法出现在回收站里', async ({ page, seed }) => {
    const idea = await seed.idea('待删的想法');

    await page.goto('/');
    await deleteIdeaInUi(page, idea.id);

    await page.goto('/trash');
    await expect(page.getByText('待删的想法', { exact: true })).toBeVisible();
  });

  test('单独删掉一条想法不会把还活着的项目带进回收站', async ({ page, seed }) => {
    const project = await seed.project(`还活着的项目-${uniqueTag()}`);
    const idea = await seed.idea('只删这一条', { projectId: project.id });

    await page.goto('/');
    await deleteIdeaInUi(page, idea.id);

    await page.goto('/trash');
    // 想法进了回收站……
    await expect(page.getByText('只删这一条', { exact: true })).toBeVisible();
    // ……但它只是顶层条目：项目还活着，回收站里不该冒出这个项目的容器条目。
    await expect(page.getByText(project.name, { exact: true })).toHaveCount(0);
  });

  test('删掉的项目进回收站：条目上看得见项目名，展开看得见它下面的想法', async ({
    page,
    seed,
  }) => {
    const project = await seed.project(`要被删的项目-${uniqueTag()}`);
    await seed.idea('项目里的想法', { projectId: project.id });

    await page.goto('/projects');
    await deleteProjectInUi(page, project.id);

    await page.goto('/trash');
    const container = trashEntry(page, project.name);
    // 容器条目上写的是项目名 —— 原属项目看得见。
    await expect(container).toBeVisible();
    await expect(container.getByText('1 条想法')).toBeVisible();

    // 展开后看得见项目下挂的那条想法。
    await container.getByRole('button', { name: '展开' }).click();
    await expect(container.getByText('项目里的想法', { exact: true })).toBeVisible();
  });

  test('回收站里被单独删掉的想法，看得出来它原来属于哪个项目', async ({ page, seed }) => {
    const project = await seed.project(`想法的原项目-${uniqueTag()}`);
    const idea = await seed.idea('从项目里删掉的想法', { projectId: project.id });

    await page.goto('/');
    await deleteIdeaInUi(page, idea.id);

    await page.goto('/trash');
    const entry = trashEntry(page, '从项目里删掉的想法');
    await expect(entry).toBeVisible();
    // 条目上要能看出它原来属于哪个项目。
    await expect(entry.getByText(project.name)).toBeVisible();
  });

  test('恢复项目 / 恢复想法成功都给出 info 提示', async ({ page, seed }) => {
    // 提示会攒着（过几秒才自动消失），这里取**最后**一条 ——
    // 直接用整组定位器会撞 Playwright 的 strict mode（匹配到多条）。
    const infoToast = page.locator('[role="status"] .toast--info').last();

    // 一条被单独删掉的想法（顶层条目）与一个被删掉的项目（容器条目）。
    //
    // ⚠️ 项目名要带 uniqueTag()：**被删的项目会一直躺在回收站里** ——
    // 回收站只进不出，复位也清不掉（复位只是把它们恢复回来、再删一遍），
    // 所以同一个库跑第二轮时，回收站里已经有同名的残渣容器了。
    // 按固定名字定位就会撞 Playwright 的 strict mode（匹配到多条）。
    // 想法不用带：复位清想法走的是合并**硬删**，正文不会留下残渣。
    const idea = await seed.idea('要恢复的想法');
    const project = await seed.project(`要恢复的项目-${uniqueTag()}`);

    await page.goto('/');
    await deleteIdeaInUi(page, idea.id);
    await page.goto('/projects');
    await deleteProjectInUi(page, project.id);
    await page.goto('/trash');

    // 恢复一条想法
    await trashEntry(page, '要恢复的想法').getByRole('button', { name: '恢复', exact: true }).click();
    await expect(infoToast).toContainText('想法已恢复');

    // 恢复项目（按项目名定位到**那一个**容器：回收站里可能还躺着同名的残渣容器）
    await trashEntry(page, project.name).getByRole('button', { name: '恢复项目' }).click();
    await expect(infoToast).toContainText('项目已恢复');
  });

  // ── 恢复 ────────────────────────────────────────────────────────────────

  test('恢复：想法回到原项目', async ({ page, seed, api }) => {
    const project = await seed.project(`要回来的项目-${uniqueTag()}`);
    const idea = await seed.idea('要回来的想法', { projectId: project.id });

    await page.goto('/');
    await deleteIdeaInUi(page, idea.id);

    await page.goto('/trash');
    const entry = trashEntry(page, '要回来的想法');
    await entry.getByRole('button', { name: '恢复', exact: true }).click();
    // 恢复过的条目不该还留在回收站里。
    await expect(entry).toHaveCount(0);

    // 回到**原项目**：选中那个项目，它出现在那个项目的列表里。
    // 定位写 `nav[aria-label="项目"]`：那是宽屏侧栏（窄屏的项目切换已经收成
    // 顶栏里的下拉了），带上这个 aria-label 就只命中侧栏里的那一个。
    await page.goto('/');
    await page.locator(`nav[aria-label="项目"] [data-project-id="${project.id}"]`).click();
    await expect(page.locator(`[data-idea-id="${idea.id}"]`)).toBeVisible();

    // 接口上也是活动状态，且挂在原项目下。
    const active = await api.listIdeas({ projectId: project.id });
    expect(active.map((item) => item.id)).toContain(idea.id);
  });

  test('恢复项目：项目与它下面的想法一起回来', async ({ page, seed, api }) => {
    const project = await seed.project(`要回来的项目-${uniqueTag()}`);
    const idea = await seed.idea('项目里的想法', { projectId: project.id });

    await api.deleteProject(project.id);

    await page.goto('/trash');
    const container = trashEntry(page, project.name);
    await expect(container).toBeVisible();
    await container.getByRole('button', { name: '恢复项目' }).click();
    await expect(container).toHaveCount(0);

    // 项目回到项目管理页。
    await page.goto('/projects');
    const row = page.locator(`[data-project-id="${project.id}"]`);
    await expect(row).toBeVisible();
    await expect(row.getByText(project.name, { exact: true })).toBeVisible();

    // 它下面的想法也一起回来了。
    const active = await api.listIdeas({ projectId: project.id });
    expect(active.map((item) => item.id)).toContain(idea.id);
  });

  test('容器里的想法能单独恢复：它离开容器、回到活动列表', async ({ page, seed, api }) => {
    const project = await seed.project(`还在回收站的项目-${uniqueTag()}`);
    const idea = await seed.idea('单独恢复的想法', { projectId: project.id });

    await api.deleteProject(project.id);

    await page.goto('/trash');
    const container = trashEntry(page, project.name);
    await container.getByRole('button', { name: '展开' }).click();

    // 容器里那一条想法的「恢复」按钮（容器的按钮叫「恢复项目」，不会撞上）。
    // ⚠️ 子条目同样没有可访问名，只能用 BEM class 定位（见上面 trashEntry 的说明）。
    const child = container.locator('.trash__child').filter({ hasText: '单独恢复的想法' });
    await child.getByRole('button', { name: '恢复', exact: true }).click();

    // 它从容器里消失了。
    await expect(container.getByText('单独恢复的想法', { exact: true })).toHaveCount(0);

    // 想法回到活动状态（项目还在回收站里，它已经是活动想法了）。
    const active = await api.listIdeas({});
    expect(active.map((item) => item.id)).toContain(idea.id);
  });

  // ── 彻底删除 ────────────────────────────────────────────────────────────

  test('回收站没有「彻底删除」：到期自动清理是唯一出口', async ({ page, seed, api }) => {
    const idea = await seed.idea('等着自动清理的想法');
    await api.deleteIdea(idea.id);

    await page.goto('/trash');
    await expect(page.getByText('等着自动清理的想法', { exact: true })).toBeVisible();

    // 页面上没有「彻底删除」这个操作。
    await expect(page.getByRole('button', { name: '彻底删除' })).toHaveCount(0);
    // 也没有别的「永久删除」说法。
    await expect(page.getByText('彻底删除')).toHaveCount(0);

    // 页面说清楚这些东西怎么才会真的没了：到期自动清理（保留期由服务端配置决定）。
    await expect(page.getByText('回收站里的内容到期后会自动清理')).toBeVisible();
  });

  test('回收站里的内容不会自己消失（只是不提供手动彻底删除）', async ({ page, seed, api }) => {
    const idea = await seed.idea('会一直躺着的那条');
    await api.deleteIdea(idea.id);

    await page.goto('/trash');
    await expect(page.getByText('会一直躺着的那条', { exact: true })).toBeVisible();

    // 刷新之后还在 —— 回收站只进不出（到期清理之前）。
    await page.reload();
    await expect(page.getByText('会一直躺着的那条', { exact: true })).toBeVisible();
  });

  // ── 空状态 ──────────────────────────────────────────────────────────────

  test('回收站是空的时给一句说明', async ({ page, api, seed }) => {
    // 起点：回收站是空的 —— 复位留下的残渣全恢复回来，回收站就空了。
    await emptyTrash(api);

    await page.goto('/trash');
    await expect(page.getByText('回收站是空的。')).toBeVisible();
    await expect(page.locator('.trash__entry')).toHaveCount(0);

    // 删一条 → 回收站不空了，空状态那句也就没了。
    const idea = await seed.idea('空状态之前的那条');
    await api.deleteIdea(idea.id);
    await page.reload();
    await expect(page.getByText('空状态之前的那条', { exact: true })).toBeVisible();
    await expect(page.getByText('回收站是空的。')).toHaveCount(0);

    // 恢复它 → 又空了，空状态那句话回来。
    // 按钮定位到**那一条**的条目里：回收站里若还有别的条目，在全页找「恢复」会撞名。
    await trashEntry(page, '空状态之前的那条')
      .getByRole('button', { name: '恢复', exact: true })
      .click();
    await expect(page.getByText('回收站是空的。')).toBeVisible();
    await expect(page.locator('.trash__entry')).toHaveCount(0);
  });
});
