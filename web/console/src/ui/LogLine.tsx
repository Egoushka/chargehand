import { cx } from './cx';

export interface LogLineProps {
  /** Display-zone time with seconds. */
  time: string;
  /** The run-event name (`session_progress`, `governor`, `question`, `integrated`, `pushed`, ...); it sets the kind colour. */
  kind: string;
  task?: string;
  detail: string;
  /** A problem line tints the whole line. */
  tone?: 'warn' | 'danger';
  /** Flashes once. */
  isNew?: boolean;
  wrap?: boolean;
}

/** One event of a run's live stream: time, kind, task, detail. Detail wraps, so a multi-line question stays readable. */
export function LogLine({ time, kind, task, detail, tone, isNew, wrap }: LogLineProps) {
  return (
    <div
      className={cx('ch-log', isNew && 'is-new', wrap && 'ch-log--wrap')}
      data-kind={kind}
      data-tone={tone}
    >
      <span className="ch-log__t">{time}</span>
      <span className="ch-log__k">{kind}</span>
      <span className="ch-log__task">{task ?? ''}</span>
      <span className="ch-log__d">{detail}</span>
    </div>
  );
}

/** The sunken pane that holds log lines. It is a log region that does not interrupt: assistive technology reads it on request. */
export function LogStream({
  label,
  children,
  tail,
}: {
  label: string;
  children: React.ReactNode;
  /** The footer, for example "following the tail". */
  tail?: React.ReactNode;
}) {
  return (
    <div className="ch-stream" role="log" aria-live="off" aria-label={label}>
      {children}
      {tail ? <div className="ch-stream__tail">{tail}</div> : null}
    </div>
  );
}
