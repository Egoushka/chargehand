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

// Nor has it a canvas, which uPlot draws on: a context and a path that take every call and draw nothing. What a chart looks like
// is checked in the browser (e2e); here it is the data it is built from.
const drawsNothing = () =>
  new Proxy(
    {},
    {
      get: (_target, name) => (name === 'measureText' ? () => ({ width: 0 }) : () => undefined),
      set: () => true,
    },
  );
const context = drawsNothing();
HTMLCanvasElement.prototype.getContext = (() =>
  context) as unknown as typeof HTMLCanvasElement.prototype.getContext;
globalThis.Path2D = function Path2D() {
  return drawsNothing();
} as unknown as typeof Path2D;

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
