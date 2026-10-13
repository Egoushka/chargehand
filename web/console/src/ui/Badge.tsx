import type { ReactNode } from 'react';
import type { Tone } from '../lib/status';
import { cx } from './cx';

export interface BadgeProps {
  label?: ReactNode;
  /** The integrator policy of a repository; it gives the badge its glyph and tone. */
  mode?: 'merge' | 'ask' | 'off';
  /** Tints a flag: `warn` for "test-integrity flagged" and [partial], `info` for governor observe, `danger` for enforce. */
  tone?: Tone;
  /** `s` 16, default 20, `l` 24 px. `S`, `M` and `L` with `kind="size"` are the task size class. */
  size?: 's' | 'l' | 'S' | 'M' | 'L';
  kind?: 'size';
  mono?: boolean;
  title?: string;
}

/** A small label that is not a state: integrator mode, size class, a flag, a source. */
export function Badge({ label, mode, tone, size, kind, mono, title }: BadgeProps) {
  if (kind === 'size' && (size === 'S' || size === 'M' || size === 'L')) {
    return (
      <span className="ch-size" data-size={size} title={title ?? `Size ${size}`}>
        {size}
      </span>
    );
  }
  return (
    <span
      className={cx(
        'ch-badge',
        size === 's' && 'ch-badge--s',
        size === 'l' && 'ch-badge--l',
        mono && 'ch-badge--mono',
      )}
      data-mode={mode}
      data-tone={tone}
      title={title}
    >
      {label ?? mode}
    </span>
  );
}

/** The task size class: S is outlined, M filled, L inverted, so the three differ by lightness. */
export function SizeBadge({ size }: { size: 'S' | 'M' | 'L' }) {
  return <Badge kind="size" size={size} />;
}

/** A number in a pill (tabs, rail). `accent` only when the count needs you. */
export function Count({ value, tone }: { value: number | string; tone?: 'accent' | 'danger' }) {
  return (
    <span className="ch-count" data-tone={tone}>
      {value}
    </span>
  );
}
