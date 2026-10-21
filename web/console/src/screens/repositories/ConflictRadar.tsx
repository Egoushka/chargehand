import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { type ConflictBody, type ConflictPullBody, conflictsQuery } from '../../api/pulls';
import { safeHttpUrl } from '../../lib/url';
import { Badge, cssVars, EmptyState, ErrorState, Panel, Skeleton } from '../../ui';

const SHOWN_FILES = 5;

const label = (pull: ConflictPullBody) => (pull.number != null ? `#${pull.number}` : pull.task_id);

/** Open pull requests of one repository whose tasks changed the same paths (CHARGEHAND-216): merging one is likely to make the other conflict. Read only. */
export function ConflictRadar() {
  const radar = useQuery(conflictsQuery());
  const data = radar.data;
  return (
    <Panel
      title="Conflict radar"
      count={data?.conflicts.length}
      countTone={data?.conflicts.length ? 'accent' : undefined}
      flush
      busy={radar.isPending}
      aside={<span className="dim">open pull requests that change the same files</span>}
    >
      {radar.isError ? (
        <div className="pad">
          <ErrorState
            title="Cannot read the conflict radar"
            call="GET /v1/pulls/conflicts"
            detail={radar.error.message}
            onRetry={() => void radar.refetch()}
          />
        </div>
      ) : !data ? (
        <div className="pad stack">
          <Skeleton width="70%" />
          <Skeleton width="50%" />
        </div>
      ) : data.conflicts.length === 0 ? (
        <EmptyState
          icon="merge"
          title="No open pull requests share a file"
          text={`Compared ${data.open_pulls - data.unread.length} of ${data.open_pulls} open pull requests chargehand opened.`}
        />
      ) : (
        <ul className="list-reset">
          {data.conflicts.map((conflict) => (
            <ConflictItem key={conflict.pulls.map((p) => p.url).join(' ')} conflict={conflict} />
          ))}
        </ul>
      )}
      {data && data.unread.length > 0 ? (
        <p className="pad dim small sep-top">
          Not compared, no changed paths recorded: {data.unread.map(label).join(', ')}.
        </p>
      ) : null}
    </Panel>
  );
}

function ConflictItem({ conflict }: { conflict: ConflictBody }) {
  const partial = conflict.pulls.filter((p) => !p.files_listed);
  const more = conflict.files_shared - Math.min(SHOWN_FILES, conflict.files.length);
  return (
    <li className="li" style={cssVars({ '--cols': 'minmax(0,1fr)' })}>
      <div className="row row--wrap">
        <span className="b">{conflict.repository}</span>
        {conflict.pulls.map((pull, i) => (
          <span key={pull.url} className="row">
            {i > 0 ? (
              <span className="dim" aria-hidden="true">
                and
              </span>
            ) : null}
            <PullLink pull={pull} />
          </span>
        ))}
        <Badge
          size="s"
          tone="warn"
          label={`${conflict.files_shared} shared ${conflict.files_shared === 1 ? 'file' : 'files'}`}
        />
      </div>
      <ul
        className="list-reset conflict-files"
        aria-label={`Files both change in ${conflict.repository}`}
      >
        {conflict.files.slice(0, SHOWN_FILES).map((file) => (
          <li key={file} className="mono small ch-trunc" title={file}>
            {file}
          </li>
        ))}
      </ul>
      {more > 0 ? <div className="li__meta">and {more} more</div> : null}
      {partial.map((pull) => (
        <div key={pull.url} className="li__meta">
          {label(pull)} changed {pull.files_total} files and its result kept fewer: an overlap may
          be missed.
        </div>
      ))}
      <div className="li__meta">
        Runs:{' '}
        {conflict.pulls.map((pull, i) => (
          <span key={pull.url}>
            {i > 0 ? ', ' : null}
            <Link to="/runs/$runId" params={{ runId: pull.batch_run_id }} className="link">
              {label(pull)} {pull.task_id}
            </Link>
          </span>
        ))}
      </div>
    </li>
  );
}

function PullLink({ pull }: { pull: ConflictPullBody }) {
  const url = safeHttpUrl(pull.url);
  return url ? (
    <a href={url} target="_blank" rel="noreferrer noopener" className="link mono">
      {label(pull)}
    </a>
  ) : (
    <span className="mono">{label(pull)}</span>
  );
}
