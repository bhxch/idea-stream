/**
 * 排版与响应式：5 个路由 × 4 个视口。
 *
 * 覆盖的东西（每一条都是「用户看得见」的，不看内部状态）：
 *
 * - 页面没有横向溢出（documentElement.scrollWidth <= clientWidth + 1）；
 * - 关键容器里没有**意外的**横向滚动条；
 * - 关键文本块没有被容器裁掉（scrollWidth/scrollHeight 不超出 clientWidth/clientHeight）；
 * - 固定元素（批量操作条、底部输入框）不互相遮挡；
 * - 移动端（375×667）可点击元素不小于 32×32；
 * - 关键元素落在视口里（用户不用先滚一下才看得到）；
 * - 底部输入框只有一层边框（外壳不画边框，输入框自己画一层）。
 *
 * 每个「路由-视口」都拍一张截图，落在 `web/e2e/screenshots/<route>-<viewport>.png`。
 *
 * ⚠️ 用例之间互不依赖：夹具在 beforeEach 里复位（见 fixtures.ts），
 * 每条用例自己造自己需要的数据。
 */
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { Locator, Page } from '@playwright/test';

import { test, expect, type IdeaApi, type Seed } from './fixtures';

/* ------------------------------------------------------------------ *
 * 视口
 * ------------------------------------------------------------------ */

/** 一个视口。 */
interface Viewport {
  /** 文件名里的短名。 */
  name: string;
  width: number;
  height: number;
}

/**
 * 四个视口：手机竖屏、平板、笔记本、宽屏。
 *
 * 375 是手机竖屏的经典尺寸（iPhone SE/8 那代），
 * 1024 是应用自己的断点（窄屏/宽屏两版布局，见 HomePage.vue 的媒体查询），
 * 所以 768 与 1280 分别落在断点两侧。
 */
const VIEWPORTS: Viewport[] = [
  { name: '375x667', width: 375, height: 667 },
  { name: '768x1024', width: 768, height: 1024 },
  { name: '1280x800', width: 1280, height: 800 },
  { name: '1920x1080', width: 1920, height: 1080 },
];

/** 手机视口：只有它按 32×32 的点按目标尺寸检查。 */
const MOBILE_VIEWPORT = VIEWPORTS[0];

/** 可点击元素的最小尺寸（CSS 像素）。 */
const MIN_TAP_TARGET = 32;

/* ------------------------------------------------------------------ *
 * 路由
 * ------------------------------------------------------------------ */

/** 要定位的元素：名字（报错时用）+ 定位器。 */
interface Named {
  /** 报错时说明是哪个元素。 */
  name: string;
  locator: Locator;
}

/** 一个路由的排版检查项。 */
interface RouteSpec {
  /** 截图文件名里的短名（不含路径分隔符）。 */
  name: string;
  /** 造数据并返回要打开的路径。 */
  prepare(seed: Seed, api: IdeaApi): Promise<string>;
  /** 页面「加载好了」的判据：等它出现。 */
  ready(page: Page): Locator;
  /** 关键元素：必须落在视口里（用户不用先滚一下）。 */
  keyElements(page: Page): Named[];
  /** 关键容器：不能被内容撑出横向滚动条。 */
  containers(page: Page): Named[];
  /** 关键文本块：文字不能被裁掉。 */
  textBlocks(page: Page): Named[];
  /** 额外的检查（只有主页有：固定元素不许互相遮挡）。 */
  extraChecks?(page: Page, label: string): Promise<void>;
}

/**
 * 有意为之的横向滚动容器。
 *
 * 目前一个都没有了 —— 窄屏的项目 Tab 条换成了顶栏里的下拉（浮层，不滚动），
 * 剩下的容器都不该横向滚动。
 */
const INTENTIONAL_H_SCROLL = new Set<string>();

