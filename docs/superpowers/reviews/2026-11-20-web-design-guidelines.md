# 复核：Vercel `web-design-guidelines` 清单逐条过一遍

- **为什么有这份文档**：规格 §9「回归」最后一条与实施计划「收尾」第 1 步都写了
  「交付前按 Vercel `web-design-guidelines` 清单逐条过一遍」——
  这件事整条分支此前**从未做过**（43 个提交里没有一条与它相关）。
  这份文档是那次复核的**书面结论**。
- **清单来源**：<https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md>
  （2026-11-20 抓取；`~/.agents/skills/web-design-guidelines` 的 SKILL.md 要求「每次复核前重新抓一遍」）。
- **复核范围**：`web/src`（页面、组件、样式、入口）。重点是这次布局重设计动过的那几样：
  对比度（新增的 `.meta__num` 圆底徽标用 `--chip-bg`）、焦点可见（新增的悬停显形控件）、
  触摸目标（断点 640→767、新增三处 32×32）、横向滚动（顶栏与长项目名）、
  语义色（`.meta__action--delete` 用 `--danger`）。
- **结论**：**通过**（不通过的 12 条已修，见「三、这一轮修了什么」；
  接受的偏差 8 条，都有理由，见「四、明确接受的偏差」）。

---

## 一、逐条结论

图例：✅ 通过　🔧 不通过 → 这一轮已修　📌 接受偏差（附理由，见第四节）

### Accessibility

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Icon-only buttons require `aria-label` | 🔧 | `ProjectSidebar.vue` 的「＋」原来只有 `title` → 已补 `aria-label="新建项目"`。其余符号按钮（🏷 🗑 ○ 🔍 ⋯ ▾）本来就有 |
| Form controls require `<label>` or `aria-label` | 🔧 | 三个输入框原来只有 placeholder（编辑正文那个连 placeholder 都没有）→ 已补 `aria-label`。其余本来就合格：搜索框有 `aria-label`、标签过滤的 checkbox 包在 `<label>` 里、Webhook 表单用 `label for` |
| Interactive elements require keyboard handlers | ✅ | 交互元素都是原生 `button` / `input` / `a`，键盘天然可用。唯一的例外是项目拖拽排序（📌） |
| Use `<button>` for actions and `<a>` for navigation | 📌 | `MoreMenu.vue` 用 button + `router.push`（📌） |
| Images require `alt` | — | 应用里没有图片 |
| Decorative icons require `aria-hidden="true"` | ✅ | `ProjectDropdown.vue:55`、`FilterBar.vue:132` 的 ▾、`HomePage.vue:1271` 的哨兵都标了 |
| Async updates require `aria-live="polite"` | 🔧 | 「↓ 有 N 条新内容」那个按钮是条件渲染的，冒出来时读屏未必会念 → 已补一个常驻的 `aria-live="polite"` 活区（**不写 `role="status"`**：那个 role 在本应用里是「全局提示条 ToastHost」的钩子，e2e 与 README 都按它定位）。`ToastHost.vue:40` 本来就有 |
| Prefer semantic HTML over ARIA | ✅ | 地标用 `nav` / `main` / `aside` / `header`，ARIA 只在补语义时用 |
| Maintain hierarchical headings and provide a skip link | 🔧 | 五个页面各有 `h1`、侧栏 `nav` 有 `aria-label`；但**没有跳到主内容的链接** → 已补（`App.vue` 的 `.skip-link` + 五个页面 `<main>` 的 `id="main-content"` / `tabindex="-1"`） |
| Heading anchors require `scroll-margin-top` | — | 没有标题锚点 |
| Media / captions / media controls | — | 没有媒体 |

