import type { InboxGroup, InboxItem, InboxKind, InboxResolved } from '../../api/inbox';

export type Filter = 'all' | InboxKind;

export const FILTERS: Array<{ id: Filter; label: string }> = [
  { id: 'all', label: 'All' },
  { id: 'approval', label: 'Spend' },
  { id: 'question', label: 'Questions' },
  { id: 'pull_request', label: 'PRs' },
  { id: 'failure', label: 'Failures' },
];

const ORDER: Record<InboxKind, number> = { approval: 0, question: 1, pull_request: 2, failure: 3 };

/** A pull request that waited this long for a person is stale (run data of 27 Sep to 22 Oct: six waited 124 to 142 hours). */
export const STALE_AFTER_HOURS = 48;

/**
 * Runs held for a spend approval first (they block a whole run), then questions (they block a task), then pull requests, then failures; newest first within each. The PRs filter lists
 * the longest-waiting first: those are the merges to do before anything else.
 */
export function ordered(items: InboxItem[], filter: Filter): InboxItem[] {
  const oldestFirst = filter === 'pull_request' ? -1 : 1;
  return items
    .filter((item) => filter === 'all' || item.kind === filter)
    .sort(
      (a, b) =>
        ORDER[a.kind] - ORDER[b.kind] ||
        (a.kind === 'pull_request'
          ? oldestFirst * b.at.localeCompare(a.at)
          : b.at.localeCompare(a.at)),
    );
}

/** Seconds an open pull request has waited for a person, when that is past `STALE_AFTER_HOURS`; else null. */
export function staleFor(item: InboxItem, now: number): number | null {
  if (item.kind !== 'pull_request' || laneOf(item) !== 'decide') return null;
  const seconds = (now - new Date(item.at).getTime()) / 1000;
  return Number.isFinite(seconds) && seconds >= STALE_AFTER_HOURS * 3600 ? seconds : null;
}

/** The stale pull requests among `items`, and how long the longest waited. */
export function stalePulls(items: InboxItem[], now: number): { count: number; longest: number } {
  const waits = items.map((item) => staleFor(item, now)).filter((w): w is number => w !== null);
  return { count: waits.length, longest: Math.max(0, ...waits) };
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

type Counts = {
  questions: number;
  pull_requests: number;
  failures: number;
  total: number;
  approvals?: number;
};

/** How many cards a filter tab holds. */
export function filterCount(counts: Counts, filter: string): number {
  if (filter === 'all') return counts.total;
  if (filter === 'approval') return counts.approvals ?? 0;
  if (filter === 'question') return counts.questions;
  return filter === 'pull_request' ? counts.pull_requests : counts.failures;
}

export function summary(counts: Counts): string {
  if (counts.total === 0) return 'All clear';
  const n = (count: number, one: string, many = `${one}s`) =>
    `${count} ${count === 1 ? one : many}`;
  const held = counts.approvals ? `${n(counts.approvals, 'run')} to approve, ` : '';
  return `${held}${n(counts.questions, 'question')}, ${n(counts.pull_requests, 'PR')} to merge, ${n(counts.failures, 'failure')}`;
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
