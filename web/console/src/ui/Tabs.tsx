import * as RadixTabs from '@radix-ui/react-tabs';
import { type ReactNode, useCallback, useEffect, useRef } from 'react';
import { Count } from './Badge';
import { cx } from './cx';
import { revealSelected, useScrollEdges } from './scrollEdges';

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

/**
 * Underline tabs for the sections of a screen. Tabs scroll sideways rather than wrap, fade at an end that hides more, and keep
 * the active one, which carries the accent underline, in view.
 */
export function Tabs({ items, value, onValueChange, touch, label, children }: TabsProps) {
  const edges = useScrollEdges<HTMLDivElement>();
  const list = useRef<HTMLDivElement | null>(null);
  const ref = useCallback(
    (el: HTMLDivElement | null) => {
      list.current = el;
      return edges(el);
    },
    [edges],
  );
  // biome-ignore lint/correctness/useExhaustiveDependencies: the active tab moves when the value does
  useEffect(() => revealSelected(list.current, '[aria-selected="true"]'), [value]);
  return (
    <RadixTabs.Root value={value} onValueChange={onValueChange}>
      <RadixTabs.List
        ref={ref}
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
  const edges = useScrollEdges<HTMLSpanElement>();
  return (
    // biome-ignore lint/a11y/useSemanticElements: a fieldset brings borders and sizing the segmented control does not want
    <span
      ref={edges}
      className={cx('ch-seg', touch && 'ch-seg--touch')}
      role="group"
      aria-label={label}
    >
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
