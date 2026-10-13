import { type ReactNode, useId, useState } from 'react';
import { Button, Cell, cssVars, cx, HeadCell, Table, TableHead, TableRow } from '../ui';
import type { Slot } from './colors';
import './charts.css';

export interface LegendItem {
  label: string;
  slot: Slot;
}

/** The numbers behind a chart, as text: the twin that works without a pointer, a screen and a colour sense. */
export interface TableSpec {
  /** Names the table for assistive technology. */
  label: string;
  columns: string[];
  rows: string[][];
}

/** A key (a short bar of the series' hue) and its name. The colour is never the only channel: the name is beside it. */
export function Legend({ items }: { items: LegendItem[] }) {
  return (
    <ul className="chart-legend">
      {items.map((item) => (
        <li key={`${item.slot}-${item.label}`} className="chart-legend__item">
          <i className="chart-key" data-slot={item.slot} aria-hidden="true" />
          <span>{item.label}</span>
        </li>
      ))}
    </ul>
  );
}

/** The table behind a chart. Numbers right-aligned, the first column (the bucket or the name) strong. */
export function ChartTable({ label, columns, rows }: TableSpec) {
  const first = 'minmax(120px, 1.4fr)';
  const cols = `${first} ${columns
    .slice(1)
    .map(() => 'minmax(76px, 1fr)')
    .join(' ')}`;
  const minWidth = 120 + Math.max(0, columns.length - 1) * 84;
  return (
    <section
      className="chart-table-box"
      aria-label={`${label}, scrollable`}
      // biome-ignore lint/a11y/noNoninteractiveTabindex: a scrollable region takes focus so that the keyboard can scroll it
      tabIndex={0}
      style={cssVars({ '--min-w': `${minWidth}px` })}
    >
      <Table cols={cols} label={label} className="chart-table">
        <TableHead>
          {columns.map((c, i) => (
            <HeadCell key={c} num={i > 0}>
              {c}
            </HeadCell>
          ))}
        </TableHead>
        {rows.map((row) => (
          <TableRow key={row.join('|')}>
            {row.map((cell, i) => (
              // biome-ignore lint/suspicious/noArrayIndexKey: a row's cells are positional
              <Cell key={i} num={i > 0} strong={i === 0}>
                {cell}
              </Cell>
            ))}
          </TableRow>
        ))}
      </Table>
    </section>
  );
}

export interface ChartFrameProps {
  /** Two or more series: a legend is always present then. One series needs none, the panel title names it. */
  legend?: LegendItem[];
  table?: TableSpec;
  /** A line under the chart: how to read it, what it leaves out. */
  note?: ReactNode;
  children: ReactNode;
  className?: string;
}

/** A chart with its legend, a note, and a button that shows the numbers as a table. */
export function ChartFrame({ legend, table, note, children, className }: ChartFrameProps) {
  const [open, setOpen] = useState(false);
  const id = useId();
  return (
    <div className={cx('chart-frame', className)}>
      {children}
      {legend && legend.length > 1 ? <Legend items={legend} /> : null}
      {note ? <div className="chart-note">{note}</div> : null}
      {table ? (
        <>
          <div className="chart-actions">
            <Button
              size="sm"
              variant="quiet"
              icon="list"
              label={open ? 'Hide data' : 'Show data'}
              aria-expanded={open}
              aria-controls={id}
              onClick={() => setOpen(!open)}
            />
          </div>
          <div id={id} hidden={!open}>
            {open ? <ChartTable {...table} /> : null}
          </div>
        </>
      ) : null}
    </div>
  );
}
