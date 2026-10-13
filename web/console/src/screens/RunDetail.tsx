import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';
import { runQuery } from '../api/queries';
import { useRunEvents } from '../api/useRunEvents';
import { formatWhen } from '../lib/format';

/** One run: its state and summary, and the live events while it runs. A placeholder for the full screen. */
export function RunDetail() {
  const { runId } = useParams({ from: '/runs/$runId' });
  const run = useQuery(runQuery(runId));
  const live = useRunEvents(runId);
  return (
    <section>
      <p>
        <Link to="/runs">← Runs</Link>
      </p>
      <h1>
        Run <span className="mono">{runId}</span>
      </h1>
      {run.isPending ? <p className="muted">Loading…</p> : null}
      {run.error ? (
        <p role="alert" className="notice error">
          {run.error.message}
        </p>
      ) : null}
      {run.data?.state === 'finished' ? (
        <>
          <p>
            State: {run.data.result.status}
            {run.data.result.error ? ` (${run.data.result.error.code})` : ''}
          </p>
          <p>{run.data.result.summary}</p>
        </>
      ) : null}
      {run.data && run.data.state !== 'finished' ? <p>State: {run.data.status.status}</p> : null}
      <h2>Events</h2>
      {live.error ? (
        <p role="alert" className="notice error">
          {live.error}
        </p>
      ) : null}
      {live.events.length === 0 ? (
        <p className="muted">{live.closed ? 'No events.' : 'Waiting for events…'}</p>
      ) : (
        <ol className="events">
          {live.events.map(({ id, data: event }) => (
            <li key={id}>
              <time>{formatWhen(event.at)}</time> <strong>{event.event ?? event.status}</strong>
              {event.detail ? ` ${event.detail}` : ''}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
