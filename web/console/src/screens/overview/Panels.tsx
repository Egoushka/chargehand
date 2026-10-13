import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import type { InboxItem } from '../../api/inbox';
import type { OverviewBody } from '../../api/overview';
import { usePulls } from '../../api/pulls';
import { systemQuery } from '../../api/system';
import {
  baseName,
  DASH,
  DISPLAY_ZONE_LABEL,
  formatAgo,
  formatClock,
  formatDateTime,
  formatDuration,
  formatPercent,
  formatTime,
} from '../../lib/format';
import { inboxStatus, STATUS } from '../../lib/status';
import { safeHttpUrl } from '../../lib/url';
import { useShell } from '../../shell/ShellContext';
import {
  Badge,
  Button,
  buttonClass,
  CapMeter,
  cssVars,
  EmptyState,
  ErrorState,
  Panel,
  Skeleton,
  StatusPill,
} from '../../ui';
import { gib, inboxPreview, recentMerges, spendRows } from './model';

/** The newest things that wait for you, with the one action that takes you to each. */
export function NeedsPanel({
  items,
  total,
  loading,
  error,
}: {
  items: InboxItem[];
  total: number;
  loading: boolean;
  error?: string;
}) {
  const rows = inboxPreview(items);
  return (
    <Panel
      title="Needs you"
      count={total}
      countTone="accent"
      busy={loading}
      flush
      className="o1"
      aside={
        <Link to="/needs-you" className="link">
          Open inbox
        </Link>
      }
    >
      {loading ? (
        <div className="pad stack stack--tight">
          {[80, 65, 75].map((w) => (
            <Skeleton key={w} width={`${w}%`} />
          ))}
        </div>
      ) : error ? (
        <div className="pad dim">Cannot read the inbox: {error}</div>
      ) : rows.length === 0 ? (
        <EmptyState
          title="Nothing needs you"
          text="No task is waiting for an answer, no PR is waiting for a merge and no run failed in the last 24 hours."
        />
      ) : (
        rows.map((item) => <NeedsRow key={item.id} item={item} />)
      )}
    </Panel>
  );
}

function NeedsRow({ item }: { item: InboxItem }) {
  const status = inboxStatus(item);
  const pr = item.pull_request;
  const url = safeHttpUrl(pr?.url);
  const run = item.batch_run_id ?? item.run_id;
  return (
    <div className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
      <div className="li__main">
        <div className="row row--wrap">
          <StatusPill status={status} bare />
          <span className="mono dim ch-trunc">{baseName(item.repository)}</span>
          {pr?.test_integrity_flagged ? (
            <Badge size="s" tone="warn" label="test-integrity flagged" />
          ) : null}
        </div>
        <div className="li__title" title={item.title}>
          {item.title}
        </div>
        <div className="li__meta">
          {item.kind === 'failure' && item.error ? `${item.error.code} · ` : ''}
          {item.task_id ?? item.run_id ?? ''} · {formatTime(item.at)}
        </div>
      </div>
      {item.kind === 'question' ? (
        <Link to="/needs-you" className={buttonClass({ variant: 'primary', size: 'sm' })}>
          Answer
        </Link>
      ) : item.kind === 'pull_request' && url ? (
        <a
          href={url}
          target="_blank"
          rel="noreferrer noopener"
          className={buttonClass({ variant: 'primary', size: 'sm' })}
        >
          Open PR
        </a>
      ) : run ? (
        <Link to="/runs/$runId" params={{ runId: run }} className={buttonClass({ size: 'sm' })}>
          Open run
        </Link>
      ) : null}
    </div>
  );
}

