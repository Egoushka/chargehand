import type { ArtifactRef, BatchBody } from '../../api/batch';
import type { BatchTaskDetailBody } from '../../api/batch-task';
import type { IconName } from '../../ui';

export type ArtifactView = 'changes' | 'verification' | 'json' | 'ref';

export interface ArtifactItem {
  id: string;
  name: string;
  meta: string;
  icon: IconName;
  view: ArtifactView;
  /** The artifact's content when the server serves it inline. */
  value?: unknown;
  /** The reference when it does not. */
  ref?: ArtifactRef;
}

export const asRecord = (value: unknown): Record<string, unknown> | null =>
  value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;

const text = (value: unknown): string | null => (typeof value === 'string' ? value : null);
const num = (value: unknown): number | null => (typeof value === 'number' ? value : null);

const NAMES: Record<string, { name: string; icon: IconName }> = {
  branch: { name: 'Branch', icon: 'branch' },
  'pull-request': { name: 'Pull request', icon: 'pr' },
  'governor-log': { name: 'Governor log', icon: 'activity' },
  'session-log': { name: 'Session log', icon: 'terminal' },
  'session-tests': { name: 'Session tests', icon: 'check' },
  verification: { name: 'Verification', icon: 'shield-check' },
  changes: { name: 'Changes', icon: 'file' },
  review: { name: 'Review', icon: 'eye' },
  'kept-work': { name: 'Kept work', icon: 'box' },
  'driven-batch': { name: 'Tasks of the batch', icon: 'layers' },
  plan: { name: 'Plan', icon: 'list' },
  retro: { name: 'Retro', icon: 'history' },
};

const label = (kind: string) => NAMES[kind] ?? { name: kind, icon: 'file' as IconName };

/** `+49 −4` of a changes artifact, from its file list. */
export function changeTotals(
  changes: unknown,
): { files: number; added: number; removed: number } | null {
  const c = asRecord(changes);
  const files = Array.isArray(c?.files) ? c.files.map(asRecord) : null;
  if (!c || !files) return null;
  return {
    files: num(c.files_total) ?? files.length,
    added: files.reduce((n, f) => n + (num(f?.added) ?? 0), 0),
    removed: files.reduce((n, f) => n + (num(f?.removed) ?? 0), 0),
  };
}

/** The test files and build files chargehand's test-integrity check named, for the diff to flag. */
export function testIntegrityPaths(verification: unknown): string[] {
  const integrity = asRecord(asRecord(verification)?.test_integrity);
  const paths = [integrity?.test_paths, integrity?.build_paths]
    .flatMap((p) => (Array.isArray(p) ? p : []))
    .filter((p): p is string => typeof p === 'string');
  return paths;
}

export function verificationFacts(verification: unknown) {
  const v = asRecord(verification);
  if (!v) return null;
  const argv = Array.isArray(v.argv)
    ? v.argv.filter((a): a is string => typeof a === 'string')
    : [];
  const integrity = asRecord(v.test_integrity);
  return {
    command: argv.join(' '),
    source: text(v.source),
    exitCode: num(v.exit_code),
    passed: v.passed === true,
    timedOut: v.timed_out === true,
    seconds: num(v.duration_seconds),
    network: text(v.network),
    sandbox: text(v.sandbox),
    tail: text(v.output_tail),
    flagged: integrity?.flagged === true,
    reason: text(integrity?.reason),
  };
}

export function batchArtifacts(batch: BatchBody): ArtifactItem[] {
  const items: ArtifactItem[] = [];
  if (batch.result)
    items.push({
      id: 'result',
      name: 'Result JSON',
      meta: `${batch.result.status}`,
      icon: 'code',
      view: 'json',
      value: batch.result,
    });
  if (batch.plan != null)
    items.push({
      id: 'plan',
      name: 'Plan',
      meta: 'planning step',
      icon: 'list',
      view: 'json',
      value: batch.plan,
    });
  if (batch.retro != null)
    items.push({
      id: 'retro',
      name: 'Retro',
      meta: 'after the batch',
      icon: 'history',
      view: 'json',
      value: batch.retro,
    });
  for (const ref of batch.artifacts) {
    if (ref.kind === 'plan' || ref.kind === 'retro') continue;
    items.push({
      id: ref.kind,
      name: label(ref.kind).name,
      meta: ref.media_type,
      icon: label(ref.kind).icon,
      view: 'ref',
      ref,
    });
  }
  return items;
}

export function taskArtifacts(detail: BatchTaskDetailBody): ArtifactItem[] {
  const items: ArtifactItem[] = [];
  const inline: Array<[string, unknown, ArtifactView]> = [
    ['changes', detail.changes, 'changes'],
    ['verification', detail.verification, 'verification'],
    ['session-tests', detail.session_tests, 'json'],
    ['review', detail.review, 'json'],
    ['kept-work', detail.kept_work, 'json'],
  ];
  for (const [kind, value, view] of inline) {
    if (value == null) continue;
    const totals = kind === 'changes' ? changeTotals(value) : null;
    const facts = kind === 'verification' ? verificationFacts(value) : null;
    items.push({
      id: kind,
      name: label(kind).name,
      icon: label(kind).icon,
      view,
      value,
      meta: totals
        ? `${totals.files} ${totals.files === 1 ? 'file' : 'files'}, +${totals.added} −${totals.removed}`
        : facts
          ? facts.passed
            ? 'passed'
            : `failed${facts.exitCode != null ? ` (exit ${facts.exitCode})` : ''}`
          : kind === 'session-tests'
            ? 'the session’s claim'
            : 'inline',
    });
  }
  for (const ref of detail.artifacts) {
    if (items.some((i) => i.id === ref.kind)) continue;
    items.push({
      id: ref.kind,
      name: label(ref.kind).name,
      meta: ref.uri ? 'reference' : ref.media_type,
      icon: label(ref.kind).icon,
      view: 'ref',
      ref,
    });
  }
  items.push({
    id: 'result',
    name: 'Result JSON',
    meta: detail.task.state,
    icon: 'code',
    view: 'json',
    value: {
      task: detail.task.id,
      state: detail.task.state,
      summary: detail.summary ?? null,
      error: detail.result_error ?? detail.task.error ?? null,
      branch: detail.task.branch ?? null,
      pr_url: detail.task.pr_url ?? null,
      artifacts: detail.artifacts,
    },
  });
  return items;
}
