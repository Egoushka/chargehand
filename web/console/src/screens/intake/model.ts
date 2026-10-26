import type { PullRequestBody } from '../../api/common';
import type {
  IntakeBody,
  IntakeNoticeBody,
  IntakePendingBody,
  IntakeSourceRunBody,
  IntakeSourcesBody,
  IntakeTaskBody,
} from '../../api/intake';
import { isMerged } from '../../api/pulls';
import type { ProfileIntegrator } from '../../api/system';
import { DASH, DISPLAY_ZONE, shortId } from '../../lib/format';

export type TriggerResult = 'acted' | 'waiting' | 'ignored';
export type TriggerFilter = 'all' | 'acted' | 'not-acted';

export interface TriggerRow {
  id: string;
  /** When intake acted. A notice carries no time (`not_recorded`), so an ignored trigger has none. */
  at: string | null;
  kind: string;
  where: string;
  result: TriggerResult;
  reason: string;
}

/** What each reason code of `intake/v1` `notices` means, in the words intake wrote on the issue. */
const REASONS: Record<string, string> = {
  active: 'Already working on this issue; it answers there when that run ends.',
  author:
    'The repository merges by policy, so it takes work only from an issue written by an allowed author.',
  gone: 'The run to continue is no longer in the run log.',
  wait: 'Waiting for a free slot; it starts when a triggered batch ends.',
  cap: 'Refused by the daily cap of starts of a release before 0.19.4; it was not started later.',
  notask:
    'No finished task for this branch is in the run log, so the branch is unknown to chargehand.',
  busy: 'The pull request’s task is still running; the comments are taken when it ends.',
  rounds: 'The review rounds of this pull request are used up.',
};

/** Reasons that mean "not yet" rather than "no". */
const WAITING = new Set(['wait', 'busy']);

export const reasonText = (code: string): string => REASONS[code] ?? `Reason “${code}”.`;

/**
 * The trigger key as intake keeps it: `label:<repo>#<issue>:<event>`, `comment:<repo>:<comment>`, or `<repo>#<pull>` (optionally
 * followed by `:<run>`) for a pull request.
 */
