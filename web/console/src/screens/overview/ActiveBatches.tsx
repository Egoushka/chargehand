import { useQueries, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { batchQuery } from '../../api/batch';
import { runsQuery } from '../../api/queries';
import { baseName, formatCount, shortId } from '../../lib/format';
import {
  Badge,
  buttonClass,
  EmptyState,
  ErrorState,
  Panel,
  SegmentedMeter,
  Skeleton,
  StatusPill,
} from '../../ui';
import { Elapsed } from './Elapsed';
import { activeBatch } from './model';

/** The batches that are running or queued now. They come from the same list the shell watches; each one's tasks come from its own view. */
const LIMIT = 6;

export function ActiveBatches() {
  const runs = useQuery(runsQuery({ kind: 'batch', limit: 50 }));
  const open = (runs.data ?? [])
    .filter((r) => r.status === 'running' || r.status === 'queued')
    .slice(0, LIMIT);
  const batches = useQueries({ queries: open.map((run) => batchQuery(run.run_id, 0)) });
  const loading = runs.isPending;

  return (
    <Panel
      title="Active batches"
      count={runs.data ? open.length : undefined}
      busy={loading}
      flush
      className="o3"
      aside={
        <Link to="/runs" className="link">
          All runs
        </Link>
      }
    >
      {loading ? (
        <div className="pad stack stack--tight">
          {[60, 100, 45].map((w, i) => (
            // biome-ignore lint/suspicious/noArrayIndexKey: fixed skeleton lines
            <Skeleton key={i} width={`${w}%`} height={i === 1 ? 6 : undefined} />
          ))}
        </div>
      ) : runs.isError && !runs.data ? (
        <div className="pad">
          <ErrorState
            title="Cannot list the batches"
            call="GET /v1/runs"
            detail={runs.error.message}
            onRetry={() => void runs.refetch()}
          />
        </div>
      ) : open.length === 0 ? (
        <EmptyState
          icon="layers"
          title="No batch is running"
          text="Start one from a template, or label a GitHub issue chargehand and intake starts it."
          action={
            <Link to="/new" className={buttonClass({ variant: 'primary' })}>
              Start a batch
            </Link>
          }
        />
      ) : (
        open.map((run, i) => {
          const detail = batches[i]?.data;
          const model = detail ? activeBatch(detail) : null;
          return (
            <Link
              key={run.run_id}
              to="/runs/$runId"
              params={{ runId: run.run_id }}
              className="batchrow"
            >
              <div className="batchrow__top">
                <StatusPill status={run.status === 'queued' ? 'queued' : 'running'} bare />
                <span className="mono-b ch-trunc" title={run.run_id}>
                  {shortId(run.run_id)}
                </span>
                {run.name ? <span className="dim">{run.name}</span> : null}
                <Badge
                  size="s"
                  label={baseName(run.repository)}
                  title={run.repository ?? undefined}
                />
                {run.commit ? <span className="mono dim">@{run.commit.slice(0, 7)}</span> : null}
                <span className="spacer" />
                {model ? (
                  <span
                    className="slots"
                    title={`${model.progress.running} of ${model.slots.length} slots busy`}
                  >
                    {model.slots.map((on, n) => (
                      // biome-ignore lint/suspicious/noArrayIndexKey: slots have no identity
                      <i key={n} className="slot" data-on={on ? 'true' : 'false'} />
                    ))}
                  </span>
                ) : null}
              </div>
              {model ? (
                <>
                  <SegmentedMeter segments={model.progress.segments} label={model.progress.label} />
                  <div className="batchrow__facts">
                    <span>
                      <b>
                        {model.progress.done} of {model.progress.total}
                      </b>{' '}
                      done
                    </span>
                    <span>
                      <b>{model.progress.running}</b> running
                    </span>
                    <span>
                      <b>{model.tokens}</b> tokens
                    </span>
                    <span>
                      <b>{model.usd}</b> of {model.cap}
                    </span>
                    <span>
                      elapsed{' '}
                      <b>
                        <Elapsed since={run.started_at} />
                      </b>
                    </span>
                  </div>
                </>
              ) : batches[i]?.isError ? (
                <div className="batchrow__facts">
                  <span>Tasks unavailable: {batches[i]?.error.message}</span>
                  <span>
                    <b>{formatCount(run.tokens)}</b> tokens
                  </span>
                </div>
              ) : (
                <Skeleton width="100%" height={6} />
              )}
            </Link>
          );
        })
      )}
    </Panel>
  );
}