/** 路由清单。 */
const ROUTES: RouteSpec[] = [
  {
    name: 'home',
    async prepare(seed) {
      await seed.project('布局测试项目');
      await seed.idea('排版探测想法：第一行\n第二行正文，用来测试正文换行。');
      // 一条特别长的、里面没有空格的正文：窄屏上最容易把容器撑破。
      await seed.idea(
        'SupercalifragilisticexpialidociousSupercalifragilisticexpialidociousSupercalifragilisticexpialidocious',
      );
      await seed.idea('带标签的排版探测想法', { tags: ['排版探测标签'] });
      return '/';
    },
    // 想法没有标题了：就绪探针取正文里的一个文本。
    ready: (page) => page.getByText('排版探测想法', { exact: false }).first(),
    keyElements: (page) => [
      { name: '标题「想法」', locator: page.getByRole('heading', { name: '想法', exact: true }) },
      { name: '状态过滤组', locator: page.getByRole('group', { name: '状态过滤' }) },
      { name: '底部输入框', locator: page.getByRole('textbox', { name: /记一条想法/ }) },
      { name: '「记下」按钮', locator: page.getByRole('button', { name: '记下' }) },
    ],
    containers: (page) => [
      // 消息流的滚动容器、整块内容区。
      // 用 BEM class 定位：这些是纯布局容器，没有 role / aria-label 可用。
      { name: '.home__scroll', locator: page.locator('.home__scroll') },
      { name: '.home__content', locator: page.locator('.home__content') },
      { name: '.composer', locator: page.locator('.composer') },
    ],
    textBlocks: (page) => [
      // 没有标题层了（<h2 class="title"> 已拆掉）：文本块只剩正文。
      { name: '想法条目的正文', locator: page.locator('.idea .body').first() },
      { name: '超长不换行正文', locator: page.locator('.idea .body').nth(1) },
    ],
    extraChecks: checkFixedElements,
  },
  {
    name: 'projects',
    async prepare(seed) {
      await seed.project('一个名字很长的项目用来测试换行与裁剪行为');
      return '/projects';
    },
    ready: (page) =>
      page.getByText('一个名字很长的项目用来测试换行与裁剪行为', { exact: true }).first(),
    keyElements: (page) => [
      { name: '标题「项目管理」', locator: page.getByRole('heading', { name: '项目管理' }) },
      { name: '管理页导航', locator: page.getByRole('navigation', { name: '管理' }) },
      { name: '导航条上的「想法」入口', locator: page.getByRole('navigation', { name: '管理' }).getByRole('link', { name: '想法' }) },
      { name: '新项目名称输入框', locator: page.getByRole('textbox', { name: '新项目名称' }) },
      { name: '「新建项目」按钮', locator: page.getByRole('button', { name: '新建项目' }) },
      { name: '排序方式按钮组', locator: page.getByRole('group', { name: '排序方式' }) },
      { name: '项目行的「Webhook」入口', locator: page.getByRole('link', { name: '项目级 Webhook' }).first() },
    ],
    containers: (page) => [
      { name: '.page', locator: page.locator('.page') },
      { name: '.projects__list', locator: page.locator('.projects__list') },
    ],
    textBlocks: (page) => [
      { name: '项目名', locator: page.locator('.projects__name').first() },
      { name: '操作提示', locator: page.locator('.projects__hint') },
    ],
  },
  {
    name: 'trash',
    async prepare(seed, api) {
      const idea = await seed.idea('要删掉的想法');
      await api.deleteIdea(idea.id);
      return '/trash';
    },
    ready: (page) => page.getByText('要删掉的想法', { exact: true }).first(),
    keyElements: (page) => [
      { name: '标题「回收站」', locator: page.getByRole('heading', { name: '回收站' }) },
      { name: '管理页导航', locator: page.getByRole('navigation', { name: '管理' }) },
      { name: '导航条上的「想法」入口', locator: page.getByRole('navigation', { name: '管理' }).getByRole('link', { name: '想法' }) },
    ],
    containers: (page) => [
      { name: '.page', locator: page.locator('.page') },
      { name: '.trash__list', locator: page.locator('.trash__list') },
    ],
    textBlocks: (page) => [
      { name: '回收站条目正文', locator: page.locator('.trash__body').first() },
      { name: '回收站说明', locator: page.locator('.trash__hint') },
    ],
  },
  {
    name: 'webhooks',
    async prepare(seed) {
      await seed.webhook({ name: '排版探测 webhook' });
      return '/webhooks';
    },
    ready: (page) => page.getByText('排版探测 webhook', { exact: true }).first(),
    keyElements: (page) => [
      { name: '标题「Webhook」', locator: page.getByRole('heading', { name: 'Webhook', exact: true }) },
      { name: '管理页导航', locator: page.getByRole('navigation', { name: '管理' }) },
      { name: '导航条上的「想法」入口', locator: page.getByRole('navigation', { name: '管理' }).getByRole('link', { name: '想法' }) },
      { name: '「新建 webhook」按钮', locator: page.getByRole('button', { name: '新建 webhook' }) },
    ],
    containers: (page) => [
      { name: '.page', locator: page.locator('.page') },
      { name: '.webhooks__list', locator: page.locator('.webhooks__list') },
    ],
    textBlocks: (page) => [
      { name: 'webhook 名称', locator: page.locator('.webhooks__name').first() },
      { name: 'webhook URL', locator: page.locator('.webhooks__url').first() },
    ],
  },
  {
    name: 'project-webhooks',
    async prepare(seed) {
      const project = await seed.project('布局测试项目');
      await seed.webhook({
        name: '项目级排版探测 webhook',
        scope: 'project',
        projectId: project.id,
      });
      return `/projects/${project.id}/webhooks`;
    },
    ready: (page) => page.getByText('项目级排版探测 webhook', { exact: true }).first(),
    keyElements: (page) => [
      { name: '标题「项目 Webhook」', locator: page.getByRole('heading', { name: /项目 Webhook/ }) },
      { name: '管理页导航', locator: page.getByRole('navigation', { name: '管理' }) },
      { name: '导航条上的「想法」入口', locator: page.getByRole('navigation', { name: '管理' }).getByRole('link', { name: '想法' }) },
      { name: '「新建 webhook」按钮', locator: page.getByRole('button', { name: '新建 webhook' }) },
    ],
    containers: (page) => [
      { name: '.page', locator: page.locator('.page') },
      { name: '.webhooks__list', locator: page.locator('.webhooks__list') },
    ],
    textBlocks: (page) => [
      { name: 'webhook 名称', locator: page.locator('.webhooks__name').first() },
      { name: 'webhook URL', locator: page.locator('.webhooks__url').first() },
    ],
  },
];

