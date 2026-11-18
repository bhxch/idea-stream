/**
 * 项目管理区的 E2E 用例。
 *
 * 覆盖：
 *   - /projects 页的新建、改名、删除项目；
 *   - 内置收件箱（inbox）不可删除；
 *   - 项目排序：切到手动排序 → 拖拽重排 → sortOrder 变了 → 刷新后仍保持
 *     （项目管理页与宽屏侧栏两处都能拖）；
 *   - 宽屏（侧栏）与窄屏（顶栏的项目下拉）两种形态下切换项目、当前项目高亮；
 *   - 项目里想法的标签增删与标签补全；
 *   - 项目级 webhook 页的入口（每个项目行一个「Webhook」链接）；
 *   - 空状态文案。
 *
 * 约定见 web/e2e/README.md：test/expect 一律从 ./fixtures 导入（复位自动生效），
 * 选择器按 role / 文本 / label 优先，data-testid 优先于 BEM class。
 */
import { test, expect, INBOX_ID } from './fixtures';
import type { Page } from '@playwright/test';

/**
 * /projects 页上的项目名（按页面上的显示顺序）。
 *
 * 项目名既不是 button 也不是 heading，没有可访问名可用 ——
 * 只能按 class 定位（README 里 BEM class 是最后手段，这里确实没有别的办法）。
 * toHaveText 传数组时是**按顺序**逐个比对的，正好用来断言「顺序」。
 */
const projectNames = (page: Page) => page.locator('.projects__name');

