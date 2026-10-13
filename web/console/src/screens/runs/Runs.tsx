import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { runsQuery } from '../../api/queries';
import type { RunSummary } from '../../api/types';
import { baseName, DASH, formatTime, formatTokens, formatUsd, shortId } from '../../lib/format';
import { runStatus } from '../../lib/status';
import { safeHttpUrl } from '../../lib/url';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Cell,
  EmptyState,
  ErrorState,
  HeadCell,
  Panel,
  Skeleton,
  StatusPill,
  Table,
  TableHead,
  TableRow,
} from '../../ui';

const COLS = '84px 150px 130px 70px 110px minmax(120px,1fr) 110px 96px 80px minmax(0,1.4fr) 44px';
const HEADS = [
  'Run',
  'Started',
  'State',
  'Kind',
  'Preset',
  'Repository',
  'Actor',
  'Cost',
  'Tokens',
  'Branch',
  'PR',
];

/** The runs from GET /v1/runs, newest first. A first version: filters, sorting and a virtual list come with the full screen. */
export function Runs() {
  const runs = useQuery(runsQuery());
  const phone = useIsPhone();
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Runs</h1>
          <p className="screen__sub">Every run, newest first, refreshed every 5 seconds.</p>
        </div>
      </div>
      {runs.isError ? (
        <ErrorState
          title="Cannot list runs"
          call="GET /v1/runs"
          detail={runs.error.message}
          onRetry={() => void runs.refetch()}
        />
      ) : null}
      <Panel title="Runs" count={runs.data?.length} flush busy={runs.isPending}>
        {runs.isPending ? (
          <div className="pad stack stack--tight">
            <Skeleton width="80%" />
            <Skeleton width="60%" />
            <Skeleton width="70%" />
          </div>
        ) : runs.data && runs.data.length === 0 ? (
          <EmptyState
            icon="list"
            title="No runs yet"
            text="Start a run or a batch and it appears here."
          />
        ) : runs.data ? (
          phone ? (
            <ul className="list-reset">
              {runs.data.map((run) => (
                <RunCard key={run.run_id} run={run} />
              ))}
            </ul>
          ) : (
            <div className="table-scroll">
              <Table cols={COLS} label="Runs">
                <TableHead>
                  {HEADS.map((name) => (
                    <HeadCell key={name} num={name === 'Cost' || name === 'Tokens'}>
                      {name}
                    </HeadCell>
                  ))}
                </TableHead>
                {runs.data.map((run) => {
                  const pr = safeHttpUrl(run.pr_url);
                  return (
                    <TableRow
                      key={run.run_id}
                      dim={run.status === 'failed' || run.status === 'denied'}
                    >
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
                      <Cell>{formatTime(run.started_at)}</Cell>
                      <Cell title={run.error_code ?? undefined}>
                        <StatusPill status={runStatus(run.status)} bare />
                        {run.error_code ? (
                          <span className="mono dim"> {run.error_code}</span>
                        ) : null}
                      </Cell>
                      <Cell>{run.kind ?? DASH}</Cell>
                      <Cell>{run.preset}</Cell>
                      <Cell title={run.repository ?? undefined}>{baseName(run.repository)}</Cell>
                      <Cell>{run.actor ?? DASH}</Cell>
                      <Cell num>{formatUsd(run.usd)}</Cell>
                      <Cell num title={run.tokens != null ? String(run.tokens) : undefined}>
                        {formatTokens(run.tokens)}
                      </Cell>
                      <Cell mono title={run.branch ?? undefined}>
                        {run.branch ?? ''}
                      </Cell>
                      <Cell>
                        {pr ? (
                          <a href={pr} target="_blank" rel="noreferrer noopener">
                            PR
                          </a>
                        ) : null}
                      </Cell>
                    </TableRow>
                  );
                })}
              </Table>
            </div>
          )
        ) : null}
      </Panel>
    </div>
  );
}

/** A run on a phone: id and state on the first line, repository, spend and time on the second. */
function RunCard({ run }: { run: RunSummary }) {
  const pr = safeHttpUrl(run.pr_url);
  return (
    <li className="li run-card">
      <div className="row row--between">
        <Link
          to="/runs/$runId"
          params={{ runId: run.run_id }}
          className="mono-b"
          title={run.run_id}
        >
          {shortId(run.run_id)}
        </Link>
        <StatusPill status={runStatus(run.status)} bare />
      </div>
      <div className="li__meta">
        {baseName(run.repository)} · {formatUsd(run.usd)} · {formatTime(run.started_at)}
        {run.error_code ? ` · ${run.error_code}` : ''}
        {pr ? (
          <>
            {' · '}
            <a href={pr} target="_blank" rel="noreferrer noopener">
              PR
            </a>
          </>
        ) : null}
      </div>
    </li>
  );
}