/** Spend today against each account's cap. An account with no cap says so on a dashed track, never a full or empty bar. */
export function SpendPanel({ overview }: { overview: OverviewBody }) {
  const rows = spendRows(overview);
  return (
    <Panel
      title="Spend today vs caps"
      className="o4"
      aside={`resets ${formatClock(overview.standing.spend.day.resets_at)} ${DISPLAY_ZONE_LABEL}`}
    >
      <div className="stack">
        {rows.map((row) => (
          <CapMeter
            key={row.name}
            label={`${row.name}, spend today`}
            head={
              <>
                <b className="meter-name">{row.name}</b> · {row.tokens} tokens
              </>
            }
            value={row.value}
            cap={row.cap}
            text={row.text}
            level={row.level}
          />
        ))}
      </div>
    </Panel>
  );
}

function Slots({ running, max }: { running: number; max: number }) {
  return (
    <span className="slots">
      {Array.from({ length: max }, (_, i) => (
        // biome-ignore lint/suspicious/noArrayIndexKey: slots have no identity
        <i key={i} className="slot" data-on={i < running ? 'true' : 'false'} />
      ))}
    </span>
  );
}

/** What decides whether the next task starts: the host's memory and load, the session slots, the rate-limit pause, and the halt switch. */
export function AdmissionPanel({ overview, owner }: { overview: OverviewBody; owner: boolean }) {
  const shell = useShell();
  const { driven, server } = overview;
  const host = driven.host;
  const slotsMax = driven.sessions.max_parallel_total ?? null;
  const total = host?.total_mb ?? null;
  const available = host?.available_mb ?? null;
  const reservePct = total && host ? (host.reserve_mb / total) * 100 : null;
  const runner = driven.runner;
  return (
    <Panel
      title="Admission"
      className="o5"
      aside={owner && runner?.configured ? 'host' : undefined}
    >
      <div className="stack">
        {!driven.enabled ? (
          <p className="dim">Driven sessions are off in this profile, so no task is admitted.</p>
        ) : null}
        {owner && runner?.configured && runner.reachable === false ? (
          <p className="dim" role="status">
            <span className="mono">Runner</span> is unreachable
            {runner.error ? `: ${runner.error}` : ''}. Tasks wait until it answers.
          </p>
        ) : null}
        {host && total && available != null ? (
          <>
            <CapMeter
              label="Memory available"
              head="Memory available"
              value={available}
              cap={total}
              text={`${gib(available)} of ${gib(total)}`}
              level={
                (host.free_after_reserve_mb ?? 1) <= 0
                  ? 'danger'
                  : available / total < 0.2
                    ? 'warn'
                    : undefined
              }
              ticks={reservePct != null ? [reservePct] : undefined}
            />
            <p className="dim small">
              Tick: the {gib(host.reserve_mb)} reserve admission keeps free.{' '}
              {gib(host.free_after_reserve_mb)} are free beyond it.
            </p>
          </>
        ) : null}
        {host && host.load1 != null && host.cpus ? (
          <CapMeter
            label="Load, 1 min"
            value={host.load1}
            cap={host.cpus}
            text={`${host.load1.toFixed(1)} on ${host.cpus} CPUs · ${formatPercent(Math.round((host.load1 / host.cpus) * 100))} each`}
            ticks={host.max_load_per_cpu ? [Math.min(100, host.max_load_per_cpu * 100)] : undefined}
          />
        ) : null}
        <div className="row row--between">
          <span className="dim">Sessions</span>
          <span className="row">
            {slotsMax ? <Slots running={driven.sessions.running} max={slotsMax} /> : null}
            <b>
              {slotsMax ? `${driven.sessions.running} of ${slotsMax}` : driven.sessions.running}
            </b>
          </span>
        </div>
        <div className="row row--between">
          <span className="dim">Rate limit</span>
          {driven.rate_limit.paused ? (
            <StatusPill
              status="rate_limited"
              bare
              label={`Paused until ${formatTime(driven.rate_limit.resets_at)}`}
            />
          ) : (
            <StatusPill status="done" bare label="No pause" />
          )}
        </div>
        {owner ? (
          <>
            <hr className="hr" />
            <div className="row row--between">
              <div>
                <div className="b">Server</div>
                <div className="dim small">
                  {server.halted
                    ? 'Halted: nothing starts'
                    : 'Accepting runs; halt cancels every run'}
                </div>
              </div>
              {server.halted ? (
                <Button
                  label="Resume server"
                  onClick={shell.resumeServer}
                  loading={shell.haltBusy}
                />
              ) : (
                <Button label="Halt server" variant="danger-quiet" onClick={shell.requestHalt} />
              )}
            </div>
          </>
        ) : null}
      </div>
    </Panel>
  );
}

