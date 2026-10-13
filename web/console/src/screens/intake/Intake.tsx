import { type UseQueryResult, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import type { IntakeBody } from '../../api/intake';
import { intakeQuery } from '../../api/intake';
import { type PullsBody, pullsQuery } from '../../api/pulls';
import { type ProfileIntegrator, type SystemBody, systemQuery } from '../../api/system';
import { DASH, formatDateTime, formatTime, formatTokens, formatUsd } from '../../lib/format';
import { safeHttpUrl } from '../../lib/url';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  CapMeter,
  Cell,
  cssVars,
  EmptyState,
  ErrorState,
  HeadCell,
  KeyValue,
  Panel,
  Segmented,
  StatusPill,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import { isForbidden, useOwnerGate } from '../admin/gate';
import { OwnerOnly } from '../admin/OwnerOnly';
import { Later, lastSeen, NotRecorded, PanelSkeleton, ScrollBox } from '../admin/parts';
import {
  filterRows,
  type PolicyRow,
  policyRows,
  type RoundRow,
  reviewRounds,
  startsPerDay,
  type TriggerFilter,
  type TriggerResult,
  type TriggerRow,
  triggerRows,
} from './model';
import './intake.css';

const RESULT: Record<TriggerResult, { status: 'done' | 'waiting' | 'cancelled'; label: string }> = {
  acted: { status: 'done', label: 'Acted' },
  waiting: { status: 'waiting', label: 'Waiting' },
  ignored: { status: 'cancelled', label: 'Ignored' },
};

const LOG_COLS = '92px minmax(0,1.1fr) 84px minmax(0,1.5fr)';
const POLICY_COLS = 'minmax(0,1.2fr) 88px 84px minmax(0,1.7fr) 56px';

/** GitHub intake: what starts work, what it did with each trigger, how many starts the day has left, and the integrator's policy per repository. */
export function Intake() {
  const gate = useOwnerGate();
  const intake = useQuery({ ...intakeQuery(), enabled: gate.allowed });
  const system = useQuery({ ...systemQuery(), enabled: gate.allowed });
  const pulls = useQuery({
    ...pullsQuery({ since: '14d', limit: 200 }),
    enabled: gate.allowed,
  });
  const data = intake.data;

  if (gate.denied || isForbidden(intake.error))
    return <OwnerOnly title="Intake and automation" route="GET /v1/intake" account={gate.name} />;

  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Intake and automation</h1>
          <p className="screen__sub">
            GitHub issues and review comments that start work, and the integrator policy for each
            repository. chargehand polls GitHub as its App; there is no webhook.
          </p>
        </div>
        <div className="screen__actions">
          {data?.enabled ? (
            <span className="ch-live" data-state={data.state_present ? undefined : 'paused'}>
              {data.poll_seconds ? `Polling every ${data.poll_seconds} s` : 'Polling'}
              {data.state_saved_at ? (
                <span title="intake/github.json is written when intake acts, not on every poll">
                  {' '}
                  · state saved {formatTime(data.state_saved_at, { seconds: true })}
                </span>
              ) : null}
            </span>
          ) : data ? (
            <span className="ch-live" data-state="off">
              Off
            </span>
          ) : null}
        </div>
      </div>

      {intake.isError ? (
        <ErrorState
          title="Cannot read intake"
          call="GET /v1/intake"
          detail={intake.error.message}
          age={data ? lastSeen(intake.dataUpdatedAt) : undefined}
          onRetry={() => void intake.refetch()}
        />
      ) : null}

      {!data && !intake.isError ? (
        <div className="g2" aria-busy="true">
          <div className="stack">
            <PanelSkeleton title="Trigger log" lines={[90, 100, 85, 95]} />
            <PanelSkeleton title="Integrator policy" />
          </div>
          <div className="stack">
            <PanelSkeleton title="Starts per day" />
            <PanelSkeleton title="Rules" />
          </div>
        </div>
      ) : null}

      {data && !data.enabled ? (
        <Panel title="Intake">
          <EmptyState
            icon="bolt"
            title="Intake is off"
            text={
              <>
                The profile has no <span className="mono">automation.github</span> block. Add the
                watched repositories, the allowed authors and the GitHub App of the integrator, then
                restart the server. Issues labelled with the trigger label then start a batch of one
                task.
              </>
            }
          />
        </Panel>
      ) : null}

      {data?.enabled ? (
        <IntakeBoard
          data={data}
          system={system}
          pulls={pulls}
          integrator={system.data?.profile?.driven?.integrator}
        />
      ) : null}

      {data ? <NotRecorded lines={data.not_recorded} /> : null}
    </div>
  );
}

