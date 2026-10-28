import type { InboxGroup, InboxItem, InboxKind, InboxResolved } from '../../api/inbox';

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

/** A card of the list: one item, or a group of failures with the same repository and code and its items here, newest first. */
export type Row =
  | { type: 'item'; id: string; item: InboxItem }
  | { type: 'group'; id: string; group: InboxGroup; items: InboxItem[] };

/** The ordered items as cards: the items of a group fold into one card where its newest item stands. */
export function rows(items: InboxItem[], groups: InboxGroup[] = []): Row[] {
  const groupOf = new Map<string, InboxGroup>();
  for (const group of groups) for (const id of group.items) groupOf.set(id, group);
  const members = new Map<string, InboxItem[]>();
  for (const item of items) {
    const group = groupOf.get(item.id);
    if (group) members.set(group.id, [...(members.get(group.id) ?? []), item]);
  }
  const out: Row[] = [];
  for (const item of items) {
    const group = groupOf.get(item.id);
    const shown = group ? (members.get(group.id) ?? []) : [];
    if (!group || shown.length < 2) out.push({ type: 'item', id: item.id, item });
    else if (shown[0] === item) out.push({ type: 'group', id: group.id, group, items: shown });
  }
  return out;
}

/** The ids "Dismiss all shown" sends: a group's id stands for all of its failures. */
export function rowIds(list: Row[]): string[] {
  return list.map((row) => row.id);
}

/** How many items the cards stand for. */
export function rowItems(list: Row[]): number {
  return list.reduce((n, row) => n + (row.type === 'group' ? row.group.items.length : 1), 0);
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
  const auto =
    resolved.superseded + resolved.infrastructure + resolved.continued + (resolved.pr_closed ?? 0);
  const parts = [
    auto > 0 ? `${auto} resolved on their own` : '',
    resolved.dismissed > 0 ? `${resolved.dismissed} dismissed` : '',
    resolved.duplicates > 0
      ? `${resolved.duplicates} duplicate${resolved.duplicates === 1 ? '' : 's'} folded`
      : '',
  ].filter(Boolean);
  return parts.join(', ');
}

/** The lane an open item is in (ADR 0085): the server's, else a failure is Fix and the rest Decide (a server before it). */
export function laneOf(item: InboxItem): 'decide' | 'fix' {
  return item.lane === 'fix' || (!item.lane && item.kind === 'failure') ? 'fix' : 'decide';
}

/** Why a handled item left the inbox, in words. */
export function handledBy(reason: string | null | undefined): string {
  switch (reason) {
    case 'superseded':
      return 'A later run did the same work';
    case 'infrastructure':
      return 'A later run on the repository worked';
    case 'continued':
      return 'A batch continued it';
    case 'pr_closed':
      return 'Its pull request was merged or closed';
    case 'dismissed':
      return 'Dismissed';
    default:
      return reason ? `Resolved: ${reason.replace(/_/g, ' ')}` : 'Resolved';
  }
}
