import type { InboxItem, InboxKind, InboxResolved } from '../../api/inbox';

export type Filter = 'all' | InboxKind;

export const FILTERS: Array<{ id: Filter; label: string }> = [
  { id: 'all', label: 'All' },
  { id: 'question', label: 'Questions' },
  { id: 'pull_request', label: 'PRs' },
  { id: 'failure', label: 'Failures' },
];

const ORDER: Record<InboxKind, number> = { question: 0, pull_request: 1, failure: 2 };

/** Questions first (they block a task), then pull requests, then failures; newest first within each. */
export function ordered(items: InboxItem[], filter: Filter): InboxItem[] {
  return items
    .filter((item) => filter === 'all' || item.kind === filter)
    .sort((a, b) => ORDER[a.kind] - ORDER[b.kind] || b.at.localeCompare(a.at));
}

export function summary(counts: {
  questions: number;
  pull_requests: number;
  failures: number;
  total: number;
}): string {
  if (counts.total === 0) return 'All clear';
  const n = (count: number, one: string, many = `${one}s`) =>
    `${count} ${count === 1 ? one : many}`;
  return `${n(counts.questions, 'question')}, ${n(counts.pull_requests, 'PR')} to merge, ${n(counts.failures, 'failure')}`;
}

/** What the PR waits for, in words. */
export function waitingFor(reason: string | null | undefined): string {
  switch (reason) {
    case 'test_integrity':
      return 'Only test files or build files changed, so the integrator will not merge it without you.';
    case 'partial_draft':
      return 'A [partial] draft holds the work a stopped task kept. It stays a draft until you decide.';
    default:
      return 'Policy is ask: the pull request is ready and waits for you to merge it.';
  }
}

/** The run to open for an item: the batch, else the run itself. */
export function targetRun(item: InboxItem): string | null {
  return item.batch_run_id ?? item.run_id ?? null;
}

/** What left the inbox without you (ADR 0064), in words; empty when nothing did. */
export function resolvedNote(resolved: InboxResolved | undefined): string {
  if (!resolved) return '';
  const auto = resolved.superseded + resolved.infrastructure + resolved.continued;
  const parts = [
    auto > 0 ? `${auto} resolved on their own` : '',
    resolved.dismissed > 0 ? `${resolved.dismissed} dismissed` : '',
    resolved.duplicates > 0
      ? `${resolved.duplicates} duplicate${resolved.duplicates === 1 ? '' : 's'} folded`
      : '',
  ].filter(Boolean);
  return parts.join(', ');
}