### Focus States

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Interactive elements need visible focus, preferably `focus-visible` | 🔧 | `Composer.vue` 原来有 `.input:focus { outline: none }`（scoped，权重比全局那条高）→ 把全局的 `:focus-visible` 盖掉了，键盘 Tab 到输入框上一点痕迹都没有 → 已删 |
| Never remove outlines without a replacement | 🔧 | 同上 |
| Prefer `:focus-visible` over `:focus` | ✅ | 全局只有 `:focus-visible`（`styles/base.css:84`） |
| Compound controls may use `:focus-within` | ✅ | `MessageItem.vue` 的显形规则用了 `:focus-within` |
| Sticky or fixed UI must not obscure focused elements | 🔧 | 宽屏上过滤行是 sticky 的：Tab 到被它盖住的卡片时，浏览器把它滚到视口顶上、正好压在过滤行底下 → 已给卡片加 `scroll-margin-top` |

### Forms

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Inputs need `autocomplete` and meaningful `name` | 📌 | 应用是本地单用户 SPA，没有账号信息、也不提交传统表单（📌） |
| Use appropriate input types and `inputmode` | ✅ | 搜索框 `type="search"`、标签框 `type="text"` |
| Never block pasting | ✅ | 没有 `paste` 拦截 |
| Labels must be clickable through `htmlFor` or wrapping | ✅ | `WebhookForm.vue` 用 `label for`；`TagFilter.vue` 的 checkbox 包在 `<label>` 里 |
| Disable spellcheck for emails, codes, usernames | — | 没有这类输入 |
| Checkboxes and radios share one label/control hit target | 📌 | 想法条目上的勾选框是独立 `input`（`aria-label="选择 …"`），点按目标就是那 13×13 的原生方块（📌） |
| Keep submit buttons enabled until submission begins; show progress | ✅ | 「记下」在请求在飞时禁用（`Composer.vue` 的 `submitting`） |
| Show inline errors and focus the first error | 📌 | 错误走全局提示条 Toast（📌） |
| Placeholders end with an ellipsis and show an example | ✅ | 「搜索想法…」「加标签…」「记一条想法…（Enter 提交，Shift+Enter 换行）」 |
| `autocomplete="off"` on non-auth fields | 📌 | 同「autocomplete」那条 |
| Warn before navigating away with unsaved changes | 📌 | 标签编辑器有「还有未保存的改动」确认；底部输入框的草稿没有（📌） |

### Animation

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Respect `prefers-reduced-motion` | ✅ | `styles/base.css` 有全局的 reduce 规则（动画与过渡都压到 0.01ms） |
| Animate only `transform` and `opacity` | 📌 | 深链定位的高亮闪的是 `background`（1.5s，📌） |
| Avoid `transition: all` | ✅ | 全库 grep 无 `transition: all` |
| Set an appropriate `transform-origin` | ✅ | 只有「↓ 有 N 条新内容」的 `translateX(-50%)`，与 origin 无关 |
| SVG transformations / `transform-box` | — | 没有 SVG |
| Animations should be interruptible | ✅ | 过渡只有 opacity，可随时打断 |
| Long autoplay motion / muted decorative loops | — | 没有自动播放的动画 |

### Typography

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Use the ellipsis character rather than three periods | ✅ | 全库是「…」，没有「...」 |
| Use curly quotation marks | — | 中文界面 |
| Non-breaking spaces for measurements | — | 没有这类文本 |
| Loading text ends with an ellipsis | ✅ | 「加载中…」「加载更早的…」「刷新中…」 |
| Use tabular numerals for numeric columns and comparisons | 🔧 | 编号（`.meta__num`）、侧栏条数、标签条数原来不是等宽数字，位数不同排下来是「跳」的 → 已加 `font-variant-numeric: tabular-nums` |
| Balance or prettify heading text | — | 标题都是一两个词 |

