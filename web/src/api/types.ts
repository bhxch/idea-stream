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

/** SSE 推来的一条业务事件。 */
export interface IdeaEvent {
  id: string;
  type: string;
  timestamp: string;
  project: { id: string; name: string } | null;
  idea: Idea | null;
  changed?: string[];
  mergedFrom?: string[];
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
