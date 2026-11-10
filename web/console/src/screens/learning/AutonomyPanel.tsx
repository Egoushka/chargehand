import { useQueries } from '@tanstack/react-query';
import { learnQuery } from '../../api/queries';
import type { LearnReport } from '../../api/types';
import { TightEmpty } from '../../charts/TightEmpty';
import { DASH, formatCount, formatPercent, shortRepo } from '../../lib/format';
import { Badge, Cell, cssVars, HeadCell, Panel, Table, TableHead, TableRow } from '../../ui';
import { autonomyRows, TREND_SINCE, weeklyTrend } from './autonomy';

const COLS = 'minmax(160px, 1.4fr) minmax(150px, 1fr) 56px 64px minmax(200px, 2fr)';

/** What each repository earned (ADR 0097) beside the merge rate it is computed from, and the 30-day trend of that rate, one point per week. */
export function AutonomyPanel({ report, phone }: { report: LearnReport; phone: boolean }) {
  const weeks = useQueries({ queries: TREND_SINCE.map((since) => learnQuery(since)) });
  const trend = weeklyTrend(weeks.map((w) => w.data));
  const rows = autonomyRows(report.autonomy ?? [], report);

  return (
    <Panel title="Autonomy" aside="earned per repository · GET /v1/learn autonomy" flush>
      {rows.length === 0 ? (
        <TightEmpty
          icon="shield"
          title="No autonomy to show"
          text="A repository appears here once a finished task is recorded for it. The server reports its rights from the clean streak of pull requests merged unchanged."
        />
      ) : phone ? (
        <div className="list">
          {rows.map((r) => (
            <div key={r.repository} className="li" style={cssVars({ '--cols': 'minmax(0, 1fr)' })}>
              <div className="row row--wrap">
                <span className="b li__title" title={r.repository}>
                  {shortRepo(r.repository)}
                </span>
                <Badge size="s" tone={r.earned ? 'ok' : undefined} label={r.grant} />
              </div>
              <div className="li__meta ln-wrap">
                merged as is {r.rate} · clean streak {r.streak}
              </div>
              <div className="li__meta ln-wrap">{r.reason}</div>
            </div>
          ))}
        </div>
      ) : (
        <div className="table-scroll">
          <Table cols={COLS} label="Earned autonomy by repository" className="ln-table">
            <TableHead>
              <HeadCell>Repository</HeadCell>
              <HeadCell>Grant</HeadCell>
              <HeadCell num>Merged as is</HeadCell>
              <HeadCell num>Streak</HeadCell>
              <HeadCell>Reason</HeadCell>
            </TableHead>
            {rows.map((r) => (
              <TableRow key={r.repository}>
                <Cell strong title={r.repository}>
                  {shortRepo(r.repository)}
                </Cell>
                <Cell>
                  <Badge size="s" tone={r.earned ? 'ok' : undefined} label={r.grant} />
                </Cell>
                <Cell num>{r.rate}</Cell>
                <Cell num>{r.streak}</Cell>
                <Cell>{r.reason}</Cell>
              </TableRow>
            ))}
          </Table>
        </div>
      )}
      <div className="ln-foot">
        <b>Merge rate by week, last 30 days</b> (pull requests merged as is, of those merged or
        closed that week):
        {trend === null ? (
          <span className="dim"> loading</span>
        ) : (
          <ol className="ln-trend" aria-label="Merge rate per week">
            {trend.map((p) => (
              <li key={p.from} className="ln-trend__week">
                <span className="dim">
                  {p.from === 7 ? 'last 7 days' : `${p.from - 7}-${p.from} days ago`}
                </span>{' '}
                <b>{p.rate === null ? DASH : formatPercent(p.rate * 100)}</b>{' '}
                <span className="dim">({formatCount(p.resolved)} resolved)</span>
              </li>
            ))}
          </ol>
        )}
      </div>
    </Panel>
  );
}
