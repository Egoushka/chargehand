// The API as the browser sees it: answered in the browser from the test fixtures, so a smoke test needs no server and no network.
import type { Page } from '@playwright/test';
import * as adm from '../src/testing/admin';
import * as fx from '../src/testing/fixtures';
import {
  analyticsBody,
  finishedBatch,
  finishedRuns,
  learnReport,
  RETRO_TEXT,
} from '../src/testing/learning';
import * as st from '../src/testing/start';

export const ORIGIN = `http://127.0.0.1:${process.env.CONSOLE_E2E_PORT ?? '4173'}`;
export const LOGIN = fx.LOGIN;

export interface Mock {
  /** Every request the page made to the API, in order. */
  calls: Array<{ method: string; path: string; body: unknown; headers: Record<string, string> }>;
}

export interface Options {
  /** A different answer for a path (before the query), or a function to build one. */
  replies?: Record<string, { status?: number; body: unknown }>;
  account?: boolean;
  empty?: boolean;
  /** Answer `/v1/learn` and the finished batch runs with the records of Analytics and Learning. */
  insight?: boolean;
}

/** Answers every /v1 call from the fixtures, and records what was asked. */
export async function mockApi(page: Page, options: Options = {}): Promise<Mock> {
  const mock: Mock = { calls: [] };
  let sources: ReturnType<typeof adm.intakeSources> | undefined;
  await page.route('**/v1/**', async (route) => {
    const request = route.request();
    const { pathname } = new URL(request.url());
    const body = request.postData() ? JSON.parse(request.postData() as string) : undefined;
    mock.calls.push({ method: request.method(), path: pathname, body, headers: request.headers() });
    if (pathname === '/v1/accounts/identity' && request.headers().authorization === undefined)
      return route.fulfill({ status: 204, body: '' });
    if (request.headers().authorization !== `Bearer ${LOGIN}`)
      return route.fulfill({ status: 401, body: '' });
    const replace =
      options.replies?.[`${request.method()} ${pathname}`] ?? options.replies?.[pathname];
    if (replace) return route.fulfill({ status: replace.status ?? 200, json: replace.body });
    const now = Date.now();
    const query = new URL(request.url()).searchParams;
    const runs =
      options.empty || options.account
        ? []
        : options.insight && query.get('kind') === 'batch'
          ? finishedRuns(now)
          : [fx.run(fx.BATCH_A, {}, now), fx.run(fx.BATCH_B, { tokens: 205_000 }, now)];
    switch (pathname) {
      case '/v1/accounts/me':
        return route.fulfill({
          json: options.account
            ? fx.me({ name: 'ci-bot', owner: false, limits: { max_usd_per_day: 10 } }, now)
            : fx.me({}, now),
        });
      case '/v1/overview':
        return route.fulfill({
          json: options.account ? fx.accountOverview(now) : fx.overview({}, now),
        });
      case '/v1/inbox':
        return route.fulfill({
          json: fx.inbox(options.empty || options.account ? [] : fx.inboxItems(now), now),
        });
      case '/v1/pulls/conflicts':
        return route.fulfill({ json: adm.conflicts({}, now) });
      case '/v1/pulls':
        return route.fulfill({
          json: options.empty ? { ...fx.pulls(now), pulls: [] } : fx.pulls(now),
        });
      case '/v1/repositories':
        return route.fulfill({ json: st.repositories(now) });
      case '/v1/learn':
        // Start a batch and Learning read the same route and want different invented records: `insight` picks Learning's.
        return route.fulfill({
          json: !options.insight
            ? st.learn()
            : options.empty
              ? { since: null, tasks: 0, groups: [] }
              : learnReport(query.get('since') ?? undefined, now),
        });
      case '/v1/analytics': {
        // The Repositories screen reads this route too, with its own invented history; `insight` picks Analytics'.
        if (!options.insight) return route.fulfill({ json: adm.repositoryHistory(now) });
        const body = analyticsBody(
          {
            since: query.get('since') ?? undefined,
            bucket: (query.get('bucket') as 'day' | null) ?? undefined,
            group_by: (query.get('group_by') as 'none' | null) ?? undefined,
          },
          now,
        );
        return route.fulfill({
          json: options.empty
            ? { ...body, tasks: { ...body.tasks, tasks: 0, points: [] }, runs: [] }
            : body,
        });
      }
      case '/v1/system':
        return options.account
          ? route.fulfill({ status: 403, json: { errors: ['owner only'] } })
          : route.fulfill({ json: adm.systemFull(now) });
      case '/v1/intake':
        return options.account
          ? route.fulfill({ status: 403, json: { errors: ['owner only'] } })
          : route.fulfill({ json: adm.intake({}, now) });
      case '/v1/intake/sources':
        if (options.account)
          return route.fulfill({ status: 403, json: { errors: ['owner only'] } });
        sources = sources ?? adm.intakeSources({}, now);
        if (request.method() === 'PUT') sources = adm.switched(sources, body);
        return route.fulfill({ json: sources });
      case '/v1/accounts':
        return options.account
          ? route.fulfill({ status: 403, json: { errors: ['owner only'] } })
          : route.fulfill({ json: adm.accounts(now) });
      case '/v1/runs':
        if (request.method() === 'POST')
          return route.fulfill({
            status: 202,
            json: { run_id: 'run-20261013-150000-abc123', status: 'queued' },
            headers: { Location: '/v1/runs/run-20261013-150000-abc123' },
          });
        return route.fulfill({ json: { runs } });
      case '/v1/halt':
      case '/v1/resume':
        return route.fulfill({ json: { halted: pathname === '/v1/halt' } });
      default: {
        const batch = /^\/v1\/batches\/([^/]+)$/.exec(pathname);
        if (batch) {
          const id = decodeURIComponent(batch[1] ?? '');
          // The finished batches of the run list carry budgets, and the newest two a retro.
          const finished = finishedRuns(now).findIndex((r) => r.run_id === id);
          return route.fulfill({
            json:
              finished < 0
                ? fx.batch(id, now)
                : finishedBatch(id, now, {
                    minutesAgo: 60 + finished * 120,
                    retro:
                      finished < 2 ? { status: 'written', tasks: 3, text: RETRO_TEXT } : undefined,
                  }),
          });
        }
        if (pathname.endsWith('/events'))
          return route.fulfill({ contentType: 'text/event-stream', body: '' });
        if (pathname.startsWith('/v1/runs/'))
          return route.fulfill({
            json: {
              contract_version: 'result/v1',
              task_id: 'r-1',
              status: 'completed',
              summary: 'Found the cause.',
              confidence: 0.8,
              usage: { input: 1, output: 1, cache_read: 0, cache_write: 0, usd: 0.5 },
            },
          });
        return route.fulfill({ status: 404, json: { errors: [`no route ${pathname}`] } });
      }
    }
  });
  return mock;
}

