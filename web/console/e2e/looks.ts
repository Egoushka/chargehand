// What every look at a screen shares: the two sizes, a screenshot on request, a settled page, and axe.
import { join } from 'node:path';
import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';

export const SIZES = {
  desktop: { width: 1280, height: 900 },
  phone: { width: 375, height: 812 },
} as const;

/** `CONSOLE_SHOTS=<dir>` saves a screenshot of the look there. */
export const shot = async (page: Page, name: string) => {
  if (process.env.CONSOLE_SHOTS)
    await page.screenshot({ path: join(process.env.CONSOLE_SHOTS, `${name}.png`), fullPage: true });
};

/** Waits until the page has nothing left to load, so axe reads a settled page. */
export const settle = async (page: Page) => {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts?.ready);
};

/** The serious and critical axe violations of the page, or of what `include` selects. */
export async function violations(page: Page, include?: string) {
  const builder = new AxeBuilder({ page });
  if (include) builder.include(include);
  const { violations: found } = await builder.analyze();
  return found
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).join('; ')}`);
}

/** How far the page scrolls sideways; 0 or less is none. */
export const sidewaysScroll = (page: Page) =>
  page.evaluate(() => {
    const scroller = document.querySelector<HTMLElement>('.main') ?? document.documentElement;
    return (
      Math.max(document.documentElement.scrollWidth, scroller.scrollWidth) -
      document.documentElement.clientWidth
    );
  });
