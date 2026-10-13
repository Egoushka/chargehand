import { Command } from 'cmdk';
import { Fragment, type ReactNode } from 'react';
import { Icon, type IconName } from './Icon';

/** The label with the first match of `query` wrapped in `<mark>`, as the palette shows it. */
export function highlight(label: string, query: string): ReactNode {
  const q = query.trim().toLowerCase();
  const at = q ? label.toLowerCase().indexOf(q) : -1;
  if (at < 0) return label;
  return (
    <Fragment>
      {label.slice(0, at)}
      <mark>{label.slice(at, at + q.length)}</mark>
      {label.slice(at + q.length)}
    </Fragment>
  );
}

export interface CommandItemProps {
  /** What cmdk matches and ranks on; defaults to the label plus the meta. */
  value?: string;
  label: string;
  icon?: IconName;
  /** Repository and state, in monospace; or "asks first" for a destructive action. */
  meta?: string;
  /** Shortcuts, one key per entry. */
  keys?: string[];
  query?: string;
  onSelect: () => void;
}

/** One result row of the palette. cmdk marks the active row `aria-selected`; arrows move it and Enter runs it. */
export function CommandItem({
  value,
  label,
  icon = 'chevron-right',
  meta,
  keys,
  query = '',
  onSelect,
}: CommandItemProps) {
  return (
    <Command.Item className="ch-cmd" value={value ?? `${label} ${meta ?? ''}`} onSelect={onSelect}>
      <Icon name={icon} />
      <span className="ch-cmd__label">{highlight(label, query)}</span>
      {meta ? <span className="ch-cmd__meta">{meta}</span> : null}
      {keys?.length ? (
        <span className="ch-cmd__keys">
          {keys.map((key) => (
            <kbd key={key} className="ch-kbd">
              {key}
            </kbd>
          ))}
        </span>
      ) : null}
    </Command.Item>
  );
}
