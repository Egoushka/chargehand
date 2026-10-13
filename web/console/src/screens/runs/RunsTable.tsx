import { Link, useNavigate } from '@tanstack/react-router';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useRef } from 'react';
import type { RunSummary } from '../../api/types';
import {
  baseName,
  DASH,
  formatDateTime,
  formatDuration,
  formatTime,
  formatTokens,
  formatUsd,
  shortId,
} from '../../lib/format';
import { runWord } from '../../lib/status';
import { safeHttpUrl } from '../../lib/url';
import {
  Badge,
  Cell,
  Check,
  cssVars,
  HeadCell,
  StatusPill,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import { Elapsed } from '../overview/Elapsed';
import { isActive, pullNumber } from './model';

// select, state, run, repository, kind, preset, account, tokens, spend, started, elapsed, PR
const COLS = '24px 96px 64px minmax(104px,1.2fr) 52px 72px 76px 60px 64px 100px 68px 52px';
const HEADS: Array<{ name: string; num?: boolean }> = [
  { name: 'State' },
  { name: 'Run' },
  { name: 'Repository' },
  { name: 'Kind' },
  { name: 'Preset' },
  { name: 'Account' },
  { name: 'Tokens', num: true },
  { name: 'Spend', num: true },
  { name: 'Started', num: true },
  { name: 'Elapsed', num: true },
  { name: 'PR' },
];

/** How tall a row is before it is measured; the real height follows the density. */
const ESTIMATE = 32;

export interface RunsTableProps {
  rows: RunSummary[];
  selected: ReadonlySet<string>;
  cursor: number;
  onToggle: (runId: string) => void;
  onToggleAll: () => void;
  /** Called when the list is scrolled near its end. */
  onNearEnd?: () => void;
  scrollTo?: { index: number; at: number } | null;
}

/**
 * The runs as the design's dense table, virtualized: only the rows in view (and a few around them) are in the page, so a long list
 * costs what a short one does. Each row is measured, so a density change or a long cell does not break the layout.
 */
export function RunsTable({
  rows,
  selected,
  cursor,
  onToggle,
  onToggleAll,
  onNearEnd,
  scrollTo,
}: RunsTableProps) {
  const parent = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  const virtual = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parent.current,
    estimateSize: () => ESTIMATE,
    overscan: 8,
    getItemKey: (index) => rows[index]?.run_id ?? index,
  });
  const items = virtual.getVirtualItems();
  const last = items[items.length - 1]?.index ?? -1;

  useEffect(() => {
    if (onNearEnd && rows.length > 0 && last >= rows.length - 6) onNearEnd();
  }, [last, rows.length, onNearEnd]);

  // The keyboard cursor moves the view with it.
  const { index: scrollIndex, at: scrollAt } = scrollTo ?? { index: -1, at: 0 };
  useEffect(() => {
    if (scrollAt > 0 && scrollIndex >= 0) virtual.scrollToIndex(scrollIndex, { align: 'auto' });
  }, [scrollAt, scrollIndex, virtual]);

  const checked = rows.length > 0 && rows.every((r) => selected.has(r.run_id));
  const some = !checked && rows.some((r) => selected.has(r.run_id));
  const all = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (all.current) all.current.indeterminate = some;
  }, [some]);

  return (
    <div className="tbl__scroll" ref={parent}>
      <Table cols={COLS} label="Runs" className="runs-table" aria-rowcount={rows.length + 1}>
        <TableHead className="runs-table__head" aria-rowindex={1}>
          <HeadCell>
            <Check
              ref={all}
              aria-label="Select all runs in the list"
              checked={checked}
              onChange={onToggleAll}
            />
          </HeadCell>
          {HEADS.map((h) => (
            <HeadCell key={h.name} num={h.num}>
              {h.name}
            </HeadCell>
          ))}
        </TableHead>
        <div className="vrows" style={cssVars({ '--h': virtual.getTotalSize() })}>
          {items.map((item) => {
            const run = rows[item.index];
            if (!run) return null;
            const pr = safeHttpUrl(run.pr_url);
            const number = pullNumber(run.pr_url);
            const word = runWord(run);
            return (
              <div
                key={item.key}
                ref={virtual.measureElement}
                data-index={item.index}
                role="presentation"
                className="vrow"
                style={cssVars({ '--y': item.start })}
              >
                <TableRow
                  aria-rowindex={item.index + 2}
                  selected={selected.has(run.run_id)}
                  cursor={item.index === cursor}
                  dim={word === 'failed' || word === 'cancelled'}
                  onClick={(event) => {
                    // A click on a control inside the row is that control's; the rest of the row opens the run.
                    if ((event.target as HTMLElement).closest('a, button, input, label')) return;
                    void navigate({ to: '/runs/$runId', params: { runId: run.run_id } });
                  }}
                >
                  <Cell>
                    <Check
                      aria-label={`Select run ${shortId(run.run_id)}`}
                      checked={selected.has(run.run_id)}
                      onChange={() => onToggle(run.run_id)}
                    />
                  </Cell>
                  <Cell title={run.error_code ?? undefined}>
                    <StatusPill status={word} bare />
                    {run.error_code && word !== 'cancelled' ? (
                      <span className="mono dim ch-trunc"> {run.error_code}</span>
                    ) : null}
                  </Cell>
                  <Cell mono>
                    <Link
                      to="/runs/$runId"
                      params={{ runId: run.run_id }}
                      title={run.run_id}
                      aria-label={run.run_id}
                    >
                      {shortId(run.run_id)}
                    </Link>
                  </Cell>
                  <Cell title={run.repository ?? undefined}>{baseName(run.repository)}</Cell>
                  <Cell>{run.kind ? <Badge size="s" label={run.kind} /> : DASH}</Cell>
                  <Cell>{run.preset}</Cell>
                  <Cell title={run.actor ?? undefined}>{run.actor ?? DASH}</Cell>
                  <Cell num title={run.tokens != null ? String(run.tokens) : undefined}>
                    {formatTokens(run.tokens)}
                  </Cell>
                  <Cell num>{formatUsd(run.usd)}</Cell>
                  <Cell num title={formatDateTime(run.started_at)}>
                    {formatTime(run.started_at)}
                  </Cell>
                  <Cell num>
                    {isActive(run) ? (
                      <Elapsed since={run.started_at} />
                    ) : run.finished_at ? (
                      formatDuration(
                        (Date.parse(run.finished_at) - Date.parse(run.started_at)) / 1000,
                      )
                    ) : (
                      DASH
                    )}
                  </Cell>
                  <Cell>
                    {pr ? (
                      <a href={pr} target="_blank" rel="noreferrer noopener" title={pr}>
                        {number ? `#${number}` : 'PR'}
                      </a>
                    ) : null}
                  </Cell>
                </TableRow>
              </div>
            );
          })}
        </div>
      </Table>
    </div>
  );
}
