import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';
import { runQuery } from '../../api/queries';
import { useRunEvents } from '../../api/useRunEvents';
import { formatTime } from '../../lib/format';
import { runStatus } from '../../lib/status';
import {
  EmptyState,
  ErrorState,
  KeyValue,
  LogLine,
  LogStream,
  Panel,
  Skeleton,
  StatusPill,
} from '../../ui';

/** One run: its state and summary, and the live events while it runs. A first version: the full screen replaces it. */
export function RunDetail() {
  const { runId } = useParams({ from: '/runs/$runId' });
  const run = useQuery(runQuery(runId));
  const live = useRunEvents(runId);
  const state =
    run.data?.state === 'finished'
      ? runStatus(run.data.result.status)
      : run.data
        ? runStatus(run.data.status.status)
        : null;
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">
            Run <span className="mono-b">{runId}</span>
          </h1>
          <p className="screen__sub">
            <Link to="/runs">← Runs</Link>
          </p>
        </div>
        <div className="screen__actions">
          {state ? <StatusPill status={state} size="lg" /> : null}
        </div>
      </div>
      {run.isError ? (
        <ErrorState
          title="Cannot read the run"
          call={`GET /v1/runs/${runId}`}
          detail={run.error.message}
          onRetry={() => void run.refetch()}
        />
      ) : null}
      {run.isPending ? <Skeleton width="60%" /> : null}
      {run.data?.state === 'finished' ? (
        <Panel title="Result">
          <KeyValue
            items={[
              ['State', run.data.result.status],
              ['Summary', run.data.result.summary],
              ...(run.data.result.error
                ? ([
                    [
                      'Error',
                      `${run.data.result.error.code}: ${run.data.result.error.message}`,
                      true,
                    ],
                  ] as Array<[string, string, boolean]>)
                : []),
            ]}
          />
        </Panel>
      ) : null}
      <Panel title="Events" flush>
        {live.error ? (
          <div className="pad">
            <ErrorState title="The event stream failed" detail={live.error} />
          </div>
        ) : live.events.length === 0 ? (
          <EmptyState
            icon="timeline"
            title={live.closed ? 'No events' : 'Waiting for events'}
            text={
              live.closed
                ? 'The server holds no events for this run.'
                : 'Events appear here as the run goes on.'
            }
          />
        ) : (
          <LogStream label="Events of this run">
            {live.events.map(({ id, data: event }) => (
              <LogLine
                key={id}
                time={formatTime(event.at, { seconds: true })}
                kind={event.event ?? event.status}
                task={event.task_id}
                detail={event.detail ?? ''}
              />
            ))}
          </LogStream>
        )}
      </Panel>
    </div>
  );
}
