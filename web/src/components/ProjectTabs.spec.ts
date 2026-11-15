import { describe, expect, it, afterEach } from 'vitest';
import { mount } from '@vue/test-utils';
import type { Project } from '../api/types';
import ProjectTabs from './ProjectTabs.vue';

/** 两个项目。 */
const projects: Project[] = [
  {
    id: 'project-1',
    name: '老项目',
    sortOrder: 0,
    ideaCount: 3,
    lastActivityAt: '2026-11-01T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-01T10:00:00Z',
  },
  {
    id: 'project-2',
    name: '新项目',
    sortOrder: 1,
    ideaCount: 7,
    lastActivityAt: '2026-11-10T10:00:00Z',
    createdAt: '2026-11-01T10:00:00Z',
    updatedAt: '2026-11-10T10:00:00Z',
  },
];

/** 挂一排 Tab。 */
function mountTabs(overrides: Record<string, unknown> = {}) {
  return mount(ProjectTabs, {
    props: {
      projects,
      currentId: null,
      mode: 'recent' as const,
      ...overrides,
    },
  });
}

/** 假想的排版：容器 x=12..279（宽 267），内容宽 276 —— 横向溢出 9px。 */
const CONTAINER_BOX = { left: 12, right: 279, width: 267, height: 40 };

/**
 * 补上 Range 的排版接口。
 *
 * jsdom 没有排版引擎，`Range.prototype.getBoundingClientRect` 压根不存在，
 * 组件量不到字形的位置就会退化成「不渐隐」。
 * 这里按测试数据手工给每个字一个矩形。
 */
function stubCharRects(rects: Record<string, [number, number]>): void {
  Range.prototype.getBoundingClientRect = function (this: Range): DOMRect {
    const ch = (this.startContainer.textContent ?? '')[this.startOffset] ?? '';
    const [left, right] = rects[ch] ?? [0, 0];
    return {
      x: left,
      y: 0,
      left,
      top: 0,
      right,
      bottom: 12,
      width: right - left,
      height: 12,
      toJSON: () => ({}),
    } as DOMRect;
  };
}

/**
 * 挂一个横向溢出了的 Tab 条，并触发一次测量。
 *
 * 溢出量照实况来：容器 267px 宽，内容 276px 宽，只溢出 9px。
 */
async function mountScrollableTabs(chars: Record<string, [number, number]>) {
  stubCharRects(chars);
  const wrapper = mountTabs();
  const list = wrapper.get('.project-tabs__list').element as HTMLElement;
  Object.defineProperty(list, 'scrollWidth', { value: 276, configurable: true });
  Object.defineProperty(list, 'clientWidth', { value: 267, configurable: true });
  list.getBoundingClientRect = () =>
    ({
      ...CONTAINER_BOX,
      x: CONTAINER_BOX.left,
      y: 0,
      top: 0,
      bottom: CONTAINER_BOX.height,
      toJSON: () => ({}),
    }) as DOMRect;
  await list.dispatchEvent(new Event('scroll'));
  await wrapper.vm.$nextTick();
  return { wrapper, list };
}

/** 渐隐带宽度（px）。 */
function fadeWidthOf(list: HTMLElement): number {
  return Number.parseFloat(list.style.getPropertyValue('--tabs-fade'));
}

afterEach(() => {
  // jsdom 里这个方法本来不存在，用完还回去。
  delete (Range.prototype as unknown as { getBoundingClientRect?: unknown }).getBoundingClientRect;
});

