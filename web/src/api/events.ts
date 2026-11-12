import type { IdeaEvent } from './types';

/**
 * SSE 客户端：连 `GET /api/events`，把推来的事件交给调用方，断线自动重连。
 *
 * <p>协议与后端 `EventEndpoints` 逐字对齐：每一帧三行加一个空行 ——
 * `id: <事件 id>`、`event: <事件类型>`、`data: <单行 JSON>`。
 * 空闲时服务端每 25 秒发一行 `: ping` **注释帧**保活；注释帧不是事件帧，
 * 浏览器不会为它派发事件，客户端不用（也不能）把它当事件处理。
 *
 * <p>`resync` 是**控制帧**（没有 `id:` 行）：服务端在两种场合发它 ——
 * ① 重连时 `Last-Event-ID` 在缓冲里补发不到；② 某个订阅者太慢、
 * 它的通道被挤掉了一条事件。两种场合的共同点是「客户端可能漏了事件」，
 * 所以客户端只遵守一条规则：**收到 resync 就重新拉一次当前列表**。
 *
 * <p>收到 resync 时还要把记住的事件 id **清零**：那个 id 是服务端用来定位
 * 补发位置的，服务端既然发了 resync 就说明它**已经补不到**了。再带着它重连，
 * 服务端只会再回一帧 resync —— 每一次重连都白白多一轮「整表重拉」。
 * 清零之后下次重连按「全新订阅」处理，正确性由「重连后整表重拉」兜住。
 */

/** SSE 端点。 */
const EVENTS_URL = '/api/events';

/** 第一次重连的等待时间。 */
const INITIAL_RETRY_MS = 2_000;

/** 重连等待时间的上限：指数退避封顶在这里。 */
const MAX_RETRY_MS = 30_000;

/**
 * 后端会推的事件类型全集（与后端 `EventTypes.All` 一致）。
 *
 * 显式列出来、逐个 `addEventListener`，而不是只挂 `onmessage`：
 * SSE 的具名事件**不会**触发 `onmessage`，不逐个注册就一个都收不到。
 */
export const EVENT_TYPES = [
  'idea.created',
  'idea.updated',
  'idea.deleted',
  'idea.restored',
  'idea.merged',
  'project.created',
  'project.updated',
  'project.deleted',
] as const;

/** 业务事件的回调。 */
export type EventListener = (e: IdeaEvent) => void;

/**
 * 坏帧的可观测出口。
 *
 * 单帧坏掉无所谓（丢掉这一帧即可），但**持续性**的协议错配
 * （服务端改了 `data` 的结构）如果完全没有出口，表现就只是
 * 「列表不更新」——没有任何线索可查。这个回调就是那个线索。
 */
export type EventErrorListener = (error: Error) => void;

/** `connectEvents` 返回的连接句柄。 */
export interface EventStream {
  /** 断开连接，并且不再重连。 */
  close(): void;
  /** 最后收到的事件 id（重连时拿它向服务端要补发）。 */
  readonly lastEventId: string | null;
}

/** `connectEvents` 的回调集合。 */
export interface EventHandlers {
  /** 收到一条业务事件。 */
  onEvent: EventListener;
  /** 需要重新拉取当前列表（服务端发了 resync，或重连后可能有漏掉的事件）。 */
  onResync: () => void;
  /** 连接状态变化：连上了 true，断开了 false。 */
  onStatus?: (connected: boolean) => void;
  /**
   * 收到坏帧时报出来（**可选**，不传就不调用）。
   *
   * 坏帧本身已经被丢掉、不会流进 `onEvent`；这个回调只是把
   * 「丢了什么、为什么丢」报出去，免得协议错配完全隐形。
   */
  onError?: EventErrorListener;
}

/**
 * 校验一帧报文的形状。
 *
 * 返回 `null` 表示通过；返回字符串表示坏在哪（会写进报给 `onError` 的消息里）。
 *
 * <p>为什么不能只判「是不是对象」：`JSON.parse('[]')` 解析出来也是对象
 * （数组），不排掉的话一帧数组会被当成事件交给 `onEvent`。
 * 这里是把服务端报文送进应用的**唯一入口**，未经校验的报文不能进。
 */
function validatePayload(value: unknown): string | null {
  if (value === null || typeof value !== 'object') {
    return 'data 不是对象';
  }
  if (Array.isArray(value)) {
    return 'data 是数组，不是事件对象';
  }
  const record = value as Record<string, unknown>;
  if (typeof record.type !== 'string') {
    return 'type 缺失或不是字符串';
  }
  // changed / mergedFrom 后端恒定写出，值要么是 null、要么是字符串数组。
  // 键整个缺失（undefined）也算坏帧：那是协议错配，不是「没有这个概念」。
  if (!(record.changed === null || Array.isArray(record.changed))) {
    return 'changed 缺失，或既不是 null 也不是数组';
  }
  if (!(record.mergedFrom === null || Array.isArray(record.mergedFrom))) {
    return 'mergedFrom 缺失，或既不是 null 也不是数组';
  }
  return null;
}

