// biome-ignore-all lint/a11y/useSemanticElements: the design's table is a grid of divs, so the table roles are set by hand
// biome-ignore-all lint/a11y/useFocusableInteractive: header cells and cells are not controls; a sort button inside one is
import type { ComponentPropsWithoutRef, ReactNode } from 'react';
import { cssVars, cx } from './cx';

/**
 * The dense table: a grid of fixed-height rows (`--row-h` follows the density), numbers right-aligned, unknown an em dash.
 * `cols` is one grid template for every row, set once here and inherited. Rows and cells carry table roles because the
 * markup is a grid of divs. On a phone render the same data as two-line rows instead of scrolling sideways.
 */
export function Table({
  cols,
  label,
  className,
  children,
  ...rest
}: { cols: string; label: string } & ComponentPropsWithoutRef<'div'>) {
  return (
    <div
      {...rest}
      role="table"
      aria-label={label}
      className={cx('ch-table', className)}
      style={cssVars({ '--cols': cols })}
    >
      {children}
    </div>
  );
}

/** The box that scrolls a table sideways when the screen is narrower than it. A tab stop, so the keyboard can scroll it too. */
export function TableScroll({ label, children }: { label: string; children: ReactNode }) {
  return (
    // biome-ignore lint/a11y/noNoninteractiveTabindex: a region that scrolls sideways must be reachable by keyboard
    <div className="table-scroll" role="region" aria-label={label} tabIndex={0}>
      {children}
    </div>
  );
}

export function TableHead({ children, className, ...rest }: ComponentPropsWithoutRef<'div'>) {
  return (
    <div {...rest} role="row" className={cx('ch-tr ch-tr--head', className)}>
      {children}
    </div>
  );
}

export function HeadCell({
  children,
  num,
  sort,
}: {
  children: ReactNode;
  num?: boolean;
  /** The sort state of a sortable column; the child is then the button that sorts. */
  sort?: 'ascending' | 'descending' | 'none';
}) {
  return (
    <div role="columnheader" aria-sort={sort} className={cx('ch-td', num && 'ch-td--num')}>
      {children}
    </div>
  );
}

export interface TableRowProps extends ComponentPropsWithoutRef<'div'> {
  /** The selection (`row-selected`). */
  selected?: boolean;
  /** The keyboard cursor: an inset focus ring. */
  cursor?: boolean;
  /** A row finished without a result: dim by colour, not by opacity. */
  dim?: boolean;
  /** Flashes once: the row is new. */
  fresh?: boolean;
}

export function TableRow({
  selected,
  cursor,
  dim,
  fresh,
  className,
  children,
  ...rest
}: TableRowProps) {
  return (
    <div
      {...rest}
      role="row"
      aria-selected={selected}
      data-cursor={cursor ? 'true' : undefined}
      className={cx('ch-tr', dim && 'ch-tr--dim', fresh && 'is-new', className)}
    >
      {children}
    </div>
  );
}

export interface CellProps extends ComponentPropsWithoutRef<'div'> {
  /** A number: right-aligned, tabular. */
  num?: boolean;
  /** An id, branch or path: monospace and muted. */
  mono?: boolean;
  strong?: boolean;
}

export function Cell({ num, mono, strong, className, children, ...rest }: CellProps) {
  return (
    <div
      {...rest}
      role="cell"
      className={cx(
        'ch-td',
        num && 'ch-td--num',
        mono && 'ch-td--id',
        strong && 'ch-td--strong',
        className,
      )}
    >
      {children}
    </div>
  );
}
