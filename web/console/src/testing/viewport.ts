import { vi } from 'vitest';

/** Stubs the browser's width and colour-scheme queries: a phone or a desktop, a browser that asks for dark or light. */
export function viewport(phone: boolean, scheme: 'dark' | 'light' = 'dark'): void {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('max-width')
      ? phone
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
