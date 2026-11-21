/**
 * E2E 公共夹具：复位（reset）+ 造数据（seed）。
 *
 * 用例文件这样用：
 *
 * ```ts
 * import { test, expect } from './fixtures';
 *
 * test('收件箱里能看到刚记的想法', async ({ page, seed }) => {
 *   await seed.idea('今天想到一个点子');
 *   await page.goto('/');
 *   await expect(page.getByText('今天想到一个点子')).toBeVisible();
 * });
 * ```
 *
 * 每个用例开始前会自动复位一次（见文件末尾的 beforeEach）——
 * 用例之间互不干扰，且与执行顺序无关。
 *
 * ⚠️ 复位走的是 HTTP API，**不碰数据库文件**。应用本身没有「彻底删除」
 * 的入口（回收站只允许恢复，标签字典只增不减），所以复位之后：
 *
 * - 活动想法 0 条、活动项目只有「收件箱」、webhook 0 个 —— 这些是干净的；
 * - **回收站里会留下残渣**：被删项目的容器条目，以及每个项目一条正文为
 *   「e2e-reset」的合并产物；
 * - **标签字典里会留下残渣**：用过的标签名还在，计数为 0。
 *
 * 这两处清不掉是应用的设计（见 server/IdeaApp/Application/TrashService.cs
 * 与 Infrastructure/TagRepository.cs 的注释），不是夹具偷懒。
 * 需要完全干净的库时用 {@link freshDatabase}（删库重建，约 5 秒）。
 */

import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { test as base, expect, type APIRequestContext, type Locator } from '@playwright/test';

// API 的返回类型：**直接用应用的那份**（`import type` 是纯类型导入，
// 运行时不产生任何依赖 —— e2e 仍然是打真实服务，不 import 应用的运行时代码）。
//
// 早先这里是手抄一份，理由是「应用改了类型也不该让 e2e 编译失败」。
// 但那份手抄本身就是漂移的来源：应用给 Idea 加了字段，e2e 这边还是旧的
// （两边各自独立，编译器看不出来）。改成类型导入之后，
// 「e2e 用的字段在应用里不存在」与「两边形状对不上」都会在编译期红。
import type {
  Idea,
  Project,
  Tag,
  TrashEntry,
  Webhook,
} from '../src/api/types';

/** 内置收件箱的项目 id（首次启动时写进库里的那一行）。 */
export const INBOX_ID = 'inbox';
/** 内置收件箱的项目名。 */
export const INBOX_NAME = '收件箱';

/** 复位留下的合并产物的**正文** —— 固定值，方便断言时把它排除掉。 */
export const RESET_ARTIFACT = 'e2e-reset';

/**
 * 复位夹具额外拿到的时限（毫秒）。
 *
 * 理由见 {@link E2EFixtures} 的夹具定义：复位是几十个 HTTP 往返，
 * 机器慢时它会先撞上用例的总时限 —— 而那报出来的是「夹具超时」，
 * 看着像应用坏了，其实只是环境慢。
 */
const RESET_TIMEOUT_MARGIN_MS = 60_000;

/** 单个请求超过这个毫秒数就打一行日志（慢在哪儿要看得见）。 */
const SLOW_REQUEST_MS = 200;

/**
 * 用例**正文**的时限（毫秒）—— 与 playwright.config.ts 里的 `timeout` 一样。
 *
 * 夹具放宽之后要按这个数把正文的时限还回去（见夹具定义）：
 * 慢的是复位，不是用例。
 */
const BODY_TIMEOUT_MS = 30_000;

export interface CreateIdeaInput {
  /** 所属项目；不传 = 收件箱。 */
  projectId?: string;
  content: string;
  tags?: string[];
}

export interface CreateWebhookInput {
  name?: string;
  url?: string;
  method?: string;
  headers?: Record<string, string>;
  bodyTemplate?: string;
  events?: string[];
  /** 'global'（默认，创建后禁用）或 'project'（创建后启用）。 */
  scope?: 'global' | 'project';
  projectId?: string | null;
  secret?: string | null;
}

