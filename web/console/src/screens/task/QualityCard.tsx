import { useState } from 'react';
import { useMergePull } from '../../api/actions';
import type { BatchTaskDetailBody } from '../../api/batch-task';
import { ApiError } from '../../api/client';
import { Banner, Button, ConfirmDialog } from '../../ui';
import { type Mark, mutationFacts, qualityPassed, reviewClaims, verdictItems } from './quality';

const GLYPH: Record<Mark, string> = { ok: '✓', fail: '✗', warn: '⚠', none: '–' };

/** The head of a task's page: the verdict of chargehand's own checks, the mutation evidence, and the review's claims. */
export function QualityCard({ detail }: { detail: BatchTaskDetailBody }) {
  const items = verdictItems(detail);
  const mutation = mutationFacts(detail.mutation);
  const claims = reviewClaims(detail.review);
  return (
    <section className="stack stack--tight quality-card" aria-label="Quality card">
      <div className="label-caps">Verdict</div>
      <ul className="quality-card__verdict">
        {items.map((i) => (
          <li key={i.key} className={`quality-card__item quality-card__item--${i.mark}`}>
            <span aria-hidden="true">{GLYPH[i.mark]}</span> {i.text}
          </li>
        ))}
      </ul>
      {mutation?.ran && mutation.survivors != null && mutation.killed != null ? (
        <p className="dim small">
          mutants: {mutation.survivors} survived, {mutation.killed} killed
        </p>
      ) : null}
      {claims.length > 0 ? (
        <>
          <div className="label-caps">Review claims</div>
          <ul className="quality-card__claims">
            {claims.map((c) => (
              <li key={`${c.verdict}:${c.text}`}>
                <span className="dim">{c.verdict}</span> {c.text}
              </li>
            ))}
          </ul>
        </>
      ) : null}
      <MergeAction detail={detail} />
    </section>
  );
}

/** Approve merge: only for a task whose pull request is open, enabled only when the card passed, and it asks first. The server decides again (policy, required checks) and says why when it refuses. */
function MergeAction({ detail }: { detail: BatchTaskDetailBody }) {
  const pull = detail.task.pull_request;
  const [asking, setAsking] = useState(false);
  const merge = useMergePull();
  const [owner, repo] = (pull?.repository ?? '').split('/');
  if (pull?.state !== 'open' || pull.number == null || !owner || !repo) return null;
  const number = pull.number;
  const passed = qualityPassed(detail);
  const done = merge.isSuccess;
  return (
    <div className="stack stack--tight">
      <div>
        <Button
          variant="primary"
          icon="pr"
          label="Approve merge"
          disabled={!passed || done}
          title={
            passed ? undefined : 'The quality card has not passed, so there is nothing to approve.'
          }
          onClick={() => setAsking(true)}
        />
      </div>
      {done ? <p className="dim small">Merged.</p> : null}
      {merge.isError ? (
        <Banner tone="warn" icon="alert-triangle" title="Not merged" role="alert">
          {merge.error instanceof ApiError ? merge.error.errors.join('; ') : 'The request failed.'}
        </Banner>
      ) : null}
      <ConfirmDialog
        open={asking}
        onOpenChange={setAsking}
        title={`Merge pull request ${number}?`}
        text={`This squash-merges ${pull.repository} #${number} into its base branch. The repository's required checks and the integrator's policy are checked again first. A merge is not undone from here.`}
        keepLabel="Keep it open"
        confirmLabel={`Merge PR ${number}`}
        busy={merge.isPending}
        onConfirm={() => {
          merge.mutate({ owner, repo, pull: number }, { onSettled: () => setAsking(false) });
        }}
      />
    </div>
  );
}