interface IntakeBoardProps {
  data: IntakeBody;
  system: UseQueryResult<SystemBody>;
  pulls: UseQueryResult<PullsBody>;
  integrator: ProfileIntegrator | null | undefined;
}

function IntakeBoard({ data, system, pulls, integrator }: IntakeBoardProps) {
  const rows = policyRows(
    integrator,
    data.watched.map((w) => w.repository),
    pulls.data?.pulls ?? [],
  );
  return (
    <div className="g2">
      <div className="stack">
        <TriggerLog data={data} />
        <PolicyPanel
          rows={rows}
          integrator={integrator}
          loading={system.isPending || pulls.isPending}
          error={system.isError ? system.error.message : undefined}
          onRetry={() => void system.refetch()}
        />
      </div>
      <div className="stack">
        <StartsPanel data={data} />
        <RulesPanel data={data} />
        <WatchedPanel data={data} rows={rows} />
        <RoundsPanel data={data} cap={integrator?.ci_fix.attempts} />
      </div>
    </div>
  );
}

function TriggerLog({ data }: { data: IntakeBody }) {
  const [filter, setFilter] = useState<TriggerFilter>('all');
  const phone = useIsPhone();
  const all = triggerRows(data);
  const shown = filterRows(all, filter);
  const { handled } = data;
  return (
    <Panel
      title="Trigger log"
      count={all.length}
      flush
      style={cssVars({ '--o': 2 })}
      aside={
        <Segmented<TriggerFilter>
          label="Filter the trigger log"
          touch={phone}
          value={filter}
          onValueChange={setFilter}
          items={[
            { id: 'all', label: 'All' },
            { id: 'acted', label: 'Acted' },
            { id: 'not-acted', label: 'Not acted' },
          ]}
        />
      }
    >
      <p className="pad dim small">
        {handled.total === 0
          ? 'No trigger handled yet.'
          : `Handled so far: ${handled.total} (${handled.labels} labels, ${handled.comments} comments, ${handled.reviews} review comments, ${handled.rounds} rounds). A trigger is handled once.`}
      </p>
      {shown.length === 0 ? (
        <EmptyState
          icon="bolt"
          title={all.length === 0 ? 'No trigger to show' : 'Nothing in this filter'}
          text={
            all.length === 0
              ? 'The state file lists no task, no waiting review comment and no notice. A label from an allowed author starts one.'
              : 'Choose All to see every trigger the state file holds.'
          }
        />
      ) : phone ? (
        <ul className="list-reset">
          {shown.map((row) => (
            <TriggerCard key={row.id} row={row} />
          ))}
        </ul>
      ) : (
        <ScrollBox label="Trigger log">
          <Table cols={LOG_COLS} label="Trigger log">
            <TableHead>
              <HeadCell>Time</HeadCell>
              <HeadCell>Trigger</HeadCell>
              <HeadCell>Result</HeadCell>
              <HeadCell>Reason</HeadCell>
            </TableHead>
            {shown.map((row) => (
              <TableRow key={row.id}>
                <Cell mono title={row.at ? formatDateTime(row.at) : 'not recorded'}>
                  {row.at ? formatTime(row.at) : DASH}
                </Cell>
                <Cell className="wrap" title={row.where}>
                  <div className="intake-where">
                    <Badge size="s" label={row.kind} />
                    <span className="mono dim">{row.where}</span>
                  </div>
                </Cell>
                <Cell>
                  <ResultPill result={row.result} />
                </Cell>
                <Cell className="wrap dim">{row.reason}</Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      <p className="pad dim small sep-top">
        An ignored trigger carries no time, and a trigger from an author who is not allowed carries
        no reason: the server does not record either (see Not recorded).
      </p>
    </Panel>
  );
}

const ResultPill = ({ result }: { result: TriggerResult }) => (
  <StatusPill status={RESULT[result].status} label={RESULT[result].label} bare />
);

function TriggerCard({ row }: { row: TriggerRow }) {
  return (
    <li className="li" style={cssVars({ '--cols': 'minmax(0,1fr)' })}>
      <div className="row row--wrap">
        <ResultPill result={row.result} />
        <Badge size="s" label={row.kind} />
        <span className="mono dim">{row.at ? formatTime(row.at) : DASH}</span>
      </div>
      <div className="mono ch-trunc" title={row.where}>
        {row.where}
      </div>
      <div className="li__meta wrap">{row.reason}</div>
    </li>
  );
}

function PolicyPanel({
  rows,
  integrator,
  loading,
  error,
  onRetry,
}: {
  rows: PolicyRow[];
  integrator: ProfileIntegrator | null | undefined;
  loading: boolean;
  error?: string;
  onRetry: () => void;
}) {
  const phone = useIsPhone();
  const fixer = integrator?.ci_fix;
  return (
    <Panel
      title="Integrator policy"
      flush
      busy={loading}
      style={cssVars({ '--o': 4 })}
      aside={<span className="dim">acts as the GitHub App; never bypasses a rule</span>}
    >
      {error ? (
        <div className="pad">
          <ErrorState
            title="Cannot read the integrator policy"
            call="GET /v1/system"
            detail={error}
            onRetry={onRetry}
          />
        </div>
      ) : loading ? (
        <div className="pad dim">Loading the policy.</div>
      ) : rows.length === 0 ? (
        <EmptyState
          icon="merge"
          title="No repository listed"
          text="The profile lists no repository under driven.integrator and intake watches none: a draft pull request is left alone."
        />
      ) : phone ? (
        <ul className="list-reset">
          {rows.map((row) => (
            <li key={row.repo} className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
              <div className="li__main">
                <div className="b ch-trunc" title={row.repo}>
                  {row.repo}
                </div>
                <div className="li__meta wrap">{row.recent}</div>
              </div>
              <span className="row">
                {row.alwaysAsk ? <Badge size="s" tone="accent" label="always_ask" /> : null}
                <Badge size="s" mode={row.mode} />
              </span>
            </li>
          ))}
        </ul>
      ) : (
        <ScrollBox label="Integrator policy">
          <Table cols={POLICY_COLS} label="Integrator policy">
            <TableHead>
              <HeadCell>Repository</HeadCell>
              <HeadCell>Policy</HeadCell>
              <HeadCell>always_ask</HeadCell>
              <HeadCell>Recent actions</HeadCell>
              <HeadCell>Edit</HeadCell>
            </TableHead>
            {rows.map((row) => (
              <TableRow key={row.repo}>
                <Cell strong className="wrap" title={row.repo}>
                  {row.repo}
                </Cell>
                <Cell>
                  <Badge mode={row.mode} />
                </Cell>
                <Cell>{row.alwaysAsk ? 'yes' : DASH}</Cell>
                <Cell className="wrap dim">{row.recent}</Cell>
                <Cell>
                  <Later />
                </Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      {integrator ? (
        <div className="pad stack stack--tight sep-top">
          <div className="row row--wrap">
            <span className="b">CI fixer</span>
            <Badge size="s" mono label={`max_attempts ${fixer?.attempts}`} />
            <Badge size="s" mono label={`max_turns ${fixer?.turns}`} />
            <Badge size="s" mono label={`max_minutes ${fixer?.minutes}`} />
            <Badge size="s" mono label={`wait_minutes ${fixer?.wait}`} />
          </div>
          <div className="dim small">
            Never twice for the same failing jobs. A review round counts as an attempt: one cap per
            pull request.
          </div>
        </div>
      ) : !loading && !error ? (
        <p className="pad dim small sep-top">
          The profile has no <span className="mono">driven.integrator</span> block, so chargehand
          never touches a draft pull request.
        </p>
      ) : null}
    </Panel>
  );
}

function StartsPanel({ data }: { data: IntakeBody }) {
  const starts = data.starts;
  const cap = starts?.cap ?? data.caps?.max_starts_per_day ?? 0;
  const bars = startsPerDay(data.tasks, cap);
  const peak = Math.max(cap, ...bars.map((b) => b.count), 1);
  const [left, right, base, height] = [20, 296, 104, 80];
  const slot = (right - left) / bars.length;
  const y = (n: number) => base - (n / peak) * height;
  return (
    <Panel
      title="Starts per day"
      style={cssVars({ '--o': 1 })}
      aside={cap ? `cap ${cap} in any 24 h` : 'no cap'}
    >
      <div className="stack stack--tight">
        {starts ? (
          <CapMeter
            label="Starts in the last 24 hours"
            value={starts.last24h}
            cap={starts.cap}
            text={`${starts.last24h} of ${starts.cap}`}
          />
        ) : null}
        {starts?.next_slot_at ? (
          <p className="dim small" role="status">
            The cap is full: the next start is possible at {formatTime(starts.next_slot_at)}. A
            trigger over the cap is refused with one comment and not started later.
          </p>
        ) : starts ? (
          <p className="dim small">
            {starts.remaining} {starts.remaining === 1 ? 'start' : 'starts'} left in this window.
          </p>
        ) : null}
        <svg
          className="starts-chart"
          viewBox="0 0 300 124"
          role="img"
          aria-label={`Starts per day, last 7 days: ${bars.map((b) => `${b.label}: ${b.count}`).join(', ')}${cap ? `; cap ${cap}` : ''}`}
        >
          <line className="base" x1={left} x2={right} y1={base} y2={base} />
          {cap ? <path className="cap" d={`M${left} ${y(cap).toFixed(1)}H${right}`} /> : null}
          {bars.map((bar, i) => {
            const x = left + i * slot + 8;
            const h = (bar.count / peak) * height;
            return (
              <g key={bar.key}>
                <rect
                  className={bar.atCap ? 'dangerf' : 'neutralf'}
                  x={x}
                  y={base - h}
                  width={slot - 16}
                  height={h}
                  rx={1}
                />
                <text x={x + (slot - 16) / 2} y={base - h - 4} textAnchor="middle">
                  {bar.count}
                </text>
                <text x={x + (slot - 16) / 2} y={base + 14} textAnchor="middle">
                  {bar.label}
                </text>
              </g>
            );
          })}
        </svg>
        <p className="dim small">
          Counted from the {data.tasks.length} tasks the state file lists (the latest 100), by day
          in Kyiv time. The cap itself holds over any 24 hours.
        </p>
      </div>
    </Panel>
  );
}

function RulesPanel({ data }: { data: IntakeBody }) {
  const caps = data.caps;
  const authors = data.allowed_authors;
  return (
    <Panel title="Rules" style={cssVars({ '--o': 5 })} aside={<Later>edit later</Later>}>
      <KeyValue
        stacked
        items={[
          [
            'Label',
            data.label ? `${data.label} (the person who applied it must be allowed)` : DASH,
            true,
          ],
          [
            'Mention',
            data.mention ? `${data.mention} (the comment author must be allowed)` : DASH,
            true,
          ],
          [
            'Allowed authors',
            authors.length ? (
              authors.join(', ')
            ) : (
              <span key="none" className="dim">
                none: nothing triggers
              </span>
            ),
            authors.length > 0,
          ],
          [
            'Size class',
            caps
              ? `from the issue’s size:S, size:M or size:L label, else ${caps.budget_class}`
              : DASH,
          ],
          [
            'Per trigger',
            caps
              ? `${formatTokens(caps.max_tokens_total)} tokens, ${formatUsd(caps.max_usd_total)}; ${caps.max_concurrent} concurrent; ${caps.max_starts_per_day} starts per day; ${caps.max_review_rounds} review rounds`
              : DASH,
          ],
        ]}
      />
    </Panel>
  );
}

function WatchedPanel({ data, rows }: { data: IntakeBody; rows: PolicyRow[] }) {
  const modeOf = (name: string) =>
    rows.find((row) => row.repo.toLowerCase() === name.toLowerCase())?.mode ?? 'off';
  return (
    <Panel
      title="Watched repositories"
      count={data.watched.length}
      flush
      style={cssVars({ '--o': 6 })}
    >
      {data.watched.length === 0 ? (
        <EmptyState
          icon="folder"
          title="No repository watched"
          text="automation.github.repositories is empty, so no issue or comment starts anything."
        />
      ) : (
        <ul className="list-reset">
          {data.watched.map((repo) => (
            <li
              key={repo.repository}
              className="li"
              style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}
            >
              <div className="li__main">
                <div className="b ch-trunc" title={repo.repository}>
                  {repo.repository}
                </div>
                <div className="li__meta mono" title={repo.path}>
                  {repo.path}
                </div>
              </div>
              <Badge size="s" mode={modeOf(repo.repository)} />
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function RoundsPanel({ data, cap }: { data: IntakeBody; cap?: number }) {
  const rounds = reviewRounds(data);
  const limit = data.caps?.max_review_rounds;
  return (
    <Panel title="Review rounds" count={rounds.length} flush style={cssVars({ '--o': 3 })}>
      {rounds.length === 0 ? (
        <EmptyState
          icon="repeat"
          title="No review round yet"
          text="An inline review comment from an allowed author on an open chargehand pull request starts one."
        />
      ) : (
        <ul className="list-reset">
          {rounds.map((row) => (
            <RoundItem key={row.key} row={row} limit={limit} />
          ))}
        </ul>
      )}
      <p className="pad dim small sep-top">
        {limit === 0
          ? 'Review rounds are off (max_review_rounds is 0).'
          : `Intake allows ${limit ?? DASH} rounds per pull request`}
        {cap != null ? `; the cap is shared with the CI fixer (${cap} attempts in all).` : '.'}
      </p>
    </Panel>
  );
}

function RoundItem({ row, limit }: { row: RoundRow; limit?: number }) {
  const url = safeHttpUrl(row.url);
  const title = `${row.repository}#${row.pull}`;
  const state =
    row.phase === 'running'
      ? 'running'
      : row.phase === 'asked'
        ? 'asked a question'
        : row.phase === 'done'
          ? 'done'
          : 'waiting for a round';
  return (
    <li className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
      <div className="li__main">
        <div className="b ch-trunc" title={title}>
          {url ? (
            <a href={url} target="_blank" rel="noreferrer noopener">
              {title}
            </a>
          ) : (
            title
          )}
        </div>
        <div className="li__meta">
          {row.pending > 0
            ? `${row.pending} comment${row.pending === 1 ? '' : 's'} waiting`
            : 'no comment waiting'}
        </div>
      </div>
      <div className="intake-round">
        <Badge
          size="s"
          mono
          label={
            row.round > 0 ? `round ${row.round}${limit ? ` of ${limit}` : ''}` : 'no round yet'
          }
        />
        <div className="dim small">{state}</div>
      </div>
    </li>
  );
}
