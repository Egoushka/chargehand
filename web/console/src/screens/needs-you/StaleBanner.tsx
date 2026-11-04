import { formatDuration } from '../../lib/format';
import { Button } from '../../ui';
import { STALE_AFTER_HOURS } from './model';

export function StaleBanner({
  count,
  longest,
  onShow,
}: {
  count: number;
  longest: number;
  onShow: (() => void) | null;
}) {
  return (
    <div className="card pad-lg row" role="status">
      <span>
        {count} pull request{count === 1 ? ' has' : 's have'} waited for you more than{' '}
        {STALE_AFTER_HOURS} hours, the longest {formatDuration(longest)}.
      </span>
      <span className="spacer" />
      {onShow ? (
        <Button variant="quiet" label="Show PRs, longest waiting first" onClick={onShow} />
      ) : null}
    </div>
  );
}
