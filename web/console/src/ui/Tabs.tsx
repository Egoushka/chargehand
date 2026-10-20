import * as RadixTabs from '@radix-ui/react-tabs';
import { type ReactNode, useEffect, useRef } from 'react';
import { Count } from './Badge';
import { cx } from './cx';

export interface TabItem {
  id: string;
  label: string;
  count?: number;
}

export interface TabsProps {
  items: TabItem[];
  value: string;
  onValueChange: (id: string) => void;
  /** 44 px high, for a phone. */
  touch?: boolean;
  label?: string;
  /** The panels: one `TabPanel` per item. */
  children?: ReactNode;
}

/** Underline tabs for the sections of a screen. Tabs scroll sideways rather than wrap; the active one carries the accent underline. */
export function Tabs({ items, value, onValueChange, touch, label, children }: TabsProps) {
  const list = useRef<HTMLDivElement>(null);

  // A tab that sits past the edge of the strip is brought in, by the strip's own scroll so the page does not jump.
  // biome-ignore lint/correctness/useExhaustiveDependencies: it runs when the selected tab changes, which the effect reads from the DOM
  useEffect(() => {
    const strip = list.current;
    const tab = strip?.querySelector<HTMLElement>('[aria-selected="true"]');
    if (!strip || !tab) return;
    const a = strip.getBoundingClientRect();
    const b = tab.getBoundingClientRect();
    if (b.right > a.right) strip.scrollLeft += b.right - a.right + 16;
    else if (b.left < a.left) strip.scrollLeft -= a.left - b.left + 16;
  }, [value]);

  // On a phone the section above the strip can fill the screen: a new tab's body is then below the fold, so the strip goes to the top.
  const pick = (id: string) => {
    onValueChange(id);
    if (touch) list.current?.scrollIntoView?.({ block: 'start' });
  };

  return (
    <RadixTabs.Root value={value} onValueChange={pick}>
      <RadixTabs.List
        ref={list}
        className={cx('ch-tabs', touch && 'ch-tabs--touch')}
        aria-label={label}
      >
        {items.map((item) => (
          <RadixTabs.Trigger key={item.id} value={item.id} className="ch-tab">
            {item.label}
            {item.count != null ? <Count value={item.count} /> : null}
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {children}
    </RadixTabs.Root>
  );
}

export function TabPanel({ value, children }: { value: string; children: ReactNode }) {
  return <RadixTabs.Content value={value}>{children}</RadixTabs.Content>;
}

export interface SegmentedProps<T extends string> {
  items: Array<{ id: T; label: ReactNode }>;
  value: T;
  onValueChange: (id: T) => void;
  label: string;
  touch?: boolean;
}

/** A group of toggle buttons for a range, a density, a theme or a filter. */
export function Segmented<T extends string>({
  items,
  value,
  onValueChange,
  label,
  touch,
}: SegmentedProps<T>) {
  return (
    // biome-ignore lint/a11y/useSemanticElements: a fieldset brings borders and sizing the segmented control does not want
    <span className={cx('ch-seg', touch && 'ch-seg--touch')} role="group" aria-label={label}>
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          aria-pressed={item.id === value}
          onClick={() => onValueChange(item.id)}
        >
          {item.label}
        </button>
      ))}
    </span>
  );
}