/** Version, uptime, runner and run slots. Uptime and the runner's containers come from the owner's system view. */
export function SystemPanel({ overview, owner }: { overview: OverviewBody; owner: boolean }) {
  const system = useQuery({ ...systemQuery(), enabled: owner });
  const { server, driven } = overview;
  const started = system.data?.started_at;
  const runner = system.data?.runner ?? driven.runner;
  const rows: Array<{ tone?: 'warn' | 'danger' | 'off'; name: string; value: string }> = [
    {
      name: 'Server',
      value: `${server.version}${started ? ` · up ${formatDuration((Date.now() - Date.parse(started)) / 1000)}` : ''}`,
    },
    {
      name: 'Runs',
      value: `${server.runs_running} running, ${server.runs_queued} queued · limit ${server.max_unfinished}`,
    },
  ];
  if (owner && runner)
    rows.push({
      tone: !runner.configured ? 'off' : runner.reachable === false ? 'danger' : undefined,
      name: 'Runner',
      value: !runner.configured
        ? 'not configured'
        : runner.reachable === false
          ? 'unreachable'
          : system.data?.runner.container_count != null
            ? `reachable · ${system.data.runner.container_count} containers`
            : 'reachable',
    });
  return (
    <Panel
      title="System"
      className="o6"
      flush
      aside={
        owner ? (
          <Link to="/system" className="link">
            Details
          </Link>
        ) : undefined
      }
    >
      {rows.map((row) => (
        <div key={row.name} className="sysrow">
          <i className="dot" data-tone={row.tone} />
          <span>{row.name}</span>
          <span className="dim" title={row.value}>
            {row.value}
          </span>
        </div>
      ))}
    </Panel>
  );
}

/** Pull requests merged in the last 24 hours. */
export function MergesPanel() {
  const pulls = usePulls({ since: '7d', limit: 200 });
  const merges = recentMerges(pulls.data?.pulls ?? []);
  const shown = merges.slice(0, 5);
  return (
    <Panel title="Recent merges" className="o7" busy={pulls.isPending} flush aside="last 24 hours">
      {pulls.isPending ? (
        <div className="pad stack stack--tight">
          <Skeleton width="70%" />
          <Skeleton width="50%" />
        </div>
      ) : pulls.isError ? (
        <div className="pad">
          <ErrorState
            title="Cannot list pull requests"
            call="GET /v1/pulls"
            detail={pulls.error.message}
            onRetry={() => void pulls.refetch()}
          />
        </div>
      ) : shown.length === 0 ? (
        <EmptyState
          icon="merge"
          title="Nothing merged"
          text="No pull request that chargehand opened was merged in the last 24 hours."
        />
      ) : (
        shown.map((pr) => {
          const at = pr.state_at ?? pr.opened_at ?? '';
          const url = safeHttpUrl(pr.url);
          const title = `${pr.repository ?? DASH}${pr.number ? ` #${pr.number}` : ''}`;
          return (
            <div key={pr.url} className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
              <div className="li__main">
                <div className="li__title" title={title}>
                  {url ? (
                    <a href={url} target="_blank" rel="noreferrer noopener">
                      {title}
                    </a>
                  ) : (
                    title
                  )}
                </div>
                <div className="li__meta">
                  <span className="mono">{pr.task_id}</span> · {STATUS.merged.label.toLowerCase()}
                  {pr.integrator_mode === 'merge' ? ' by the integrator' : ''}
                </div>
              </div>
              <span className="dim nowrap" title={formatDateTime(at)}>
                {formatAgo(at)}
              </span>
            </div>
          );
        })
      )}
    </Panel>
  );
}
