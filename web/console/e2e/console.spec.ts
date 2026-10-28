import { expect, test } from '@playwright/test';
import * as fx from '../src/testing/fixtures';
import { SIZES, settle, shot, sidewaysScroll, violations } from './looks.ts';
import { listenForViolations, mockApi, signIn, watch } from './mock.ts';

// The shell, the Overview and Needs you, at a desktop and a phone width, in the dark and the light theme. Each look also passes axe
// with no serious or critical violation, and none of them may log an error, break the page's policy or call another origin.
// CONSOLE_SHOTS=<dir> also saves a screenshot of each look there.

for (const [device, viewport] of Object.entries(SIZES)) {
  for (const scheme of ['dark', 'light'] as const) {
    test.describe(`${device}, ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });

      test.beforeEach(async ({ page }) => {
        await listenForViolations(page);
      });

      test('Overview: what the fleet is doing, with no horizontal scroll, no accessibility violation and no policy violation', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await signIn(page);

        await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible();
        await expect(page.getByText('Sessions running')).toBeVisible();
        await expect(page.getByText('$26.14')).toBeVisible();
        await expect(page.getByRole('link', { name: /^Running 1fdf01/ })).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Recent merges' })).toBeVisible();
        await expect(page.getByText('example/payments-api #409')).toBeVisible();
        await settle(page);

        expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe(scheme);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);

        if (device === 'phone') {
          const bar = page.getByRole('navigation', { name: 'Main' });
          await expect(bar).toHaveClass(/ch-tabbar/);
          await expect(page.locator('.ch-rail')).toHaveCount(0);
          const targets = await bar
            .locator('a, button')
            .evaluateAll((els) => els.map((el) => el.getBoundingClientRect().height));
          expect(targets).toHaveLength(5);
          expect(Math.min(...targets)).toBeGreaterThanOrEqual(44);
          // Every control in the content is a 44 px target on a phone.
          const small = await page
            .locator('main .ch-btn, main .ch-ibtn, main a.ch-btn')
            .evaluateAll((els) =>
              els.map((el) => el.getBoundingClientRect().height).filter((h) => h < 44),
            );
          expect(small).toEqual([]);
        } else {
          await expect(page.getByRole('navigation', { name: 'Main' })).toHaveClass(/ch-rail/);
          await expect(page.locator('.ch-tabbar')).toHaveCount(0);
        }

        await shot(page, `overview-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Needs you: a question, a pull request and a failure, and the answer goes out', async ({
        page,
      }) => {
        const mock = await mockApi(page);
        const seen = watch(page);
        await signIn(page);
        const nav = page.getByRole('navigation', { name: 'Main' });
        await nav.getByRole('link', { name: /^Needs you/ }).click();

        await expect(page.getByRole('heading', { level: 1, name: 'Needs you' })).toBeVisible();
        await expect(page.getByRole('article')).toHaveCount(3);
        await expect(
          page.getByText(
            'Should the TTL come from the environment or from the container template?',
          ),
        ).toBeVisible();
        await expect(page.getByRole('link', { name: /Open PR 412/ })).toBeVisible();
        await expect(page.getByText('verification_failed')).toBeVisible();
        await settle(page);

        await shot(page, `needs-you-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        const send = page.getByRole('button', { name: 'Answer and continue' });
        await expect(send).toBeDisabled();
        await page.getByLabel('Your answer').fill('Use the container template.');
        await send.click();
        await expect(page.getByText(/Answer sent\. A new batch continues the task/)).toBeVisible();
        await expect(page.getByText('Answer sent to t3-memory-ttl').first()).toBeVisible();
        const post = mock.calls.find((c) => c.method === 'POST');
        expect(post?.path).toBe('/v1/runs');
        expect(post?.headers.prefer).toBe('wait=0');
        expect(post?.body).toMatchObject({
          context: { interactive: false, preset: 'driven' },
          driven: { tasks: [{ id: 't3-memory-ttl', answers: ['Use the container template.'] }] },
        });

        await settle(page);
        await shot(page, `needs-you-answered-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Needs you: repeated failures read as one group card, expanded and not', async ({
        page,
      }) => {
        await mockApi(page, { replies: { '/v1/inbox': { body: fx.groupedInbox(Date.now()) } } });
        const seen = watch(page);
        await signIn(page);
        const nav = page.getByRole('navigation', { name: 'Main' });
        await nav.getByRole('link', { name: /^Needs you/ }).click();

        const card = page.getByRole('article', { name: /6 failures: checkout_has_secrets/ });
        await expect(card).toBeVisible();
        await expect(page.getByRole('article')).toHaveCount(4);
        await settle(page);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `needs-you-grouped-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        await card.getByRole('button', { name: 'Show 3 runs' }).click();
        await expect(card.getByRole('list')).toBeVisible();
        await settle(page);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `needs-you-grouped-open-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('Needs you: decision cards in lanes, each option a 44 px target, Handled open', async ({
        page,
      }) => {
        await mockApi(page, { replies: { '/v1/inbox': { body: fx.decisionInbox(Date.now()) } } });
        const seen = watch(page);
        await signIn(page);
        const nav = page.getByRole('navigation', { name: 'Main' });
        await nav.getByRole('link', { name: /^Needs you/ }).click();

        const card = page.getByRole('article', {
          name: 'A task on example/payments-api failed its tests',
        });
        await expect(card).toBeVisible();
        for (const button of await card
          .getByRole('list', { name: /What you can do/ })
          .getByRole('button')
          .all())
          expect((await button.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
        await page.getByText('Handled', { exact: false }).first().click();
        await settle(page);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `needs-you-decisions-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await seen.finish()).toEqual([]);
      });

      test('the palette, the shortcuts and the halt dialog are modal, named, and break no policy', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await signIn(page);
        await expect(page.getByText('Sessions running')).toBeVisible();

        // The palette: ⌘K on a desktop, the search button on a phone.
        if (device === 'desktop') await page.keyboard.press('Control+k');
        else await page.getByRole('button', { name: 'Search' }).click();
        const palette = page.getByRole('dialog', { name: 'Command palette' });
        await expect(palette).toBeVisible();
        await expect(
          page.getByRole('combobox', { name: 'Search commands and runs' }),
        ).toBeFocused();
        await page.getByRole('combobox', { name: 'Search commands and runs' }).fill('77bd0a');
        await expect(palette.getByRole('option', { name: /Batch 77bd0a/ })).toBeVisible();
        await settle(page);
        await shot(page, `palette-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        await page.keyboard.press('Escape');
        await expect(palette).toBeHidden();

        // The halt dialog asks first and says what stops; Keep running leaves the server as it was.
        const halt = page.getByRole('button', { name: 'Halt server' });
        await halt.scrollIntoViewIfNeeded();
        await halt.click();
        const dialog = page.getByRole('alertdialog', { name: 'Halt the server?' });
        await expect(dialog).toBeVisible();
        await expect(dialog).toContainText('5 running sessions stop');
        await shot(page, `halt-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        await dialog.getByRole('button', { name: 'Keep running' }).click();
        await expect(dialog).toBeHidden();
        await expect(halt).toBeFocused(); // focus goes back to what opened it

        if (device === 'desktop') {
          await page.keyboard.press('?');
          const sheet = page.getByRole('dialog', { name: 'Keyboard shortcuts' });
          await expect(sheet).toBeVisible();
          await expect(sheet).toContainText('Command palette');
          await shot(page, `shortcuts-${device}-${scheme}`);
          expect(await violations(page)).toEqual([]);
          await page.keyboard.press('Escape');
          await expect(sheet).toBeHidden();
        } else {
          await page.getByRole('button', { name: 'More' }).click();
          const more = page.getByRole('dialog', { name: 'More' });
          await expect(more).toBeVisible();
          await shot(page, `more-${device}-${scheme}`);
          expect(await violations(page)).toEqual([]);
          await page.keyboard.press('Escape');
          await expect(more).toBeHidden();
        }

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

  test('the rail collapses with [, keeps each name in a tooltip, and the account menu opens: none of it breaks the policy', async ({
    page,
  }) => {
    await mockApi(page);
    const seen = watch(page);
    await signIn(page);
    const rail = page.getByRole('navigation', { name: 'Main' });
    await expect(rail).not.toHaveClass(/ch-rail--collapsed/);

    await page.locator('body').click({ position: { x: 700, y: 400 } });
    await page.keyboard.press('[');
    await expect(rail).toHaveClass(/ch-rail--collapsed/);
    await rail.getByRole('link', { name: 'Runs' }).hover();
    await expect(page.getByRole('tooltip', { name: 'Runs' })).toBeVisible();
    await shot(page, 'overview-rail-collapsed');
    expect(await violations(page)).toEqual([]);

    await page.getByRole('button', { name: 'Account' }).click();
    await expect(page.getByRole('menuitem', { name: 'Sign out' })).toBeVisible();
    await expect(page.getByText(/Owner key · today \$26\.14/)).toBeVisible();
    await shot(page, 'account-menu');
    expect(await violations(page)).toEqual([]);
    await page.keyboard.press('Escape');

    expect(await seen.finish()).toEqual([]);
  });

  test('the density and theme choices stay across a reload, in this browser only', async ({
    page,
  }) => {
    await mockApi(page);
    const seen = watch(page);
    await signIn(page);
    await page.getByRole('button', { name: 'Toggle dark and light theme' }).click();
    await page.getByRole('button', { name: /^Row density/ }).click();
    expect(await page.evaluate(() => document.documentElement.dataset.theme)).not.toBe('');
    const before = await page.evaluate(() => [
      document.documentElement.dataset.theme,
      document.body.dataset.density,
    ]);
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Overview' })).toBeVisible();
    expect(
      await page.evaluate(() => [
        document.documentElement.dataset.theme,
        document.body.dataset.density,
      ]),
    ).toEqual(before);
    expect(await page.evaluate(() => Object.keys(localStorage))).toEqual(['chargehand.console']);
    expect(await seen.finish()).toEqual([]);
  });

  test('Overview and Needs you when there is nothing to show say why, and offer the way on', async ({
    page,
  }) => {
    await mockApi(page, { empty: true });
    const seen = watch(page);
    await signIn(page);
    await expect(page.getByText('No batch is running')).toBeVisible();
    await expect(page.getByText('Nothing needs you')).toBeVisible();
    await expect(page.getByText('Nothing merged')).toBeVisible();
    await settle(page);
    await shot(page, 'overview-empty');
    expect(await violations(page)).toEqual([]);
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: /^Needs you/ })
      .click();
    await expect(page.getByText('No task is waiting for an answer')).toBeVisible();
    await shot(page, 'needs-you-empty');
    expect(await violations(page)).toEqual([]);
    expect(await seen.finish()).toEqual([]);
  });

  test('a failing server is shown as a failing server: the call, the status, a retry', async ({
    page,
  }) => {
    await mockApi(page, {
      replies: {
        '/v1/overview': { status: 503, body: { errors: ['timed out after 10 s'] } },
        '/v1/inbox': { status: 503, body: { errors: ['timed out after 10 s'] } },
      },
    });
    await signIn(page);
    const box = page.getByRole('alert').filter({ hasText: 'Cannot reach the server' });
    await expect(box).toContainText('GET /v1/overview');
    await expect(box).toContainText('timed out after 10 s');
    await expect(box.getByRole('button', { name: 'Retry' })).toBeVisible();
    await settle(page);
    await shot(page, 'overview-error');
    expect(await violations(page)).toEqual([]);
  });
});