describe('ProjectTabs', () => {
  it('渲染项目名与想法条数', () => {
    const wrapper = mountTabs();

    expect(wrapper.text()).toContain('老项目');
    expect(wrapper.text()).toContain('新项目');
    expect(wrapper.get('[data-project-id="project-1"] .project-tab__count').text()).toBe('3');
    expect(wrapper.get('[data-project-id="project-2"] .project-tab__count').text()).toBe('7');
  });

  it('「全部」单独一项，条数是各项目条数之和', () => {
    const wrapper = mountTabs();

    const all = wrapper.get('[data-project-id="all"]');
    expect(all.text()).toContain('全部');
    expect(all.get('.project-tab__count').text()).toBe('10');
  });

  it('点 Tab 发出 select（带项目 id），当前项目标出来', () => {
    const wrapper = mountTabs({ currentId: 'project-2' });

    expect(wrapper.get('[data-project-id="project-2"]').classes()).toContain('is-active');
    expect(wrapper.get('[data-project-id="project-1"]').classes()).not.toContain('is-active');

    void wrapper.get('[data-project-id="project-1"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([['project-1']]);
  });

  it('当前 Tab 带 aria-current="page"（辅助技术读得出当前在哪）', () => {
    const wrapper = mountTabs({ currentId: 'project-2' });

    expect(wrapper.get('[data-project-id="project-2"]').attributes('aria-current')).toBe('page');
    // 别的 Tab 不带：aria-current 是「当前这一项」，不是「这一类」。
    expect(wrapper.get('[data-project-id="project-1"]').attributes('aria-current')).toBeUndefined();
    expect(wrapper.get('[data-project-id="all"]').attributes('aria-current')).toBeUndefined();
  });

  it('当前是「全部」时，标在「全部」上', () => {
    const wrapper = mountTabs({ currentId: null });

    expect(wrapper.get('[data-project-id="all"]').attributes('aria-current')).toBe('page');
    expect(wrapper.get('[data-project-id="project-1"]').attributes('aria-current')).toBeUndefined();
  });

  it('项目名元素带稳定的 data-testid（用例不用靠 class 定位）', () => {
    const wrapper = mountTabs();

    expect(wrapper.get('[data-testid="project-tabs-name-all"]').text()).toBe('全部');
    expect(wrapper.get('[data-testid="project-tabs-name-project-1"]').text()).toBe('老项目');
    expect(wrapper.get('[data-testid="project-tabs-name-project-2"]').text()).toBe('新项目');
  });

  it('Tab 条 nav 有稳定的 aria-label="项目切换"（不随排序方式变）', () => {
    const recent = mountTabs({ mode: 'recent' });
    const manual = mountTabs({ mode: 'manual' });

    // 与宽屏侧栏的 nav[aria-label="项目"] **刻意不同名**：两套导航同时在 DOM 里
    // （一套用 CSS 隐藏），同名会让读屏听到两个同名地标，也让按 nav[aria-label=项目]
    // 定位的选择器同时命中两处（Playwright strict mode 直接报撞车）。
    expect(recent.get('nav').attributes('aria-label')).toBe('项目切换');
    expect(manual.get('nav').attributes('aria-label')).toBe('项目切换');
  });

  it('渐隐带只盖住「文字到容器右边缘」的空隙，不盖住还看得清的字', async () => {
    // 实况：容器 x=12..279，最后一个胶囊的条数「7」在 270..277 —— 完整露出，
    // 右边只剩 2px 空隙。写死 16px 的渐隐带会盖到 263..279，把「7」整个淡掉
    // （对比度从 5.3:1 掉到 3.5:1，AA 要 4.5:1）。
    const { list } = await mountScrollableTabs({
      '7': [270, 277],
      '目': [240, 247],
      '项': [233, 240],
      '新': [226, 233],
    });

    const fade = fadeWidthOf(list);
    expect(fade).toBe(2);
    // 渐隐带 = 277..279：既不碰「7」，也不碰更左边的字。
    expect(CONTAINER_BOX.right - fade).toBeGreaterThanOrEqual(277);
    // 旧写法是写死的 16px。
    expect(fade).not.toBe(16);
    // 量出宽度了才加遮罩：0 宽的渐变会把整条内容淡没了。
    expect(list.classList.contains('is-fading')).toBe(true);
  });

  it('文字被切掉时，渐隐带盖住被切的那半截，但不碰完整露出的字', async () => {
    // 实况：最后一个条数「7」在 274..281 —— 被容器右边缘（279）切了一半。
    const { list } = await mountScrollableTabs({
      '7': [274, 281],
      '目': [240, 247],
      '项': [233, 240],
      '新': [226, 233],
    });

    const fade = fadeWidthOf(list);
    const bandLeft = CONTAINER_BOX.right - fade;
    // 被切的「7」（274 起）在渐隐带里。
    expect(bandLeft).toBeLessThanOrEqual(274);
    // 完整露出的「目」（247 结束）在渐隐带外。
    expect(bandLeft).toBeGreaterThan(247);
  });

  it('没溢出就不渐隐（不往内容上盖遮罩）', () => {
    const wrapper = mountTabs();

    expect(wrapper.get('.project-tabs__list').classes()).not.toContain('is-scrollable');
    expect(wrapper.get('.project-tabs__list').classes()).not.toContain('is-fading');
  });

  it('点「全部」发出 select（带 null）', () => {
    const wrapper = mountTabs();

    void wrapper.get('[data-project-id="all"]').trigger('click');
    expect(wrapper.emitted('select')).toEqual([[null]]);
  });

  it('「新建项目」发出 new-project', () => {
    const wrapper = mountTabs();

    void wrapper.get('.project-tabs__new').trigger('click');
    expect(wrapper.emitted('new-project')).toHaveLength(1);
  });

  it('「更多」发出 open-menu', () => {
    const wrapper = mountTabs();

    void wrapper.get('.project-tabs__menu').trigger('click');
    expect(wrapper.emitted('open-menu')).toHaveLength(1);
  });
});
