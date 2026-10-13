// What every look of a screen is checked for: a desktop and a phone width, the dark and the light theme, no sideways scroll,
// no serious or critical accessibility violation (axe). CONSOLE_SHOTS=<dir> also saves a screenshot of each look.
import { join } from 'node:path';
import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';

export const SIZES = {
  desktop: { width: 1280, height: 900 },
  phone: { width: 375, height: 812 },
} as const;

export const shot = async (page: Page, name: string) => {
  if (process.env.CONSOLE_SHOTS)
    await page.screenshot({ path: join(process.env.CONSOLE_SHOTS, `${name}.png`), fullPage: true });
};

/** Waits until the page has nothing left to load, so axe reads a settled page. */
export const settle = async (page: Page) => {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts?.ready);
};

export async function violations(page: Page, include?: string) {
  const builder = new AxeBuilder({ page });
  if (include) builder.include(include);
  const { violations: found } = await builder.analyze();
  return found
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join('; ')}`);
}

/** How far the page is wider than the window: positive means it scrolls sideways. */
export const overflow = (page: Page) =>
  page.evaluate(() => {
    const scroller = document.querySelector<HTMLElement>('.main') ?? document.documentElement;
    return (
      Math.max(document.documentElement.scrollWidth, scroller.scrollWidth) -
      document.documentElement.clientWidth
    );
  });

/** The heights of the controls in the content that are shown and shorter than a thumb, for a phone. */
export const smallTargets = (page: Page) =>
  page
    .locator('main .ch-btn, main .ch-ibtn, main a.ch-btn')
    .evaluateAll((els) =>
      els.map((el) => el.getBoundingClientRect().height).filter((h) => h > 0 && h < 44),
    );
