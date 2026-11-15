import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import {
  type SweepBody,
  type SweepNeedsYouItem,
  type SweepRepositoryBody,
  sweepQuery,
  useScan,
} from '../../api/sweep';
import { formatDateTime, formatTime } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Banner,
  Button,
  Cell,
  cssVars,
  EmptyState,
  ErrorState,
  HeadCell,
  Panel,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import { isForbidden, useOwnerGate } from '../admin/gate';
import { OwnerOnly } from '../admin/OwnerOnly';
import { PanelSkeleton, ScrollBox } from '../admin/parts';
import { isScanning, NEEDS_YOU_SHOWN, needsYouTotal, sweepRows, waitText } from './model';
import './sweep.css';

const COLS = 'minmax(0,1.3fr) repeat(7,78px) minmax(0,2fr)';
const COUNTS = [
  ['ready', 'Ready'],
  ['enqueued', 'Started'],
  ['running', 'Running'],
  ['needs_you', 'Needs you'],
  ['blocked', 'Blocked'],
  ['epic', 'Epic'],
  ['deferred', 'Deferred'],
] as const;

/** What the issue sweep read in each repository, and a button to run it now (the schedule does not matter to that). */
export function Sweep() {
  const gate = useOwnerGate();
  const sweep = useQuery({ ...sweepQuery(), enabled: gate.allowed });
  const scan = useScan();
  if (gate.denied || isForbidden(sweep.error))
    return <OwnerOnly title="Sweep" route="GET /v1/sweep" account={gate.name} />;
  const data = sweep.data;
  const scanning = data !== undefined && (isScanning(data) || scan.isPending);
  return (
    <div className="screen">
      <div className="screen__head sweep-head">
        <div>
          <h1 className="screen__title">Sweep</h1>
          <p className="screen__sub">
            Reads every open issue in the watched repositories, labels them, and starts the ones
            that are ready.
          </p>
        </div>
        {data?.configured ? (
          <div className="screen__actions sweep-actions">
            <span className="sweep-when dim small">{whenText(data)}</span>
            <Button
              label={scanning ? 'scanning…' : 'Scan now'}
              icon="refresh"
              loading={scanning}
              disabled={scanning}
              onClick={() => scan.mutate()}
            />
          </div>
        ) : null}
      </div>
      {sweep.isError ? (
        <ErrorState
          title="Cannot read the sweep"
          call="GET /v1/sweep"
          detail={sweep.error.message}
          onRetry={() => sweep.refetch()}
        />
      ) : data === undefined ? (
        <PanelSkeleton title="Last sweep" lines={[100, 90, 95]} />
      ) : !data.configured ? (
        <div className="card">
          <EmptyState
            icon="bolt"
            title="The sweep is not configured"
            text="The profile has no automation.github, so there is no issue to read."
          />
        </div>
      ) : (
        <>
          {scan.isError ? (
            <Banner tone="danger" icon="alert-circle" title="The sweep did not start" role="alert">
              POST /v1/sweep: {scan.error.message}
            </Banner>
          ) : null}
          {data.running ? (
            <Banner tone="info" icon="refresh" title="Sweep running">
              <span className="mono">{data.running.id}</span> started{' '}
              {formatTime(data.running.started)} ({data.running.trigger}).
            </Banner>
          ) : null}
          <Repositories data={data} />
          <Holds data={data} />
        </>
      )}
    </div>
  );
}

/** The last sweep's time, the next one's, and whether the schedule is off. */
function whenText(data: SweepBody): string {
  const last = data.last ? `last ${formatTime(data.last.finished)}` : 'no sweep yet';
  const next = data.schedule_on
    ? data.next
      ? `next ${formatTime(data.next)}`
      : 'next not planned'
    : 'schedule off';
  return `${last} · ${next}`;
}

