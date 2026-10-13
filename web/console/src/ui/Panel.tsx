import type { ComponentPropsWithoutRef, ReactNode } from 'react';
import type { Tone } from '../lib/status';
import { Count } from './Badge';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';
import { IconButton } from './IconButton';

export interface PanelProps extends Omit<ComponentPropsWithoutRef<'section'>, 'title'> {
  title: string;
  /** Links and controls on the right of the head. */
  aside?: ReactNode;
  /** A count next to the title; `accent` when the count needs you. */
  count?: number;
  countTone?: 'accent' | 'danger';
  /** The body is a table or a list that runs edge to edge. */
  flush?: boolean;
  /** Data is on its way: assistive technology hears it. */
  busy?: boolean;
}

/** The container for everything on a screen: flat, a surface and a hairline, no shadow. */
export function Panel({
  title,
  aside,
  count,
  countTone,
  flush,
  busy,
  className,
  children,
  ...rest
}: PanelProps) {
  return (
    <section {...rest} className={cx('ch-panel', className)} aria-busy={busy || undefined}>
      <header className="ch-panel__head">
        <h3 className="ch-panel__title">{title}</h3>
        {count != null ? <Count value={count} tone={countTone} /> : null}
        {aside ? <div className="ch-panel__aside">{aside}</div> : null}
      </header>
      <div className={cx('ch-panel__body', flush && 'ch-panel__body--flush')}>{children}</div>
    </section>
  );
}

export interface BannerProps {
  tone: Tone;
  icon: IconName;
  title: string;
  children?: ReactNode;
  /** One action. */
  action?: ReactNode;
  /** `alert` for a condition that needs attention now (halted); `status` otherwise. */
  role?: 'alert' | 'status';
  className?: string;
}

/** A server-wide condition (halted, rate-limit pause, notifications) with one action. */
export function Banner({
  tone,
  icon,
  title,
  children,
  action,
  role = 'status',
  className,
}: BannerProps) {
  return (
    <div className={cx('ch-banner', className)} data-tone={tone} role={role}>
      <Icon name={icon} />
      <div className="ch-banner__text">
        <div className="ch-banner__title">{title}</div>
        {children ? <span className="ch-muted">{children}</span> : null}
      </div>
      {action}
    </div>
  );
}

export interface KpiProps {
  label: string;
  value: ReactNode;
  sub?: ReactNode;
  /** A sparkline may sit beside the number. */
  aside?: ReactNode;
}

/** A label, a display number and a muted sub-line. */
export function Kpi({ label, value, sub, aside }: KpiProps) {
  return (
    <div className="ch-kpi">
      <span className="ch-kpi__label">{label}</span>
      <span className="ch-kpi__value">
        {value}
        {aside}
      </span>
      {sub ? <span className="ch-kpi__sub">{sub}</span> : null}
    </div>
  );
}

/** One active filter with a remove button; the group of chips is the saved view. */
export function Chip({
  label,
  value,
  onRemove,
}: {
  label: string;
  value: string;
  onRemove: () => void;
}) {
  return (
    <span className="ch-chip">
      <b>{label}</b> {value}
      <IconButton icon="x" size="sm" label={`Remove filter ${label} ${value}`} onClick={onRemove} />
    </span>
  );
}