### Content Handling

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Text containers accommodate long content | ✅ | 正文 `-webkit-line-clamp: 3`；长项目名 `overflow-wrap: anywhere` + `text-overflow: ellipsis`；`.project-dropdown__current` 是块级盒（行内盒上 `text-overflow` 不生效） |
| Flex children need `min-w-0` | ✅ | `.home__topbar-project`、`.cell`、`.meta__tags`、`.filter-bar__search` 等都写了 `min-width: 0` |
| Handle empty strings and arrays gracefully | ✅ | 空列表有「这里还没有想法。」、标签下拉有「还没有标签」 |
| Design for short, average and very long content | ✅ | e2e 有专门的长正文用例（`layout.spec.ts` 的「超长不换行正文」） |

### Images / Performance / Hydration

| 清单条目 | 结论 | 依据 |
|---|---|---|
| `<img>` needs width/height；lazy-load；preload | — | 没有图片 |
| Virtualize or use `content-visibility` for lists over 50 items | 📌 | 一页 100 条、最多 1000 条，没有虚拟化（📌） |
| Avoid layout measurements during rendering | ✅ | 唯一的高度测量是往回翻时的视口补偿，在 `nextTick` 之后做一次，不在渲染期 |
| Batch DOM reads and writes | ✅ | 没有渲染期读写交替 |
| Prefer uncontrolled inputs | 📌 | Vue 的 `v-model` 是这里的惯用法（📌） |
| Preconnect / preload fonts | — | 没有外部资源 |
| Video / GIF | — | 没有 |
| Controlled inputs need `onChange`；SSR 水合 | — | 没有 SSR |

### Navigation & State

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Reflect filters, tabs, pagination, expanded panels in the URL | 📌 | 过滤条件不进 URL（过滤在服务端做，规格 §11.1）；URL 里只有 `?idea=<id>` 深链（📌） |
| Use real links for navigation | 📌 | `MoreMenu.vue` 用 button + `router.push`（📌） |
| Deep-link stateful UI where practical | ✅ | `?idea=<id>` 深链定位（`useDeepLink`） |
| Destructive actions require confirmation or an undo window | ✅ | 删想法、批量删除、删项目、删 webhook 都有二次确认 |

### Touch & Interaction

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Use `touch-action: manipulation` | 🔧 | 全库没有 → 已给按钮与链接加上（去掉双击缩放那 300ms 延迟） |
| Set tap-highlight colors intentionally | 📌 | 没有自定义，用系统默认（📌） |
| Contain overscrolling in modals, drawers, sheets | 🔧 | 下拉面板与合并对话框都可滚但没关住滚动串联 → 已加 `overscroll-behavior: contain` |
| Suppress page rubber-banding only for fine pointers | — | 页面本身不滚（滚的是列表容器） |
| Disable text selection and use `inert` while dragging | 🔧 | 拖动项目时会选出一片字 → 已加 `user-select: none`（`inert` 不需要：拖的不是浮层） |
| Gesture-only operations need tap/click/keyboard alternatives | 📌 | 项目拖拽排序只有鼠标手势（📌） |
| Use `autoFocus` sparingly | ✅ | 没有 `autofocus` 属性；编辑时聚焦编辑框是用户自己点出来的 |

### Safe Areas & Layout

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Full-bleed layouts must account for safe-area insets | 📌 | 没有用 `env(safe-area-inset-*)`（📌） |
| Avoid unwanted horizontal scrollbars | ✅ | 每个「路由-视口」都有守卫：`documentElement.scrollWidth <= clientWidth + 1`（`layout.spec.ts`）；长项目名由 `text-overflow: ellipsis` 兜住 |
| Prefer flexbox/grid over JavaScript-based measurement | ✅ | 两列流是 CSS grid；只有往回翻的视口补偿用了一次测量 |

### Dark Mode & Theming

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Set `color-scheme: dark` on `<html>` for dark themes | 🔧 | 没写 → 暗色主题下原生控件（滚动条等）还是亮色 → 已在 `theme.css` 里按主题写 `color-scheme` |
| Match `<meta name="theme-color">` to the page background | 🔧 | 没有 → 已按亮/暗两档补上（与 `--bg` 一致） |
| Style native selects explicitly for Windows dark mode | ✅ | `base.css` 给 `select` 设了 background/border/color；唯一的原生 select（WebhookForm 的「方法」）有 `id` + `label for` |