/**
 * 解析一帧的 `data`。
 *
 * 解析不了或形状不对就丢掉这一帧：坏帧不该把整条流搞崩，
 * 后面的事件照样要能收。但坏帧**不能静默** —— 持续性的协议错配
 * （服务端改了 `data` 的结构）如果完全看不出来，表现就只是「列表不更新」。
 * 所以坏帧会带着原因报给 `onError`（不传 `onError` 就只丢掉）。
 */
function parseEvent(data: string): { event: IdeaEvent | null; error: string | null } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    return { event: null, error: 'data 不是合法 JSON' };
  }
  const reason = validatePayload(parsed);
  if (reason !== null) {
    return { event: null, error: reason };
  }
  return { event: parsed as IdeaEvent, error: null };
}

/**
 * 连上事件流。
 *
 * <p>**重连时怎么续上**：`EventSource` 不能自定义请求头，浏览器原生重连时
 * 才会自动带 `Last-Event-ID`；我们重建连接时只能把最后的事件 id 放进
 * 查询参数（`?lastEventId=<id>`）。
 *
 * <p>后端**两种都收**（`EventEndpoints.ResolveLastEventId`）：
 * `Last-Event-ID` **请求头优先**、`?lastEventId=` **查询参数兜底**。
 * 所以浏览器重建连接时带的这个查询参数是**能生效**的 ——
 * 断线期间的事件会被补发回来。
 *
 * <p>即便如此，重连成功后仍然统一触发一次 `onResync`（整表重拉），
 * 当**冗余安全网**：补发可能失败（服务端重启过、缓冲里已经没有那个 id），
 * 而事件 id 是单调 UUIDv7、不是连续序号，客户端自己检不出这个空洞
 * （这正是服务端要发 resync 的原因）。宁可多拉一次列表，也不能静默漏数据。
 */
export function connectEvents(handlers: EventHandlers): EventStream {
  let source: EventSource | null = null;
  let lastEventId: string | null = null;
  let retryTimer: ReturnType<typeof setTimeout> | null = null;
  let retryDelay = INITIAL_RETRY_MS;
  /** 建过几次连接：大于 1 就说明是重连，重连后要重新拉列表。 */
  let connections = 0;
  /** 主动关掉了：关掉之后不再重连。 */
  let closed = false;

  function buildUrl(): string {
    if (lastEventId === null) {
      return EVENTS_URL;
    }
    return `${EVENTS_URL}?lastEventId=${encodeURIComponent(lastEventId)}`;
  }

  /** 处理一帧业务事件。 */
  function handleFrame(ev: MessageEvent): void {
    // 帧里的 `id:` 行就是事件 id —— 记住它，重连时要用。
    if (ev.lastEventId) {
      lastEventId = ev.lastEventId;
    }
    const { event, error } = parseEvent(ev.data);
    if (event === null) {
      // 坏帧丢掉，但要让调用方看得见：否则协议错配完全隐形。
      handlers.onError?.(new Error(`SSE 坏帧已丢弃：${error}`));
      return;
    }
    handlers.onEvent(event);
  }

  /** 断开并安排重连。 */
  function scheduleReconnect(): void {
    if (closed || retryTimer !== null) {
      return;
    }
    // 自己重建连接前先把旧的关掉：浏览器原生重连我们不要
    //（它不按我们的退避走，也没法保证带上我们要的续传参数）。
    if (source) {
      source.close();
      source = null;
    }

    const delay = retryDelay;
    retryDelay = Math.min(retryDelay * 2, MAX_RETRY_MS);
    retryTimer = setTimeout(() => {
      retryTimer = null;
      if (closed) {
        return;
      }
      open();
    }, delay);
  }

  /** 建一次连接并挂上监听器。 */
  function open(): void {
    connections += 1;
    const current = new EventSource(buildUrl(), { withCredentials: true });
    source = current;

    current.onopen = () => {
      // 连上了：退避重置回 2 秒（这一轮连接是好的）。
      retryDelay = INITIAL_RETRY_MS;
      handlers.onStatus?.(true);
      if (connections > 1) {
        // 重连：断线期间可能漏了事件（见上面的说明），让调用方重拉一次列表。
        handlers.onResync();
      }
    };

    current.onerror = () => {
      handlers.onStatus?.(false);
      scheduleReconnect();
    };

    for (const type of EVENT_TYPES) {
      current.addEventListener(type, (ev: Event) => {
        handleFrame(ev as MessageEvent);
      });
    }

    // resync 是控制帧：它不是业务事件，不能进 onEvent。
    current.addEventListener('resync', () => {
      // 把记住的事件 id 清零：服务端发了 resync 就说明它补不到那个 id 了，
      // 再带着它重连只会再要一帧 resync（于是每次重连都多一轮整表重拉）。
      // 清零之后按「全新订阅」重连，正确性由 open 时的整表重拉兜住。
      lastEventId = null;
      handlers.onResync();
    });
  }

  open();

  return {
    close(): void {
      closed = true;
      if (retryTimer !== null) {
        clearTimeout(retryTimer);
        retryTimer = null;
      }
      if (source) {
        source.close();
        source = null;
      }
      handlers.onStatus?.(false);
    },
    get lastEventId(): string | null {
      return lastEventId;
    },
  };
}
