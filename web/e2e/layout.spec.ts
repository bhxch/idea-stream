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

/**
 * 宽屏视口（两列 + 侧栏）：规格 §9 的验收视口。
 */
const WIDE_VIEWPORT = { width: 1920, height: 1080 };

/**
 * 平板竖屏（两列、无侧栏）：规格 §D9 的 768–1023px 那一档。
 */
const TABLET_VIEWPORT = { width: 768, height: 1024 };

/**
 * 桌面视口（宽屏 + 有悬停能力）：显形规则（规格 §D8）的判据用它 ——
 * 悬停显形 / 聚焦显形都是「桌面端」的行为。
 */
const DESKTOP_VIEWPORT = { width: 1280, height: 800 };

/**
 * 主页一轮造的想法条数。
 *
 * 14 条这个数是算出来的，不是拍脑袋：
 * - 宽屏一屏要能看到 ≥10 条（规格 §9）；
 * - 下面「新记的一条落在最下面」那条用例靠它推编号（14 + 1 = 15 → 奇数位 → 左格）。
 */
const HOME_IDEA_COUNT = 14;

/** 可点击元素的最小尺寸（CSS 像素）。 */
const MIN_TAP_TARGET = 32;

/**
 * 主页的造数据。
 *
 * 每条新用例自己造（用例之间互不依赖，夹具在 beforeEach 里复位），
 * 所以这个函数被主页路由的 prepare 与新用例共用。
 */
async function seedHomeIdeas(seed: Seed): Promise<void> {
  await seed.project('布局测试项目');
  await seed.idea('排版探测想法：第一行\n第二行正文，用来测试正文换行。');
  // 一条特别长的、里面没有空格的正文：窄屏上最容易把容器撑破。
  await seed.idea(
    'SupercalifragilisticexpialidociousSupercalifragilisticexpialidociousSupercalifragilisticexpialidocious',
  );
  await seed.idea('带标签的排版探测想法', { tags: ['排版探测标签'] });
  // 再补 11 条：宽屏一屏要能看到 ≥10 条（规格 §9 的验收标准）。
  for (let index = 4; index <= HOME_IDEA_COUNT; index += 1) {
    await seed.idea(`排版探测想法 ${index}：用来把一屏填满的普通正文。`);
  }
}

/** 在底部输入框里记一条想法。 */
async function submitIdea(page: Page, content: string): Promise<void> {
  await page.getByRole('textbox', { name: /记一条想法/ }).fill(content);
  await page.getByRole('button', { name: '记下' }).click();
}

/* ------------------------------------------------------------------ *
 * 路由
 * ------------------------------------------------------------------ */

/** 要定位的元素：名字（报错时用）+ 定位器。 */
interface Named {
  /** 报错时说明是哪个元素。 */
  name: string;
  locator: Locator;
}

/**
 * 一条想法的编辑态文本块。
 *
 * 「哪一条」得单说：编辑框是**逐条**的（点谁进谁），
 * 不知道要点第几条，就只会量到第一条 —— 其余条目的守卫等于没写。
 */
interface EditingTextBlocks {
  /** 这一条想法在列表里的序号（0 = 第一条）。 */
  ideaIndex: number;
  /** 这一条想法编辑态下要量的块。 */
  blocks: Named[];
}