### Locale & i18n

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Format dates and times with `Intl.DateTimeFormat` | 📌 | `formatRelativeTime` 是手写的（📌） |
| Format numbers and currency with `Intl.NumberFormat` | — | 应用不显示大数字或金额 |
| Detect language from request headers | — | 纯客户端应用，没有服务端渲染 |
| Mark brand names / code tokens with `translate="no"` | — | 没有这类文本 |

### Hover & Interactive States / Content & Copy

| 清单条目 | 结论 | 依据 |
|---|---|---|
| Buttons and links need hover feedback | ✅ | `button:hover` 变边框色；下拉项 hover 有底色 |
| Hover, active and focus states need strong contrast | 🔧 | 强调色 `#2f6fed` 在**页面背景**上只有 4.21:1（「当前页」那种 13px 的强调文字达不到 4.5:1）→ 已调深到 `#2c66e6`（4.69:1） |
| Prefer active voice | ✅ | 「点『记下』提交」「清除」… |
| Title Case for headings and buttons | — | 中文 |
| Express counts with numerals | ✅ | 「已选 3 条」「有 3 条新内容」 |
| Use specific action labels | ✅ | 「确认删除」「取消」「清除」 |
| Errors explain the next step | ✅ | 错误文案取服务端 `ProblemDetails.detail` |

### 清单点名的反面模式

| 反面模式 | 结论 | 依据 |
|---|---|---|
| `user-scalable=no` / `maximum-scale=1` | ✅ | viewport 是 `width=device-width, initial-scale=1.0` |
| 阻止粘贴 | ✅ | 无 |
| `transition: all` | ✅ | 无 |
| 去掉焦点圈又不给替代 | 🔧 | `Composer.vue`（已修） |
| 点击触发导航却不用链接 | 📌 | `MoreMenu.vue`（📌） |
| 可点的 `div` / `span` | ✅ | 交互元素都是 `button` / `input` / `a` |
| 图片没有尺寸 | — | 没有图片 |
| 大数组不虚拟化 | 📌 | （📌） |
| 表单控件没有名字 | 🔧 | 三个输入框（已修） |
| 图标按钮没有无障碍名 | 🔧 | 侧栏的「＋」（已修） |
| 硬编码的日期/数字格式 | 📌 | （📌） |
| 没有理由的 `autoFocus` | ✅ | 无 |
| 用 GIF 代替视频 | — | 没有 |
| 只有手势的操作 | 📌 | 拖拽排序（📌） |

### 对比度（终审点名要查的那几样）

按 WCAG 2.x 的相对亮度公式算（`--fg` / `--panel` / `--muted` / `--chip-bg` / `--accent` /
`--accent-fg` / `--danger` / `--bg`，亮暗两套主题各算一遍）：

| 组合 | 亮色 | 暗色 | 结论 |
|---|---:|---:|---|
| 正文 `--fg` / `--panel` | 17.01:1 | 14.40:1 | ✅ |
| 次要文字 `--muted` / `--panel` | 5.28:1 | 6.09:1 | ✅ |
| 编号徽标 `--fg` / `--chip-bg`（新增的 `.meta__num`） | 14.55:1 | 12.75:1 | ✅ |
| 标签 chip `--fg` / `--chip-bg` | 14.55:1 | 12.75:1 | ✅ |
| 语义色 `--danger` / `--panel`（`.meta__action--delete` 的 🗑） | 5.44:1 | 6.11:1 | ✅ |
| 白字 / `--accent` 底（主按钮、选中项、搜索高亮 `<mark>`） | 5.06:1 | 6.88:1 | ✅ |
| 强调色 / `--panel`（焦点圈、边框这类非文字对比度要 ≥3:1） | 5.06:1 | 6.73:1 | ✅ |
| 强调色 / `--bg`（**小字号的强调文字**） | 4.69:1 | 6.73:1 | ✅（原来是 4.21:1，这一轮调深了） |

