// A fake server for tests: answers the routes the console calls from the fixtures, and records what was asked.
import { vi } from 'vitest';
import type { AccountsBody } from '../api/accounts';
import type { BatchBody } from '../api/batch';
import type { InboxBody } from '../api/inbox';
import type { IntakeBody } from '../api/intake';
import type { OverviewBody } from '../api/overview';
import type { PullsBody } from '../api/pulls';
import type { RepositoriesBody } from '../api/repositories';
import type { SystemBody } from '../api/system';
import type { Me, RunSummary } from '../api/types';
import * as adm from './admin';
import * as fx from './fixtures';

export interface Scenario {
  key?: string;
  me?: Me;
  overview?: OverviewBody;
  inbox?: InboxBody;
  runs?: RunSummary[];
  batches?: Record<string, BatchBody>;
  pulls?: PullsBody;
  system?: SystemBody | null;
  intake?: IntakeBody;
  repositories?: RepositoriesBody;
  accounts?: AccountsBody;
  /** Answers by exact path (before the query), replacing the defaults: `[status, body, headers]`. */
  replies?: Record<string, [number, unknown, Record<string, string>?]>;
  /** A route that never answers: for a loading state. */
  hang?: string[];
}

export interface Call {
  path: string;
  method: string;
  body: unknown;
  headers: Record<string, string>;
}

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });

/** Installs the fake as `fetch` and returns what it was asked, in order. */
export function serveApi(scenario: Scenario = {}) {
  const key = scenario.key ?? fx.LOGIN;
  const calls: Call[] = [];
  const mock = vi.fn((input: string, init?: RequestInit) => {
    const [path = ''] = input.split('?');
    const headers = (init?.headers ?? {}) as Record<string, string>;
    const method = init?.method ?? 'GET';
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    calls.push({ path: input, method, body, headers });
    if (headers.Authorization !== `Bearer ${key}`)
      return Promise.resolve(new Response('', { status: 401 }));
    if (scenario.hang?.includes(path)) return new Promise<Response>(() => {});
    const replace = scenario.replies?.[`${method} ${path}`] ?? scenario.replies?.[path];
    if (replace) return Promise.resolve(json(replace[1], replace[0], replace[2]));
    const now = Date.now();
    if (path === '/v1/accounts/me') return Promise.resolve(json(scenario.me ?? fx.me({}, now)));
    if (path === '/v1/overview')
      return Promise.resolve(json(scenario.overview ?? fx.overview({}, now)));
    if (path === '/v1/inbox')
      return Promise.resolve(json(scenario.inbox ?? fx.inbox(undefined, now)));
    if (path === '/v1/pulls') return Promise.resolve(json(scenario.pulls ?? fx.pulls(now)));
    if (path === '/v1/system')
      return Promise.resolve(
        scenario.system === null
          ? json({ errors: ['owner only'] }, 403)
          : json(scenario.system ?? adm.systemFull(now)),
      );
    if (path === '/v1/intake') return Promise.resolve(json(scenario.intake ?? adm.intake({}, now)));
    if (path === '/v1/repositories')
      return Promise.resolve(json(scenario.repositories ?? adm.repositories(now)));
    if (path === '/v1/accounts')
      return Promise.resolve(json(scenario.accounts ?? adm.accounts(now)));
    if (path === '/v1/runs' && (init?.method ?? 'GET') === 'GET')
      return Promise.resolve(json({ runs: scenario.runs ?? [] }));
    if (path === '/v1/runs' && method === 'POST')
      return Promise.resolve(
        json({ run_id: 'run-20261013-150000-abc123', status: 'queued' }, 202, {
          Location: '/v1/runs/run-20261013-150000-abc123',
        }),
      );
    if (path === '/v1/halt' || path === '/v1/resume')
      return Promise.resolve(json({ halted: path === '/v1/halt' }));
    const batch = /^\/v1\/batches\/([^/]+)$/.exec(path);
    if (batch) {
      const id = decodeURIComponent(batch[1] ?? '');
      return Promise.resolve(json(scenario.batches?.[id] ?? fx.batch(id, now)));
    }
    if (path.endsWith('/events')) return Promise.resolve(new Response('', { status: 200 }));
    if (path.startsWith('/v1/runs/'))
      return Promise.resolve(json({ status: 'running', run_id: path.split('/').pop() }, 202));
    return Promise.resolve(json({ errors: [`no route ${input}`] }, 404));
  });
  vi.stubGlobal('fetch', mock);
  return {
    mock,
    calls,
    last: (path: string) => [...calls].reverse().find((c) => c.path.startsWith(path)),
  };
}
