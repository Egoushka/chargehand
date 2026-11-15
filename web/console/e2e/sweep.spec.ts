import { expect, test } from '@playwright/test';
import { SIZES, settle, shot, sidewaysScroll, violations } from './looks.ts';
import { listenForViolations, mockApi, signIn, watch } from './mock.ts';

// Settings turns two days off and saves; the Sweep screen then shows its table and starts a scan once.
// Each look passes axe with no serious or critical violation and has no sideways scroll.
for (const [device, viewport] of Object.entries(SIZES)) {
  for (const scheme of ['dark', 'light'] as const) {
    test.describe(`${device}, ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });

      test.beforeEach(async ({ page }) => {
        await listenForViolations(page);
      });

      test('Settings day toggles are saved, and the Sweep screen scans', async ({ page }) => {
        const mock = await mockApi(page);
        const seen = watch(page);
        await signIn(page, '/console/settings');
        await expect(page.getByRole('button', { name: 'Mon' })).toBeVisible();
        await settle(page);
        await shot(page, `settings-sweep-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        await page.getByRole('button', { name: 'Sat' }).click();
        await page.getByRole('button', { name: 'Sun' }).click();
        await page.getByRole('button', { name: 'Save sweep' }).click();
        await expect
          .poll(() =>
            mock.calls.filter((c) => c.method === 'PUT' && c.path === '/v1/sweep/settings'),
          )
          .toHaveLength(1);
        const saved = mock.calls.find((c) => c.method === 'PUT' && c.path === '/v1/sweep/settings');
        expect(saved?.body).toEqual({
          days: ['monday', 'tuesday', 'wednesday', 'thursday', 'friday'],
        });
        await expect(page.getByRole('button', { name: 'Sat' })).toHaveAttribute(
          'aria-pressed',
          'false',
        );

        await page.goto('/console/sweep');
        await expect(page.getByRole('heading', { level: 1, name: 'Sweep' })).toBeVisible();
        await expect(page.getByText('example/app').first()).toBeVisible();
        await expect(page.getByText(/checkout refused/).first()).toBeVisible();
        await settle(page);
        await shot(page, `sweep-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);

        await page.getByRole('button', { name: 'Scan now' }).click();
        await expect
          .poll(() => mock.calls.filter((c) => c.method === 'POST' && c.path === '/v1/sweep'))
          .toHaveLength(1);
        expect(await seen.finish()).toEqual([]);
      });
    });
  }
}
