import { expect, type Page, test } from '@playwright/test';
import { SIZES, settle, shot, sidewaysScroll, violations } from './looks.ts';
import { listenForViolations, mockApi, signIn, watch } from './mock.ts';

// Analytics and Learning, at a desktop and a phone width, in the dark and the light theme. Each look passes axe with no serious
// or critical violation, has no horizontal scroll, draws its charts on a real canvas and breaks no policy: uPlot writes no
// style element and nothing is blocked. CONSOLE_SHOTS=<dir> also saves a screenshot of each look there.

/** Waits until the page has stopped scrolling: a scroll event after the focus would close a tooltip that just opened. */
const stillScrolling = (page: Page) =>
  page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        const y = () => window.scrollY + (document.querySelector('.main')?.scrollTop ?? 0);
        let last = -1;
        let steady = 0;
        const tick = () => {
          steady = y() === last ? steady + 1 : 0;
          last = y();
          if (steady >= 5) resolve();
          else requestAnimationFrame(tick);
        };
        tick();
      }),
  );

/** How many pixels of a chart's canvas are painted: a chart that drew nothing has none. */
const painted = (page: Page, name: string) =>
  page
    .getByRole('img', { name })
    .locator('canvas')
    .evaluate((canvas: HTMLCanvasElement) => {
      const context = canvas.getContext('2d');
      if (!context) return 0;
      const { data } = context.getImageData(0, 0, canvas.width, canvas.height);
      let count = 0;
      for (let i = 3; i < data.length; i += 4) if ((data[i] ?? 0) > 0) count += 1;
      return count;
    });

for (const [device, viewport] of Object.entries(SIZES)) {
  for (const scheme of ['dark', 'light'] as const) {
    test.describe(`${device}, ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });

      test.beforeEach(async ({ page }) => {
        await listenForViolations(page);
      });

      test('Analytics: spend, tokens, cache, pass rate, budgets and merges, drawn from the learning records, with every number traceable', async ({
        page,
      }) => {
        test.slow(); // a canvas, a hover and a focus: a busy machine needs the time
        await mockApi(page, { insight: true });
        const seen = watch(page);
        await signIn(page, '/console/analytics');

        await expect(page.getByRole('heading', { level: 1, name: 'Analytics' })).toBeVisible();
        await expect(page.getByText('Governor false stops')).toBeVisible();
        await expect(page.getByText('tasks.total.usd_estimate')).toBeVisible();
        const spend = 'Spend per day, stacked by repository';
        await expect(page.getByRole('img', { name: spend })).toBeVisible();
        await expect(
          page.getByRole('img', { name: 'Cache read share of input per day' }),
        ).toBeVisible();
        await expect(
          page.getByRole('list', { name: /^Pass rate by repository and size/ }),
        ).toBeVisible();
        await expect(page.getByText('Not recorded yet')).toBeVisible();
        await expect(page.getByText(/No route serves benchmark results/)).toBeVisible();
        await settle(page);

        // uPlot drew on its canvas, in the theme's colours, and added no style element.
        expect(await painted(page, spend)).toBeGreaterThan(2000);
        expect(await page.locator('style').count()).toBe(0);
        expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe(scheme);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);

        if (device === 'desktop') {
          // Hover shows the bucket's value and the route and field it is read from.
          const over = page.locator('.timechart .u-over').first();
          const box = await over.boundingBox();
          if (!box) throw new Error('the plot has no box');
          await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.5);
          const tip = page.locator('.chart-tip');
          await expect(tip).toBeVisible();
          await expect(tip).toContainText(
            'analytics/v1 tasks.points · usd_estimate · bucket_start',
          );
          await expect(tip).toContainText('Total');
          await shot(page, `analytics-hover-${device}-${scheme}`);
          await page.mouse.move(0, 0);
          await expect(tip).toBeHidden();
        }

        // At either end of the plot the hover moves inside the chart instead of narrowing to a letter per line.
        for (const at of [0.01, 0.99]) {
          const over = page.locator('.timechart .u-over').first();
          await over.scrollIntoViewIfNeeded();
          const box = await over.boundingBox();
          const chart = await page.locator('.timechart').first().boundingBox();
          if (!box || !chart) throw new Error('the plot has no box');
          await page.mouse.move(box.x + box.width * at, box.y + box.height * 0.5);
          const tip = await page.locator('.chart-tip').boundingBox();
          if (!tip) throw new Error('no hover at the edge');
          expect(tip.width).toBeGreaterThan(200);
          expect(tip.height).toBeLessThan(box.height);
          expect(tip.x).toBeGreaterThanOrEqual(chart.x);
          expect(tip.x + tip.width).toBeLessThanOrEqual(chart.x + chart.width + 0.5);
          await page.mouse.move(0, 0);
        }

        // A mark of an SVG chart is reached with the keyboard, and shows the same facts.
        const mark = page.getByRole('img', { name: /^acme\/ledger · size S: pass rate/ });
        // Scrolled into view first: Radix closes a tooltip when its trigger's scroller moves, and focus scrolls an offscreen mark.
        await mark.scrollIntoViewIfNeeded();
        await stillScrolling(page);
        await mark.focus();
        const markTip = page
          .locator('.ch-tooltip', { hasText: 'GET /v1/learn · groups[acme/ledger, S]' })
          .first();
        await expect(markTip).toBeVisible();
        // A long tooltip wraps inside the window rather than running off a phone.
        const markBox = await markTip.boundingBox();
        if (!markBox) throw new Error('the tooltip has no box');
        expect(markBox.x).toBeGreaterThanOrEqual(0);
        expect(markBox.x + markBox.width).toBeLessThanOrEqual(viewport.width);
        await page.keyboard.press('Escape');

        await shot(page, `analytics-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Analytics: the range, the bucket and the grouping are asked of the server, and the numbers are one click away as a table', async ({
        page,
      }) => {
        await mockApi(page, { insight: true });
        const seen = watch(page);
        await signIn(page, '/console/analytics');
        await expect(page.getByRole('img', { name: /^Spend per day/ })).toBeVisible();

        const range = page.waitForRequest((r) =>
          r.url().endsWith('/v1/analytics?since=90d&bucket=week&group_by=repository'),
        );
        await page
          .getByRole('group', { name: 'Range' })
          .getByRole('button', { name: '90 d' })
          .click();
        await range;
        const grouped = page.waitForRequest((r) =>
          r.url().endsWith('/v1/analytics?since=90d&bucket=week&group_by=size'),
        );
        await page
          .getByRole('group', { name: 'Group by' })
          .getByRole('button', { name: 'Size' })
          .click();
        await grouped;
        await expect(
          page.getByRole('img', { name: 'Spend per week, stacked by size' }),
        ).toBeVisible();
        await expect(page.getByText('Size S').first()).toBeVisible();
        await settle(page);

        const frame = page.locator('section', {
          has: page.getByRole('heading', { name: 'Spend over time' }),
        });
        await frame.getByRole('button', { name: 'Show data' }).click();
        const table = frame.getByRole('table', { name: 'Spend per week' });
        await expect(table).toBeVisible();
        await expect(table.getByRole('columnheader', { name: 'Size L' })).toBeVisible();
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `analytics-table-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Learning: what past tasks cost and how they ended, the budgets in force, the lessons and a retro', async ({
        page,
      }) => {
        await mockApi(page, { insight: true });
        const seen = watch(page);
        await signIn(page, '/console/learning');

        await expect(page.getByRole('heading', { level: 1, name: 'Learning' })).toBeVisible();
        await expect(page.getByText('How budgets are chosen')).toBeVisible();
        if (device === 'desktop') {
          const table = page.getByRole('table', {
            name: 'Budgets and outcomes by repository and size class',
          });
          await expect(table).toBeVisible();
          await expect(table.getByRole('row')).toHaveCount(10);
        } else {
          await expect(page.getByRole('table')).toHaveCount(0);
          await expect(page.getByText(/ tasks · median /).first()).toBeVisible();
        }
        await expect(page.getByRole('heading', { name: 'Budgets in force' })).toBeVisible();
        await expect(page.getByText(/p80 of 23 past tasks, this repository/)).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Lessons' })).toBeVisible();
        await expect(
          page.getByRole('article').filter({ hasText: 'Evidence:' }).first(),
        ).toBeVisible();
        await expect(page.getByText(/cost 1\.4x its budget/).first()).toBeVisible();
        await expect(page.getByText("a model's advice")).toBeVisible();
        await settle(page);

        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `learning-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });
    });
  }
}

