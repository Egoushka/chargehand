import { expect, type Page, test } from '@playwright/test';
import { FINISHED_ID, QUEUED_ID, RESEARCH_ID } from '../src/testing/answer-runs';
import { BATCH_A } from '../src/testing/fixtures';
import { overflow, SIZES, settle, shot, smallTargets, violations } from './look.ts';
import { listenForViolations, mockApi, signIn, watch } from './mock.ts';
import { mockRuns } from './mock-runs.ts';

// Runs, a batch with each of its tabs, a task as a drawer and as a page, and a research result: at a desktop and a phone width, in the dark
// and the light theme. Each look passes axe with no serious or critical violation, does not scroll sideways, and breaks no policy
// (the build is served with the server's Content-Security-Policy, so a library that injects a style or starts a blob worker fails here).

const TABS = [
  'Tasks',
  'Timeline',
  'Events',
  'Governor',
  'Spend',
  'Pull requests',
  'Artifacts',
  'Request',
] as const;

async function open(page: Page, path: string) {
  const mock = await mockApi(page);
  const runs = await mockRuns(page);
  const seen = watch(page);
  await signIn(page, path);
  return { mock, runs, seen };
}

/** Looks the page over the way every screen is looked over. */
async function check(page: Page, name: string, device: string, scheme: string) {
  await settle(page);
  expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe(scheme);
  expect(await overflow(page)).toBeLessThanOrEqual(0);
  if (device === 'phone') expect(await smallTargets(page)).toEqual([]);
  await shot(page, `${name}-${device}-${scheme}`);
  expect(await violations(page)).toEqual([]);
}