function Repositories({ data }: { data: SweepBody }) {
  const phone = useIsPhone();
  const rows = sweepRows(data);
  const aside =
    data.last && !phone ? (
      <span title={formatDateTime(data.last.finished)}>
        sweep <span className="mono">{data.last.id}</span> · {data.last.trigger}
      </span>
    ) : undefined;
  return (
    <Panel title="Repositories" count={rows.length} aside={aside} flush>
      {rows.length === 0 ? (
        <EmptyState
          icon="folder"
          title="No sweep has finished yet"
          text="Scan now reads every watched repository; the result is here when it ends."
        />
      ) : phone ? (
        <ul className="list-reset">
          {data.last ? (
            <li className="pad dim small sweep-counts">
              sweep <span className="mono">{data.last.id}</span> · {data.last.trigger}
            </li>
          ) : null}
          {rows.map((row) => (
            <RepoCard key={row.repository} row={row} />
          ))}
        </ul>
      ) : (
        <ScrollBox label="Repositories of the last sweep">
          <Table cols={COLS} label="Repositories of the last sweep">
            <TableHead>
              <HeadCell>Repository</HeadCell>
              {COUNTS.map(([key, label]) => (
                <HeadCell key={key} num>
                  {label}
                </HeadCell>
              ))}
              <HeadCell>Problem</HeadCell>
            </TableHead>
            {rows.map((row) => (
              <TableRow key={row.repository}>
                <Cell strong className="ch-trunc" title={row.repository}>
                  {row.repository}
                </Cell>
                {COUNTS.map(([key]) => (
                  <Cell key={key} num>
                    {row[key]}
                  </Cell>
                ))}
                <Cell className="wrap sweep-problem">{row.problem ?? '—'}</Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      <NeedsYou items={data.needs_you_items} total={needsYouTotal(rows)} />
    </Panel>
  );
}

/** The last sweep's needs-you issues with the sorter's reason, the first few of them. */
function NeedsYou({ items, total }: { items: SweepNeedsYouItem[]; total: number }) {
  const count = Math.max(total, items.length);
  if (count === 0) return null;
  const more = items.length - NEEDS_YOU_SHOWN;
  return (
    <section className="sep-top" aria-label="Needs you">
      <p className="pad dim small">
        {waitText(count)}: they carry the label that says so.{' '}
        <Link to="/needs-you">Open Needs you</Link>
      </p>
      {items.length > 0 ? (
        <ul className="list-reset">
          {items.slice(0, NEEDS_YOU_SHOWN).map((item) => (
            <li key={item.url} className="li" style={cssVars({ '--cols': 'minmax(0,1fr)' })}>
              <div className="li__main">
                <span className="sweep-counts">
                  <a href={item.url} target="_blank" rel="noreferrer noopener" title={item.url}>
                    <span className="mono">#{item.issue}</span> {item.title}
                  </a>{' '}
                  <span className="dim small">{item.repository}</span>
                </span>
                <div className="li__meta sweep-counts">{item.reason}</div>
              </div>
            </li>
          ))}
          {more > 0 ? <li className="pad dim small">and {more} more</li> : null}
        </ul>
      ) : null}
    </section>
  );
}

function RepoCard({ row }: { row: SweepRepositoryBody }) {
  return (
    <li className="li" style={cssVars({ '--cols': 'minmax(0,1fr)' })}>
      <div className="li__main">
        <span className="b ch-trunc" title={row.repository}>
          {row.repository}
        </span>
        <div className="li__meta sweep-counts">
          {COUNTS.map(([key, label]) => `${label.toLowerCase()} ${row[key]}`).join(' · ')}
        </div>
        {row.problem ? <div className="sweep-problem">{row.problem}</div> : null}
      </div>
    </li>
  );
}

/** Items the pool holds back for a repository (a problem or an open breaker), and until when. */
function Holds({ data }: { data: SweepBody }) {
  if (data.pool_holds.length === 0) return null;
  return (
    <Panel title="Held back" count={data.pool_holds.length}>
      <ul className="list-reset pad stack">
        {data.pool_holds.map((hold) => (
          <li key={`${hold.repository}/${hold.text}`}>
            <span className="b">{hold.repository}</span>{' '}
            <span className="sweep-problem">{hold.text}</span>{' '}
            <span className="dim small">until {formatTime(hold.until)}</span>
          </li>
        ))}
      </ul>
    </Panel>
  );
}