test.describe('项目管理页：新建 / 改名 / 删除', () => {
  test('新建项目：填名字提交后出现在列表里', async ({ page, api }) => {
    await page.goto('/projects');

    await page.getByLabel('新项目名称').fill('我的新项目');
    await page.getByRole('button', { name: '新建项目' }).click();

    await expect(page.getByText('我的新项目')).toBeVisible();
    // 输入框清空：可以接着建下一个。
    await expect(page.getByLabel('新项目名称')).toHaveValue('');
    // 后端也有这个项目了。
    expect((await api.listProjects()).map((p) => p.name)).toContain('我的新项目');
  });

  test('新建项目：名称为空时给出提示，不新建', async ({ page, api }) => {
    await page.goto('/projects');
    const before = (await api.listProjects()).length;

    await page.getByRole('button', { name: '新建项目' }).click();

    await expect(page.getByRole('status')).toContainText('项目名称不能为空');
    expect((await api.listProjects()).length).toBe(before);
  });

  test('新建的项目在宽屏侧栏与窄屏顶栏的项目下拉里都能看到', async ({ page }) => {
    await page.goto('/projects');
    await page.getByLabel('新项目名称').fill('侧栏也要看到');
    await page.getByRole('button', { name: '新建项目' }).click();
    await expect(page.getByText('侧栏也要看到')).toBeVisible();

    // 回到首页：宽屏侧栏（≥1024px）里能看到这个项目。
    await page.goto('/');
    const sidebar = page.getByRole('navigation', { name: '项目' });
    await expect(sidebar).toBeVisible();
    await expect(sidebar.getByRole('button', { name: /侧栏也要看到/ })).toBeVisible();

    // 窄屏：同一份数据收成顶栏里的项目下拉（浮层，不滚动）。
    await page.setViewportSize({ width: 600, height: 800 });
    await page.locator('.project-dropdown__trigger').click();
    await expect(page.locator('.project-dropdown__panel')).toBeVisible();
    await expect(
      page.locator('.project-dropdown__panel').getByRole('button', { name: '侧栏也要看到' }),
    ).toBeVisible();
  });

  test('重命名项目：保存后页面与后端都换成新名字', async ({ page, api, seed }) => {
    const project = await seed.project('原来的名字');
    await page.goto('/projects');

    const row = page.locator(`[data-project-id="${project.id}"]`);
    await row.getByRole('button', { name: '改名' }).click();
    await row.getByLabel('项目名称').fill('改过的名字');
    await row.getByRole('button', { name: '保存' }).click();

    await expect(row.getByText('改过的名字')).toBeVisible();
    expect((await api.listProjects()).find((p) => p.id === project.id)?.name).toBe('改过的名字');
  });

  test('新建 / 改名 / 删除成功都给出 info 提示', async ({ page, api, seed }) => {
    // 提示会攒着（过几秒才自动消失），这里取**最后**一条 ——
    // 直接用整组定位器会撞 Playwright 的 strict mode（匹配到多条）。
    const infoToast = page.locator('[role="status"] .toast--info').last();

    // 新建
    await page.goto('/projects');
    await page.getByLabel('新项目名称').fill('提示用项目');
    await page.getByRole('button', { name: '新建项目' }).click();
    await expect(infoToast).toContainText('项目已创建');

    // 改名
    const project = await seed.project('改名前的名字');
    await page.reload();
    const row = page.locator(`[data-project-id="${project.id}"]`);
    await row.getByRole('button', { name: '改名' }).click();
    await row.getByLabel('项目名称').fill('改名后的名字');
    await row.getByRole('button', { name: '保存' }).click();
    await expect(infoToast).toContainText('项目已改名');

    // 删除
    await row.getByRole('button', { name: '删除' }).click();
    await row.getByRole('button', { name: '确认删除' }).click();
    await expect(infoToast).toContainText('项目已删除');
  });

  test('重命名项目：点取消不改名字', async ({ page, api, seed }) => {
    const project = await seed.project('不改的名字');
    await page.goto('/projects');

    const row = page.locator(`[data-project-id="${project.id}"]`);
    await row.getByRole('button', { name: '改名' }).click();
    await row.getByLabel('项目名称').fill('临时改的');
    await row.getByRole('button', { name: '取消' }).click();

    await expect(row.getByText('不改的名字')).toBeVisible();
    expect((await api.listProjects()).find((p) => p.id === project.id)?.name).toBe('不改的名字');
  });

  test('删除项目：确认后从列表里消失', async ({ page, api, seed }) => {
    const project = await seed.project('要删的项目');
    await page.goto('/projects');

    const row = page.locator(`[data-project-id="${project.id}"]`);
    await row.getByRole('button', { name: '删除' }).click();
    // 先问一句：确认条出现了。
    await expect(row.getByText('删除后进回收站（它下面的想法也一起进）。')).toBeVisible();
    await row.getByRole('button', { name: '确认删除' }).click();

    await expect(page.locator(`[data-project-id="${project.id}"]`)).toHaveCount(0);
    expect((await api.listProjects()).map((p) => p.id)).not.toContain(project.id);
  });

  test('删除项目：点取消项目还在', async ({ page, api, seed }) => {
    const project = await seed.project('不删的项目');
    await page.goto('/projects');

    const row = page.locator(`[data-project-id="${project.id}"]`);
    await row.getByRole('button', { name: '删除' }).click();
    await row.getByRole('button', { name: '取消' }).click();

    await expect(page.locator(`[data-project-id="${project.id}"]`)).toHaveCount(1);
    expect((await api.listProjects()).map((p) => p.id)).toContain(project.id);
  });
});

test.describe('内置收件箱', () => {
  test('收件箱不能删除：给出提示，项目还在', async ({ page, api }) => {
    await page.goto('/projects');

    const row = page.locator(`[data-project-id="${INBOX_ID}"]`);
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: '删除' }).click();
    await row.getByRole('button', { name: '确认删除' }).click();

    await expect(page.getByRole('status')).toContainText('内置收件箱不能删除');
    // 收件箱还在（失败时确认条留着，用户还能取消或再试）。
    await expect(page.locator(`[data-project-id="${INBOX_ID}"]`)).toHaveCount(1);
    expect((await api.listProjects()).map((p) => p.id)).toContain(INBOX_ID);
  });
});

