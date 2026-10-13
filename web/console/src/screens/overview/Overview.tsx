import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useInbox } from '../../api/inbox';
import { useOverview } from '../../api/overview';
import { usePulls } from '../../api/pulls';
import { meQuery } from '../../api/queries';
import { DISPLAY_ZONE_LABEL, formatTime } from '../../lib/format';
import { buttonClass, ErrorState, Kpi, Panel, Skeleton } from '../../ui';
import { ActiveBatches } from './ActiveBatches';
import { kpis, recentMerges } from './model';
import { AdmissionPanel, MergesPanel, NeedsPanel, SpendPanel, SystemPanel } from './Panels';
import './overview.css';

/** How old a query's data is, for the error box: "Showing the last data, from 13:14." */
function lastSeen(updatedAt: number): string | undefined {
  return updatedAt > 0
    ? `Showing the last data, from ${formatTime(updatedAt, { seconds: true })}.`
    : undefined;
}

/** The console's first screen: what the fleet is doing now, what the host can still take, spend against caps, and what needs you. */
export function Overview() {
  const overview = useOverview();
  const inbox = useInbox();
  const pulls = usePulls({ since: '7d', limit: 200 });
  const me = useQuery(meQuery());
  const data = overview.data;
  const owner = (data?.scope ?? (me.data?.owner ? 'owner' : 'account')) === 'owner';
  const model = data ? kpis(data, recentMerges(pulls.data?.pulls ?? [])) : null;

  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Overview</h1>
          <p className="screen__sub">
            {data
              ? `${formatTime(data.generated_at)} ${DISPLAY_ZONE_LABEL} · server ${data.server.version}`
              : overview.isError
                ? 'Not loaded'
                : 'Loading'}
          </p>
        </div>
        <div className="screen__actions">
          <Link to="/new" className={buttonClass({ variant: 'primary' })}>
            <span className="ch-i ch-i-play" aria-hidden="true" />
            Start a batch
          </Link>
        </div>
      </div>

      {overview.isError ? (
        <ErrorState
          title="Cannot reach the server"
          call="GET /v1/overview"
          detail={overview.error.message}
          age={data ? lastSeen(overview.dataUpdatedAt) : undefined}
          onRetry={() => void overview.refetch()}
        />
      ) : null}

      {data || !overview.isError ? (
        <div className="g4" aria-busy={model ? undefined : true}>
          {model ? (
            <>
              <Kpi
                label="Sessions running"
                value={
                  <>
                    {model.sessions.running}
                    {model.sessions.max != null ? (
                      <span className="kpi-of"> of {model.sessions.max}</span>
                    ) : null}
                  </>
                }
                sub={model.sessions.sub}
              />
              <Kpi label="Spend today" value={model.spend.value} sub={model.spend.sub} />
              <Kpi label="Needs you" value={model.needs.value} sub={model.needs.sub} />
              <Kpi label="Merged" value={model.merged.value} sub={model.merged.sub} />
            </>
          ) : (
            [1, 2, 3, 4].map((n) => (
              <div key={n} className="ch-kpi">
                <Skeleton width="50%" />
                <Skeleton width="35%" height={28} />
                <Skeleton width="70%" />
              </div>
            ))
          )}
        </div>
      ) : null}

      {data || !overview.isError ? (
        <div className="g2">
          <div className="stack">
            <ActiveBatches />
            <NeedsPanel
              items={inbox.data?.items ?? []}
              total={data?.inbox.total ?? inbox.data?.counts.total ?? 0}
              loading={inbox.isPending}
              error={inbox.isError && !inbox.data ? inbox.error.message : undefined}
            />
          </div>
          <div className="stack">
            {data ? (
              <>
                <SpendPanel overview={data} />
                <AdmissionPanel overview={data} owner={owner} />
                <SystemPanel overview={data} owner={owner} />
                <MergesPanel />
              </>
            ) : (
              <Panel title="System" busy>
                <div className="stack stack--tight">
                  {[90, 60, 75].map((w) => (
                    <Skeleton key={w} width={`${w}%`} />
                  ))}
                </div>
              </Panel>
            )}
          </div>
        </div>
      ) : null}
    </div>
  );
}
