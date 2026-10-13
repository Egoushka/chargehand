import { expect, type Page, test } from '@playwright/test';
import { CONTENT_SECURITY_POLICY } from '../csp.ts';
import { SCREENS } from '../src/shell/screens.ts';

const KEY = 'chk1.smoke.secret';
const ORIGIN = 'http://127.0.0.1:4173';

const me = {
  name: 'smoke',
  owner: false,
  driven: false,
  limits: { max_usd_per_day: 5 },
  spend: {
    day: { usd: 1.25, tokens: 300, resets_at: '2026-10-16T00:00:00Z' },
    month: { usd: 3.25, tokens: 400, resets_at: '2026-11-01T00:00:00Z' },
  },
  unfinished_runs: 0,
  halted: false,
  presets: ['draft'],
};

const runs = ['r-1', 'r-2'].map((run_id, i) => ({
  contract_version: 'run-summary/v1',
  run_id,
  preset: 'draft',
  status: i === 0 ? 'completed' : 'failed',
  error_code: i === 0 ? null : 'cost_cap_reached',
  started_at: `2026-10-15T11:0${i}:00Z`,
  usd: i === 0 ? 0.5 : null,
  tokens: 1234,
  repository: '/srv/app',
  kind: 'single',
  actor: 'smoke',
}));

const events = [
  { event: 'accepted', status: 'queued', run_id: 'r-1', at: '2026-10-15T11:00:00Z' },
  { event: 'run_finished', status: 'completed', run_id: 'r-1', at: '2026-10-15T11:00:05Z' },
]
  .map((e) => `event: ${e.event}\ndata: ${JSON.stringify(e)}\n\n`)
  .join('');

/** The API is mocked in the browser: the page talks to nothing but the preview server and these answers. */
async function mockApi(page: Page) {
  await page.route('**/v1/**', async (route) => {
    const { pathname } = new URL(route.request().url());
    if (route.request().headers().authorization !== `Bearer ${KEY}`)
      return route.fulfill({ status: 401, body: '' });
    if (pathname === '/v1/accounts/me') return route.fulfill({ json: me });
    if (pathname === '/v1/runs') return route.fulfill({ json: { runs } });
    if (pathname === '/v1/runs/r-1/events')
      return route.fulfill({ contentType: 'text/event-stream', body: events });
    if (pathname === '/v1/runs/r-1')
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
  });
}

/** Everything the browser would complain about: console errors, uncaught errors, CSP violations, requests to any other origin. */
function watch(page: Page) {
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

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    const violations: string[] = [];
    (window as unknown as { violations: string[] }).violations = violations;
    document.addEventListener('securitypolicyviolation', (e) =>
      violations.push(`${e.violatedDirective} ${e.blockedURI}`),
    );
  });
  await mockApi(page);
});

test('signs in, lists runs, opens one with its live events, and signs out', async ({ page }) => {
  const seen = watch(page);

  const response = await page.goto('/console/runs');
  // The preview serves the policy the server serves, so a violation fails this test.
  expect(response?.headers()['content-security-policy']).toBe(CONTENT_SECURITY_POLICY);
  await expect(page.getByLabel('API key')).toBeVisible();

  await page.getByLabel('API key').fill(KEY);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Runs' })).toBeVisible();
  await expect(page.getByRole('row')).toHaveCount(3); // header and two runs
  await expect(page.getByText('cost_cap_reached')).toBeVisible();
  expect(await page.evaluate(() => sessionStorage.getItem('chargehand.key'))).toBe(KEY);
  expect(await page.evaluate(() => localStorage.length)).toBe(0);
  expect(await page.context().cookies()).toEqual([]);

  await page.getByRole('link', { name: 'r-1' }).click();
  await expect(page).toHaveURL(/\/console\/runs\/r-1$/);
  await expect(page.getByRole('heading', { name: 'Run r-1' })).toBeVisible();
  await expect(page.getByText('Found the cause.')).toBeVisible();
  await expect(page.getByRole('listitem').filter({ hasText: 'run_finished' })).toBeVisible();

  // A deep link reloads into the same screen: the server falls back to index.html for client routes, and the key survives in the tab.
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Run r-1' })).toBeVisible();

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByLabel('API key')).toBeVisible();
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);

  expect(await seen.finish()).toEqual([]);
});

test('stores nothing for a key the server refuses', async ({ page }) => {
  const seen = watch(page);
  await page.goto('/console/runs');

  await page.getByLabel('API key').fill('wrong');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('alert')).toHaveText('The server rejected that key.');
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
  // The browser logs the 401 itself; that is the one console line this test expects.
  expect((await seen.finish()).filter((p) => !p.includes('401'))).toEqual([]);
});

test('every planned screen opens from the navigation', async ({ page }) => {
  const seen = watch(page);
  await page.goto('/console/');
  await page.getByLabel('API key').fill(KEY);
  await page.getByRole('button', { name: 'Sign in' }).click();

  for (const screen of SCREENS) {
    await page
      .getByRole('navigation')
      .getByRole('link', { name: screen.label, exact: true })
      .click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(screen.label);
  }

  expect(await seen.finish()).toEqual([]);
});