test.describe('项目排序', () => {
  test('切到手动排序：设置落库，页面顺序与后端给的一致', async ({ page, api, seed }) => {
    await seed.project('甲');
    await seed.project('乙');
    await page.goto('/projects');

    await page.getByRole('group', { name: '排序方式' }).getByRole('button', { name: '手动排序' }).click();

    await expect.poll(async () => (await api.getSettings())['project.sort_mode']).toBe('manual');
    // 两种排序方式下后端给的顺序不一样，页面显示的必须是后端给的那份。
    const order = await api.listProjects();
    await expect(projectNames(page)).toHaveText(order.map((p) => p.name));
  });

  test('切回最近活动排序：设置落库，顺序回到按最近活动排', async ({ page, api, seed }) => {
    await seed.project('甲');
    await seed.project('乙');
    await page.goto('/projects');

    const modes = page.getByRole('group', { name: '排序方式' });
    await modes.getByRole('button', { name: '手动排序' }).click();
    await expect.poll(async () => (await api.getSettings())['project.sort_mode']).toBe('manual');

    await modes.getByRole('button', { name: '最近活动' }).click();
    await expect.poll(async () => (await api.getSettings())['project.sort_mode']).toBe('recent');
    const order = await api.listProjects();
    await expect(projectNames(page)).toHaveText(order.map((p) => p.name));
  });

  test('项目管理页拖动重排：顺序变了、sortOrder 变了，刷新后仍保持', async ({ page, api, seed }) => {
    await seed.project('甲');
    await seed.project('乙');
    await seed.project('丙');
    await page.goto('/projects');

    await page.getByRole('group', { name: '排序方式' }).getByRole('button', { name: '手动排序' }).click();
    await expect.poll(async () => (await api.getSettings())['project.sort_mode']).toBe('manual');

    // 以后端给的顺序为基准（不假设「最近活动」下的先后）。
    const before = await api.listProjects();
    await expect(projectNames(page)).toHaveText(before.map((p) => p.name));

    // 把第一个项目拖到最后一个项目身上：往下拖落到目标的**后面**。
    const source = before[0];
    const target = before[before.length - 1];
    await page
      .locator(`[data-project-id="${source.id}"]`)
      .dragTo(page.locator(`[data-project-id="${target.id}"]`));

    const expectedIds = [...before.slice(1).map((p) => p.id), source.id];
    const expectedNames = [...before.slice(1).map((p) => p.name), source.name];
    await expect(projectNames(page)).toHaveText(expectedNames);

    // 后端的顺序（= sortOrder）也跟着变了：sortOrder 就是列表下标。
    const after = await api.listProjects();
    expect(after.map((p) => p.id)).toEqual(expectedIds);
    expect(after.map((p) => p.sortOrder)).toEqual(expectedIds.map((_, index) => index));

    // 刷新后还是这个顺序（顺序存在后端，不是页面里的临时状态）。
    await page.reload();
    await expect(projectNames(page)).toHaveText(expectedNames);
    expect((await api.listProjects()).map((p) => p.id)).toEqual(expectedIds);
  });

  test('宽屏侧栏里拖动重排：顺序同样落库', async ({ page, api, seed }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await seed.project('甲');
    await seed.project('乙');
    await seed.project('丙');
    await page.goto('/');

    const sidebar = page.getByRole('navigation', { name: '项目' });
    // 侧栏上的排序方式按钮：当前是「最近活动」，点一下切成手动排序。
    await sidebar.getByRole('button', { name: '最近活动' }).click();
    await expect.poll(async () => (await api.getSettings())['project.sort_mode']).toBe('manual');
    await expect(sidebar.getByRole('button', { name: '手动排序' })).toBeVisible();

    const before = await api.listProjects();
    // 等侧栏上的顺序跟上后端（手动模式下按 sortOrder 排）再拖。
    await expect(sidebar.locator('.project-sidebar__name')).toHaveText([
      '全部项目',
      ...before.map((p) => p.name),
    ]);

    const source = before[0];
    const target = before[before.length - 1];
    await sidebar
      .locator(`[data-project-id="${source.id}"]`)
      .dragTo(sidebar.locator(`[data-project-id="${target.id}"]`));

    const expectedIds = [...before.slice(1).map((p) => p.id), source.id];
    await expect.poll(async () => (await api.listProjects()).map((p) => p.id)).toEqual(expectedIds);
    // 侧栏上显示的顺序也跟着变（第一项是「全部项目」，不算项目）。
    await expect(sidebar.locator('.project-sidebar__name')).toHaveText([
      '全部项目',
      ...expectedIds.map((id) => before.find((p) => p.id === id)!.name),
    ]);
  });
});

