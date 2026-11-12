import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiGet, apiSend } from './client';

function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

function problemResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('apiGet', () => {
  it('把路径放在 /api 下，并带上 same-origin 凭证', async () => {
    const fetchMock = vi.fn(async () => jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);

    await apiGet('/ideas');

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/ideas');
    expect(init.credentials).toBe('same-origin');
    expect(init.method).toBe('GET');
  });

  it('已经带 /api 前缀的路径不会重复加前缀', async () => {
    const fetchMock = vi.fn(async () => jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);

    await apiGet('/api/ideas');

    const [url] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/ideas');
  });

  it('把查询参数拼到 URL 上，undefined 的参数不参与拼接', async () => {
    const fetchMock = vi.fn(async () => jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);

    await apiGet('/ideas', { projectId: 'p1', status: 'open', tagMode: undefined });

    const [url] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/ideas?projectId=p1&status=open');
  });

  it('204 响应返回 undefined', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiGet('/ideas/1')).resolves.toBeUndefined();
  });

  it('非 2xx 且响应是 ProblemDetails 时抛 ApiError，带上 code 与 detail', async () => {
    const fetchMock = vi.fn(async () =>
      problemResponse(400, {
        type: 'about:blank',
        title: '请求失败',
        status: 400,
        detail: '想法正文为空或全是空白',
        code: 'invalid_content',
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const error = await apiGet('/ideas').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.problem.code).toBe('invalid_content');
    expect(apiError.problem.detail).toBe('想法正文为空或全是空白');
    expect(apiError.problem.status).toBe(400);
    expect(apiError.message).toBe('想法正文为空或全是空白');
  });

  it('2xx 但响应体不是 JSON 时抛 ApiError，而不是裸 SyntaxError', async () => {
    // 后端接上 index.html 兜底后，路径写错的 API 会拿到 200 + HTML。
    const fetchMock = vi.fn(
      async () => new Response('<!doctype html><title>index</title>', { status: 200 }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const error = await apiGet('/typo').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.problem.title).toContain('JSON');
    expect(apiError.problem.status).toBe(200);
  });

  it('非 2xx 且响应体不是 JSON 时抛 ApiError，problem 只剩 title 与 status', async () => {
    const fetchMock = vi.fn(async () => new Response('<html>oops</html>', { status: 502 }));
    vi.stubGlobal('fetch', fetchMock);

    const error = await apiGet('/ideas').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.problem.status).toBe(502);
    expect(apiError.problem.code).toBeUndefined();
    expect(apiError.problem.detail).toBeUndefined();
    expect(apiError.problem.title).toBeTypeOf('string');
  });
});

describe('apiSend', () => {
  it('POST 会把 body 序列化成 JSON 并设置 content-type', async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ id: 'i1' }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiSend('POST', '/ideas', { projectId: 'p1', content: '内容' });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/ideas');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('same-origin');
    expect(init.body).toBe(JSON.stringify({ projectId: 'p1', content: '内容' }));
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(result).toEqual({ id: 'i1' });
  });

  it('DELETE 不带 body 时不发 content-type', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiSend('DELETE', '/ideas/i1');

    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(init.method).toBe('DELETE');
    expect(init.body).toBeUndefined();
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined();
  });
});
