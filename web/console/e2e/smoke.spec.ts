import { expect, test } from '@playwright/test';
import { CONTENT_SECURITY_POLICY } from '../csp.ts';
import { SCREENS } from '../src/shell/screens.ts';
import { LOGIN, listenForViolations, mockApi, signIn, watch } from './mock.ts';

test.beforeEach(async ({ page }) => {
  await listenForViolations(page);
  await mockApi(page);
});

test('signs in, lists runs, opens a batch with its tasks, and signs out', async ({ page }) => {
  const seen = watch(page);

  const response = await page.goto('/console/runs');
  // The preview serves the policy the server serves, so a violation fails this test.
  expect(response?.headers()['content-security-policy']).toBe(CONTENT_SECURITY_POLICY);
  await expect(page.getByLabel('API key')).toBeVisible();

  await page.getByLabel('API key').fill(LOGIN);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Runs' })).toBeVisible();
  await expect(page.getByRole('table', { name: 'Runs' }).getByRole('row')).toHaveCount(3); // header and two runs
  expect(await page.evaluate(() => sessionStorage.getItem('chargehand.key'))).toBe(LOGIN);
  expect(await page.context().cookies()).toEqual([]);

  await page.getByRole('link', { name: 'run-20261013-124239-1fdf01' }).click();
  await expect(page).toHaveURL(/\/console\/runs\/run-20261013-124239-1fdf01$/);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('run-20261013-124239-1fdf01');
  await expect(page.getByRole('tab', { name: /^Tasks/ })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('table', { name: 'Tasks' }).getByRole('row')).toHaveCount(7); // header and six tasks

  // A deep link reloads into the same screen: the server falls back to index.html for client routes, and the key survives in the tab.
  await page.reload();
  await expect(page.getByRole('heading', { level: 1 })).toContainText('run-20261013-124239-1fdf01');

  await page.getByRole('button', { name: 'Account' }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
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
  await signIn(page);

  for (const screen of SCREENS) {
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: screen.label })
      .click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(screen.label);
  }

  expect(await seen.finish()).toEqual([]);
});
