import { STATUS, type Status } from '../lib/status';
import { cx } from './cx';

export interface StatusPillProps {
  status: Status;
  /** Overrides the status word (for example "Paused until 13:36"). */
  label?: string;
  /** No fill: for table cells. */
  bare?: boolean;
  /** The glyph alone, with the word as its name: only beside a name that already states the state. */
  dot?: boolean;
  size?: 'lg';
}

/** The state of a run, task or integrator action as a glyph and a word. One pill per object. */
export function StatusPill({ status, label, bare, dot, size }: StatusPillProps) {
  const known = STATUS[status] ?? STATUS.queued;
  const className = cx(
    'ch-pill',
    bare && 'ch-pill--bare',
    dot && 'ch-pill--dot',
    size === 'lg' && 'ch-pill--lg',
  );
  if (dot)
    return (
      <span
        className={className}
        data-status={status}
        title={known.label}
        role="img"
        aria-label={known.label}
      />
    );
  return (
    <span className={className} data-status={status}>
      {label ?? known.label}
    </span>
  );
}
