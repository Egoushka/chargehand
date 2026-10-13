import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';
import { clearKey } from './api/key';
import { resetPreferences } from './shell/preferences';
import { viewport } from './testing/viewport';

// happy-dom has no layout, so the parts of the browser that Radix and cmdk measure with are stubbed.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub;
Element.prototype.scrollIntoView ??= () => {};
Element.prototype.hasPointerCapture ??= () => false;
Element.prototype.setPointerCapture ??= () => {};
Element.prototype.releasePointerCapture ??= () => {};

beforeEach(() => {
  viewport(false, 'dark'); // a desktop whose browser asks for dark, unless a test says otherwise
  localStorage.clear();
  resetPreferences();
});

afterEach(() => {
  cleanup();
  clearKey();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