test.describe('宽屏侧栏与窄屏顶栏的项目下拉', () => {
  test('宽屏：侧栏可见、顶栏下拉不可见；点侧栏切项目并高亮', async ({ page, seed }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    const a = await seed.project('项目甲');
    await seed.idea('甲的想法', { projectId: a.id });
    const b = await seed.project('项目乙');
    await seed.idea('乙的想法', { projectId: b.id });

    await page.goto('/');

    // 宽屏下只有一个导航地标露着（窄屏的项目下拉是个按钮，不是地标）。
    await expect(page.getByRole('navigation')).toHaveCount(1);
    const sidebar = page.getByRole('navigation', { name: '项目' });
    await expect(sidebar).toBeVisible();
    await expect(page.locator('.project-dropdown__trigger')).toBeHidden();

    await page.getByRole('button', { name: /项目甲/ }).click();

    // 列表换成这个项目的范围。
    await expect(page.getByText('甲的想法')).toBeVisible();
    await expect(page.getByText('乙的想法')).toBeHidden();

    // 当前项目高亮。
    await expect(page.getByRole('button', { name: /项目甲/ })).toHaveClass(/is-active/);
    await expect(page.getByRole('button', { name: /全部项目/ })).not.toHaveClass(/is-active/);

    // 切到另一个项目：高亮跟着走。
    await page.getByRole('button', { name: /项目乙/ }).click();
    await expect(page.getByText('乙的想法')).toBeVisible();
    await expect(page.getByRole('button', { name: /项目乙/ })).toHaveClass(/is-active/);
    await expect(page.getByRole('button', { name: /项目甲/ })).not.toHaveClass(/is-active/);
  });

  test('窄屏：顶栏的项目下拉可见、侧栏不可见；点下拉里的项目切项目', async ({ page, seed }) => {
    await page.setViewportSize({ width: 600, height: 800 });
    const a = await seed.project('项目甲');
    await seed.idea('甲的想法', { projectId: a.id });
    const b = await seed.project('项目乙');
    await seed.idea('乙的想法', { projectId: b.id });

    await page.goto('/');

    // 窄屏下没有导航地标露着（侧栏被藏了，项目切换在下拉里）。
    await expect(page.getByRole('navigation')).toHaveCount(0);
    const trigger = page.locator('.project-dropdown__trigger');
    await expect(trigger).toBeVisible();
    await expect(trigger).toHaveText(/全部项目/);

    await trigger.click();
    const panel = page.locator('.project-dropdown__panel');
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: '项目甲' }).click();

    await expect(page.getByText('甲的想法')).toBeVisible();
    await expect(page.getByText('乙的想法')).toBeHidden();

    // 当前项目显示在触发器上（面板点了就收起，触发器上的名字是唯一可见的那个）。
    await expect(page.locator('.project-dropdown__trigger')).toHaveText(/项目甲/);
  });
});