/** 列表接口的查询参数。 */
export interface ListIdeasParams {
  projectId?: string;
  status?: 'all' | 'open' | 'done';
  tags?: string[];
  tagMode?: 'and' | 'or';
  /**
   * 关键词（后端的 `q`）。
   *
   * 空串**不发出去**（与前端 `toQueryParams` 同一个口径）：不发 = 没有关键词 = 不过滤。
   */
  q?: string;
  /** 一页最多几条。 */
  limit?: number;
  /** 已经翻过多少条（从**最新**一端数：`offset=0` 是最新那页）。 */
  offset?: number;
}

/**
 * 应用 API 的薄封装。
 *
 * 全部走 Playwright 的 `request` 夹具 —— 它带着配置里的 baseURL
 * （本轮的地址，端口每轮都可能是不同的），所以路径都写成 '/api/...'。
 */
export class IdeaApi {
  constructor(private readonly request: APIRequestContext) {}

  /** 发一个请求；失败时把状态码与响应体一起抛出来（错误码 code 在 body 里）。 */
  private async send<T>(method: string, path: string, body?: unknown): Promise<T> {
    const started = Date.now();
    const response = await this.request.fetch(path, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      data: body === undefined ? undefined : JSON.stringify(body),
    });
    const elapsed = Date.now() - started;

    if (elapsed >= SLOW_REQUEST_MS) {
      console.warn(`[e2e] 慢请求 ${method} ${path} 用了 ${elapsed}ms`);
    }

    if (!response.ok()) {
      const text = await response.text();
      throw new Error(
        `${method} ${path} 失败：HTTP ${response.status()} ${response.statusText()} —— ${text}`,
      );
    }
    if (response.status() === 204) {
      return undefined as T;
    }
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  // ── 健康 ────────────────────────────────────────────────────────────

  async health(): Promise<{ status: string }> {
    return this.send('GET', '/api/health');
  }

  // ── 项目 ────────────────────────────────────────────────────────────

  async listProjects(): Promise<Project[]> {
    return this.send('GET', '/api/projects');
  }

  async createProject(name: string): Promise<Project> {
    return this.send('POST', '/api/projects', { name });
  }

  async renameProject(id: string, name: string): Promise<Project> {
    return this.send('PATCH', `/api/projects/${id}`, { name });
  }

  async deleteProject(id: string): Promise<void> {
    await this.send('DELETE', `/api/projects/${id}`);
  }

  async restoreProject(id: string): Promise<Project> {
    return this.send('POST', `/api/projects/${id}/restore`);
  }

  // ── 想法 ────────────────────────────────────────────────────────────

  async listIdeas(params: ListIdeasParams = {}): Promise<Idea[]> {
    const query = new URLSearchParams();
    if (params.projectId !== undefined) query.set('projectId', params.projectId);
    if (params.status !== undefined) query.set('status', params.status);
    if (params.tags !== undefined) query.set('tags', params.tags.join(','));
    if (params.tagMode !== undefined) query.set('tagMode', params.tagMode);
    if (params.q) query.set('q', params.q);
    if (params.limit !== undefined) query.set('limit', String(params.limit));
    if (params.offset !== undefined) query.set('offset', String(params.offset));
    const suffix = query.toString();
    return this.send('GET', `/api/ideas${suffix ? `?${suffix}` : ''}`);
  }

  async createIdea(input: CreateIdeaInput): Promise<Idea> {
    return this.send('POST', '/api/ideas', {
      projectId: input.projectId ?? INBOX_ID,
      content: input.content,
      tags: input.tags ?? [],
    });
  }

  async updateIdea(id: string, patch: { content?: string }): Promise<Idea> {
    return this.send('PATCH', `/api/ideas/${id}`, patch);
  }

  async setTags(id: string, tags: string[]): Promise<Idea> {
    return this.send('PUT', `/api/ideas/${id}/tags`, { tags });
  }

  async setDone(id: string, done: boolean): Promise<Idea> {
    return this.send('POST', `/api/ideas/${id}/complete`, { done });
  }

  async deleteIdea(id: string): Promise<void> {
    await this.send('DELETE', `/api/ideas/${id}`);
  }

  async restoreIdea(id: string): Promise<Idea> {
    return this.send('POST', `/api/ideas/${id}/restore`);
  }

