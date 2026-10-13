import { useQuery } from '@tanstack/react-query';
import type { BatchBody } from '../../api/batch';
import { pullsQuery } from '../../api/pulls';
import { DASH, formatDateTime, formatTime } from '../../lib/format';
import { safeHttpUrl } from '../../lib/url';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Cell,
  EmptyState,
  HeadCell,
  Panel,
  StatusPill,
  Table,
  TableHead,
  TableRow,
  TableScroll,
} from '../../ui';
import { AWAITING, batchPulls, notesAbout, prState } from './model';
import { NotRecorded } from './NotRecorded';

const COLS = '64px minmax(160px,1.4fr) 132px 96px minmax(180px,1.4fr) minmax(140px,1fr)';

/** The pull requests chargehand opened for the batch, with the integrator's policy and the last thing it said. */
export function PullsTab({ batch }: { batch: BatchBody }) {
  const phone = useIsPhone();
  const pulls = useQuery(pullsQuery({ since: batch.run.started_at, limit: 200 }));
  const rows = batchPulls(batch, pulls.data?.pulls ?? []);
  const modes = [...new Set(rows.map((r) => r.integrator_mode))];
  return (
    <div className="stack">
      <Panel
        title="Pull requests"
        count={rows.length}
        flush
        aside={
          modes.length > 0 ? (
            <>
              {modes.map((m) => (
                <Badge key={m} size="s" mode={m} label={m} />
              ))}
              <span className="dim">integrator policy</span>
            </>
          ) : undefined
        }
      >
        {rows.length === 0 ? (
          <EmptyState
            icon="pr"
            title="No pull request yet"
            text="A task opens a draft pull request when it finishes and its branch passes the handover checks."
          />
        ) : phone ? (
          <ul className="list-reset">
            {rows.map((pr) => {
              const url = safeHttpUrl(pr.url);
              const state = prState(pr);
              return (
                <li key={pr.url} className="li pr-card">
                  <div className="row row--wrap">
                    <b>
                      {url ? (
                        <a href={url} target="_blank" rel="noreferrer noopener">
                          PR {pr.number ?? ''}
                        </a>
                      ) : (
                        `PR ${pr.number ?? ''}`
                      )}
                    </b>
                    <StatusPill status={state.status} label={state.label} bare />
                    <Badge size="s" mode={pr.integrator_mode} label={pr.integrator_mode} />
                  </div>
                  <div className="mono ch-trunc">{pr.task_id}</div>
                  {pr.integrator_last ? <div className="li__meta">{pr.integrator_last}</div> : null}
                </li>
              );
            })}
          </ul>
        ) : (
          <TableScroll label="Pull requests">
            <Table cols={COLS} label="Pull requests">
              <TableHead>
                <HeadCell>PR</HeadCell>
                <HeadCell>Task</HeadCell>
                <HeadCell>State</HeadCell>
                <HeadCell>Integrator</HeadCell>
                <HeadCell>Last integrator message</HeadCell>
                <HeadCell>Flags</HeadCell>
              </TableHead>
              {rows.map((pr) => {
                const url = safeHttpUrl(pr.url);
                const state = prState(pr);
                return (
                  <TableRow key={pr.url}>
                    <Cell>
                      {url ? (
                        <a href={url} target="_blank" rel="noreferrer noopener" title={url}>
                          {pr.number ? `#${pr.number}` : 'PR'}
                        </a>
                      ) : (
                        (pr.number ?? DASH)
                      )}
                    </Cell>
                    <Cell mono title={pr.task_id}>
                      {pr.task_id}
                    </Cell>
                    <Cell title={pr.state_at ? formatDateTime(pr.state_at) : undefined}>
                      <StatusPill status={state.status} label={state.label} bare />
                    </Cell>
                    <Cell>
                      <Badge size="s" mode={pr.integrator_mode} label={pr.integrator_mode} />
                    </Cell>
                    <Cell className="dim" title={pr.integrator_last ?? undefined}>
                      {pr.integrator_last ?? DASH}
                    </Cell>
                    <Cell>
                      <span className="row row--wrap">
                        {pr.awaiting_person && pr.awaiting_reason ? (
                          <Badge
                            size="s"
                            tone="warn"
                            label={AWAITING[pr.awaiting_reason] ?? pr.awaiting_reason}
                          />
                        ) : null}
                        {pr.test_integrity_flagged ? (
                          <Badge size="s" tone="warn" label="test-integrity" />
                        ) : null}
                        {pr.partial ? <Badge size="s" tone="warn" label="[partial]" /> : null}
                        {pr.opened_at ? (
                          <span className="dim small" title={formatDateTime(pr.opened_at)}>
                            opened {formatTime(pr.opened_at)}
                          </span>
                        ) : null}
                      </span>
                    </Cell>
                  </TableRow>
                );
              })}
            </Table>
          </TableScroll>
        )}
      </Panel>
      <NotRecorded notes={notesAbout(batch.not_recorded, 'pull request')} />
    </div>
  );
}