/* ------------------------------------------------------------------ *
 * 检查
 * ------------------------------------------------------------------ */

/** 页面本身没有横向溢出。 */
async function expectNoHorizontalOverflow(page: Page, label: string): Promise<void> {
  const doc = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }));
  expect(
    doc.scrollWidth,
    `${label}：页面出现了横向溢出（scrollWidth ${doc.scrollWidth} > clientWidth ${doc.clientWidth}）`,
  ).toBeLessThanOrEqual(doc.clientWidth + 1);
}

/**
 * 关键容器里没有意外的横向滚动条。
 *
 * 「意外」= 这个容器不是有意横向滚动的那种（有意的那几个在 INTENTIONAL_H_SCROLL 里）。
 * 其余容器一律不许被内容撑宽 —— 撑宽了要么出横向滚动条，要么把字裁掉。
 *
 * 判据用 scrollWidth > clientWidth（允许 1px 的取整误差）：
 * overflow-x 是 auto/scroll 时这就是「真的出了横向滚动条」，
 * 是 hidden/clip 时是「内容被裁掉了」，是 visible 时是「内容溢出到容器外面」——
 * 三种都是排版事故，只是后果不同，所以报错里把 overflow-x 一起写出来。
 */
async function expectNoUnexpectedHorizontalScroll(
  page: Page,
  containers: Named[],
  label: string,
): Promise<void> {
  let measured = 0;
  for (const container of containers) {
    if (INTENTIONAL_H_SCROLL.has(container.name)) {
      continue;
    }
    const info = await container.locator.evaluate((el) => {
      const style = getComputedStyle(el);
      return {
        overflowX: style.overflowX,
        scrollWidth: el.scrollWidth,
        clientWidth: el.clientWidth,
        // 宽屏下窄屏布局整块 display:none（.home__tabs），量了没意义。
        rendered: style.display !== 'none' && el.clientWidth > 0,
      };
    });
    if (!info.rendered) {
      continue;
    }
    measured += 1;
    const overflow = info.scrollWidth - info.clientWidth;
    expect(
      overflow,
      `${label}：容器「${container.name}」的内容被撑破了` +
        `（scrollWidth ${info.scrollWidth} > clientWidth ${info.clientWidth}，超出 ${overflow}px，` +
        `overflow-x: ${info.overflowX} → ${
          info.overflowX === 'visible'
            ? '内容会溢出到容器外面'
            : info.overflowX === 'hidden' || info.overflowX === 'clip'
              ? '内容会被裁掉'
              : '会出现横向滚动条'
        }）`,
    ).toBeLessThanOrEqual(1);
  }
  // 一个都没量到 = 选择器没打中（或者容器整块没渲染），检查是空转的。
  expect(measured, `${label}：一个容器都没量到（选择器可能没打中）`).toBeGreaterThan(0);
}

