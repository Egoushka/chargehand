import { expect, type Page, test } from '@playwright/test';
import * as adm from '../src/testing/admin';
import { SIZES, settle, shot, sidewaysScroll, violations } from './looks.ts';
import { listenForViolations, mockApi, signIn, watch } from './mock.ts';

// Intake and automation, Repositories, Accounts and keys and System at a desktop and a phone width, in the dark and the light theme.
// Each look passes axe with no serious or critical violation, has no sideways scroll, and logs no error, breaks no policy and calls
// no other origin. An account key sees the owner-only screens as a plain "owner only" state and not in the navigation.
// CONSOLE_SHOTS=<dir> also saves a screenshot of each look there.

// Start a batch reads the same route, so the shared mock answers it with its own checkouts: these looks ask for the ones with a failing read.
const checkouts = { '/v1/repositories': { body: adm.repositories() } };

/** On a phone every control in the content is a 44 px target. */
const smallTargets = (page: Page) =>
  page.locator('main .ch-btn, main .ch-ibtn, main .link').evaluateAll((els) =>
    els
      .map((el) => el.getBoundingClientRect())
      .filter((r) => r.width > 0 && r.height < 44)
      .map((r) => r.height),
  );

for (const [device, viewport] of Object.entries(SIZES)) {
  for (const scheme of ['dark', 'light'] as const) {
    test.describe(`${device}, ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });

      test.beforeEach(async ({ page }) => {
        await listenForViolations(page);
      });

      test('Intake: the trigger log, the waiting triggers, the rules and the integrator policy', async ({
        page,
      }) => {
        const mock = await mockApi(page);
        const seen = watch(page);
        await signIn(page, '/console/intake');

        await expect(
          page.getByRole('heading', { level: 1, name: 'Intake and automation' }),
        ).toBeVisible();
        await expect(page.getByText(/Polling every 120 s/)).toBeVisible();
        await expect(page.getByText('1 waiting')).toBeVisible();
        await expect(
          page.getByText(/Comment by octocat on src\/Retry\.cs:41/).first(),
        ).toBeVisible();
        await expect(page.getByText(/Refused by the daily cap of starts/).first()).toBeVisible();
        await expect(page.getByText('max_attempts 3')).toBeVisible();
        await expect(
          page.getByRole('heading', { name: 'Not recorded by the server' }),
        ).toBeVisible();
        await settle(page);

        expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe(scheme);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        if (device === 'phone') expect(await smallTargets(page)).toEqual([]);
        else await expect(page.getByRole('table', { name: 'Trigger log' })).toBeVisible();

        await shot(page, `intake-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        // Every source with its switch: a click sets it with one PUT and the switch follows the answer.
        await expect(page.getByText('1 running, 2 queued')).toBeVisible();
        const shop = page.getByRole('switch', { name: 'Watch example/web-shop' });
        await expect(shop).toBeChecked();
        await shop.click();
        await expect(shop).not.toBeChecked();
        expect(mock.calls.filter((c) => c.method === 'PUT').map((c) => c.body)).toEqual([
          { sources: [{ repository: 'example/web-shop', enabled: false }] },
        ]);
        if (device === 'phone') {
          const box = await page.locator('.intake-switch').first().boundingBox();
          expect(Math.min(box?.width ?? 0, box?.height ?? 0)).toBeGreaterThanOrEqual(44);
        }

        await page.getByRole('button', { name: 'Acted', exact: true }).click();
        await expect(page.getByText(/Refused by the daily cap of starts/)).toHaveCount(0);
        await shot(page, `intake-acted-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Repositories: a card per checkout with its checks, its success history and its open pull requests', async ({
        page,
      }) => {
        await mockApi(page, { replies: checkouts });
        const seen = watch(page);
        await signIn(page, '/console/repositories');

        await expect(page.getByRole('heading', { level: 1, name: 'Repositories' })).toBeVisible();
        await expect(page.getByRole('heading', { name: 'payments-api' })).toBeVisible();
        await expect(page.getByText('78% of 18 tasks')).toBeVisible();
        await expect(page.getByText('main @ a91c0ff')).toBeVisible();
        await expect(page.getByText('git could not read HEAD').first()).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Conflict radar' })).toBeVisible();
        await expect(page.getByText('7 shared files')).toBeVisible();
        await expect(page.getByText('src/Payments/Retry.cs')).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Merge train' })).toBeVisible();
        await expect(page.getByText(/After 1 and 2: shares 7 files/)).toBeVisible();
        await page.getByRole('button', { name: '2 open PRs' }).click();
        const payments = page.locator('section', {
          has: page.getByRole('heading', { name: 'payments-api' }),
        });
        await expect(payments.getByRole('link', { name: '#412' })).toBeVisible();
        await settle(page);

        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        if (device === 'phone') expect(await smallTargets(page)).toEqual([]);
        await shot(page, `repositories-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Accounts and keys: caps, spend, credential presence, keys, and edits that wait for later', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await signIn(page, '/console/accounts');

        await expect(
          page.getByRole('heading', { level: 1, name: 'Accounts and keys' }),
        ).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Keys of ci-bot' })).toBeVisible();
        await expect(page.getByText('94%').first()).toBeVisible();
        await expect(page.getByRole('button', { name: 'Add account' })).toBeDisabled();
        await expect(page.getByText('The secret item resolves.')).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Month forecast' })).toBeVisible();
        await expect(page.getByText('The forecast passes the monthly cap')).toBeVisible();
        await expect(page.getByText('Automated starts are held')).toBeVisible();
        await settle(page);

        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        if (device === 'phone') expect(await smallTargets(page)).toEqual([]);
        await shot(page, `accounts-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        await page.getByRole('button', { name: 'unfunded' }).click();
        await expect(page.getByRole('heading', { name: 'Keys of unfunded' })).toBeVisible();
        await expect(page.getByRole('switch', { name: 'allow_server_credential' })).toBeDisabled();
        await shot(page, `accounts-unfunded-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        // A key's value never reaches the page: only labels and public ids.
        expect(await page.locator('body').innerText()).not.toMatch(/chk1\.[a-z0-9]+\.[a-z0-9]+/);
        expect(await seen.finish()).toEqual([]);
      });

      test('System: version, runner, containers, presets, pinned images and the redacted profile; halt asks first', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await signIn(page, '/console/system');

        await expect(page.getByRole('heading', { level: 1, name: 'System' })).toBeVisible();
        await expect(page.getByText('0.9.1').first()).toBeVisible();
        await expect(page.getByText('t3-memory-ttl · 1fdf01').first()).toBeVisible();
        await expect(page.getByText('Not pinned')).toBeVisible();
        await expect(page.getByRole('region', { name: 'Redacted profile' })).toBeVisible();
        await settle(page);

        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        if (device === 'phone') expect(await smallTargets(page)).toEqual([]);
        await shot(page, `system-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        const halt = page.getByRole('button', { name: 'Halt server' });
        await halt.scrollIntoViewIfNeeded();
        await halt.click();
        const dialog = page.getByRole('alertdialog', { name: 'Halt the server?' });
        await expect(dialog).toBeVisible();
        await shot(page, `system-halt-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        await dialog.getByRole('button', { name: 'Keep running' }).click();
        await expect(dialog).toBeHidden();
        expect(await seen.finish()).toEqual([]);
      });

      test('an account key sees an "owner only" state on the three owner screens and not in the navigation', async ({
        page,
      }) => {
        const mock = await mockApi(page, { account: true });
        const seen = watch(page);
        await signIn(page, '/console/intake');
        for (const [path, title] of [
          ['intake', 'Intake and automation'],
          ['accounts', 'Accounts and keys'],
          ['system', 'System'],
        ] as const) {
          // The first screen is the one signing in opened: leaving it before the key is kept would sign out.
          if (path !== 'intake') await page.goto(`/console/${path}`);
          await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible();
          await expect(page.getByText('This screen needs the owner key')).toBeVisible();
          await settle(page);
          await shot(page, `owner-only-${path}-${device}-${scheme}`);
          expect(await violations(page)).toEqual([]);
          expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        }
        // The shell lists no owner-only screen for an account (the rail on a desktop, the More sheet on a phone).
        if (device === 'desktop') {
          const nav = page.getByRole('navigation', { name: 'Main' });
          await expect(nav.getByRole('link', { name: 'Repositories' })).toBeVisible();
          for (const name of ['Intake and automation', 'Accounts and keys', 'System'])
            await expect(nav.getByRole('link', { name })).toHaveCount(0);
        } else {
          await page.getByRole('button', { name: 'More' }).click();
          const more = page.getByRole('dialog', { name: 'More' });
          await expect(more.getByRole('button', { name: 'Repositories' })).toBeVisible();
          for (const name of ['Intake and automation', 'Accounts and keys', 'System'])
            await expect(more.getByRole('button', { name })).toHaveCount(0);
        }
        // The owner-only routes were never asked for: no 403 reached the browser.
        expect(
          mock.calls.filter((c) => ['/v1/intake', '/v1/accounts', '/v1/system'].includes(c.path)),
        ).toEqual([]);

        // An account still reads the repositories its key allows.
        await page.goto('/console/repositories');
        await expect(page.getByRole('heading', { name: 'payments-api' })).toBeVisible();
        await settle(page);
        await shot(page, `repositories-account-${device}-${scheme}`);
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

  test('intake that is off, a runner that does not answer and a failing server say so, and break no policy', async ({
    page,
  }) => {
    await mockApi(page, {
      replies: {
        '/v1/intake': { body: adm.intakeOff() },
        '/v1/accounts': { status: 503, body: { errors: ['run log is locked'] } },
        '/v1/system': {
          body: {
            ...adm.systemFull(),
            runner: {
              ...adm.systemFull().runner,
              reachable: false,
              error: 'no answer within 3 s',
              containers: [],
              container_count: null,
              host_snapshot: null,
            },
            runners: [
              {
                name: 'local',
                host: 'http://runner-a.example.test:4301',
                reachable: false,
                error: 'no answer within 3 s',
                max_sessions: 4,
              },
              ...(adm.systemFull().runners ?? []).slice(1),
            ],
          },
        },
      },
    });
    const seen = watch(page);
    await signIn(page, '/console/intake');
    await expect(page.getByText('GitHub intake is off')).toBeVisible();
    await settle(page);
    await shot(page, 'intake-off');
    expect(await violations(page)).toEqual([]);

    await page.goto('/console/system');
    await expect(page.getByText('The runner does not answer')).toBeVisible();
    const runners = page.getByRole('table', { name: 'Runners' });
    await expect(runners.getByText('unreachable')).toBeVisible();
    await expect(runners.getByText('draining')).toBeVisible();
    await settle(page);
    await shot(page, 'system-runner-down');
    expect(await violations(page)).toEqual([]);

    await page.goto('/console/accounts');
    const box = page.getByRole('alert').filter({ hasText: 'Cannot read the accounts' });
    await expect(box).toContainText('GET /v1/accounts');
    await expect(box).toContainText('run log is locked');
    await expect(box.getByRole('button', { name: 'Retry' })).toBeVisible();
    await settle(page);
    await shot(page, 'accounts-error');
    expect(await violations(page)).toEqual([]);
    // The two failing answers are the only things the browser may report.
    expect((await seen.finish()).filter((p) => !/503|Failed to load resource/.test(p))).toEqual([]);
  });

  test('repositories with nothing to show say why', async ({ page }) => {
    await mockApi(page, {
      replies: { '/v1/repositories': { body: { ...adm.repositories(), repositories: [] } } },
    });
    const seen = watch(page);
    await signIn(page, '/console/repositories');
    await expect(page.getByText('No repository to show')).toBeVisible();
    await settle(page);
    await shot(page, 'repositories-empty');
    expect(await violations(page)).toEqual([]);
    expect(await seen.finish()).toEqual([]);
  });
});
