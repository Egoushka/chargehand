import { asRecord, verificationFacts } from '../batch/artifacts';

/** `mutation` of `batch-task/v1`: the share of mutants of the changed lines the tests killed, against the share required. */
export interface MutationFacts {
  ran: boolean;
  score: number | null;
  threshold: number | null;
  passed: boolean;
  survivors: number | null;
  killed: number | null;
  notRun: string | null;
}

const num = (v: unknown): number | null => (typeof v === 'number' ? v : null);

export function mutationFacts(mutation: unknown): MutationFacts | null {
  const m = asRecord(mutation);
  if (!m) return null;
  return {
    ran: m.ran === true,
    score: num(m.score),
    threshold: num(m.threshold),
    passed: m.passed === true,
    survivors: num(m.survivors),
    killed: num(m.killed),
    notRun: typeof m.not_run === 'string' ? m.not_run : null,
  };
}

export type Mark = 'ok' | 'fail' | 'warn' | 'none';

export interface VerdictItem {
  key: string;
  mark: Mark;
  text: string;
}

const pct = (share: number) => `${Math.round(share * 1000) / 10}%`;

function mutationItem(mutation: unknown): VerdictItem {
  const m = mutationFacts(mutation);
  if (!m) return { key: 'mutation', mark: 'none', text: 'mutation: not checked' };
  if (!m.ran) {
    return {
      key: 'mutation',
      mark: 'fail',
      text: `mutation: could not run${m.notRun ? ` (${m.notRun})` : ''}`,
    };
  }
  if (m.score == null) return { key: 'mutation', mark: 'ok', text: 'mutation: no mutant was made' };
  const against = m.threshold != null ? ` ${m.passed ? '≥' : '<'} ${m.threshold.toFixed(2)}` : '';
  const why = m.passed
    ? ''
    : ` (below the required ${m.threshold != null ? pct(m.threshold) : 'share'})`;
  return {
    key: 'mutation',
    mark: m.passed ? 'ok' : 'fail',
    text: `mutation ${m.score.toFixed(2)}${against}${why}`,
  };
}

/** The verdict row of a quality card: what chargehand's own checks said, one item each. */
export function verdictItems(detail: {
  verification?: unknown;
  mutation?: unknown;
}): VerdictItem[] {
  const v = verificationFacts(detail.verification);
  const verification: VerdictItem = v
    ? {
        key: 'verification',
        mark: v.passed ? 'ok' : 'fail',
        text: v.passed
          ? `verification passed${v.seconds != null ? ` (${Math.round(v.seconds)}s)` : ''}`
          : v.timedOut
            ? 'verification timed out'
            : `verification failed (exit ${v.exitCode ?? '?'})`,
      }
    : { key: 'verification', mark: 'none', text: 'verification not recorded' };
  const items = [verification, mutationItem(detail.mutation)];
  if (v?.flagged) {
    items.push({ key: 'integrity', mark: 'warn', text: `integrity: ${v.reason ?? 'flagged'}` });
  }
  return items;
}

export interface ReviewClaim {
  text: string;
  verdict: string;
}

/** The claims of the `review` artifact with their support verdicts, when the artifact carries them. */
export function reviewClaims(review: unknown): ReviewClaim[] {
  const claims = asRecord(review)?.claims;
  if (!Array.isArray(claims)) return [];
  return claims.flatMap((c) => {
    const r = asRecord(c);
    const text =
      typeof r?.text === 'string' ? r.text : typeof r?.claim === 'string' ? r.claim : null;
    const verdict = r?.support ?? r?.verdict;
    return text && typeof verdict === 'string' ? [{ text, verdict }] : [];
  });
}

/** Whether the card says the task passed: chargehand's own verification passed, and no check failed or was flagged. A check that did not apply (mutation on a change with no C# file) does not count against it. */
export function qualityPassed(detail: { verification?: unknown; mutation?: unknown }): boolean {
  const items = verdictItems(detail);
  return (
    items.find((i) => i.key === 'verification')?.mark === 'ok' &&
    items.every((i) => i.mark === 'ok' || i.mark === 'none')
  );
}
