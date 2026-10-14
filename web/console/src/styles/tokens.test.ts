import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { build, generate, TOKENS_JSON } from '../../scripts/gen-tokens.mjs';

const read = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');
const tokens = JSON.parse(readFileSync(TOKENS_JSON, 'utf8')) as {
  color: { tokens: Array<{ name: string; value: { dark: string; light: string } }> };
};

/** The declarations of one top-level block, by its selector line. */
function block(css: string, selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start, `${selector} block`).toBeGreaterThanOrEqual(0);
  return css.slice(start, css.indexOf('\n}', start));
}

describe('tokens.css', () => {
  const css = read('./tokens.css');

  it('is what the committed token file generates (run `npm run tokens` after changing it)', () => {
    expect(css).toBe(build());
  });

  it('defines every colour token, 133 of them, in the dark theme and again in the light one', () => {
    expect(tokens.color.tokens).toHaveLength(133);
    const dark = block(css, ':root,\n[data-theme="dark"]');
    const light = block(css, '[data-theme="light"]');
    for (const { name } of tokens.color.tokens) {
      expect(dark, `dark --${name}`).toContain(`  --${name}: `);
      expect(light, `light --${name}`).toContain(`  --${name}: `);
    }
  });

  it('makes dark the default and light a choice: by attribute, or by the browser when none is set', () => {
    expect(css).toContain(':root,\n[data-theme="dark"] {\n  color-scheme: dark;');
    expect(css).toContain('[data-theme="light"] {\n  color-scheme: light;');
    const media = css.slice(css.indexOf('@media (prefers-color-scheme: light)'));
    expect(media).toContain(':root:not([data-theme]) {');
    expect(media).toContain('color-scheme: light;');
    // The browser's light is the same set of values as the explicit one.
    const declarations = (text: string) =>
      text
        .split('\n')
        .filter((line) => /^\s+(--[\w-]+|color-scheme):/.test(line))
        .map((line) => line.trim());
    expect(declarations(block(media, '  :root:not([data-theme])'))).toEqual(
      declarations(block(css, '[data-theme="light"]')),
    );
  });

  it('writes a role token that points at a ramp step as a reference, and a value as itself', () => {
    const dark = block(css, ':root,\n[data-theme="dark"]');
    expect(dark).toContain('  --bg: var(--neutral-1);');
    expect(dark).toContain('  --accent: var(--accent-9);');
    expect(dark).toMatch(/ {2}--neutral-1: oklch\(/);
    expect(dark).toContain('  --on-accent: oklch(0.18 0.012 240);');
    expect(block(css, '[data-theme="light"]')).toContain('  --accent-mark: var(--accent-8);');
  });

  it('carries the lime accent: hue 122, in both themes', () => {
    for (const theme of [':root,\n[data-theme="dark"]', '[data-theme="light"]'])
      expect(block(css, theme)).toMatch(/ {2}--accent-9: oklch\([\d. ]+ 122\);/);
  });

  it('gives each theme its own shadow, and every size, space, radius, duration and layer once', () => {
    expect(block(css, ':root,\n[data-theme="dark"]')).toContain(
      '--shadow-modal: 0 0 0 1px rgba(0, 0, 0, 0.7)',
    );
    expect(block(css, '[data-theme="light"]')).toContain(
      '--shadow-modal: 0 0 0 1px rgba(16, 24, 40, 0.08)',
    );
    const plain = css.slice(css.lastIndexOf(':root {'));
    for (const name of [
      'space-1',
      'space-16',
      'radius-pill',
      'size-touch',
      'size-rail',
      'size-tabbar',
      'dur-fast',
      'z-toast',
      'font-sans',
      'font-mono',
    ])
      expect(plain).toContain(`  --${name}: `);
  });

  it('uses system fonts only: no web font is named', () => {
    expect(css).not.toMatch(/@font-face|url\(/);
  });

  it('is deterministic', () => {
    expect(generate(JSON.parse(read('../../design/tokens.json')))).toBe(css);
  });
});

describe('bundle.css', () => {
  const tokensCss = read('./tokens.css');
  const bundle = read('./bundle.css');
  // Properties a component sets on itself or takes from the screen that uses it.
  const local = new Set([
    '--ch-mask',
    '--g',
    '--p',
    '--n',
    '--at',
    '--cols',
    '--row-h',
    '--t-bg',
    '--t-fg',
    '--t-bd',
    '--k',
    '--spark',
  ]);

  it('asks only for properties that tokens.css or the component itself defines', () => {
    const defined = new Set([...tokensCss.matchAll(/^\s+(--[\w-]+):/gm)].map((m) => m[1]));
    const used = new Set([...bundle.matchAll(/var\((--[\w-]+)/g)].map((m) => m[1] as string));
    const missing = [...used].filter((name) => !defined.has(name) && !local.has(name));
    expect(missing).toEqual([]);
  });

  it('holds no literal colour: every colour comes from a token', () => {
    const outsideUrls = bundle.replace(/url\("[^"]*"\)/g, '');
    expect(outsideUrls).not.toMatch(/#[0-9a-fA-F]{3,8}\b|rgba?\(|oklch\(/);
  });
});