  /** 合并想法。源想法会被**硬删**（行真的没了），返回合并出来的那一条。 */
  async mergeIdeas(ids: string[], options: { content?: string } = {}): Promise<Idea> {
    return this.send('POST', '/api/ideas/merge', {
      ids,
      content: options.content ?? null,
    });
  }

  // ── 标签 ────────────────────────────────────────────────────────────

  async listTags(): Promise<Tag[]> {
    return this.send('GET', '/api/tags');
  }

  async suggestTags(projectId: string, prefix: string): Promise<string[]> {
    return this.send(
      'GET',
      `/api/tags/suggest?projectId=${encodeURIComponent(projectId)}&prefix=${encodeURIComponent(prefix)}`,
    );
  }

  // ── webhook ─────────────────────────────────────────────────────────

  /** 列 webhook。不给 projectId 时后端只回**全局**的。 */
  async listWebhooks(projectId?: string): Promise<Webhook[]> {
    const suffix = projectId ? `?projectId=${encodeURIComponent(projectId)}` : '';
    return this.send('GET', `/api/webhooks${suffix}`);
  }

  async createWebhook(input: CreateWebhookInput = {}): Promise<Webhook> {
    const scope = input.scope ?? 'global';
    return this.send('POST', '/api/webhooks', {
      name: input.name ?? '测试 webhook',
      url: input.url ?? 'http://127.0.0.1:9/e2e',
      method: input.method ?? 'POST',
      headers: input.headers ?? {},
      bodyTemplate: input.bodyTemplate ?? '{"text":"{{idea.content}}"}',
      events: input.events ?? ['idea.created'],
      scope,
      projectId: scope === 'project' ? (input.projectId ?? null) : null,
      secret: input.secret ?? null,
    });
  }

  async deleteWebhook(id: string): Promise<void> {
    await this.send('DELETE', `/api/webhooks/${id}`);
  }

  // ── 回收站与设置 ────────────────────────────────────────────────────

  async listTrash(): Promise<TrashEntry[]> {
    return this.send('GET', '/api/trash');
  }

  async getSettings(): Promise<Record<string, string>> {
    return this.send('GET', '/api/settings');
  }

  async setSetting(key: string, value: string): Promise<Record<string, string>> {
    return this.send('PUT', `/api/settings/${encodeURIComponent(key)}`, { value });
  }

  // ── 复位 ────────────────────────────────────────────────────────────

  /**
   * 复位超过这个毫秒数就打一行日志（带每一段的耗时）。
   *
   * 为什么要有：偶发的「Test timeout of 30000ms exceeded while setting up
   * "resetBeforeEach"」—— 那**不是断言失败**，是复位太慢。
   * 没有日志的话，事后只知道「5 条用例挂在夹具上」，
   * 查不出是哪一段慢、慢了多少（而多半是机器负载，不是代码）。
   */
  static readonly SLOW_RESET_MS = 5_000;

