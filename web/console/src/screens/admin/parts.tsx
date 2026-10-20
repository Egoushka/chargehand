// biome-ignore-all lint/a11y/noNoninteractiveTabindex: a region that scrolls must be reachable by keyboard
import type { ComponentPropsWithoutRef, ReactNode } from 'react';
import { formatTime } from '../../lib/format';
import { cx, Panel, Skeleton, useScrollEdges } from '../../ui';
import './admin.css';

/** Marks an affordance the server has no route for yet: shown disabled, with this beside it. */
export function Later({ children = 'later' }: { children?: ReactNode }) {
  return <span className="later">{children}</span>;
}

/** A disabled action with "later" beside it, drawn muted and without a border so it does not read as a button that failed. */
export function Planned({ children }: { children: ReactNode }) {
  return (
    <span className="planned">
      {children}
      <Later />
    </span>
  );
}

/**
 * A region that may scroll sideways. It takes focus so the keyboard can scroll it, and it has a name, as a scrollable region must.
 */
export function ScrollBox({
  label,
  className,
  children,
  ...rest
}: { label: string } & ComponentPropsWithoutRef<'section'>) {
  const edges = useScrollEdges<HTMLElement>();
  return (
    <section
      {...rest}
      ref={edges}
      className={cx('wide-scroll', className)}
      tabIndex={0}
      aria-label={label}
    >
      {children}
    </section>
  );
}

/** The things the console might expect and the server does not keep, in the server's own words. */
export function NotRecorded({ lines }: { lines: readonly string[] }) {
  if (lines.length === 0) return null;
  return (
    <Panel title="Not recorded by the server" aside="so not shown here">
      <ul className="not-recorded list-reset">
        {lines.map((line) => (
          <li key={line}>{line}</li>
        ))}
      </ul>
    </Panel>
  );
}

/** A value the route does not carry: a dash and the reason, never a zero or a guess. */
export function Unknown({ why }: { why: string }) {
  return (
    <span className="dim" title={why}>
      — <span className="unknown__why">{why}</span>
    </span>
  );
}

/** How old a query's data is, for the error box: "Showing the last data, from 13:14." */
export function lastSeen(updatedAt: number): string | undefined {
  return updatedAt > 0
    ? `Showing the last data, from ${formatTime(updatedAt, { seconds: true })}.`
    : undefined;
}

/** A panel that is waiting for its data: the title is real, the body is a few lines at the height of what comes. */
export function PanelSkeleton({
  title,
  lines = [80, 60, 70],
}: {
  title: string;
  lines?: number[];
}) {
  return (
    <Panel title={title} busy>
      <div className="stack stack--tight">
        {lines.map((width) => (
          <Skeleton key={width} width={`${width}%`} />
        ))}
      </div>
    </Panel>
  );
}
