import type { CSSProperties } from 'react';

/** Joins the class names that are set. */
export function cx(...names: Array<string | false | null | undefined>): string {
  return names.filter(Boolean).join(' ');
}

/**
 * Custom properties for an element (`--p`, `--n`, `--cols`). React writes them through the style object model, which the page's
 * Content-Security-Policy allows; an inline style attribute in markup it would not.
 */
export function cssVars(vars: Record<`--${string}`, string | number | undefined>): CSSProperties {
  return vars as CSSProperties;
}