  /**
   * 把库恢复干净。走 API，不碰数据库文件。
   *
   * 顺序是有讲究的：
   * 1. 先删 webhook（这个是真删）。**必须在删项目之前**：
   *    项目一软删就不再出现在 `GET /api/projects` 里，只顺着活动项目查的话，
   *    它的 webhook 会漏掉 —— 那些钩子一直启用着、越积越多，每次复位
   *    都给死地址投递，把投递队列塞满（还会拖慢别的用例）。
   *    放在最前面还有一层好处：后面的恢复/合并/删除都会发事件，
   *    钩子先没了，那些事件就没有投递对象。
   * 2. 再把回收站里的东西**恢复**回来 —— 回收站只进不出的坏处是
   *    「删过的东西会一直堆在那儿」，恢复一遍等于把上一轮的东西平账。
   *    恢复完之后所有项目、想法都回到活动状态。
   * 3. 再清想法：同一个项目里的想法用合并**硬删**（行真的没了，标签链接
   *    随外键级联清掉），只剩一条合并产物，把它清空标签后软删 ——
   *    残渣统一成正文为「e2e-reset」的一条，形态可预测。
   * 4. 再删项目（收件箱除外）：项目的想法随项目一起进回收站。
   * 5. 设置项复位成默认值。
   *
   * **快**：复位是几十个 HTTP 往返，而每个用例前都要来一次 ——
   * 它慢，用例就慢，机器一慢还会撞上 Playwright 的超时上限
   * （报出来的是「夹具超时」，看不出是哪一段慢）。
   * 所以这里把**清单只取一次**（回收站、项目各取一次就够），
   * 少发两个来回。
   *
   * ⚠️ **写操作必须顺序发**（理由与实测见 {@link RESET_READ_CONCURRENCY}）：
   * 服务端写 SQLite 只有一个写者，并发写会互相等锁，
   * 而 SQLite 的忙等待按 100ms 一档退避 —— 并发写比顺序写慢好几倍。
   */
  async reset(): Promise<void> {
    const started = Date.now();
    const marks: string[] = [];
    const mark = (name: string) => {
      marks.push(`${name} ${Date.now() - started}ms`);
    };

    // 清单只取一次：清 webhook 与恢复回收站都要看回收站，清 webhook 与
    // 清项目都要看项目 —— 重复取一遍就是白等一个来回。
    const trash = await this.listTrash();
    const activeProjects = await this.listProjects();
    mark('清单');

    await this.clearWebhooks(trash, activeProjects);
    mark('清 webhook');

    await this.restoreTrash(trash);
    mark('恢复回收站');

    await this.clearIdeas();
    mark('清想法');

    await this.clearProjects(activeProjects, trash);
    mark('清项目');

    await this.setSetting('project.sort_mode', 'recent');
    mark('复位设置项');

    const elapsed = Date.now() - started;

    if (elapsed >= IdeaApi.SLOW_RESET_MS) {
      console.warn(
        `[e2e] 复位用了 ${elapsed}ms（${marks.join(' · ')}）—— 服务慢了。`
        + '机器负载高的时候会出现；一直这样就该看看服务端的写路径。',
      );
    }
  }

  /** 把回收站里的条目全部恢复回来（回收站清空，所有项目与想法回到活动状态）。 */
  private async restoreTrash(trash: TrashEntry[]): Promise<void> {
    // 先恢复想法：容器的项目还没恢复时，恢复想法是允许的。
    const ideaIds: string[] = [];
    for (const entry of trash) {
      if (entry.kind === 'idea' && entry.idea) {
        ideaIds.push(entry.idea.id);
      } else if (entry.kind === 'project') {
        for (const idea of entry.ideas) {
          ideaIds.push(idea.id);
        }
      }
    }
    // ⚠️ **顺序**恢复，不并发：见 {@link RESET_READ_CONCURRENCY} 的说明。
    for (const id of ideaIds) {
      await this.restoreIdea(id);
    }

    // 再恢复项目：连带恢复「因删项目而删」的想法。
    //
    // ⚠️ 这一段**不能**与上面并发：恢复项目会连带把它的想法恢复掉，
    // 与单独恢复同一条想法撞上，那一条就已经不在回收站里了（会 404）。
    const projectIds = trash
      .filter((entry) => entry.kind === 'project' && entry.project)
      .map((entry) => entry.project!.id);
    for (const id of projectIds) {
      await this.restoreProject(id);
    }
  }

  /**
   * 清掉所有活动想法。
   *
   * 残渣形态：**每个有想法的项目留一条正文为「e2e-reset」的想法**。
   * 单条想法的项目会临时造一条一起合并（合并至少要两条），
   * 这样残渣的形态统一，断言时好排除。
   */
  private async clearIdeas(): Promise<void> {
    const ideas = await this.listIdeas();
    const byProject = new Map<string, Idea[]>();
    for (const idea of ideas) {
      const group = byProject.get(idea.projectId);
      if (group) {
        group.push(idea);
      } else {
        byProject.set(idea.projectId, [idea]);
      }
    }
    // ⚠️ **顺序**清，不并发：见 {@link RESET_READ_CONCURRENCY} 的说明。
    for (const [projectId, group] of byProject) {
      let ids = group.map((idea) => idea.id);
      if (ids.length === 1) {
        const helper = await this.createIdea({
          projectId,
          content: RESET_ARTIFACT,
        });
        ids = [ids[0], helper.id];
      }
      // 合并 = 硬删：源想法的行没了，标签链接随外键级联清掉。
      const merged = await this.mergeIdeas(ids, {
        content: RESET_ARTIFACT,
      });
      await this.setTags(merged.id, []);
      await this.deleteIdea(merged.id);
    }
  }

