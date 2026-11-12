/**
 * 与后端 DTO 一一对应的对外类型。
 *
 * 字段名必须与 `openapi/idea-v1.json` 的 schema 逐字一致：
 * 后端用 System.Text.Json 的 camelCase 命名策略输出，
 * 字段名对不上时 TS 不会报错，但运行时数据会静默变成 undefined。
 */

/** 一条想法。`title` 可以为 null —— 只记正文不记标题的想法是允许的。 */
export interface Idea {
  id: string;
  projectId: string;
  title: string | null;
  content: string;
  tags: string[];
  done: boolean;
  createdAt: string;
  updatedAt: string;
}

/** 一个项目，含想法计数与最近活动时间。 */
export interface Project {
  id: string;
  name: string;
  sortOrder: number;
  ideaCount: number;
  lastActivityAt: string | null;
  createdAt: string;
  updatedAt: string;
}

/** 一个标签及其关联的想法数量。 */
export interface Tag {
  name: string;
  count: number;
}

/** 一个 webhook 的配置。`hasSecret` 只表示有没有设过 secret，不回传明文。 */
export interface Webhook {
  id: string;
  name: string;
  url: string;
  method: string;
  headers: Record<string, string>;
  bodyTemplate: string;
  events: string[];
  scope: 'global' | 'project';
  projectId: string | null;
  enabled: boolean;
  hasSecret: boolean;
  createdAt: string;
  updatedAt: string;
}

/** 一次 webhook 投递记录。 */
export interface Delivery {
  id: string;
  webhookId: string;
  eventId: string;
  eventType: string;
  payload: string;
  status: string;
  attempts: number;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
}

/** 回收站里的一条条目：`kind` 为 `project` 是容器，为 `idea` 是顶层单条。 */
export interface TrashEntry {
  kind: 'project' | 'idea';
  project: Project | null;
  ideas: Idea[];
  idea: Idea | null;
}

/**
 * 项目在事件里的快照。
 *
 * 与 {@link Project} **不是一回事**：快照只有 id、名字与排序位置，
 * 没有想法计数、创建时间这些列表端点的字段。
 *
 * `sortOrder` 必须在快照里：项目重排（`project.updated`）时订阅端要拿到
 * **新值**才能更新本地顺序，缺了它就只能整表重拉。
 */
export interface ProjectSnapshot {
  id: string;
  name: string;
  sortOrder: number;
}

/**
 * SSE 推来的一条业务事件。
 *
 * `changed` 与 `mergedFrom` 后端**恒定写出**（`JsonSourceGenerationOptions`
 * 没设 `DefaultIgnoreCondition`，null 不会被省略），所以线上报文里这两个键
 * 总是存在，值可能是 `null`。
 *
 * 因此这两个属性是**必填可空**（`string[] | null`，**没有 `?`**）——
 * 类型如实反映现实：运行时永远不会是 `undefined`。
 *
 * 去掉 `?` 换来的两道编译期护栏：
 * - 构造事件对象时漏写这两个键 → TS2739（属性缺失）；
 * - 不判空就 `changed.length` → TS18047（可能是 null）。
 *
 * 注意：**`changed === undefined` 不会被 TS 拦下**（相等性比较的可比性检查
 * 在目标类型是 null/undefined 时直接放行），它只是运行时恒假 ——
 * 所以「该事件没有 changed 概念」必须用 `changed === null` 判定。
 *
 * `changed` 是**三态**，三种都要分别处理：
 * - `null`：该事件类型没有「改了什么」的概念（如 `idea.created`）——
 *   判定要用 `changed === null`，**不是** `changed === undefined`；
 * - 空数组：写了但值没变，数据本身仍然是最新的，别把这次更新跳过；
 * - 非空数组：这次操作写过的字段名（如 `title`、`done`、`sortOrder`）。
 *
 * `mergedFrom` 只在合并事件里有值（被硬删的源想法 id 列表），其余事件是 `null`。
 */
export interface IdeaEvent {
  id: string;
  type: string;
  timestamp: string;
  project: ProjectSnapshot | null;
  idea: Idea | null;
  changed: string[] | null;
  mergedFrom: string[] | null;
}

/**
 * 后端返回的错误体：RFC 9457 的 ProblemDetails + 顶层扩展字段 `code`。
 *
 * `code` 是稳定的机器可读错误码（`invalid_content` 之类），
 * `detail` 是给人看的说明，UI 上展示的是它。
 */
export interface ApiProblem {
  title: string;
  status: number;
  detail?: string;
  code?: string;
}
