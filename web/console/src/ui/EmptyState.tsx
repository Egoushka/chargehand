import type { ReactNode } from 'react';
import { Button } from './Button';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';

export interface EmptyStateProps {
  /** Says what is absent. */
  title: string;
  /** Says why, in one sentence. */
  text: ReactNode;
  icon?: IconName;
  /** The one action that fills it. */
  action?: ReactNode;
}

/** What a panel shows when it has nothing. */
export function EmptyState({ title, text, icon = 'inbox', action }: EmptyStateProps) {
  return (
    <div className="ch-empty">
      <div className="ch-empty__mark">
        <Icon name={icon} />
      </div>
      <h3 className="ch-empty__title">{title}</h3>
      <p className="ch-empty__text">{text}</p>
      {action}
    </div>
  );
}

/** A skeleton line at the real height of what it stands for. Hidden from assistive technology: the container says `aria-busy`. */
export function Skeleton({
  width,
  height,
  className,
}: {
  width?: string;
  height?: string | number;
  className?: string;
}) {
  return (
    <span
      className={cx('ch-skel', className)}
      aria-hidden="true"
      style={{ width, height: typeof height === 'number' ? `${height}px` : height }}
    />
  );
}

export interface ErrorStateProps {
  title: string;
  /** The failing call, shown in monospace: `GET /v1/overview`. */
  call?: string;
  /** What the server or the browser said. */
  detail?: string;
  /** How old the data on screen is. */
  age?: string;
  onRetry?: () => void;
}

/** A failed call: the call and its status, the age of the data still on screen, and Retry. */
export function ErrorState({ title, call, detail, age, onRetry }: ErrorStateProps) {
  return (
    <div className="errbox" role="alert">
      <Icon name="alert-triangle" />
      <div className="stack stack--tight">
        <div className="b">{title}</div>
        <div className="dim">
          {call ? <span className="mono">{call}</span> : null}
          {call && detail ? ': ' : null}
          {detail}
          {age ? ` ${age}` : null}
        </div>
        {onRetry ? (
          <div className="row">
            <Button label="Retry" size="sm" icon="refresh" onClick={onRetry} />
          </div>
        ) : null}
      </div>
    </div>
  );
}