/**
 * 关键文本块没被容器裁掉。
 *
 * 判定：scrollWidth/scrollHeight 不超出 clientWidth/clientHeight。
 * 允许 1px 的误差 —— 小数高度取整时会差一点点，不是真的裁了。
 */
async function expectTextNotClipped(
  page: Page,
  blocks: Named[],
  label: string,
): Promise<void> {
  let measured = 0;
  for (const block of blocks) {
    const info = await block.locator.evaluate((el) => {
      const style = getComputedStyle(el);
      return {
        // 行内元素量不出宽高（clientWidth/Height 恒为 0），量了也没意义。
        measurable: style.display !== 'inline' && el.clientWidth > 0,
        scrollWidth: el.scrollWidth,
        clientWidth: el.clientWidth,
        scrollHeight: el.scrollHeight,
        clientHeight: el.clientHeight,
      };
    });
    if (!info.measurable) {
      continue;
    }
    measured += 1;
    expect(
      info.scrollWidth,
      `${label}：文本「${block.name}」被横向裁掉了` +
        `（scrollWidth ${info.scrollWidth} > clientWidth ${info.clientWidth}）`,
    ).toBeLessThanOrEqual(info.clientWidth + 1);
    expect(
      info.scrollHeight,
      `${label}：文本「${block.name}」被纵向裁掉了` +
        `（scrollHeight ${info.scrollHeight} > clientHeight ${info.clientHeight}）`,
    ).toBeLessThanOrEqual(info.clientHeight + 1);
  }
  // 一个都没量到 = 选择器没打中，检查是空转的 —— 那不算通过。
  expect(measured, `${label}：一个文本块都没量到（选择器可能没打中）`).toBeGreaterThan(0);
}

/** 元素在视口里的位置与尺寸（CSS 像素，与视口左上角为原点）。 */
interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** 元素是否与视口矩形相交（露出来一点就算「看得见」）。 */
function intersectsViewport(box: Box, viewport: { width: number; height: number }): boolean {
  return (
    box.x < viewport.width &&
    box.y < viewport.height &&
    box.x + box.width > 0 &&
    box.y + box.height > 0
  );
}

/** 元素有没有完整落在视口里；没有的话，差多少像素（写在报错里）。 */
function viewportOverflows(box: Box, viewport: { width: number; height: number }): string[] {
  const out: string[] = [];
  if (box.x < -0.5) out.push(`左边超出 ${Math.round(-box.x)}px`);
  if (box.y < -0.5) out.push(`上边超出 ${Math.round(-box.y)}px`);
  if (box.x + box.width > viewport.width + 0.5) {
    out.push(`右边超出 ${Math.round(box.x + box.width - viewport.width)}px`);
  }
  if (box.y + box.height > viewport.height + 0.5) {
    out.push(`下边超出 ${Math.round(box.y + box.height - viewport.height)}px`);
  }
  return out;
}

/**
 * 关键元素落在视口里（用户不用先滚一下才看得到）。
 *
 * 判据用矩形相交而不是 toBeInViewport()：报错里要带上量到的数字
 * （元素的位置与尺寸、视口尺寸），红的时候一眼看出偏了多少。
 */
