import { Link } from '@tanstack/react-router';
import type { RunSummary } from '../../api/types';
import { baseName, formatTime, formatTokens, formatUsd, shortId } from '../../lib/format';
import { runWord } from '../../lib/status';
import { safeHttpUrl } from '../../lib/url';
import { Badge, StatusPill } from '../../ui';
import { pullNumber } from './model';

/** A run on a phone: state, id and kind on the first line, repository and preset on the second, spend and time on the third. */
export function RunCard({ run }: { run: RunSummary }) {
  const pr = safeHttpUrl(run.pr_url);
  return (
    <li className="li run-card">
      <div className="row row--wrap">
        <StatusPill status={runWord(run)} bare />
        <Link
          to="/runs/$runId"
          params={{ runId: run.run_id }}
          className="mono-b"
          title={run.run_id}
          aria-label={run.run_id}
        >
          {shortId(run.run_id)}
        </Link>
        {run.kind ? <Badge size="s" label={run.kind} /> : null}
        <span className="spacer" />
        <span className="b">{formatUsd(run.usd)}</span>
      </div>
      <div className="li__title run-card__repo">
        {baseName(run.repository)} · {run.preset}
      </div>
      <div className="li__meta run-card__meta">
        {formatTokens(run.tokens)} tokens · {formatTime(run.started_at)}
        {run.error_code ? ` · ${run.error_code}` : ''}
        {pr ? (
          <>
            {' · '}
            <a href={pr} target="_blank" rel="noreferrer noopener">
              PR{pullNumber(run.pr_url) ? ` #${pullNumber(run.pr_url)}` : ''}
            </a>
          </>
        ) : null}
      </div>
    </li>
  );
}
