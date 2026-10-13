import type { ComponentProps, MouseEvent, ReactNode } from 'react';
import type { Tone } from '../lib/status';
import { Count } from './Badge';
import { cx } from './cx';
import { Icon, type IconName } from './Icon';

/** A click that the browser should handle itself (a new tab, a download) is left alone; any other runs `go` and stays on the page. */
export function followLink(event: MouseEvent, go?: () => void) {
  if (event.defaultPrevented || event.button !== 0) return;
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  go?.();
}

export interface NavItemProps extends Omit<ComponentProps<'a'>, 'href' | 'children'> {
  icon: IconName;
  label: string;
  href: string;
  current?: boolean;
  count?: number;
  tone?: Tone;
  onNavigate?: () => void;
}

/** One screen in the rail. When the rail is collapsed the label hides but stays the accessible name. */
export function NavItem({
  icon,
  label,
  href,
  current,
  count,
  tone,
  onNavigate,
  ...rest
}: NavItemProps) {
  return (
    <a
      {...rest}
      className="ch-nav"
      href={href}
      aria-current={current ? 'page' : undefined}
      aria-label={count ? `${label}, ${count} waiting` : label}
      onClick={(event) => followLink(event, onNavigate)}
    >
      <Icon name={icon} />
      <span className="ch-nav__label">{label}</span>
      {count ? <Count value={count} tone={tone === 'accent' ? 'accent' : undefined} /> : null}
    </a>
  );
}

/** The left rail: 216 px, 48 px collapsed. */
export function Rail({
  collapsed,
  label,
  className,
  children,
}: {
  collapsed?: boolean;
  label: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <nav className={cx('ch-rail', collapsed && 'ch-rail--collapsed', className)} aria-label={label}>
      {children}
    </nav>
  );
}

export function RailSection({ children }: { children: ReactNode }) {
  return <div className="ch-rail__section">{children}</div>;
}

/** The name, set in the heading face with a small mark: there is no logo. */
export function Brand() {
  return (
    <div className="ch-brand">
      <span className="ch-brand__mark" aria-hidden="true">
        {[true, false, false, false, true, false, false, false, true].map((on, i) => (
          // The nine cells never reorder, so the position is the identity.
          // biome-ignore lint/suspicious/noArrayIndexKey: fixed decorative grid
          <i key={i} className={on ? 'on' : undefined} />
        ))}
      </span>
      <span className="ch-nav__label">chargehand</span>
    </div>
  );
}

export function TopBar({ children }: { children: ReactNode }) {
  return <header className="ch-topbar">{children}</header>;
}

export interface TabBarItemProps {
  icon: IconName;
  label: string;
  href?: string;
  current?: boolean;
  count?: number;
  onNavigate?: () => void;
}

/** One place in the phone's bottom bar. A link when it has an address, else a button (More). */
export function TabBarItem({ icon, label, href, current, count, onNavigate }: TabBarItemProps) {
  const body = (
    <>
      <Icon name={icon} />
      {label}
      {count ? <Count value={count} tone="accent" /> : null}
    </>
  );
  return href ? (
    <a
      href={href}
      aria-current={current ? 'page' : undefined}
      aria-label={count ? `${label}, ${count} waiting` : undefined}
      onClick={(event) => followLink(event, onNavigate)}
    >
      {body}
    </a>
  ) : (
    <button type="button" aria-current={current ? 'page' : undefined} onClick={onNavigate}>
      {body}
    </button>
  );
}

/** The phone's bottom bar: Overview, Needs you, Runs, Start, More. It pads for the home indicator. */
export function TabBar({ label, children }: { label: string; children: ReactNode }) {
  return (
    <nav className="ch-tabbar" aria-label={label}>
      {children}
    </nav>
  );
}