  /**
   * 删掉除收件箱外的项目。
   *
   * @param activeProjects 复位开始时的活动项目。
   * @param trash 复位开始时的回收站（里面的项目这会儿已经被恢复回来了）。
   */
  private async clearProjects(activeProjects: Project[], trash: TrashEntry[]): Promise<void> {
    // 活动项目 = 复位开始时的活动项目 + 从回收站恢复回来的项目。
    // 不重新查一次：这一轮里没有别的地方会建项目。
    const projects = new Map<string, Project>();
    for (const project of activeProjects) {
      projects.set(project.id, project);
    }
    for (const entry of trash) {
      if (entry.kind === 'project' && entry.project) {
        projects.set(entry.project.id, entry.project);
      }
    }

    const ids = [...projects.values()]
      .filter((project) => project.id !== INBOX_ID && project.name !== INBOX_NAME)
      .map((project) => project.id);

    // ⚠️ **顺序**删，不并发：见 {@link RESET_READ_CONCURRENCY} 的说明。
    for (const id of ids) {
      await this.deleteProject(id);
    }
  }

  /**
   * 删掉所有 webhook（全局的与项目级的都要删）。
   *
   * 项目级的要**按项目逐个查**：`GET /api/webhooks` 不带 projectId 时只回全局的。
   * 而项目一软删就从 `GET /api/projects` 里消失了 —— 只顺着活动项目查的话，
   * 被删项目的钩子会漏掉（它们还启用着，每次复位都往死地址投递）。
   * 所以除了活动项目，**回收站里的项目**也要查一遍：
   * `GET /api/webhooks?projectId=<已删项目id>` 仍然列得出来。
   *
   * 复位时它跑在「恢复回收站」**之前** —— 那时回收站里还留着上一轮删掉的项目，
   * 它们的项目级钩子正在这一段里被清掉。
   *
   * @param trash 复位开始时的回收站（还没恢复，所以里面的项目还在）。
   * @param activeProjects 复位开始时的活动项目。
   */
  private async clearWebhooks(trash: TrashEntry[], activeProjects: Project[]): Promise<void> {
    const projectIds = new Set<string>(activeProjects.map((project) => project.id));
    for (const entry of trash) {
      if (entry.kind === 'project' && entry.project) {
        projectIds.add(entry.project.id);
      }
    }

    // 各项目的钩子互不相干：**读**可以并发（见 {@link RESET_READ_CONCURRENCY}）。
    const perProject = await inBatches(
      [...projectIds],
      RESET_READ_CONCURRENCY,
      (projectId) => this.listWebhooks(projectId),
    );
    const globals = await this.listWebhooks();

    const webhooks = [...globals, ...perProject.flat()];

    // ⚠️ **顺序**删，不并发：见 {@link RESET_READ_CONCURRENCY} 的说明。
    for (const webhook of webhooks) {
      await this.deleteWebhook(webhook.id);
    }
  }
}

/**
 * 复位里**读**请求的并发上限。
 *
 * ⚠️ **只有读能并发，写必须顺序** —— 这不是保守，是量出来的：
 *
 * 服务端写 SQLite 只有一个写者，并发的写请求要排队等锁，而 SQLite 的忙等待
 * 是**按 100ms 一档退避**的（1 → 2 → 5 → 10 → … → 100ms）。于是并发的写
 * 一个个往后挪，每个都多等一档 —— 复位的写请求耗时从几毫秒涨到几百毫秒，
 * 整个子集从 42 秒变成 4 分钟（实测，同一台机器、同一批用例）。
 *
 * 读不一样：WAL 模式下读不挡写（WAL 模式下读者看到的是快照），
 * 并发读是真的省时间。
 */
const RESET_READ_CONCURRENCY = 8;

