import { useCallback } from 'react';

/** Which ends of a sideways-scrolling box hide content: "start", "end", both, or none. */
export function hiddenEdges(el: Pick<HTMLElement, 'scrollLeft' | 'scrollWidth' | 'clientWidth'>) {
  const max = el.scrollWidth - el.clientWidth;
  const left = Math.abs(el.scrollLeft);
  return [left > 1 && 'start', left < max - 1 && 'end'].filter(Boolean).join(' ');
}

/**
 * A ref for a box that scrolls sideways with its scrollbar hidden or thin: it sets `data-edges` to the ends that hide content,
 * and ui.css fades those ends so the box shows there is more.
 */
export function useScrollEdges<T extends HTMLElement>() {
  return useCallback((el: T | null) => {
    if (!el) return;
    const update = () => {
      const edges = hiddenEdges(el);
      if (edges) el.dataset.edges = edges;
      else delete el.dataset.edges;
    };
    update();
    el.addEventListener('scroll', update, { passive: true });
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update);
    resize?.observe(el);
    if (el.firstElementChild) resize?.observe(el.firstElementChild);
    return () => {
      el.removeEventListener('scroll', update);
      resize?.disconnect();
    };
  }, []);
}

/** Scrolls a strip sideways, and only sideways, so its selected item is in view. */
export function revealSelected(strip: HTMLElement | null, selector: string) {
  const item = strip?.querySelector<HTMLElement>(selector);
  if (!strip || !item) return;
  const box = strip.getBoundingClientRect();
  const at = item.getBoundingClientRect();
  const margin = 32;
  if (at.left < box.left) strip.scrollLeft -= box.left - at.left + margin;
  else if (at.right > box.right) strip.scrollLeft += at.right - box.right + margin;
}
