import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { type CostRow, costQuery } from '../../api/analytics';
import { TightEmpty } from '../../charts/TightEmpty';
import { formatCount, formatUsd } from '../../lib/format';
import {
  Cell,
  ErrorState,
  HeadCell,
  Panel,
  Segmented,
  Skeleton,
  Table,
  TableHead,
  TableRow,
  TableScroll,
} from '../../ui';

const COLS = 'minmax(0,2fr) minmax(0,1fr) 4rem 5rem 4rem 4rem 4rem 5.5rem 5.5rem';

const days = (n: number) => `${n} days`;

function Line({ row, total }: { row: CostRow; total?: boolean }) {
  return (
    <TableRow>
      <Cell strong={total}>{total ? 'All' : row.repository}</Cell>
      <Cell mono>{total ? '' : row.preset}</Cell>
      <Cell num>{formatCount(row.runs)}</Cell>
      <Cell num>{formatUsd(row.usd)}</Cell>
      <Cell num>{formatCount(row.pull_requests)}</Cell>
      <Cell num>{formatCount(row.merged)}</Cell>
      <Cell num>{formatCount(row.closed_unmerged)}</Cell>
      <Cell num strong>
        {formatUsd(row.usd_per_merged)}
      </Cell>
      <Cell num>{formatUsd(row.wasted_usd)}</Cell>
    </TableRow>
  );
}

/** Dollars per merged pull request, per repository and preset: the number that says which automation pays. Reads `GET /v1/cost`, which asks the forge for each pull request's state. */
export function CostPanel() {
  const cost = useQuery(costQuery());
  const [window, setWindow] = useState('7');
  const call = 'GET /v1/cost';
  if (cost.isPending)
    return (
      <Panel className="an-wide" title="Dollars per merged pull request">
        <Skeleton width="100%" height={120} />
      </Panel>
    );
  if (cost.isError)
    return (
      <Panel className="an-wide" title="Dollars per merged pull request">
        <ErrorState
          title="Cannot read cost per merged pull request"
          call={call}
          detail={cost.error.message}
        />
      </Panel>
    );
  const chosen = cost.data.windows.find((w) => String(w.days) === window) ?? cost.data.windows[0];
  return (
    <Panel
      className="an-wide"
      title="Dollars per merged pull request"
      aside="by repository and preset, from the run log and the forge"
    >
      {chosen === undefined ? (
        <TightEmpty
          icon="info"
          title="No run in the log"
          text="Nothing finished in the last 30 days."
        />
      ) : (
        <div className="stack">
          <Segmented
            label="Window"
            value={window}
            onValueChange={setWindow}
            items={cost.data.windows.map((w) => ({ id: String(w.days), label: days(w.days) }))}
          />
          <TableScroll label="Cost per merged pull request">
            <Table
              className="an-cost"
              cols={COLS}
              label={`Cost per merged pull request, last ${days(chosen.days)}`}
            >
              <TableHead>
                <HeadCell>Repository</HeadCell>
                <HeadCell>Preset</HeadCell>
                <HeadCell num>Runs</HeadCell>
                <HeadCell num>Spend</HeadCell>
                <HeadCell num>PRs</HeadCell>
                <HeadCell num>Merged</HeadCell>
                <HeadCell num>Closed</HeadCell>
                <HeadCell num>$ / merged</HeadCell>
                <HeadCell num>Wasted</HeadCell>
              </TableHead>
              <Line row={chosen.total} total />
              {chosen.rows.map((row) => (
                <Line key={`${row.repository}|${row.preset}`} row={row} />
              ))}
            </Table>
          </TableScroll>
          {chosen.total.unknown > 0 ? (
            <p className="dim small">
              {formatCount(chosen.total.unknown)} pull requests could not be read (no forge
              credential and no recorded outcome); they count as neither merged nor closed.
            </p>
          ) : null}
          <div className="dim small">
            {call} · usd_per_merged = usd / merged; wasted_usd = no_pull_request_usd + closed_usd
          </div>
          {cost.data.not_recorded.map((note) => (
            <div key={note} className="dim small">
              {note}
            </div>
          ))}
        </div>
      )}
    </Panel>
  );
}