for (const [device, viewport] of Object.entries(SIZES)) {
  for (const scheme of ['dark', 'light'] as const) {
    test.describe(`${device}, ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });

      test.beforeEach(async ({ page }) => {
        await listenForViolations(page);
      });

      test('Runs: a long list, filters, a saved view and a bulk cancel', async ({ page }) => {
        const { runs, seen } = await open(page, '/console/runs');
        await expect(page.getByRole('heading', { level: 1, name: 'Runs' })).toBeVisible();

        if (device === 'desktop') {
          const table = page.getByRole('table', { name: 'Runs' });
          await expect(table.getByRole('row').nth(5)).toBeVisible();
          // The list is virtual: a hundred and some runs, a few dozen rows in the page.
          expect(await table.getByRole('row').count()).toBeLessThan(60);
          await expect(page.getByText(/Showing \d+ of \d+ loaded/)).toBeVisible();
        } else {
          await expect(page.locator('.run-card').first()).toBeVisible();
        }
        await check(page, 'runs', device, scheme);

        // A filter the server knows is asked of the server. A phone keeps the filters behind a button.
        if (device === 'phone') await page.getByRole('button', { name: /^Filters/ }).click();
        await page.getByRole('combobox', { name: 'Kind' }).selectOption('single');
        await expect.poll(() => runs.calls.some((c) => c.path.includes('kind=single'))).toBe(true);
        await expect(page.getByRole('combobox', { name: 'Kind' })).toHaveValue('single');
        await page.getByLabel('Search runs').fill('a10002');
        await expect(page.getByText(/1 of \d+ loaded runs match/)).toBeVisible();
        await check(page, 'runs-filtered', device, scheme);

        // Save the view, and it survives a reload.
        await page.getByRole('button', { name: 'Save view' }).click();
        const dialog = page.getByRole('dialog', { name: 'Save this view' });
        await dialog.getByLabel('Name').fill('One single run');
        await dialog.getByRole('button', { name: 'Save view' }).click();
        await expect(dialog).toBeHidden();
        await page.reload();
        await expect(page.getByLabel('Saved views')).toBeVisible();
        await expect(
          page.getByLabel('Saved views').locator('option', { hasText: 'View: One single run' }),
        ).toHaveCount(1);
        expect(
          await page.evaluate(() => localStorage.getItem('chargehand.console.runs.views')),
        ).toContain('One single run');
        expect(await seen.finish()).toEqual([]);
      });

      test('Runs: a bulk cancel asks first and cancels only the runs that are running', async ({
        page,
      }) => {
        test.skip(device === 'phone', 'Selecting runs is a desktop table feature');
        const { runs, seen } = await open(page, '/console/runs');
        const table = page.getByRole('table', { name: 'Runs' });
        await expect(table.getByRole('row').nth(3)).toBeVisible();
        await table
          .getByLabel(/^Select run /)
          .first()
          .check();
        await table
          .getByLabel(/^Select run /)
          .nth(1)
          .check();
        await expect(page.getByText('2 selected')).toBeVisible();
        await check(page, 'runs-selected', device, scheme);
        await page.getByRole('button', { name: /^Cancel 1 running run$/ }).click();
        const dialog = page.getByRole('alertdialog');
        await expect(dialog).toBeVisible();
        expect(await violations(page)).toEqual([]);
        expect(runs.calls.some((c) => c.path.endsWith('/cancel'))).toBe(false);
        await dialog.getByRole('button', { name: 'Cancel 1 run' }).click();
        await expect(page.getByText('Cancelling 1 of 1 run', { exact: true })).toBeVisible();
        expect(runs.calls.filter((c) => c.path.endsWith('/cancel'))).toHaveLength(1);
        expect(await seen.finish()).toEqual([]);
      });

      test('a batch: the header, the task graph laid out by ELK in a worker, and every tab', async ({
        page,
      }) => {
        // Eight tabs, each looked over with axe: more than the default half minute on a busy machine.
        test.slow();
        const { seen } = await open(page, `/console/runs/${BATCH_A}`);
        await expect(page.getByRole('heading', { level: 1, name: BATCH_A })).toBeVisible();
        await expect(page.getByText(/of 4M · \d+%/)).toBeVisible();
        await expect(page.getByText('2 of 8 done')).toBeVisible();
        const tabs = page.getByRole('tablist', { name: 'Batch sections' }).getByRole('tab');
        await expect(tabs).toHaveCount(8);

        // The graph: ELK ran in a worker that the policy allowed, and all eight nodes are drawn and are buttons.
        const graph = page.locator('.graph');
        await expect(graph).toHaveAttribute('data-layout', 'elk');
        await expect(page.locator('.react-flow__node')).toHaveCount(8);
        await expect(graph.getByRole('button', { name: /^[SMLX] t3-memory-ttl / })).toBeVisible();
        await check(page, 'batch-tasks', device, scheme);

        for (const name of TABS.slice(1)) {
          await tabs.filter({ hasText: new RegExp(`^${name}`) }).click();
          await expect(page.getByRole('tab', { name: new RegExp(`^${name}`) })).toHaveAttribute(
            'aria-selected',
            'true',
          );
          await settle(page);
          await check(page, `batch-${name.toLowerCase().replace(' ', '-')}`, device, scheme);
        }
        expect(await seen.finish()).toEqual([]);
      });

      test('a batch: the live events arrive and are filtered; the timeline shows a continuation and a pause', async ({
        page,
      }) => {
        const { seen } = await open(page, `/console/runs/${BATCH_A}?tab=events`);
        const log = page.getByRole('log', { name: 'Events of this batch' });
        await expect(log).toContainText('draft pull request opened /pull/412');
        await expect(page.getByText('Stream ended')).toBeVisible();
        await page.getByRole('button', { name: 'Governor' }).click();
        await expect(log).toContainText('observe · warn · repeated_call');
        await expect(log).not.toContainText('session container is up');
        await page.getByRole('button', { name: 'Pause' }).click();
        await expect(page.getByRole('button', { name: 'Resume' })).toBeVisible();
        await check(page, 'batch-events-filtered', device, scheme);

        await page.goto(`/console/runs/${BATCH_A}?tab=timeline`);
        await expect(page.getByText(/New starts are held until/)).toBeVisible();
        await expect(
          page.getByRole('button', { name: /t2-retry-policy, session 2/ }),
        ).toHaveAttribute('data-cont', 'true');
        expect(await seen.finish()).toEqual([]);
      });

      test('a batch: the diff of a task is read with its syntax colour, and a clone carries the request to Start a batch', async ({
        page,
      }) => {
        const { seen } = await open(page, `/console/runs/${BATCH_A}?tab=artifacts`);
        await page.getByLabel('Artifacts of').selectOption('t2-retry-policy');
        await page.getByRole('button', { name: /^Changes/ }).click();
        const diff = page.getByRole('group', { name: 'Changes: src/Payments/RetryPolicy.cs' });
        await expect(diff).toBeVisible();
        // Colour arrives with the grammar, in a chunk of its own: the token spans carry a theme token, not a literal colour.
        await expect(diff.locator('.ch-dl__c span').first()).toBeVisible();
        const colour = await diff
          .locator('.ch-dl__c span')
          .first()
          .evaluate((el) => (el as HTMLElement).style.color);
        expect(colour).toMatch(/^var\(--code-/);
        await check(page, 'batch-diff', device, scheme);

        await page.goto(`/console/runs/${BATCH_A}?tab=request`);
        const clone = page.getByRole('button', { name: 'Clone as a new batch' });
        await expect(clone).toBeEnabled();
        await clone.click();
        await expect(page).toHaveURL(/\/console\/new$/);
        const state = await page.evaluate(
          () => history.state as { usr?: { startBatch?: unknown } },
        );
        expect(JSON.stringify(state)).toContain('"startBatch"');
        expect(page.url()).not.toContain('t5-docs');
        expect(await seen.finish()).toEqual([]);
      });

      test('a task: a question is answered from the drawer, and the same task opens as a page', async ({
        page,
      }) => {
        const { mock, seen } = await open(page, `/console/runs/${BATCH_A}`);
        await page
          .getByRole('button', {
            name: device === 'phone' ? 'Open task t3-memory-ttl' : 't3-memory-ttl',
            exact: true,
          })
          .first()
          .click();

        if (device === 'desktop') {
          const drawer = page.getByRole('dialog', { name: 'Task t3-memory-ttl' });
          await expect(drawer).toBeVisible();
          await expect(drawer.getByText('The session asked', { exact: true })).toBeVisible();
          await check(page, 'task-drawer', device, scheme);
          await drawer
            .getByRole('link', { name: 'Open as a page' })
            .or(drawer.getByRole('button', { name: 'Open as a page' }))
            .click();
        }
        await expect(page).toHaveURL(new RegExp(`/console/runs/${BATCH_A}/tasks/t3-memory-ttl$`));
        await expect(page.getByRole('heading', { level: 1, name: 't3-memory-ttl' })).toBeVisible();
        await expect(page.getByText('The session asked', { exact: true })).toBeVisible();
        await check(page, 'task-page', device, scheme);

        const send = page.getByRole('button', { name: 'Answer and continue' });
        await expect(send).toBeDisabled();
        await page.getByLabel('Your answer 1').fill('From the container template.');
        await page.getByLabel('Your answer 2').fill('Size M.');
        await send.click();
        await expect(page.getByText(/Answer sent\. A new batch continues the task/)).toBeVisible();
        const post = mock.calls.find((c) => c.method === 'POST' && c.path === '/v1/runs');
        expect(post?.headers.prefer).toBe('wait=0');
        expect(post?.body).toMatchObject({
          driven: {
            tasks: [{ id: 't3-memory-ttl', answers: ['From the container template.', 'Size M.'] }],
          },
        });
        await check(page, 'task-answered', device, scheme);
        expect(await seen.finish()).toEqual([]);
      });

      test('a task as a page shows its diff, its verification and its pull request', async ({
        page,
      }) => {
        const { seen } = await open(page, `/console/runs/${BATCH_A}/tasks/t2-retry-policy`);
        await expect(
          page.getByRole('heading', { level: 1, name: 't2-retry-policy' }),
        ).toBeVisible();
        await expect(page.getByText('Passed', { exact: true })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Open PR 412' })).toBeVisible();
        await expect(
          page.getByRole('group', { name: /^Changes: src\/Payments\/RetryPolicy\.cs/ }),
        ).toBeVisible();
        await check(page, 'task-finished', device, scheme);
        expect(await seen.finish()).toEqual([]);
      });

      test('a research result: claims, the lines they cite at the commit, open questions and a signature', async ({
        page,
      }) => {
        const { seen } = await open(page, `/console/runs/${RESEARCH_ID}`);
        await expect(page.getByRole('heading', { level: 1, name: RESEARCH_ID })).toBeVisible();
        await expect(page.getByText(/Payments retry through RetryPolicy/)).toBeVisible();
        await expect(page.getByText('Partly supported')).toBeVisible();
        await expect(page.getByText('ES256')).toBeVisible();
        await check(page, 'research', device, scheme);

        await page
          .getByLabel('Repository web address')
          .fill('https://github.com/example/payments-api');
        await page.getByRole('button', { name: 'Use this address' }).click();
        const cite = page.getByRole('link', { name: 'src/Payments/RetryPolicy.cs:12-18' });
        await expect(cite).toHaveAttribute(
          'href',
          /^https:\/\/github\.com\/example\/payments-api\/blob\/a91c0ffe21b7\/src\/Payments\/RetryPolicy\.cs#L12-L18$/,
        );
        await check(page, 'research-linked', device, scheme);
        expect(await seen.finish()).toEqual([]);
      });

      test('a batch that has finished, one that has not started, and a run that is not there say so', async ({
        page,
      }) => {
        const { seen } = await open(page, `/console/runs/${FINISHED_ID}`);
        await expect(page.getByText('tasks_incomplete').first()).toBeVisible();
        await expect(page.getByRole('button', { name: 'Cancel batch' })).toHaveCount(0);
        await check(page, 'batch-finished', device, scheme);

        await page.goto(`/console/runs/${QUEUED_ID}`);
        await expect(page.getByText('No such run')).toBeVisible();
        await check(page, 'run-missing', device, scheme);
        expect(await seen.finish()).toEqual([]);
      });
    });
  }
}