test.describe('项目里的标签', () => {
  test('给项目里的想法加标签：标签显示出来并落库', async ({ page, api, seed }) => {
    const project = await seed.project('带标签的项目');
    const idea = await seed.idea('要打标签的想法', { projectId: project.id });

    await page.goto('/');
    await page.getByRole('button', { name: /带标签的项目/ }).click();

    const item = page.locator(`[data-idea-id="${idea.id}"]`);
    await expect(item.getByText('要打标签的想法')).toBeVisible();

    await item.getByRole('button', { name: '🏷 标签' }).click();
    await item.getByPlaceholder('加标签…').fill('重要');
    await item.getByPlaceholder('加标签…').press('Enter');

    // 编辑器里出现了这个标签（带删除按钮）。
    await expect(item.getByRole('button', { name: '删除标签 重要' })).toBeVisible();
    // 后端也存上了。
    await expect
      .poll(async () => (await api.listIdeas({ projectId: project.id }))[0]?.tags)
      .toEqual(['重要']);
  });

  test('删掉项目里想法的标签：标签从条目与库里都没了', async ({ page, api, seed }) => {
    const project = await seed.project('带标签的项目');
    const idea = await seed.idea('要打标签的想法', { projectId: project.id });
    await api.setTags(idea.id, ['旧标签']);

    await page.goto('/');
    await page.getByRole('button', { name: /带标签的项目/ }).click();

    const item = page.locator(`[data-idea-id="${idea.id}"]`);
    // 条目上看得见这个标签。
    await expect(item.getByText('旧标签')).toBeVisible();

    await item.getByRole('button', { name: '🏷 标签' }).click();
    await item.getByRole('button', { name: '删除标签 旧标签' }).click();

    await expect(item.getByRole('button', { name: '删除标签 旧标签' })).toHaveCount(0);
    await expect.poll(async () => (await api.listIdeas({ projectId: project.id }))[0]?.tags).toEqual(
      [],
    );
  });

  test('补全标签时，当前项目用过的标签排在前面', async ({ page, api, seed }) => {
    const a = await seed.project('项目甲');
    await api.createIdea({ projectId: a.id, content: '甲的标签', tags: ['专用二'] });
    const target = await api.createIdea({ projectId: a.id, content: '要补全的想法' });
    const b = await seed.project('项目乙');
    await api.createIdea({ projectId: b.id, content: '乙的标签', tags: ['专用一'] });

    await page.goto('/');
    await page.getByRole('button', { name: /项目甲/ }).click();

    const item = page.locator(`[data-idea-id="${target.id}"]`);
    await item.getByRole('button', { name: '🏷 标签' }).click();
    await item.getByPlaceholder('加标签…').fill('专用');

    // 补全下拉：当前项目用过的标签排在前面（全局字典按名称排，顺序正好相反）。
    const options = item.getByRole('button', { name: /专用/ });
    await expect(options).toHaveCount(2);
    await expect(options.first()).toHaveText('专用二');
    await expect(options.nth(1)).toHaveText('专用一');
  });
});

test.describe('空状态', () => {
  test('项目管理页只有收件箱时给出「还没有其它项目。」的提示', async ({ page }) => {
    await page.goto('/projects');

    await expect(projectNames(page)).toHaveText(['收件箱']);
    // 列表里永远有收件箱那一项（后端写死的，删不掉），
    // 所以空状态说的是「还没有**其它**项目」——不是「还没有项目」。
    await expect(page.getByText('还没有其它项目。')).toBeVisible();
  });

  test('项目管理页有别的项目时不显示空状态提示', async ({ page, seed }) => {
    await seed.project('自己的项目');
    await page.goto('/projects');

    // 顺序不钉：默认按「最近活动」排，刚建的项目排在收件箱前面。
    await expect(projectNames(page)).toHaveCount(2);
    await expect(page.getByText('自己的项目', { exact: true })).toBeVisible();
    await expect(page.getByText('还没有其它项目。')).toHaveCount(0);
  });

  test('切到没有想法的项目：想法列表给出空状态文案', async ({ page, seed }) => {
    const project = await seed.project('空项目');

    await page.goto('/');
    await page.getByRole('button', { name: /空项目/ }).click();

    await expect(page.getByText('这里还没有想法。')).toBeVisible();
  });
});

test.describe('项目级 webhook 入口', () => {
  test('每个项目行都有「Webhook」链接，点它到那个项目的 webhook 页', async ({
    page,
    seed,
  }) => {
    const project = await seed.project('带 webhook 入口的项目');
    await page.goto('/projects');

    // 每个项目行一个入口（内置收件箱也算一个）。
    await expect(page.getByRole('link', { name: '项目级 Webhook' })).toHaveCount(2);

    const row = page.locator(`li[data-project-id="${project.id}"]`);
    const link = row.getByRole('link', { name: '项目级 Webhook' });
    await expect(link).toBeVisible();
    await expect(link).toHaveAttribute('href', `/projects/${project.id}/webhooks`);

    await link.click();

    await expect(page).toHaveURL(new RegExp(`/projects/${project.id}/webhooks$`));
    // 标题里带项目名（不是空白的「项目 Webhook」）。
    await expect(page.getByRole('heading', { name: `项目 Webhook · ${project.name}` })).toBeVisible();
  });
});
