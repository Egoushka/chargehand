// A fake server for tests: answers the routes the console calls from the fixtures, and records what was asked.
import { vi } from 'vitest';
import type { AccountsBody } from '../api/accounts';
import type { AnalyticsBody, AnalyticsBucket, AnalyticsGroupBy } from '../api/analytics';
import type { BatchBody } from '../api/batch';
import type { BatchTaskDetailBody } from '../api/batch-task';
import type { InboxBody } from '../api/inbox';
import type { IntakeBody, IntakeSourcesBody } from '../api/intake';
import type { OverviewBody } from '../api/overview';
import type { ConflictsBody, PullsBody } from '../api/pulls';
import type { RepositoriesBody } from '../api/repositories';
import type { SystemBody } from '../api/system';
import type { LearnReport, Me, RunSummary } from '../api/types';
import * as adm from './admin';
import * as fx from './fixtures';
import * as rx from './fixtures-runs';
import { analyticsBody, learnReport } from './learning';

export interface Scenario {
  key?: string;
  me?: Me;
  overview?: OverviewBody;
  inbox?: InboxBody;
  runs?: RunSummary[];
  batches?: Record<string, BatchBody>;
  /** A task's own view by `<batch>/<task>`. */
  tasks?: Record<string, BatchTaskDetailBody>;
  /** The text of a run's event stream by path (`/v1/runs/<id>/events`): server-sent events. */
  streams?: Record<string, string>;
  pulls?: PullsBody;
  system?: SystemBody | null;
  intake?: IntakeBody;
  intakeSources?: IntakeSourcesBody;
  conflicts?: ConflictsBody;
  repositories?: RepositoriesBody;
  accounts?: AccountsBody;
  /** Replaces the generated `analytics/v1` for every range. */
  analytics?: AnalyticsBody;
  /** Replaces the generated `/v1/learn` report for every range. */
  learn?: LearnReport;
  /** Answers by exact path (before the query), replacing the defaults: `[status, body, headers]`. */
  replies?: Record<string, [number, unknown, Record<string, string>?]>;
  /** A route that never answers: for a loading state. */
  hang?: string[];
  /** Cloudflare Access signs the browser in: a call with no key but the console's header is accepted (ADR 0071). */
  identity?: boolean;
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
  let sources: IntakeSourcesBody | undefined;
  const mock = vi.fn((input: string, init?: RequestInit) => {
    const [path = ''] = input.split('?');
    const headers = (init?.headers ?? {}) as Record<string, string>;
    const method = init?.method ?? 'GET';
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    calls.push({ path: input, method, body, headers });
    const byIdentity =
      scenario.identity &&
      headers.Authorization === undefined &&
      headers['X-Chargehand-Console'] === '1';
    if (path === '/v1/accounts/identity' && !byIdentity) {
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    if (!byIdentity && headers.Authorization !== `Bearer ${key}`)
      return Promise.resolve(new Response('', { status: 401 }));
    if (scenario.hang?.includes(path)) return new Promise<Response>(() => {});
    const replace = scenario.replies?.[`${method} ${path}`] ?? scenario.replies?.[path];
    if (replace) return Promise.resolve(json(replace[1], replace[0], replace[2]));
    const now = Date.now();
    if (path === '/v1/accounts/me' || path === '/v1/accounts/identity')
      return Promise.resolve(json(scenario.me ?? fx.me({}, now)));
    if (path === '/v1/overview')
      return Promise.resolve(json(scenario.overview ?? fx.overview({}, now)));
    if (path === '/v1/inbox')
      return Promise.resolve(json(scenario.inbox ?? fx.inbox(undefined, now)));
    if ((path === '/v1/inbox/dismiss' || path === '/v1/inbox/restore') && method === 'POST')
      return Promise.resolve(
        json({
          action: path.endsWith('dismiss') ? 'dismiss' : 'restore',
          changed: true,
          ids: (body as { ids: string[] }).ids,
          unchanged: [],
          unknown: [],
        }),
      );
    if (path === '/v1/analytics') {
      const q = new URLSearchParams(input.split('?')[1] ?? '');
      return Promise.resolve(
        json(
          scenario.analytics ??
            analyticsBody(
              {
                since: q.get('since') ?? undefined,
                bucket: (q.get('bucket') as AnalyticsBucket | null) ?? undefined,
                group_by: (q.get('group_by') as AnalyticsGroupBy | null) ?? undefined,
              },
              now,
            ),
        ),
      );
    }
    if (path === '/v1/learn')
      return Promise.resolve(
        json(
          scenario.learn ??
            learnReport(
              new URLSearchParams(input.split('?')[1] ?? '').get('since') ?? undefined,
              now,
            ),
        ),
      );
    if (path === '/v1/pulls/conflicts')
      return Promise.resolve(json(scenario.conflicts ?? adm.conflicts({}, now)));
    if (path === '/v1/pulls') return Promise.resolve(json(scenario.pulls ?? fx.pulls(now)));
    if (path === '/v1/system')
      return Promise.resolve(
        scenario.system === null
          ? json({ errors: ['owner only'] }, 403)
          : json(scenario.system ?? adm.systemFull(now)),
      );
    if (path === '/v1/intake') return Promise.resolve(json(scenario.intake ?? adm.intake({}, now)));
    if (path === '/v1/intake/sources') {
      sources = sources ?? scenario.intakeSources ?? adm.intakeSources({}, now);
      if (method === 'PUT') sources = adm.switched(sources, body);
      return Promise.resolve(json(sources));
    }
    if (path === '/v1/repositories')
      return Promise.resolve(json(scenario.repositories ?? adm.repositories(now)));
    if (path === '/v1/accounts')
      return Promise.resolve(json(scenario.accounts ?? adm.accounts(now)));
    if (path === '/v1/runs' && method === 'GET') {
      const query = new URLSearchParams(input.split('?')[1] ?? '');
      const wanted = query.get('run_id');
      const all = scenario.runs ?? (wanted ? [fx.run(wanted, {}, now)] : []);
      const matching = wanted ? all.filter((r) => r.run_id === wanted) : all;
      return Promise.resolve(json({ runs: matching.slice(0, Number(query.get('limit') ?? 50)) }));
    }
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
    const task = /^\/v1\/batches\/([^/]+)\/tasks\/([^/]+)$/.exec(path);
    if (task) {
      const id = decodeURIComponent(task[1] ?? '');
      const name = decodeURIComponent(task[2] ?? '');
      return Promise.resolve(
        json(scenario.tasks?.[`${id}/${name}`] ?? rx.taskDetail(id, name, now)),
      );
    }
    if (path.endsWith('/events'))
      return Promise.resolve(
        new Response(scenario.streams?.[path] ?? '', {
          status: 200,
          headers: { 'content-type': 'text/event-stream' },
        }),
      );
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