/**
 * 分批并发地跑一批互不相干的**读**。
 *
 * 一批一批来（每批 {@link RESET_READ_CONCURRENCY} 个），而不是一次全放出去：
 * 一次太多只是把队排得更长，还多占着连接。
 */
async function inBatches<T, R>(
  items: T[],
  size: number,
  action: (item: T) => Promise<R>,
): Promise<R[]> {
  const results: R[] = [];

  for (let index = 0; index < items.length; index += size) {
    results.push(...(await Promise.all(items.slice(index, index + size).map((item) => action(item)))));
  }

  return results;
}

/** 造数据的便捷函数。 */
export interface Seed {
  /** 造一个项目（默认名「测试项目」）。 */
  project(name?: string): Promise<Project>;
  /** 造一条想法（默认落在收件箱）。 */
  idea(content: string, options?: { projectId?: string; tags?: string[] }): Promise<Idea>;
  /** 造一条已完成的想法。 */
  doneIdea(content: string, options?: { projectId?: string; tags?: string[] }): Promise<Idea>;
  /** 造一条带标签的想法，返回标签名（标签是全局字典，名字会自动建）。 */
  tag(name: string, options?: { content?: string; projectId?: string }): Promise<string>;
  /** 造一个 webhook（默认全局作用域、创建后是禁用的，不会真的发请求）。 */
  webhook(options?: CreateWebhookInput): Promise<Webhook>;
}

export interface E2EFixtures {
  /** 应用 API 的封装。 */
  api: IdeaApi;
  /** 造数据的便捷函数。 */
  seed: Seed;
  /** 自动夹具：每个用例开始前复位。类型上只占位，用例里拿不到它。 */
  resetBeforeEach: void;
}

export const test = base.extend<E2EFixtures>({
  /**
   * 每个用例开始前复位一次。
   *
   * 放在夹具定义里而不是让每个用例自己调 —— 「忘了复位」是顺序相关用例的
   * 常见来源，复位这件事不该由写用例的人记着。
   *
   * ⚠️ 必须是**自动夹具**（`{ auto: true }`），不能写成顶层
   * `test.beforeEach(...)`：顶层钩子会挂到「模块体首次求值时正在加载的那个
   * spec 文件」的套件上，而 fixtures.ts 被 ESM 缓存、每个 worker 只求值一次 ——
   * 于是同一轮里只有**第一个** spec 文件拿得到复位，后面的文件全都没有，
   * 脏数据在用例之间串味（表现是「单独跑这个文件全过、全量跑就红」）。
   * 自动夹具挂在夹具定义上，对**每一个**用到这个 test 的用例都生效。
   */
  resetBeforeEach: [
    async ({ api }, use, testInfo) => {
      // 给复位留够预算。
      //
      // 复位是几十个 HTTP 往返（每个用例前都要来一次）。机器一慢
      // （别的进程在抢 CPU/磁盘）它就可能撞上 30 秒的用例上限 ——
      // 报出来的是「Test timeout of 30000ms exceeded while setting up
      // "resetBeforeEach"」，而那**不是断言失败**，是环境慢，
      // 读报告的人却会以为应用坏了。
      //
      // 这里给夹具多留一分钟；**正文**的时限随后单独按「从现在起算」重设
      // （见下面两行）—— 放宽的只是夹具，不是整条用例。
      const startedAt = Date.now();
      testInfo.setTimeout(testInfo.timeout + RESET_TIMEOUT_MARGIN_MS);

      await api.reset();

      // 复位花掉多少，就从「现在」起给正文留 BODY_TIMEOUT_MS ——
      // 正文还是只有 30 秒，不会因为放宽了夹具而一起放宽。
      testInfo.setTimeout(Date.now() - startedAt + BODY_TIMEOUT_MS);

      await use();
    },
    { auto: true },
  ],
  api: async ({ request }, use) => {
    await use(new IdeaApi(request));
  },
  seed: async ({ api }, use) => {
    await use({
      project: (name = '测试项目') => api.createProject(name),
      idea: (content, options = {}) =>
        api.createIdea({
          projectId: options.projectId,
          content,
          tags: options.tags ?? [],
        }),
      doneIdea: async (content, options = {}) => {
        const idea = await api.createIdea({
          projectId: options.projectId,
          content,
          tags: options.tags ?? [],
        });
        return api.setDone(idea.id, true);
      },
      tag: async (name, options = {}) => {
        await api.createIdea({
          projectId: options.projectId,
          content: options.content ?? `带标签「${name}」的想法`,
          tags: [name],
        });
        return name;
      },
      webhook: (options = {}) => api.createWebhook(options),
    });
  },
});