---

## 二、按文件的发现（清单要求的输出格式）

```
## web/src/components/Composer.vue

web/src/components/Composer.vue:105 - `.input:focus { outline: none }` 去掉焦点圈且无替代 → 已删
web/src/components/Composer.vue:63  - 底部输入框只有 placeholder，没有无障碍名 → 已补 aria-label

## web/src/components/MessageItem.vue

web/src/components/MessageItem.vue:575 - 编辑框连 placeholder 都没有 → 已补 aria-label="编辑正文"
web/src/components/MessageItem.vue:816 - 编号不是等宽数字 → 已加 tabular-nums
web/src/components/MessageItem.vue:677 - 焦点滚到顶部会落在钉着的过滤行底下 → 已加 scroll-margin-top
web/src/components/MessageItem.vue:906 - 隐藏态的控件仍参与命中测试（看不见但能点）→ 已加 pointer-events: none

## web/src/components/ProjectSidebar.vue

web/src/components/ProjectSidebar.vue:114 - 「＋」只有 title → 已补 aria-label
web/src/components/ProjectSidebar.vue:243 - 条数不是等宽数字 → 已加 tabular-nums
web/src/components/ProjectSidebar.vue:204 - 拖动中会选出文字 → 已加 user-select: none

## web/src/components/TagFilter.vue

web/src/components/TagFilter.vue:114 - 标签条数不是等宽数字 → 已加 tabular-nums

## web/src/components/TagEditor.vue

web/src/components/TagEditor.vue:136 - 标签输入框只有 placeholder → 已补 aria-label

## web/src/components/MergeDialog.vue

web/src/components/MergeDialog.vue:90 - 对话框里的滚动没关在容器里 → 已加 overscroll-behavior: contain

## web/src/pages/HomePage.vue

web/src/pages/HomePage.vue:1299 - 「有 N 条新内容」按钮条件渲染，读屏未必念 → 已补常驻的 aria-live 活区
web/src/pages/HomePage.vue:1203 - 没有跳到主内容的链接 → 已补 skip-link 与 id="main-content"

## web/src/styles/base.css

web/src/styles/base.css:72 - 按钮与链接没有 touch-action: manipulation → 已加
web/src/styles/base.css:95 - 没有 sr-only 工具类 → 已加（活区与 skip link 要用）

## web/src/styles/dropdown.css

web/src/styles/dropdown.css:23 - 面板里的滚动会串到后面的页面 → 已加 overscroll-behavior: contain

## web/src/styles/theme.css

web/src/styles/theme.css:30 - 亮色强调色在页面背景上只有 4.21:1 → 已调深到 #2c66e6
web/src/styles/theme.css:14 - 没写 color-scheme → 已按主题写上

## web/index.html

web/index.html:10 - 没有 theme-color → 已按亮/暗两档补上
```

---

## 三、这一轮修了什么

| 提交 | 修了什么 |
|---|---|
| `feb7aff` | 桌面上「悬停才显形」的控件不再透明但可点（隐藏态 `pointer-events: none`） |
| `64950b0` | 输入框的焦点圈回来 |
| `35cce23` | 原生控件跟主题走（`color-scheme` + `theme-color`）、按钮去掉双击缩放延迟 |
| `4c22b44` | 编号与条数用等宽数字 |
| `6a54617` | 下拉面板与合并对话框把滚动关在容器里 |
| `3c2512e` | 跳到主内容的链接、异步更新报给屏幕阅读器、侧栏「＋」的无障碍名 |
| `9277c01` | 三个输入框补上无障碍名 |
| `f7dbda4` | 拖动项目时别把文字选出来 |
| `42bd18b` | 亮色的强调色调深一点，正文对比度够 4.5:1 |
| `cf32703` | 焦点滚到列表顶部时别落在钉着的过滤行底下 |
| `b1b3546` | 连带：勾选框与「＋」的选择器跟着可见性规则与无障碍名走 |

