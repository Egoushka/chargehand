import { useQuery } from '@tanstack/react-query';
import { type TrainCarBody, type TrainsBody, trainQuery } from '../../api/pulls';
import { safeHttpUrl } from '../../lib/url';
import { Badge, cssVars, EmptyState, ErrorState, Panel, Skeleton } from '../../ui';

const label = (pull: TrainCarBody['pull']) =>
  pull.number != null ? `#${pull.number}` : pull.task_id;

const HELD: Record<TrainsBody['held'][number]['reason'], string> = {
  partial_draft: 'unfinished work, stays a draft',
  test_integrity: 'changes only tests or build files',
};

/** What merges by itself and what waits for a person, for one car. */
const who = (car: TrainCarBody) =>
  car.integrator_mode === 'merge'
    ? 'the integrator merges it when its checks pass'
    : car.awaiting_person
      ? 'waits for you to merge'
      : 'the integrator leaves it alone';

/** The order to merge open pull requests in, per repository, oldest first (CHARGEHAND-216). A proposal: the integrator does not follow it. */
export function MergeTrain() {
  const train = useQuery(trainQuery());
  const data = train.data;
  const cars = data?.trains.reduce((n, t) => n + t.cars.length, 0);
  return (
    <Panel
      title="Merge train"
      count={cars}
      flush
      busy={train.isPending}
      aside={<span className="dim">oldest first; a proposal the integrator does not follow</span>}
    >
      {train.isError ? (
        <div className="pad">
          <ErrorState
            title="Cannot read the merge train"
            call="GET /v1/pulls/train"
            detail={train.error.message}
            onRetry={() => void train.refetch()}
          />
        </div>
      ) : !data ? (
        <div className="pad stack">
          <Skeleton width="60%" />
          <Skeleton width="40%" />
        </div>
      ) : data.trains.length === 0 ? (
        <EmptyState
          icon="merge"
          title="Nothing to merge"
          text="No open pull request chargehand opened is waiting to merge."
        />
      ) : (
        <div className="stack pad">
          {data.trains.map((t) => (
            <div key={t.repository}>
              <div className="b">{t.repository}</div>
              <ol className="list-reset" aria-label={`Merge train of ${t.repository}`}>
                {t.cars.map((car) => (
                  <Car key={car.pull.url} car={car} />
                ))}
              </ol>
            </div>
          ))}
        </div>
      )}
      {data && data.held.length > 0 ? (
        <p className="pad dim small sep-top">
          Held, never merged:{' '}
          {data.held.map((h) => `${label(h.pull)} (${HELD[h.reason]})`).join(', ')}.
        </p>
      ) : null}
    </Panel>
  );
}

function Car({ car }: { car: TrainCarBody }) {
  const url = safeHttpUrl(car.pull.url);
  return (
    <li className="li" style={cssVars({ '--cols': '28px minmax(0,1fr)' })}>
      <span className="mono dim" aria-hidden="true">
        {car.position}
      </span>
      <div className="li__main">
        <div className="row row--wrap">
          {url ? (
            <a href={url} target="_blank" rel="noreferrer noopener" className="link mono">
              {label(car.pull)}
            </a>
          ) : (
            <span className="mono">{label(car.pull)}</span>
          )}
          <Badge size="s" mode={car.integrator_mode} />
          <span className="dim small">{who(car)}</span>
        </div>
        {car.after.length > 0 ? (
          <div className="li__meta wrap">
            After {car.after.join(' and ')}: shares {car.files_shared_with_earlier}{' '}
            {car.files_shared_with_earlier === 1 ? 'file' : 'files'}, so its branch needs bringing
            up to date once they merge.
          </div>
        ) : null}
      </div>
    </li>
  );
}
