import { type RefObject, useEffect, useState } from 'react';

/** The width of an element, kept current while it resizes. `fallback` is used until it has one (and in tests, which have no layout). */
export function useWidth(ref: RefObject<HTMLElement | null>, fallback = 640): number {
  const [width, setWidth] = useState(0);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => setWidth(Math.round(el.getBoundingClientRect().width));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [ref]);
  return width > 0 ? width : fallback;
}
