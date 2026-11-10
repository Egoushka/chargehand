import { Link } from '@tanstack/react-router';
import { blockerLabel, useFleet } from '../../api/fleet';
import { formatDuration, shortId } from '../../lib/format';
import { Banner, EmptyState, Panel, Skeleton } from '../../ui';

/** The cockpit board: running and queued items with age and blocker, claims, and open pull requests, from `GET /v1/fleet` every 10 s. */
export function FleetBoard() {
  const fleet = useFleet();
  const data = fleet.data;
  if (!data) {
    return (
      <Panel title="Fleet" busy={fleet.isPending}>
        {fleet.isError ? (
          <div className="pad dim">Cannot read the fleet: {fleet.error.message}</div>
        ) : (
          <div className="pad stack stack--tight">
            <Skeleton width="70%" />
            <Skeleton width="55%" />
          </div>
        )}
      </Panel>
    );
  }
  const paused = data.runs.filter((r) => r.blocked_by === 'rate_limit');
  return (
    <div className="stack">
      {paused.length > 0 ? (
        <Banner tone="warn" icon="clock" title="Paused by a provider rate limit">
          {paused.length === 1 ? '1 item is' : `${paused.length} items are`} held until the limit
          resets.
        </Banner>
      ) : null}
      <Panel title="Running" count={data.runs.length} flush>
        {data.runs.length === 0 ? (
          <EmptyState title="Nothing running or queued" text="Nothing to show." />
        ) : (
          <ul className="fleet-list" aria-label="Running and queued">
            {data.runs.map((r) => (
              <li key={r.id} className="fleet-row">
                <Link to="/runs/$runId" params={{ runId: r.id }} className="link">
                  {r.kind} {shortId(r.id)}
                </Link>
                <span className="dim">{r.state}</span>
                <span className="dim">{formatDuration(r.age_seconds)}</span>
                <span data-blocker={r.blocked_by ?? undefined}>{blockerLabel(r.blocked_by)}</span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
      <Panel title="Claims" count={data.claims.length} flush>
        {data.claims.length === 0 ? (
          <EmptyState title="No claims held" text="Nothing to show." />
        ) : (
          <ul className="fleet-list" aria-label="Claims">
            {data.claims.map((c) => (
              <li key={`${c.repository}#${c.issue}`} className="fleet-row">
                <span>
                  {c.repository}#{c.issue}
                </span>
                <span className="dim">
                  {c.state} by {shortId(c.holder)}
                </span>
                <span className="dim">{formatDuration(c.age_seconds)}</span>
                <span>{blockerLabel(c.blocked_by)}</span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
      <Panel title="Open pull requests" count={data.pull_requests.length} flush>
        {data.pull_requests.length === 0 ? (
          <EmptyState title="No open pull requests" text="Nothing to show." />
        ) : (
          <ul className="fleet-list" aria-label="Open pull requests">
            {data.pull_requests.map((p) => (
              <li key={p.url} className="fleet-row">
                <a href={p.url} target="_blank" rel="noreferrer noopener" className="link">
                  {p.repository}#{p.number}
                </a>
                <span className="dim">{p.state.replaceAll('_', ' ')}</span>
                <span className="dim">{formatDuration(p.age_seconds)}</span>
                <span data-blocker={p.blocked_by ?? undefined}>{blockerLabel(p.blocked_by)}</span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}
