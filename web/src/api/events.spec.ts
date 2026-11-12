import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { connectEvents } from './events';
import type { IdeaEvent } from './types';

/**
 * 假的 `EventSource`。
 *
 * jsdom 与 Node 24 都不提供 `EventSource`（只有真实浏览器有），
 * 所以这里造一个替身：记下每个实例的 URL 与凭证、记下注册的监听器，
 * 并让测试手动触发 open / error / 事件帧。
 */
class FakeEventSource {
  /** 按创建顺序记录所有实例：测试要断言「重连时连的是哪个 URL」。 */
  static instances: FakeEventSource[] = [];

  readonly url: string;
  readonly withCredentials: boolean;

  /** 0 = CONNECTING，1 = OPEN，2 = CLOSED。 */
  readyState = 0;
  closed = false;
  onopen: ((ev: Event) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;

  private readonly listeners = new Map<string, Set<(ev: Event) => void>>();

  constructor(url: string, init?: EventSourceInit) {
    this.url = url;
    this.withCredentials = init?.withCredentials ?? false;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (ev: Event) => void): void {
    const set = this.listeners.get(type) ?? new Set<(ev: Event) => void>();
    set.add(listener);
    this.listeners.set(type, set);
  }

  removeEventListener(type: string, listener: (ev: Event) => void): void {
    this.listeners.get(type)?.delete(listener);
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }

  // ---- 测试专用 ----

  emitOpen(): void {
    this.readyState = 1;
    this.onopen?.(new Event('open'));
  }

  emitError(): void {
    this.onerror?.(new Event('error'));
  }

  /** 手动送一帧事件。`lastEventId` 对应帧里的 `id:` 行。 */
  emit(type: string, data: string, lastEventId = ''): void {
    const ev = new MessageEvent(type, { data, lastEventId });
    for (const listener of [...(this.listeners.get(type) ?? [])]) {
      listener(ev);
    }
  }
}

/** 造一条合法的事件载荷。 */
function makeEvent(type: string, id: string): IdeaEvent {
  return {
    id,
    type,
    timestamp: '2026-11-12T00:00:00Z',
    project: null,
    idea: null,
    changed: null,
    mergedFrom: null,
  };
}

/** 连上事件流，返回回调的替身与连接句柄。 */
function setup() {
  const onEvent = vi.fn();
  const onResync = vi.fn();
  const onStatus = vi.fn();
  const onError = vi.fn();
  const stream = connectEvents({ onEvent, onResync, onStatus, onError });
  return { onEvent, onResync, onStatus, onError, stream };
}

beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal('EventSource', FakeEventSource);
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('connectEvents', () => {
  it('连的是 /api/events，并且带上凭证', () => {
    setup();

    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/events');
    expect(source.withCredentials).toBe(true);
  });

  it('收到事件时把解析后的载荷交给 onEvent，并记住事件 id', () => {
    const { onEvent, stream } = setup();
    const evt = makeEvent('idea.created', 'e1');

    FakeEventSource.instances[0].emit('idea.created', JSON.stringify(evt), 'e1');

    expect(onEvent).toHaveBeenCalledTimes(1);
    expect(onEvent).toHaveBeenCalledWith(evt);
    expect(stream.lastEventId).toBe('e1');
  });

  it('八种事件类型都会转给 onEvent', () => {
    const { onEvent } = setup();
    // 类型清单写死在测试里：不能从实现里 import，否则实现少一个类型测试也发现不了。
    const types = [
      'idea.created',
      'idea.updated',
      'idea.deleted',
      'idea.restored',
      'idea.merged',
      'project.created',
      'project.updated',
      'project.deleted',
    ];

    types.forEach((type, index) => {
      const evt = makeEvent(type, `e${index}`);
      FakeEventSource.instances[0].emit(type, JSON.stringify(evt), `e${index}`);
    });

    expect(onEvent).toHaveBeenCalledTimes(types.length);
    expect(onEvent.mock.calls.map((call) => (call[0] as IdeaEvent).type)).toEqual(types);
  });

  it('resync 帧触发 onResync，不会被当成业务事件', () => {
    const { onEvent, onResync, stream } = setup();

    FakeEventSource.instances[0].emit('resync', '{}');

    expect(onResync).toHaveBeenCalledTimes(1);
    expect(onEvent).not.toHaveBeenCalled();
    // resync 帧没有 id 行，不能拿它当 Last-Event-ID 继续补发。
    expect(stream.lastEventId).toBeNull();
  });

  it('收到 resync 后清掉记住的事件 id，下次重连不再带上它', () => {
    const { onResync, stream } = setup();
    const source = FakeEventSource.instances[0];
    source.emit('idea.updated', JSON.stringify(makeEvent('idea.updated', 'e1')), 'e1');
    expect(stream.lastEventId).toBe('e1');

    source.emit('resync', '{}');

    expect(onResync).toHaveBeenCalledTimes(1);
    // 服务端补不到这个 id 了：再带着它重连只会再要一帧 resync。
    expect(stream.lastEventId).toBeNull();

    source.emitError();
    vi.advanceTimersByTime(2000);
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe('/api/events');
  });

  it('数据不是合法 JSON 时丢掉这一帧，后续事件照常处理', () => {
    const { onEvent } = setup();
    const source = FakeEventSource.instances[0];

    source.emit('idea.created', 'not-json', 'e1');
    expect(onEvent).not.toHaveBeenCalled();

    source.emit('idea.created', JSON.stringify(makeEvent('idea.created', 'e2')), 'e2');
    expect(onEvent).toHaveBeenCalledTimes(1);
  });

  it('形状不对的帧一律丢掉，不进 onEvent', () => {
    const { onEvent } = setup();
    const source = FakeEventSource.instances[0];

    // 未经校验的报文不能进应用：数组、缺 type、changed/mergedFrom 不合约定
    // 全都要当坏帧丢掉。
    const badPayloads = [
      '[]', // 数组：typeof 也是 'object'
      'null',
      '"just-a-string"',
      JSON.stringify({ id: 'e1', changed: null, mergedFrom: null }), // 缺 type
      JSON.stringify({ id: 'e1', type: 42, changed: null, mergedFrom: null }), // type 不是字符串
      JSON.stringify({ id: 'e1', type: 'idea.updated', mergedFrom: null }), // 缺 changed
      JSON.stringify({ id: 'e1', type: 'idea.updated', changed: null }), // 缺 mergedFrom
      JSON.stringify({
        id: 'e1',
        type: 'idea.updated',
        changed: 'title',
        mergedFrom: null,
      }), // changed 不是数组
      JSON.stringify({
        id: 'e1',
        type: 'idea.updated',
        changed: null,
        mergedFrom: 42,
      }), // mergedFrom 不是数组
    ];

    badPayloads.forEach((payload) => {
      source.emit('idea.updated', payload, 'e1');
    });

    expect(onEvent).not.toHaveBeenCalled();
  });

  it('坏帧会报给 onError', () => {
    const { onEvent, onError } = setup();

    FakeEventSource.instances[0].emit('idea.created', '[]', 'e1');

    expect(onEvent).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toBeInstanceOf(Error);
  });

  it('不传 onError 时坏帧只是被丢掉，不会抛异常', () => {
    const onEvent = vi.fn();
    const onResync = vi.fn();
    connectEvents({ onEvent, onResync });

    expect(() => {
      FakeEventSource.instances[0].emit('idea.created', '[]', 'e1');
    }).not.toThrow();
    expect(onEvent).not.toHaveBeenCalled();
  });

  it('断线后关闭当前连接，按 2 秒退避重连，重连的 URL 带上最后的事件 id', () => {
    const { stream } = setup();
    const source = FakeEventSource.instances[0];
    source.emit('idea.updated', JSON.stringify(makeEvent('idea.updated', 'e1')), 'e1');

    source.emitError();

    expect(source.closed).toBe(true);
    vi.advanceTimersByTime(1999);
    expect(FakeEventSource.instances).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe('/api/events?lastEventId=e1');
    expect(stream.lastEventId).toBe('e1');
  });

  it('连续失败时退避按 2 秒、4 秒、8 秒、16 秒递增', () => {
    setup();

    const delays = [2000, 4000, 8000, 16000];
    delays.forEach((delay) => {
      const before = FakeEventSource.instances.length;
      FakeEventSource.instances[before - 1].emitError();

      vi.advanceTimersByTime(delay - 1);
      expect(FakeEventSource.instances).toHaveLength(before);

      vi.advanceTimersByTime(1);
      expect(FakeEventSource.instances).toHaveLength(before + 1);
    });
  });

  it('退避最长 30 秒', () => {
    setup();

    const delays = [2000, 4000, 8000, 16000, 30000, 30000];
    delays.forEach((delay) => {
      const before = FakeEventSource.instances.length;
      FakeEventSource.instances[before - 1].emitError();

      vi.advanceTimersByTime(delay - 1);
      expect(FakeEventSource.instances).toHaveLength(before);

      vi.advanceTimersByTime(1);
      expect(FakeEventSource.instances).toHaveLength(before + 1);
    });
  });

  it('close() 之后不再重连', () => {
    const { stream } = setup();

    stream.close();
    FakeEventSource.instances[0].emitError();
    vi.advanceTimersByTime(60_000);

    expect(FakeEventSource.instances).toHaveLength(1);
  });

  it('退避等待期间 close() 会取消那次待执行的重连', () => {
    const { stream } = setup();

    FakeEventSource.instances[0].emitError();
    // 第一次重连要等 2 秒：现在还在退避窗口里。
    vi.advanceTimersByTime(1000);
    stream.close();

    vi.advanceTimersByTime(60_000);

    expect(FakeEventSource.instances).toHaveLength(1);
  });

  it('连接状态：连上时通知 true，断开时通知 false', () => {
    const { onStatus } = setup();
    const source = FakeEventSource.instances[0];

    source.emitOpen();
    expect(onStatus).toHaveBeenCalledWith(true);

    source.emitError();
    expect(onStatus).toHaveBeenCalledWith(false);
  });

  it('重连成功后让调用方重新拉一次列表（断线期间漏掉的事件补不回来）', () => {
    const { onResync } = setup();
    const source = FakeEventSource.instances[0];
    source.emitOpen();

    source.emitError();
    vi.advanceTimersByTime(2000);
    FakeEventSource.instances[1].emitOpen();

    expect(onResync).toHaveBeenCalledTimes(1);
  });

  it('首次连接就失败时，重连成功后同样要重新拉一次列表', () => {
    const { onResync } = setup();

    FakeEventSource.instances[0].emitError();
    vi.advanceTimersByTime(2000);
    FakeEventSource.instances[1].emitOpen();

    expect(onResync).toHaveBeenCalledTimes(1);
  });
});