test.describe('desktop only', () => {
  test.use({ viewport: SIZES.desktop });

  test.beforeEach(async ({ page }) => {
    await listenForViolations(page);
  });

  test('Analytics and Learning with no finished task say why, and offer the way on', async ({
    page,
  }) => {
    await mockApi(page, { empty: true, insight: true });
    const seen = watch(page);
    await signIn(page, '/console/analytics');
    await expect(page.getByText('No finished tasks in this range')).toBeVisible();
    await expect(page.getByRole('button', { name: 'All' })).toBeVisible();
    await settle(page);
    await shot(page, 'analytics-empty');
    expect(await violations(page)).toEqual([]);

    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Learning' })
      .click();
    await expect(page.getByText('Nothing learned yet')).toBeVisible();
    await expect(page.getByText(/A size class needs 20 finished tasks in 60 days/)).toBeVisible();
    await settle(page);
    await shot(page, 'learning-empty');
    expect(await violations(page)).toEqual([]);
    expect(await seen.finish()).toEqual([]);
  });

  test('a failing server is shown as one: the call, the answer, a retry, and the rest of the screen stays', async ({
    page,
  }) => {
    await mockApi(page, {
      insight: true,
      replies: { '/v1/learn': { status: 500, body: { errors: ['the learning store is locked'] } } },
    });
    await signIn(page, '/console/analytics');
    const box = page.getByRole('alert').filter({ hasText: 'GET /v1/learn' }).first();
    await expect(box).toContainText('the learning store is locked');
    await expect(page.getByRole('img', { name: /^Spend per day/ })).toBeVisible();
    await settle(page);
    await shot(page, 'analytics-error');
    expect(await violations(page)).toEqual([]);

    await mockApi(page, {
      insight: true,
      replies: { '/v1/analytics': { status: 503, body: { errors: ['timed out after 10 s'] } } },
    });
    await page.reload();
    const records = page.getByRole('alert').filter({ hasText: 'Cannot read the learning records' });
    await expect(records).toContainText('GET /v1/analytics');
    await expect(records.getByRole('button', { name: 'Retry' })).toBeVisible();
    await settle(page);
    await shot(page, 'analytics-error-records');
    expect(await violations(page)).toEqual([]);
  });

  test('an account key sees its own tasks and no grouping by account', async ({ page }) => {
    await mockApi(page, { account: true, insight: true });
    const seen = watch(page);
    await signIn(page, '/console/analytics');
    await expect(page.getByText(/your own tasks/)).toBeVisible();
    const groupBy = page.getByRole('group', { name: 'Group by' });
    await expect(groupBy.getByRole('button', { name: 'Size' })).toBeVisible();
    await expect(groupBy.getByRole('button', { name: 'Account' })).toHaveCount(0);
    expect(await seen.finish()).toEqual([]);
  });
});
