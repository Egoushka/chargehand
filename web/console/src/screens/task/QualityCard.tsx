import type { BatchTaskDetailBody } from '../../api/batch-task';
import { type Mark, mutationFacts, reviewClaims, verdictItems } from './quality';

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
    </section>
  );
}