---

## 四、明确接受的偏差

每条都给了理由；「以后怎么改」写在括号里。

1. **「⋯」菜单用 button + `router.push`，不是真链接**（`MoreMenu.vue`）
   —— 规格 §D11 明确选了「⋯ 菜单直接带上三个入口」这个形态；
   换成 `router-link` 会改掉 e2e 断言的 DOM 结构。
   （以后要改：把菜单项换成 `router-link`，并相应改 e2e。）
2. **过滤条件不进 URL** —— 过滤在服务端做（规格 §11.1），URL 里只有 `?idea=<id>` 深链。
   （以后要改：把过滤条件也写进 query，URL 变化时重拉。）
3. **想法列表不虚拟化**（一页 100 条、最多 1000 条）
   —— `content-visibility: auto` 会改变卡片的实际高度，
   而往回翻的视口补偿（`useHistoryScroll`）量的就是高度差：两者会打架。
   （以后要改：先让视口补偿不依赖实测高度，再上虚拟化。）
4. **深链高亮闪的是 `background`，不是 `transform`/`opacity`**
   —— 闪的是卡片底色（一次 1.5s 的重绘，一张卡片），用透明度做不出来这个效果。
5. **相对时间是手写的**（`lib/format.ts` 的「刚刚 / N 分钟前 / 昨天 / M月D日」）
   —— `Intl.DateTimeFormat` 在这里的输出**依赖运行时的 ICU 数据**：
   本机 Node 是 small-icu，`Intl.DateTimeFormat('zh-CN', {month:'numeric',day:'numeric'})`
   输出的是 `11/1`，而浏览器（full-icu）输出的是 `11月1日` —— 同一份代码两个界面，
   单测也会跟着飘。所以保持手写、输出确定。
6. **想法条目上的勾选框点按目标小**（桌面端 13×13 的原生方块）
   —— 桌面上有鼠标，窄屏上它已经被改成 32×32 的自绘方块（规格 §6.10）。
7. **错误走全局提示条，不做就地错误 + 聚焦第一个错**
   —— 这是应用一贯的做法（`ToastHost`），改成就地错误要动每个表单。
8. **底部输入框的草稿在离开页面时不提醒** —— 标签编辑器有「还有未保存的改动」确认，
   底部输入框没有（它是随手记的地方，草稿丢了不心疼）。
   另外三条一并接受：没有 `autocomplete` / `name`（不提交传统表单）、
   没有自定义 tap-highlight 颜色、没有用 `env(safe-area-inset-*)`
   （局域网里的桌面优先应用）、拖拽排序只有鼠标手势（规格没要求键盘排序）。

---

## 五、复核怎么做的（可重复）

```bash
# 清单本身（每次复核前重新抓一遍）
curl -sS https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md

# 逐条查证用的命令（在 web/ 下）
grep -rn "transition: all\|outline: *none\|outline: *0" src/     # 反面模式
grep -rn "user-scalable\|maximum-scale" index.html              # 缩放
grep -rn "touch-action\|overscroll-behavior\|color-scheme" src/   # 触摸 / 滚动 / 主题
grep -rn "aria-live\|aria-hidden\|aria-label" src/               # 无障碍
grep -rn "<h1" src/                                               # 标题层级
```

对比度是按 WCAG 2.x 的相对亮度公式，用 `theme.css` 里**实际在用**的色值算的
（亮暗两套主题各算一遍）；色值改了要重算 —— 公式：

```
L = 0.2126 R + 0.7152 G + 0.0722 B   （R/G/B 是线性化后的 0–1）
对比度 = (L₁ + 0.05) / (L₂ + 0.05)   （L₁ 取较亮的那个）
```