/** 一个路由的排版检查项。 */
interface RouteSpec {
  /** 截图文件名里的短名（不含路径分隔符）。 */
  name: string;
  /** 造数据并返回要打开的路径。 */
  prepare(seed: Seed, api: IdeaApi): Promise<string>;
  /** 页面「加载好了」的判据：等它出现。 */
  ready(page: Page): Locator;
  /**
   * 关键元素：必须落在视口里（用户不用先滚一下）。
   *
   * 视口宽窄要区分：窄屏（<1024px）过滤行**随内容滚走**，
   * 它仍然存在且可见，但不在视口里 —— 「可见」与「在视口里」是两回事。
   */
  keyElements(page: Page, viewport: { width: number; height: number }): Named[];
  /** 关键容器：不能被内容撑出横向滚动条。 */
  containers(page: Page): Named[];
  /**
   * 关键文本块：文字不能被裁掉。
   *
   * 空数组 = 这个路由没有「非编辑态也必须看全」的文本块
   * （主页的卡片正文会被 3 行截断，见 {@link RouteSpec.textBlocksWhenEditing}）。
   */
  textBlocks(page: Page): Named[];
  /**
   * 只在编辑态里检查「文本没被裁掉」的块（编辑框里必须能看到全文）。
   *
   * 规格 §6.11：新卡片的正文**是**会被 3 行截断的（那是设计），
   * 截断后读全文的唯一入口是「点正文进编辑态」—— 所以这条约束只对编辑态成立。
   *
   * ⚠️ 它是**函数**（跟 {@link RouteSpec.textBlocks} 一样），不是常量数组：
   * 定位器要拿 `page` 才能造，造不出与页面无关的常量数组。
   *
   * ⚠️ 它按**条**分组（{@link EditingTextBlocks}），不是平铺的块列表：
   * 编辑框是逐条的（点谁进谁），不写清「第几条」就只会量到第一条 ——
   * 第二条那些守卫（超长不换行正文）就等于没写。
   */
  textBlocksWhenEditing?: (page: Page) => EditingTextBlocks[];
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
      await seedHomeIdeas(seed);
      return '/';
    },
    // 想法没有标题了：就绪探针取正文里的一个文本。
    ready: (page) => page.getByText('排版探测想法', { exact: false }).first(),
    keyElements: (page, viewport) => [
      { name: '标题「想法」', locator: page.getByRole('heading', { name: '想法', exact: true }) },
      // 窄屏过滤行随内容滚走 → 不在视口里，「关键元素必须在视口里」这条对它不成立。
      // （它仍然存在且可见 —— 别把「可见」与「在视口里」混为一谈。）
      ...(viewport.width >= 1024
        ? [{ name: '状态过滤组', locator: page.getByRole('group', { name: '状态过滤' }) }]
        : []),
      { name: '底部输入框', locator: page.getByRole('textbox', { name: /记一条想法/ }) },
      { name: '「记下」按钮', locator: page.getByRole('button', { name: '记下' }) },
      { name: '顶栏的「⋯」菜单', locator: page.getByRole('button', { name: '更多' }) },
    ],
    containers: (page) => [
      // 消息流的滚动容器、整块内容区、顶栏。
      // 用 BEM class 定位：这些是纯布局容器，没有 role / aria-label 可用。
      { name: '.home__scroll', locator: page.locator('.home__scroll') },
      { name: '.home__content', locator: page.locator('.home__content') },
      { name: '.home__topbar', locator: page.locator('.home__topbar') },
      { name: '.composer', locator: page.locator('.composer') },
    ],
    // 非编辑态没有「必须看全」的文本块：卡片正文按规格 §6.11 被 3 行截断
    // （那是设计，不是排版事故），所以它挪到 textBlocksWhenEditing 里查。
    textBlocks: () => [],
    // 两条都要查（裁决 F2）：
    // - 第一条：普通正文 —— 编辑框里必须能看到全文；
    // - 第二条：102 个不换行字符的长正文 —— 它最容易把编辑框撑破，
    //   「编辑框里也看得下」这条守卫正是为它留的。
    textBlocksWhenEditing: (page) => [
      {
        ideaIndex: 0,
        blocks: [
          {
            name: '编辑态的正文（第一条）',
            locator: page.locator('.idea').nth(0).locator('.editor'),
          },
        ],
      },
      {
        ideaIndex: 1,
        blocks: [
          {
            name: '编辑态的正文（超长不换行那条）',
            locator: page.locator('.idea').nth(1).locator('.editor'),
          },
        ],
      },
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

/**
 * 编辑态里「文本没被裁掉」。
 *
 * 规格 §6.11：新卡片的正文**是**会被 3 行截断的（`-webkit-line-clamp`）——
 * 那是设计，不是排版事故。正文被截断后，读全文的唯一入口是
 * 「点正文进编辑态」，所以「文本没被裁掉」这条约束**只对编辑态成立**。
 *
 * 做法：逐条点开正文进入编辑态（编辑框替身正文），再量编辑框。
 *
 * ⚠️ **逐条**是必须的：编辑框是逐条的（点谁进谁），
 * 一次只量第一条的话，其余条目的守卫（超长不换行正文）就白写了。
 * 每条量完先按 Esc 收起（Esc 是「放弃这次编辑」，不发请求），
 * 免得下一条被上一条的失焦保存带跑。
 */
async function expectTextNotClippedWhileEditing(
  page: Page,
  groups: EditingTextBlocks[],
  label: string,
): Promise<void> {
  for (const group of groups) {
    const item = page.locator('.idea').nth(group.ideaIndex);
    const body = item.locator('.body');
    await expect(
      body,
      `${label}：第 ${group.ideaIndex + 1} 条想法找不到可以点开进入编辑态的正文`,
    ).toBeVisible();
    await body.click();
    // 编辑框是正文那一格的替身：它出现了才算进了编辑态。
    const editor = item.locator('.editor');
    await expect(
      editor,
      `${label}：第 ${group.ideaIndex + 1} 条想法没进编辑态（编辑框没出现）`,
    ).toBeVisible();
    await expectTextNotClipped(page, group.blocks, label);
    // 收起编辑态：Esc 是「放弃这次编辑」（不发请求）。
    await editor.press('Escape');
    await expect(
      item.locator('.editor'),
      `${label}：第 ${group.ideaIndex + 1} 条想法的编辑态没收起`,
    ).toHaveCount(0);
  }
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
 * 一屏里有几个想法（**在视口里**的才算）。
 *
 * ⚠️ 不能用 `page.locator('.idea:visible').count()`：
 * `:visible` 只说明「没被 `display:none` 藏起来」，屏幕外但没藏的卡片照样数得到 ——
 * 「一屏能看到几条」问的是「用户不用滚就能看到几条」。
 */
async function countIdeasInViewport(page: Page): Promise<number> {
  const viewport = page.viewportSize();
  expect(viewport, '拿不到视口尺寸').not.toBeNull();
  if (!viewport) {
    return 0;
  }
  const boxes = await page.locator('.idea').evaluateAll((elements) =>
    elements.map((el) => {
      const rect = el.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
    }),
  );
  return boxes.filter((box) => intersectsViewport(box, viewport)).length;
}

/**
 * 读一个元素的不透明度（显形规则就靠它）。
 *
 * 真浏览器里读得到计算样式（Vitest 的 jsdom 读不到，别学它）。
 */
async function readOpacity(locator: Locator): Promise<number> {
  const value = await locator.evaluate((el) => Number(getComputedStyle(el).opacity));
  return value;
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
        // 非编辑态的文本块：卡片正文被 3 行截断（规格 §6.11），主页这一档是空的。
        const textBlocks = route.textBlocks(page);
        if (textBlocks.length > 0) {
          await expectTextNotClipped(page, textBlocks, label);
        }
        await expectKeyElementsInViewport(page, route.keyElements(page, viewport), label);

        // 编辑态的文本块：截断后读全文的唯一入口就是编辑框（规格 §6.11）。
        // 放在关键元素检查之后 —— 点正文会把列表滚上去，别影响前面的视口断言。
        const editingGroups = route.textBlocksWhenEditing?.(page) ?? [];
        if (editingGroups.length > 0) {
          await expectTextNotClippedWhileEditing(page, editingGroups, label);
        }

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

  // ── 长项目名：窄屏顶栏不能被它撑破 ──────────────────────────────────────

  test('窄屏：长项目名不撑破顶栏（触发器截断、不压到动作区）', async ({ page, seed }) => {
    // 一个很长的项目名：触发器里放不下（窄屏顶栏只有一行）。
    const longName = '这是一个特别长的项目名字它长得足以把窄屏顶栏整个撑破看看会不会溢出';
    await seed.project(longName);
    await page.setViewportSize({
      width: MOBILE_VIEWPORT.width,
      height: MOBILE_VIEWPORT.height,
    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: '想法', exact: true })).toBeVisible();

    // 切到那个项目：窄屏用顶栏里的下拉切（侧栏在窄屏是藏着的）。
    await page.locator('.project-dropdown__trigger').click();
    const panel = page.locator('.project-dropdown__panel');
    await expect(panel).toBeVisible();
    // 面板本身也不能超出视口（长名字在面板里同样要截断）。
    const panelBox = await panel.boundingBox();
    expect(panelBox, '量不到项目下拉面板的位置').not.toBeNull();
    expect(
      panelBox!.x + panelBox!.width,
      `项目下拉面板的右边缘（${panelBox!.x + panelBox!.width}px）超出了视口（${MOBILE_VIEWPORT.width}px）`,
    ).toBeLessThanOrEqual(MOBILE_VIEWPORT.width);

    await panel.getByRole('button', { name: longName }).click();

    // 触发器上现在是那个长名字：它必须被截断，且不许压到动作区（🔍 与 ⋯）。
    const trigger = page.locator('.project-dropdown__trigger');
    const actions = page.locator('.home__topbar-actions');
    const triggerBox = await trigger.boundingBox();
    const actionsBox = await actions.boundingBox();
    expect(triggerBox, '量不到项目下拉触发器的位置').not.toBeNull();
    expect(actionsBox, '量不到顶栏动作区的位置').not.toBeNull();

    expect(
      triggerBox!.x + triggerBox!.width,
      `项目下拉触发器的右边缘（${triggerBox!.x + triggerBox!.width}px）超出了视口（${MOBILE_VIEWPORT.width}px）`,
    ).toBeLessThanOrEqual(MOBILE_VIEWPORT.width);
    expect(
      triggerBox!.x + triggerBox!.width,
      '项目下拉触发器压到了顶栏动作区（🔍 / ⋯）上',
    ).toBeLessThanOrEqual(actionsBox!.x);

    // 名字确实被截断了（否则上面两条断言是空转的）。
    const current = await page.locator('.project-dropdown__current').evaluate((el) => ({
      scrollWidth: el.scrollWidth,
      clientWidth: el.clientWidth,
      textOverflow: getComputedStyle(el).textOverflow,
    }));
    expect(current.textOverflow).toBe('ellipsis');
    expect(
      current.scrollWidth,
      '项目名没有被截断（触发器比视口还宽的话，断言是空转的）',
    ).toBeGreaterThan(current.clientWidth);
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

  // ── 新外壳：宽屏铺满、两列交错、窄屏单列 ──────────────────────────────

  /**
   * 造主页的数据并打开主页。
   *
   * ⚠️ **每条用例自己造数据**：这些用例不在「路由 × 视口」那个循环里，
   * 不会自动拿到 prepare 造的数据。
   *
   * ⚠️ **必须等列表出现**：想法列表是挂载后**异步**从服务端拉来的，
   * `page.goto()` 只等 load 事件 —— 不等就去量，量到的可能是空的列表（假红）。
   */
  async function openHome(
    page: Page,
    seed: Seed,
    viewport: { width: number; height: number },
  ): Promise<void> {
    await seedHomeIdeas(seed);
    await page.setViewportSize(viewport);
    await page.goto('/');
    await expect(page.getByRole('heading', { name: '想法', exact: true })).toBeVisible();
    await expect(page.locator('.idea')).toHaveCount(HOME_IDEA_COUNT);
  }

  test('宽屏：侧栏贴左，主区铺到右边缘', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    const sidebar = await page.locator('.home__sidebar').boundingBox();
    expect(sidebar, '量不到侧栏的位置').not.toBeNull();
    expect(sidebar!.x, `侧栏左边距 ${sidebar!.x}px（规格 §8：侧栏贴左边缘）`).toBeLessThanOrEqual(2);

    const content = await page.locator('.home__content').boundingBox();
    expect(content, '量不到主区的位置').not.toBeNull();
    const rightGap = WIDE_VIEWPORT.width - (content!.x + content!.width);
    expect(
      rightGap,
      `主区离视口右边缘还有 ${rightGap}px（规格 §9：主区铺到右边缘）`,
    ).toBeLessThanOrEqual(24);
  });

  test('宽屏：一屏能看到 ≥10 条想法', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    const count = await countIdeasInViewport(page);
    expect(
      count,
      `宽屏一屏只看到 ${count} 条想法（规格 §9：一屏 ≥10 条）`,
    ).toBeGreaterThanOrEqual(10);
  });

  test('宽屏：想法排成两列', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    const row = page.locator('.row').first();
    const left = await row.locator('.cell--left .idea').boundingBox();
    const right = await row.locator('.cell--right .idea').boundingBox();
    expect(left, '量不到第一排左格的位置').not.toBeNull();
    expect(right, '量不到第一排右格的位置').not.toBeNull();

    const viewport = page.viewportSize()!;
    expect(intersectsViewport(left!, viewport), '第一排左格不在视口里').toBe(true);
    expect(intersectsViewport(right!, viewport), '第一排右格不在视口里').toBe(true);
    expect(left!.x, '第一排左右格不在两列上').toBeLessThan(right!.x);
  });

  test('平板 768×1024：两列、无侧栏', async ({ page, seed }) => {
    await openHome(page, seed, TABLET_VIEWPORT);

    // 规格 §D9：768–1023px → 两列、无侧栏。
    await expect(page.locator('.home__sidebar'), '平板（<1024px）不该有侧栏').toBeHidden();

    const row = page.locator('.row').first();
    const left = await row.locator('.cell--left .idea').boundingBox();
    const right = await row.locator('.cell--right .idea').boundingBox();
    expect(left, '量不到第一排左格的位置').not.toBeNull();
    expect(right, '量不到第一排右格的位置').not.toBeNull();

    const viewport = page.viewportSize()!;
    expect(intersectsViewport(left!, viewport), '第一排左格不在视口里').toBe(true);
    expect(intersectsViewport(right!, viewport), '第一排右格不在视口里').toBe(true);
    expect(left!.x, '平板不是两列').toBeLessThan(right!.x);
  });

  test('窄屏 <768px：单列', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    const row = page.locator('.row').first();
    // 每一格都得有想法：「单列」是「排成一条竖列」，不是「只显示一半」。
    await expect(row.locator('.cell--left .idea')).toHaveCount(1);
    await expect(row.locator('.cell--right .idea')).toHaveCount(1);

    const left = await row.locator('.cell--left .idea').boundingBox();
    const right = await row.locator('.cell--right .idea').boundingBox();
    // 被 display:none 藏起来的元素量不到位置（boundingBox 是 null）。
    expect(left, '量不到第一排左格想法的位置').not.toBeNull();
    expect(right, '量不到第一排右格想法的位置').not.toBeNull();
    expect(left!.x, `窄屏不是单列：两格的 x 不同（${left!.x} vs ${right!.x}）`).toBe(right!.x);
    expect(left!.y, '窄屏不是单列：两格上下叠着才对').not.toBe(right!.y);
  });

  test('窄屏：没有想法被藏起来', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    // ⚠️ 不能用 `page.locator('.idea').count()`：Playwright 的 count() 数的是
    // **匹配到的 DOM 节点**、不看可见性 —— 用 display:none 藏起来的照样数得到，
    // 那个错法下它仍然是 14、照样绿。
    await expect(page.locator('.idea:visible')).toHaveCount(HOME_IDEA_COUNT);
  });

  test('窄屏：钉死的外壳只有 2 行', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    // 钉死的两行：顶栏 + 输入条。
    await expect(page.locator('.home__topbar'), '窄屏的顶栏不见了').toBeVisible();
    await expect(page.locator('.composer'), '窄屏的输入条不见了').toBeVisible();

    // 过滤行随内容滚走（规格 §D7）：滚到底之后它整个跑到视口上方。
    await page.locator('.home__scroll').evaluate((el) => {
      el.scrollTop = el.scrollHeight;
    });
    const filters = await page.locator('.home__filters').boundingBox();
    expect(filters, '量不到过滤行的位置').not.toBeNull();
    const viewport = page.viewportSize()!;
    // ⚠️ 判据是「矩形与视口不相交」，不是「y > 667」——
    // 过滤行滚走之后它的 y 是负数（整个在视口上方）。
    expect(
      intersectsViewport(filters!, viewport),
      '过滤行还在视口里（它应该随内容滚走）',
    ).toBe(false);
  });

  test('窄屏：一屏能看到 ≥5 条想法', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    const count = await countIdeasInViewport(page);
    expect(
      count,
      `窄屏一屏只看到 ${count} 条想法（规格 §9：一屏 ≥5 条）`,
    ).toBeGreaterThanOrEqual(5);
  });

  test('卡片只有一整块正文，没有标题层', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    await expect(page.locator('.idea h2.title'), '卡片里还有标题层').toHaveCount(0);
    await expect(page.locator('.idea .title'), '卡片里还有标题层').toHaveCount(0);
  });

  test('编号紧挨时间', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    const num = await page.locator('.idea .meta__num').first().boundingBox();
    const time = await page.locator('.idea .meta__time').first().boundingBox();
    expect(num, '量不到编号的位置').not.toBeNull();
    expect(time, '量不到时间的位置').not.toBeNull();
    expect(num!.x, '编号不在时间左边').toBeLessThan(time!.x);
    const gap = time!.x - (num!.x + num!.width);
    expect(gap, `编号与时间之间隔了 ${gap}px（规格 §D3：紧挨着）`).toBeLessThanOrEqual(20);
  });

  test('主页顶栏的「⋯」能直接进回收站', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    await page.getByRole('button', { name: '更多' }).click();
    await page.getByRole('button', { name: '回收站' }).click();
    await expect(page).toHaveURL(/\/trash$/);
  });

  test('窄屏点 🔍 滚回列表顶部并聚焦搜索框', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    // 先滚到底（过滤行已经滚走了）。
    await page.locator('.home__scroll').evaluate((el) => {
      el.scrollTop = el.scrollHeight;
    });

    await page.getByRole('button', { name: '搜索' }).click();

    const active = await page.evaluate(
      () => document.activeElement?.getAttribute('aria-label') ?? null,
    );
    expect(active, '点 🔍 之后焦点没落在搜索框上').toBe('搜索想法');

    const scrollTop = await page.locator('.home__scroll').evaluate((el) => el.scrollTop);
    expect(scrollTop, '点 🔍 之后没有滚回列表顶部').toBe(0);
  });

  test('桌面端：勾选框与 🏷🗑 悬停才显形，键盘聚焦也显形', async ({ page, seed }) => {
    await openHome(page, seed, DESKTOP_VIEWPORT);

    const select = page.locator('.idea .select').first();
    const action = page.locator('.idea .meta__action').first();

    // 默认（不悬停）：全透明。
    expect(await readOpacity(select), '桌面端不悬停时勾选框不是隐藏的').toBe(0);
    expect(await readOpacity(action), '桌面端不悬停时 🏷🗑 不是隐藏的').toBe(0);

    // 悬停那条卡片：显形。
    await page.locator('.idea').first().hover();
    await expect.poll(() => readOpacity(select), '悬停后勾选框没有显形').toBe(1);
    await expect.poll(() => readOpacity(action), '悬停后 🏷🗑 没有显形').toBe(1);

    // 鼠标移开：又藏回去。
    await page.mouse.move(0, 0);
    await expect.poll(() => readOpacity(select), '鼠标移开后勾选框还亮着').toBe(0);

    // 键盘聚焦到它：也得显形 —— 悬停才显形的按钮不能是某个操作的「唯一入口」。
    await select.focus();
    await expect.poll(() => readOpacity(select), '聚焦后勾选框没有显形').toBe(1);
    await expect.poll(() => readOpacity(action), '聚焦后 🏷🗑 没有显形').toBe(1);
  });

  test('窄屏：勾选框与 🏷🗑 常驻', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    expect(
      await readOpacity(page.locator('.idea .select').first()),
      '窄屏的勾选框不是常驻的',
    ).toBe(1);
    expect(
      await readOpacity(page.locator('.idea .meta__action').first()),
      '窄屏的 🏷🗑 不是常驻的',
    ).toBe(1);
  });

  test('无悬停能力的设备：勾选框与 🏷🗑 常驻', async ({ browser, baseURL, seed }) => {
    await seed.idea('无悬停能力设备的探测想法');

    // 判据是「这台设备有没有悬停能力」，不是屏宽（规格 §D8）：
    // 另开一个带触摸能力的 context，1280 宽也照样常驻。
    const context = await browser.newContext({
      baseURL,
      viewport: { width: DESKTOP_VIEWPORT.width, height: DESKTOP_VIEWPORT.height },
      hasTouch: true,
      locale: 'zh-CN',
    });
    const page = await context.newPage();
    try {
      await page.goto('/');
      await expect(page.locator('.idea').first()).toBeVisible();

      const hoverNone = await page.evaluate(() => window.matchMedia('(hover: none)').matches);
      expect(hoverNone, '这个 context 没有模拟出「没有悬停能力」').toBe(true);

      expect(
        await readOpacity(page.locator('.idea .select').first()),
        '没有悬停能力的设备上勾选框不是常驻的',
      ).toBe(1);
      expect(
        await readOpacity(page.locator('.idea .meta__action').first()),
        '没有悬停能力的设备上 🏷🗑 不是常驻的',
      ).toBe(1);
    } finally {
      await context.close();
    }
  });

  test('窄屏：顶栏的新触发器够大', async ({ page, seed }) => {
    await openHome(page, seed, MOBILE_VIEWPORT);

    await expectTapTarget(page.getByRole('button', { name: '更多' }), '顶栏的「⋯」菜单');
    await expectTapTarget(page.getByRole('button', { name: '搜索' }), '顶栏的「🔍」');
    await expectTapTarget(page.locator('.project-dropdown__trigger'), '顶栏的项目下拉触发器');
  });

  test('宽屏：新记的一条落在最下面，且左右按奇偶交替', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    // 这一轮已造 14 条 → 第 15 条是**奇数位** → 左格。
    await submitIdea(page, '新记的第一条想法');
    const lastRow = page.locator('.row').last();
    await expect(lastRow.locator('.cell--left .idea .meta__num')).toHaveText('15');
    await expect(lastRow.locator('.cell--right .idea')).toHaveCount(0);

    // 第 16 条是**偶数位** → 右格（与它挨着的那条一起排在最后一行）。
    await submitIdea(page, '新记的第二条想法');
    await expect(lastRow.locator('.cell--left .idea .meta__num')).toHaveText('15');
    await expect(lastRow.locator('.cell--right .idea .meta__num')).toHaveText('16');
  });

  test('宽屏：编辑一条靠前的想法，它跳到最下面，其下方卡片左右互换', async ({ page, seed }) => {
    await openHome(page, seed, WIDE_VIEWPORT);

    const row1 = page.locator('.row').first();
    const left = row1.locator('.cell--left .idea');
    const right = row1.locator('.cell--right .idea');
    await expect(left.locator('.meta__num')).toHaveText('1');
    await expect(right.locator('.meta__num')).toHaveText('2');

    // 记住「原来在它下面那条」（第一排右格那条）的位置 —— 编辑之后它要换到左边。
    const xBefore = (await right.boundingBox())!.x;

    // 编辑第一条并保存。
    await left.locator('.body').click();
    const editor = left.locator('.editor');
    await expect(editor, '没进编辑态').toBeVisible();
    await editor.fill('改过正文的第一条想法');
    await editor.press('Control+Enter');

    // 它跳到流的最下边（排序是 updated_at ASC，改过的那条最新）→ 14 条里的第 14 位。
    await expect(page.locator('.idea').last().locator('.meta__num')).toHaveText('14');
    await expect(page.locator('.idea').last().locator('.body')).toHaveText('改过正文的第一条想法');

    // 原来在它下面那条上移一格 → 奇偶翻转 → 左右互换：
    // 它从右格换到了左格（x 变小）。
    // ⚠️ 它的**编号**也跟着变（编号是位置，不是身份：规格 §D4）——
    // 所以这里比的是 x，不是编号。
    // ⚠️ 这是规格 §9 明确批准的取舍（§D6），不是 bug。
    const moved = page.locator('.idea').filter({ hasText: 'Supercalifragilistic' });
    await expect(moved, '原来在它下面那条不见了').toHaveCount(1);
    const xAfter = (await moved.boundingBox())!.x;
    expect(xAfter, '原来在它下面那条没有左右互换（x 没变）').toBeLessThan(xBefore);
    await expect(
      page.locator('.row').first().locator('.cell--left .idea').filter({ hasText: 'Supercalifragilistic' }),
      '原来在它下面那条没有换到左格',
    ).toHaveCount(1);
  });

  test('管理页铺满，Webhook 表单保持窄', async ({ page }) => {
    // 这条量的是**容器**的宽度，不需要数据（造了也没人断言它 → 死数据）。
    await page.setViewportSize(DESKTOP_VIEWPORT);

    for (const path of ['/trash', '/projects']) {
      await page.goto(path);
      const box = await page.locator('.page').boundingBox();
      expect(box, `量不到 ${path} 的 .page 的位置`).not.toBeNull();
      const rightGap = DESKTOP_VIEWPORT.width - (box!.x + box!.width);
      expect(rightGap, `${path} 没铺满（右边还空着 ${rightGap}px）`).toBeLessThanOrEqual(24);
    }

    await page.goto('/webhooks');
    const narrow = await page.locator('.page--narrow').boundingBox();
    expect(narrow, '量不到 Webhook 表单容器的位置').not.toBeNull();
    expect(narrow!.width, 'Webhook 表单没有保持窄').toBeLessThanOrEqual(720);
  });
});
