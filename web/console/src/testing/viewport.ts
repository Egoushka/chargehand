import { vi } from 'vitest';

/** Stubs the browser's width and colour-scheme queries: a phone, a tablet or a desktop, a browser that asks for dark or light. */
export function viewport(width: boolean | 'tablet', scheme: 'dark' | 'light' = 'dark'): void {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width: 768px')
      ? width === 'tablet'
      : query.includes('max-width')
        ? width === true
        : query.includes('prefers-color-scheme: light')
          ? scheme === 'light'
          : false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    onchange: null,
    dispatchEvent: () => false,
  }));
}