/** Everything the browser would complain about: console errors, uncaught errors, policy violations, requests to another origin. */
export function watch(page: Page) {
  const problems: string[] = [];
  page.on('console', (m) => m.type() === 'error' && problems.push(`console: ${m.text()}`));
  page.on('pageerror', (e) => problems.push(`uncaught: ${e.message}`));
  page.on('request', (r) => {
    if (!r.url().startsWith(ORIGIN) && !r.url().startsWith('data:'))
      problems.push(`other origin: ${r.url()}`);
  });
  return {
    problems,
    async finish() {
      const csp = await page.evaluate(
        () => (window as unknown as { violations: string[] }).violations,
      );
      return [...problems, ...csp.map((v) => `csp: ${v}`)];
    },
  };
}

/** Records policy violations from the first script on every page. */
export async function listenForViolations(page: Page) {
  await page.addInitScript(() => {
    const violations: string[] = [];
    (window as unknown as { violations: string[] }).violations = violations;
    document.addEventListener('securitypolicyviolation', (e) =>
      violations.push(`${e.violatedDirective} ${e.blockedURI}`),
    );
  });
}

export async function signIn(page: Page, path = '/console/') {
  await page.goto(path);
  await page.getByLabel('API key').fill(LOGIN);
  await page.getByRole('button', { name: 'Sign in' }).click();
}