async function expectKeyElementsInViewport(
  page: Page,
  elements: Named[],
  label: string,
): Promise<void> {
  const viewport = page.viewportSize();
  expect(viewport, `${label}：拿不到视口尺寸`).not.toBeNull();
  if (!viewport) {
    return;
  }
  for (const element of elements) {
    await expect(
      element.locator,
      `${label}：关键元素「${element.name}」不存在或不可见`,
    ).toBeVisible();
    const box = await element.locator.boundingBox();
    expect(box, `${label}：量不到关键元素「${element.name}」的位置`).not.toBeNull();
    if (!box) {
      continue;
    }
    expect(
      intersectsViewport(box, viewport),
      `${label}：关键元素「${element.name}」不在视口里` +
        `（元素 x=${Math.round(box.x)} y=${Math.round(box.y)} ` +
        `w=${Math.round(box.width)} h=${Math.round(box.height)}，` +
        `视口 ${viewport.width}×${viewport.height}）`,
    ).toBe(true);
  }
}

/**
 * 元素必须**完整**落在视口里。
 *
 * 用于「用户随时都要点得到」的东西（底部输入框、批量操作条）：
 * 只露一半等于点不到。
 */
async function expectFullyInsideViewport(
  locator: Locator,
  name: string,
  label: string,
): Promise<void> {
  const viewport = (await locator.page().viewportSize()) ?? { width: 0, height: 0 };
  const box = await locator.boundingBox();
  expect(box, `${label}：量不到「${name}」的位置`).not.toBeNull();
  if (!box) {
    return;
  }
  const overflows = viewportOverflows(box, viewport);
  expect(
    overflows,
    `${label}：「${name}」没有完整落在视口里` +
      `（${overflows.join('，') || '刚好贴边'}；` +
      `元素 x=${Math.round(box.x)} y=${Math.round(box.y)} ` +
      `w=${Math.round(box.width)} h=${Math.round(box.height)}，` +
      `视口 ${viewport.width}×${viewport.height}）`,
  ).toEqual([]);
}

/** 两个矩形是否相交（边挨着边不算相交）。 */
function rectanglesOverlap(
  left: { x: number; y: number; width: number; height: number },
  right: { x: number; y: number; width: number; height: number },
): boolean {
  return (
    left.x < right.x + right.width &&
    right.x < left.x + left.width &&
    left.y < right.y + right.height &&
    right.y < left.y + left.height
  );
}

/**
 * 固定元素不互相遮挡：勾一条想法 → 批量操作条出现 →
 * 它不能盖住底部的输入框（用户就点不到「记下」了），
 * 也不能把输入框挤到视口外面去（`.home` 是 overflow:hidden 的，
 * 挤出去就等于看不见也点不到）。
 */
async function checkFixedElements(page: Page, label: string): Promise<void> {
  // 底部输入框是个没有可访问名的 form，只能从里面的输入框往上找父元素。
  const composer = page.getByRole('textbox', { name: /记一条想法/ }).locator('xpath=..');
  await expect(composer, `${label}：底部输入框不在视口里`).toBeVisible();
  await expectFullyInsideViewport(composer, '底部输入框', label);

  await page.getByRole('checkbox', { name: /选择/ }).first().check();

  const batchBar = page.getByRole('group', { name: '批量操作' });
  await expect(batchBar).toBeVisible();
  await expectFullyInsideViewport(batchBar, '批量操作条', label);

  const barBox = await batchBar.boundingBox();
  const composerBox = await composer.boundingBox();
  expect(barBox, `${label}：量不到批量操作条的位置`).not.toBeNull();
  expect(composerBox, `${label}：量不到底部输入框的位置`).not.toBeNull();

  expect(
    barBox && composerBox ? rectanglesOverlap(barBox, composerBox) : true,
    `${label}：批量操作条盖住了底部的输入框` +
      `（批量操作条 ${JSON.stringify(barBox)}，输入框 ${JSON.stringify(composerBox)}）`,
  ).toBe(false);

  // 批量操作条出现之后，输入框还得完整留在视口里。
  await expectFullyInsideViewport(composer, '底部输入框（批量操作条出现后）', label);
}

/* ------------------------------------------------------------------ *
 * 截图
 * ------------------------------------------------------------------ */

