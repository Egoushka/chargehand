import type { ReactNode } from 'react';
import { cx } from './cx';

export interface KeyValueProps {
  /** Label, value and whether the value is an id, branch or path (monospace). Unknown is an em dash. */
  items: Array<[label: string, value: ReactNode, mono?: boolean]>;
  /** The label above the value: for a phone. */
  stacked?: boolean;
  /** Hairlines between rows: for desktop panels. */
  ruled?: boolean;
}

/** A definition list of facts about one run, task or account. */
export function KeyValue({ items, stacked, ruled }: KeyValueProps) {
  return (
    <dl className={cx('ch-kv', stacked && 'ch-kv--stacked', ruled && 'ch-kv--ruled')}>
      {items.flatMap(([label, value, mono]) => [
        <dt key={`${label}-t`}>{label}</dt>,
        <dd key={`${label}-d`} className={mono ? 'ch-mono' : undefined}>
          {value}
        </dd>,
      ])}
    </dl>
  );
}