export function parseTrigger(trigger: string): { kind: string; where: string } {
  const label = /^label:(.+#\d+)(?::\w+)?$/.exec(trigger);
  if (label) return { kind: 'label', where: label[1] ?? trigger };
  const comment = /^comment:([^:]+):(\w+)$/.exec(trigger);
  if (comment) return { kind: 'comment', where: `${comment[1]}, comment ${comment[2]}` };
  const pull = /^([^:]+#\d+)(?::.*)?$/.exec(trigger);
  if (pull) return { kind: 'pull request', where: pull[1] ?? trigger };
  return { kind: 'trigger', where: trigger };
}

const noticeRow = (notice: IntakeNoticeBody, index: number): TriggerRow => {
  const { kind, where } = parseTrigger(notice.trigger);
  return {
    id: `notice:${notice.reason}:${notice.trigger}:${index}`,
    at: null,
    kind,
    where,
    result: WAITING.has(notice.reason) ? 'waiting' : 'ignored',
    reason: reasonText(notice.reason),
  };
};

const taskReason = (task: IntakeTaskBody): string =>
  task.phase === 'running'
    ? `Started run ${shortId(task.batch_run_id)}, task ${task.task_id}.`
    : task.phase === 'asked' || task.awaiting_answer
      ? `Task ${task.task_id} asked a question; a reply from an allowed author continues it.`
      : task.pr_url
        ? `Task ${task.task_id} opened a draft pull request.`
        : `Task ${task.task_id} ended.`;

const taskRow = (task: IntakeTaskBody): TriggerRow => ({
  id: `task:${task.batch_run_id}:${task.task_id}`,
  at: task.started,
  kind: task.round != null ? 'review round' : 'issue',
  where: `${task.repository}#${task.issue}`,
  result: 'acted',
  reason: taskReason(task),
});

const pendingRow = (review: IntakePendingBody): TriggerRow => ({
  id: `pending:${review.id}`,
  at: review.at,
  kind: 'review comment',
  where: `${review.repository}#${review.pull}`,
  result: 'waiting',
  reason: `Comment by ${review.author}${review.path ? ` on ${review.path}${review.line != null ? `:${review.line}` : ''}` : ''} waits for a review round. The text is not returned.`,
});

/**
 * The trigger log, from what the state file holds: the tasks intake started (with their time), the review comments waiting for a
 * round (with theirs), and the notices (a reason, no time). Newest first; what has no time follows.
 */
export function triggerRows(intake: IntakeBody): TriggerRow[] {
  const timed = [...intake.tasks.map(taskRow), ...intake.pending_reviews.map(pendingRow)].sort(
    (a, b) => Date.parse(b.at ?? '') - Date.parse(a.at ?? ''),
  );
  return [...timed, ...intake.notices.map(noticeRow)];
}

export function filterRows(rows: TriggerRow[], filter: TriggerFilter): TriggerRow[] {
  return filter === 'all'
    ? rows
    : rows.filter((row) => (filter === 'acted') === (row.result === 'acted'));
}

export interface RoundRow {
  key: string;
  repository: string;
  pull: number;
  round: number;
  phase: IntakeTaskBody['phase'] | 'queued';
  pending: number;
  url: string | null;
}

/** One row per pull request that has had or is waiting for a review round: its latest round and the comments waiting. */
export function reviewRounds(intake: IntakeBody): RoundRow[] {
  const rows = new Map<string, RoundRow & { started: number }>();
  for (const task of intake.tasks) {
    if (task.round == null) continue;
    const key = `${task.repository}#${task.issue}`;
    const started = Date.parse(task.started);
    const seen = rows.get(key);
    if (seen && seen.started >= started) continue;
    rows.set(key, {
      key,
      repository: task.repository,
      pull: task.issue,
      round: task.round,
      phase: task.phase,
      pending: 0,
      url: task.pr_url ?? null,
      started,
    });
  }
  for (const review of intake.pending_reviews) {
    const key = `${review.repository}#${review.pull}`;
    const row = rows.get(key) ?? {
      key,
      repository: review.repository,
      pull: review.pull,
      round: 0,
      phase: 'queued' as const,
      pending: 0,
      url: null,
      started: Date.parse(review.at),
    };
    row.pending += 1;
    rows.set(key, row);
  }
  return [...rows.values()]
    .sort((a, b) => b.started - a.started)
    .map(({ started: _started, ...row }) => row);
}

export interface DayBar {
  key: string;
  /** The day of the month. */
  label: string;
  count: number;
}

const dayKey = (date: Date) =>
  new Intl.DateTimeFormat('en-CA', { timeZone: DISPLAY_ZONE }).format(date);

/** Starts per day for the last `days` days, counted from the tasks the state file lists (the latest 100). */
export function startsPerDay(
  tasks: IntakeTaskBody[],
  now: number = Date.now(),
  days = 7,
): DayBar[] {
  const counts = new Map<string, number>();
  for (const task of tasks) {
    const key = dayKey(new Date(task.started));
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return Array.from({ length: days }, (_, i) => {
    const date = new Date(now - (days - 1 - i) * 86_400_000);
    const key = dayKey(date);
    const count = counts.get(key) ?? 0;
    return { key, label: key.slice(8).replace(/^0/, ''), count };
  });
}

export type PolicyMode = 'merge' | 'ask' | 'off';

export interface PolicyRow {
  repo: string;
  mode: PolicyMode;
  alwaysAsk: boolean;
  watched: boolean;
  recent: string;
}

const modeOf = (value: string | undefined): PolicyMode =>
  value === 'merge' || value === 'ask' ? value : 'off';

const sameRepo = (a: string | null | undefined, b: string) => a?.toLowerCase() === b.toLowerCase();

const numbers = (prs: PullRequestBody[]) =>
  prs.map((pr) => (pr.number != null ? `#${pr.number}` : DASH)).join(', ');

/** What the integrator did with a repository’s pull requests, from `GET /v1/pulls`: merged, waiting for a person, still open. */
export function recentActions(prs: PullRequestBody[]): string {
  if (prs.length === 0) return 'No pull request of chargehand’s in the last 14 days.';
  const newest = [...prs]
    .sort(
      (a, b) =>
        Date.parse(b.state_at ?? b.opened_at ?? '') - Date.parse(a.state_at ?? a.opened_at ?? ''),
    )
    .slice(0, 12);
  const merged = newest.filter(isMerged);
  const waiting = newest.filter((pr) => pr.awaiting_person);
  const open = newest.filter((pr) => pr.state === 'open' && !pr.awaiting_person);
  const closed = newest.filter((pr) => pr.state === 'closed');
  const parts = [
    merged.length ? `merged ${numbers(merged)}` : '',
    waiting.length ? `waiting for you: ${numbers(waiting)}` : '',
    open.length ? `open: ${numbers(open)}` : '',
    closed.length ? `closed: ${numbers(closed)}` : '',
  ].filter(Boolean);
  return parts.join('; ');
}

/**
 * The integrator policy per repository: `merge`, `ask` or `off`, with `always_ask` winning over the list. Every repository the
 * profile names, in the integrator or in intake, has a row.
 */
export function policyRows(
  integrator: ProfileIntegrator | null | undefined,
  watched: string[],
  pulls: PullRequestBody[],
): PolicyRow[] {
  const names = new Map<string, string>();
  for (const name of [
    ...Object.keys(integrator?.repositories ?? {}),
    ...(integrator?.always_ask ?? []),
    ...watched,
  ])
    if (!names.has(name.toLowerCase())) names.set(name.toLowerCase(), name);
  return [...names.values()]
    .sort((a, b) => a.localeCompare(b))
    .map((repo) => {
      const always = (integrator?.always_ask ?? []).some((name) => sameRepo(name, repo));
      const listed = Object.entries(integrator?.repositories ?? {}).find(([name]) =>
        sameRepo(name, repo),
      )?.[1];
      return {
        repo,
        alwaysAsk: always,
        mode: always ? 'ask' : modeOf(listed),
        watched: watched.some((name) => sameRepo(name, repo)),
        recent: recentActions(pulls.filter((pr) => sameRepo(pr.repository, repo))),
      };
    });
}

/** A row of the Sources panel: a GitHub repository, with its switch and its queue. */
export interface SourceRow {
  /** What the switch is set by: `owner/repo`. */
  id: string;
  name: string;
  /** The checkout, and whether the profile lists the repository or the App's installation shows it. */
  detail: string;
  enabled: boolean;
  running: number;
  queued: number;
  recent: IntakeSourceRunBody[];
}

/** GitHub's repositories, in the order the server gives. */
export function sourceRows(body: IntakeSourcesBody): SourceRow[] {
  return body.sources.map(
    (s): SourceRow => ({
      id: s.repository,
      name: s.repository,
      detail: `${s.origin === 'discovered' ? 'discovered, managed clone' : 'listed'} · ${s.path}`,
      enabled: s.enabled,
      running: s.running ?? 0,
      queued: s.queued ?? 0,
      recent: s.recent ?? [],
    }),
  );
}

/** "2 running, 1 queued". */
export function queueText(row: SourceRow): string {
  return `${row.running} running, ${row.queued} queued`;
}
