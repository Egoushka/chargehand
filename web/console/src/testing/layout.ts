import { vi } from 'vitest';

/** happy-dom has no layout: give the scroll box a height and every row one, so a virtual list has something to measure. */
export function layout(box = 640, row = 32) {
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (
    this: HTMLElement,
  ) {
    return this.classList.contains('tbl__scroll') ? box : row;
  });
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(1000);
}