/**
 * 把库恢复成**完全干净**的状态：删掉数据库文件、重建、重启服务。
 *
 * 复位（{@link IdeaApi.reset}）走 API，清不掉回收站与标签字典 ——
 * 应用没有「彻底删除」的入口。需要那两处也干净时用这个（约 5 秒）。
 *
 * ⚠️ 它会**重启 E2E 服务**：只能在自己独占服务时用（配置里 workers 固定为 1，
 * 所以同一时刻只有一个用例在跑）。
 */
export function freshDatabase(): void {
  execFileSync('bash', [join(e2eDir(), 'start-server.sh'), '--fresh'], {
    cwd: e2eDir(),
    stdio: 'inherit',
  });
}

/**
 * 点条目上「悬停才显形」的控件（🏷 标签 / 🗑 删除 / 勾选框）。
 *
 * 桌面上（≥768px 且有悬停能力）这些控件默认是**隐藏的，而且不参与命中测试**
 * —— 见 `web/src/components/MessageItem.vue` 里隐藏态的 `pointer-events: none`。
 * 真实用户得先把鼠标移到那条卡片上，控件才显形、才点得到；用例里照做：
 * 先悬停，等它真的恢复成可点（`pointer-events: auto`）再点。
 *
 * ⚠️ 不悬停直接点会**卡在 Playwright 的命中检查上**（元素收不到指针事件），
 * 表现是用例超时 —— 不是应用坏了。
 *
 * ⚠️ 悬停的是**条目**，不是控件本身：控件自己 `pointer-events: none`，
 * 直接 hover 它同样会卡在命中检查上。
 *
 * @param item 条目容器（`article[data-idea-id]`）。
 * @param name 控件的无障碍名（「🏷 标签」/「🗑 删除」）。
 */
export async function clickHoverOnly(item: Locator, name: string): Promise<void> {
  await item.hover();
  const button = item.getByRole('button', { name });
  await expect
    .poll(async () => button.evaluate((element) => getComputedStyle(element).pointerEvents), {
      message: `「${name}」没有因为悬停而恢复成可点`,
    })
    .toBe('auto');
  await button.click();
}

/**
 * 勾上 / 取消条目上的勾选框。
 *
 * 勾选框在桌面上也是「悬停才显形」的控件（同 {@link clickHoverOnly}）：
 * 用户得先把鼠标移到那条卡片上，它才点得到。
 *
 * @param item 条目容器（`article[data-idea-id]`）。
 * @param checked true = 勾上，false = 取消。
 */
export async function setSelected(item: Locator, checked: boolean): Promise<void> {
  await item.hover();
  const checkbox = item.getByRole('checkbox');
  await expect
    .poll(async () => checkbox.evaluate((element) => getComputedStyle(element).pointerEvents), {
      message: '勾选框没有因为悬停而恢复成可点',
    })
    .toBe('auto');
  if (checked) {
    await checkbox.check();
  } else {
    await checkbox.uncheck();
  }
}

/** e2e 目录（夹具文件所在目录）。 */
function e2eDir(): string {
  // import.meta.url 在 ESM 下可用；这里再兜一层 cwd 推断，
  // 万一被当成 CommonJS 加载（__dirname 不可用）也不至于找不到脚本。
  try {
    return dirname(fileURLToPath(import.meta.url));
  } catch {
    for (const candidate of [join(process.cwd(), 'e2e'), join(process.cwd(), 'web', 'e2e')]) {
      if (existsSync(join(candidate, 'start-server.sh'))) {
        return candidate;
      }
    }
    throw new Error('找不到 e2e 目录（start-server.sh 不在预期位置）');
  }
}

export { expect };
