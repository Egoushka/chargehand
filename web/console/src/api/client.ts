import { clearKey, getKey } from './key';

/** The server refused the key (HTTP 401). The stored key is gone by the time this is thrown. */
export class Unauthorized extends Error {
  constructor() {
    super('The server rejected the key.');
    this.name = 'Unauthorized';
  }
}

/** Any other failed call. `errors` are the lines the server sent, or the status when it sent none. */
export class ApiError extends Error {
  readonly status: number;
  readonly errors: string[];
  constructor(status: number, errors: string[]) {
    super(errors.join('; '));
    this.name = 'ApiError';
    this.status = status;
    this.errors = errors;
  }
}

export interface Reply<T> {
  status: number;
  ok: boolean;
  /** The parsed JSON body; `{ errors: [...] }` for a body that is not JSON. */
  data: T;
  location: string | null;
}

export interface CallOptions {
  method?: 'GET' | 'POST';
  body?: unknown;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  /** A key to try instead of the stored one (sign-in). A 401 then leaves the stored key alone. */
  key?: string;
}

export function authHeaders(
  key: string | null = getKey(),
  extra: Record<string, string> = {},
): Record<string, string> {
  return { Authorization: `Bearer ${key ?? ''}`, ...extra };
}

/** One request to the server's own origin with the bearer key. Throws Unauthorized on a 401; any other status is returned. */
export async function call<T = unknown>(
  path: string,
  options: CallOptions = {},
): Promise<Reply<T>> {
  const { method = 'GET', body, headers, signal, key } = options;
  const res = await fetch(path, {
    method,
    signal,
    cache: 'no-store',
    credentials: 'same-origin',
    referrerPolicy: 'no-referrer',
    headers: authHeaders(key ?? getKey(), {
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...headers,
    }),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) {
    if (key === undefined) clearKey('The server rejected the key. Sign in again.');
    throw new Unauthorized();
  }
  const text = await res.text();
  let data: unknown = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = { errors: [`HTTP ${res.status}: ${text.slice(0, 200)}`] };
  }
  return { status: res.status, ok: res.ok, data: data as T, location: res.headers.get('Location') };
}

/** The error lines of a failed call, for display. */
export function problems(reply: Reply<unknown>): string[] {
  const errors = (reply.data as { errors?: unknown } | null)?.errors;
  return Array.isArray(errors) && errors.length > 0 ? errors.map(String) : [`HTTP ${reply.status}`];
}

/** GET a JSON body; any status but 2xx throws ApiError. */
export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const reply = await call<T>(path, { signal });
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}

/** POST without a body and read the JSON answer; any status but 2xx throws ApiError. */
export async function postJson<T>(path: string): Promise<T> {
  const reply = await call<T>(path, { method: 'POST' });
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}