/** e2e 目录（本文件所在目录）。 */
function e2eDir(): string {
  try {
    return dirname(fileURLToPath(import.meta.url));
  } catch {
    return join(process.cwd(), 'e2e');
  }
}

/** 截图的落盘路径：e2e/screenshots/<route>-<viewport>.png。 */
function screenshotPath(routeName: string, viewportName: string): string {
  return join(e2eDir(), 'screenshots', `${routeName}-${viewportName}.png`);
}

/* ------------------------------------------------------------------ *
 * 用例
 * ------------------------------------------------------------------ */

test.describe('排版与响应式', () => {
  for (const route of ROUTES) {
    for (const viewport of VIEWPORTS) {
      test(`${route.name} @ ${viewport.name}`, async ({ page, seed, api }) => {
        const label = `${route.name} @ ${viewport.name}`;

        const path = await route.prepare(seed, api);
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await page.goto(path);
        await expect(route.ready(page)).toBeVisible();

        // 截图先拍：后面的断言红了也能留现场（截图是给人看布局的）。
        // 只拍视口那一屏：文件名就是「路由-视口」，图也正好是那一屏，
        // 跨路由、跨视口能直接对比。整页截图在回收站那种页面上会长到
        // 上万像素（复位留下的残渣条目堆着），没法看。
        await page.screenshot({
          path: screenshotPath(route.name, viewport.name),
          fullPage: false,
        });

        await expectNoHorizontalOverflow(page, label);
        await expectNoUnexpectedHorizontalScroll(page, route.containers(page), label);
        await expectTextNotClipped(page, route.textBlocks(page), label);
        await expectKeyElementsInViewport(page, route.keyElements(page), label);

        if (route.extraChecks) {
          await route.extraChecks(page, label);
        }
      });
    }
  }

  for (const route of ROUTES) {
    test(`移动端点按目标：${route.name} @ ${MOBILE_VIEWPORT.name}`, async ({
      page,
      seed,
      api,
    }) => {
      const path = await route.prepare(seed, api);
      await page.setViewportSize({
        width: MOBILE_VIEWPORT.width,
        height: MOBILE_VIEWPORT.height,
      });
      await page.goto(path);
      await expect(route.ready(page)).toBeVisible();

      const scan = await page.evaluate((min) => {
        // 全部可点击的东西：按钮、链接、表单控件、自定义 role 的控件。
        const selector =
          'button, a[href], input, select, textarea, [role="button"], [role="checkbox"], [role="link"]';
        const found: Array<{ label: string; selector: string; width: number; height: number }> = [];
        let scanned = 0;
        for (const el of Array.from(document.querySelectorAll(selector))) {
          const style = getComputedStyle(el);
          if (style.display === 'none' || style.visibility === 'hidden') {
            continue;
          }
          // 藏起来的（对辅助技术隐藏 / 全透明）不算用户要点的目标。
          if (Number(style.opacity) === 0 || el.closest('[aria-hidden="true"]')) {
            continue;
          }
          const rect = el.getBoundingClientRect();
          if (rect.width === 0 || rect.height === 0) {
            continue;
          }
          scanned += 1;
          if (rect.width >= min && rect.height >= min) {
            continue;
          }
          const classes =
            typeof el.className === 'string' && el.className.trim().length > 0
              ? '.' + el.className.trim().split(/\s+/).join('.')
              : '';
          found.push({
            label: (
              el.getAttribute('aria-label') ||
              el.getAttribute('title') ||
              el.textContent ||
              el.tagName
            )
              .trim()
              .replace(/\s+/g, ' ')
              .slice(0, 40),
            selector: el.tagName.toLowerCase() + classes,
            width: Math.round(rect.width * 10) / 10,
            height: Math.round(rect.height * 10) / 10,
          });
        }
        return { scanned, found };
      }, MIN_TAP_TARGET);

      // 一个可点击元素都没扫到 = 选择器/页面不对，检查是空转的 —— 那不算通过。
      expect(
        scan.scanned,
        `移动端（${MOBILE_VIEWPORT.width}×${MOBILE_VIEWPORT.height}）下 ${route.name} ` +
          `一个可点击元素都没扫到（检查是空转的）`,
      ).toBeGreaterThan(0);

      expect(
        scan.found,
        `移动端（${MOBILE_VIEWPORT.width}×${MOBILE_VIEWPORT.height}）下 ${route.name} ` +
          `有 ${scan.found.length} 个小于 ${MIN_TAP_TARGET}×${MIN_TAP_TARGET} 的可点击元素` +
          `（一共扫了 ${scan.scanned} 个）`,
      ).toEqual([]);
    });
  }

  // ── 新加的元素：点按目标也要够大 ──────────────────────────────────────

  /**
   * 量一个元素的点按目标尺寸。
   *
   * 允许 0.5px 的误差：`min-height: 32px` 在小数像素布局下可能被浏览器算成
   * 31.99 之类（视口缩放、字体度量），那不是真的点不中。
   */
  async function expectTapTarget(
    locator: Locator,
    name: string,
  ): Promise<void> {
    await expect(locator, `「${name}」不在页面上`).toBeVisible();
    const box = await locator.boundingBox();
    expect(box, `量不到「${name}」的位置`).not.toBeNull();
    if (!box) {
      return;
    }
    expect(
      box.width,
      `「${name}」的宽度只有 ${box.width}px，手机上点不中（要 ≥${MIN_TAP_TARGET}px）`,
    ).toBeGreaterThanOrEqual(MIN_TAP_TARGET - 0.5);
    expect(
      box.height,
      `「${name}」的高度只有 ${box.height}px，手机上点不中（要 ≥${MIN_TAP_TARGET}px）`,
    ).toBeGreaterThanOrEqual(MIN_TAP_TARGET - 0.5);
  }

  test('移动端点按目标：项目行的「Webhook」入口够大', async ({ page, seed }) => {
    const project = await seed.project('点按目标探测项目');
    await page.setViewportSize({
      width: MOBILE_VIEWPORT.width,
      height: MOBILE_VIEWPORT.height,
    });
    await page.goto('/projects');

    // 项目级 webhook 页在应用里没有别的入口（只能手敲 URL），
    // 这个链接是唯一的入口 —— 它太小的话，手机上根本进不去。
    const link = page
      .locator(`li[data-project-id="${project.id}"]`)
      .getByRole('link', { name: '项目级 Webhook' });

    await expectTapTarget(link, '项目行的「Webhook」入口');
  });

  test('移动端点按目标：导航条上的「想法」入口够大', async ({ page }) => {
    await page.setViewportSize({
      width: MOBILE_VIEWPORT.width,
      height: MOBILE_VIEWPORT.height,
    });
    await page.goto('/projects');

    // 「想法」是管理页回想法列表的唯一入口（整页上再没有别的链接指回去）。
    const link = page
      .getByRole('navigation', { name: '管理' })
      .getByRole('link', { name: '想法', exact: true });

    await expectTapTarget(link, '导航条上的「想法」入口');
  });

  // ── 底部输入框的边框 ──────────────────────────────────────────────────

  test('底部输入框只有一层边框：外壳不画边框，输入框自己画一层', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('textbox', { name: /记一条想法/ })).toBeVisible();

    // 真浏览器里读得到计算样式（Vitest 的 jsdom 读不到，别学它）。
    const borders = await page.evaluate(() => {
      const composer = document.querySelector('.composer');
      const textarea = document.querySelector('.composer textarea');
      if (!composer || !textarea) {
        return null;
      }
      const read = (el: Element): { widths: string[]; style: string } => {
        const style = getComputedStyle(el);
        return {
          widths: [
            style.borderTopWidth,
            style.borderRightWidth,
            style.borderBottomWidth,
            style.borderLeftWidth,
          ],
          style: style.borderStyle,
        };
      };
      return { composer: read(composer), textarea: read(textarea) };
    });

    expect(borders, '没找到底部输入框（.composer / .composer textarea）').not.toBeNull();
    // 外壳不画边框：不然就是「框里套框」，两层同色圆角框谁也说不清哪层是输入框。
    expect(borders!.composer.widths).toEqual(['0px', '0px', '0px', '0px']);
    // 唯一的一层边框在输入框自己身上。
    expect(borders!.textarea.widths).toEqual(['1px', '1px', '1px', '1px']);
    expect(borders!.textarea.style).toBe('solid');
  });
});
