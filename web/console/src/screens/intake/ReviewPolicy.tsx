import { type ReviewPolicy as Policy, useReviewPolicy } from '../../api/intake';
import { formatUsd } from '../../lib/format';
import { Banner, Button } from '../../ui';

/** What the owner changed about reviews from an inbox card (ADR 0085), each with the Undo that puts it back. */
export function ReviewPolicy({ policy }: { policy: Policy }) {
  const change = useReviewPolicy();
  if (policy.excluded.length === 0 && policy.budget_usd === null) return null;
  return (
    <section className="pad sep-top" aria-label="Review policy">
      <div className="b">Review policy</div>
      {change.isError ? (
        <Banner tone="danger" icon="alert-circle" title="The change was not made" role="alert">
          {change.error.message}
        </Banner>
      ) : null}
      <ul className="list-reset">
        {policy.excluded.map((repository) => (
          <li key={repository} className="row">
            <span className="grow">
              <span className="mono">{repository}</span>{' '}
              <span className="dim">is not reviewed</span>
            </span>
            <Button
              variant="quiet"
              label="Undo"
              aria-label={`Review ${repository} again`}
              loading={change.isPending}
              onClick={() => change.mutate({ kind: 'include', repository })}
            />
          </li>
        ))}
        {policy.budget_usd === null ? null : (
          <li className="row">
            <span className="grow">
              One review may spend up to <b>{formatUsd(policy.budget_usd)}</b>{' '}
              <span className="dim">(the preset's own cap is lifted)</span>
            </span>
            <Button
              variant="quiet"
              label="Undo"
              aria-label="Restore the preset's review budget"
              loading={change.isPending}
              onClick={() => change.mutate({ kind: 'budget', usd: null })}
            />
          </li>
        )}
      </ul>
    </section>
  );
}
