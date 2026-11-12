import type { ApiProblem } from './types';

/** 所有业务端点的路径前缀。 */
const API_BASE = '/api';

/**
 * 后端返回的业务错误。
 *
 * 统一抛这个类，不要把裸 `Response` 到处传：
 * 调用方要拿错误码（`code`）做分支、要给人看的说明（`detail`），
 * 这两样都在 `problem` 里。
 */
export class ApiError extends Error {
  constructor(public problem: ApiProblem) {
    super(problem.detail ?? problem.title);
    this.name = 'ApiError';
  }

  /** 机器可读错误码，如 `invalid_content`；后端没给时是 undefined。 */
  get code(): string | undefined {
    return this.problem.code;
  }

  /** HTTP 状态码。 */
  get status(): number {
    return this.problem.status;
  }

  /** 给人看的说明。 */
  get detail(): string | undefined {
    return this.problem.detail;
  }
}

/**
 * 把调用方给的路径归一到 `/api` 下。
 *
 * `ideas`、`/ideas`、`/api/ideas` 都指向 `/api/ideas`，
 * 少写或多写前缀都不会打到别的地址上去。
 */
function resolvePath(path: string): string {
  const withSlash = path.startsWith('/') ? path : `/${path}`;
  if (withSlash === API_BASE || withSlash.startsWith(`${API_BASE}/`)) {
    return withSlash;
  }
  return `${API_BASE}${withSlash}`;
}

/** 拼出最终请求的 URL：路径 + 查询参数（值为 undefined 的参数不参与拼接）。 */
function buildUrl(path: string, params?: Record<string, string | undefined>): string {
  const url = resolvePath(path);
  if (!params) {
    return url;
  }
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) {
      search.append(key, value);
    }
  }
  const query = search.toString();
  return query ? `${url}?${query}` : url;
}

/**
 * 把失败响应转成 `ApiError`。
 *
 * 正常情况下响应体是 `application/problem+json`，`code` 在顶层
 * （后端的 `ApiResults` 把扩展字段平铺到了 ProblemDetails 的根上）。
 * 解析不了（反向代理返回的 HTML 错误页之类）就退回状态码。
 */
async function toApiError(response: Response): Promise<ApiError> {
  let problem: ApiProblem | undefined;
  try {
    const text = await response.text();
    if (text) {
      const parsed: unknown = JSON.parse(text);
      if (parsed && typeof parsed === 'object') {
        const body = parsed as Record<string, unknown>;
        problem = {
          title:
            typeof body.title === 'string' ? body.title : response.statusText || '请求失败',
          status: typeof body.status === 'number' ? body.status : response.status,
          ...(typeof body.detail === 'string' ? { detail: body.detail } : {}),
          ...(typeof body.code === 'string' ? { code: body.code } : {}),
        };
      }
    }
  } catch {
    // 响应体不是 JSON，按状态码兜底。
  }
  return new ApiError(
    problem ?? { title: response.statusText || '请求失败', status: response.status },
  );
}

async function request<T>(
  method: string,
  path: string,
  options: { params?: Record<string, string | undefined>; body?: unknown },
): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const init: RequestInit = {
    method,
    headers,
    // 反代前面有 basic auth，必须带上同源凭证，否则请求会被 401 掉。
    credentials: 'same-origin',
  };
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(options.body);
  }

  const response = await fetch(buildUrl(path, options.params), init);
  if (!response.ok) {
    throw await toApiError(response);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  const text = await response.text();
  if (!text) {
    return undefined as T;
  }
  return JSON.parse(text) as T;
}

/** 发 GET 请求。查询参数里值为 undefined 的会被跳过。 */
export async function apiGet<T>(
  path: string,
  params?: Record<string, string | undefined>,
): Promise<T> {
  return request<T>('GET', path, { params });
}

/** 发 POST / PUT / PATCH / DELETE 请求。`body` 为 undefined 时不带请求体。 */
export async function apiSend<T>(
  method: 'POST' | 'PUT' | 'PATCH' | 'DELETE',
  path: string,
  body?: unknown,
): Promise<T> {
  return request<T>(method, path, { body });
}
