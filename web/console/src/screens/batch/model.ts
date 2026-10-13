import type { GovernorCount } from '../../api/analytics';
import type { BatchBody, BatchTaskBody } from '../../api/batch';
import type { PullRequestBody } from '../../api/common';
import type { RunStatus } from '../../api/types';
import { DASH, formatClock, formatDuration, formatTime } from '../../lib/format';
import { type Status, taskStatus } from '../../lib/status';
import { batchProgress } from '../overview/model';

const CLOSED: ReadonlySet<string> = new Set(['completed']);

/** The design's word for a task: a pending task whose dependency has not completed is waiting, and the error code or the pull request refine the state. */
export function taskWord(task: BatchTaskBody, all: readonly BatchTaskBody[]): Status {
  if (task.state === 'failed' || task.state === 'cancelled') {
    if (task.error?.code === 'rate_limited') return 'rate_limited';
    if (task.error?.code === 'cancelled') return 'cancelled';
  }
  if (task.state === 'completed') {
    if (task.pull_request?.partial) return 'partial';
    if (task.pull_request?.state.startsWith('merged')) return 'merged';
  }
  if (task.state === 'pending') {
    const unfinished = task.depends_on.some((id) => {
      const dep = all.find((t) => t.id === id);
      return !dep || !CLOSED.has(dep.state);
    });
    return unfinished ? 'waiting' : 'queued';
  }
  return taskStatus(task.state);
}

/** What the batch has spent, reading a running task from its live counters and a finished one from its record. */
export const taskTokens = (task: BatchTaskBody): number | null => {
  if (task.spend) return task.spend.input_tokens + task.spend.output_tokens;
  return task.live?.tokens ?? null;
};

/** The dollar equivalent of a task so far (the governor's figure); the priced cost when only that is known. */
export const taskUsd = (task: BatchTaskBody): number | null =>
  task.spend?.usd_estimate ?? task.live?.usd_estimate ?? task.spend?.usd ?? null;

/** Spend as a share of the task's budget, in percent; null without both. */
export function taskShare(task: BatchTaskBody): number | null {
  const used = taskUsd(task);
  const budget = task.budget?.usd;
  return used != null && budget != null && budget > 0 ? (used / budget) * 100 : null;
}

/** The bar of a graph node: spend of budget while there is one, else done or not. */
export function nodeProgress(task: BatchTaskBody): number {
  const share = taskShare(task);
  if (share != null) return Math.min(100, Math.round(share));
  return task.state === 'completed' ? 100 : 0;
}

/** The left of a node's footer: where the task is, or why it is not. */
export function taskMeta(task: BatchTaskBody, all: readonly BatchTaskBody[]): string {
  switch (taskWord(task, all)) {
    case 'running': {
      const live = task.live;
      return live
        ? [
            live.stage ? `stage ${live.stage}` : null,
            live.turns != null ? `${live.turns} turns` : null,
          ]
            .filter(Boolean)
            .join(' · ') || 'starting'
        : 'starting';
    }
    case 'done':
    case 'merged': {
      const n = task.sessions.length;
      return `${task.spend?.turns ?? DASH} turns · ${n} ${n === 1 ? 'session' : 'sessions'}`;
    }
    case 'partial':
      return 'work kept in a draft';
    case 'question':
      return `asked ${task.questions.length} ${task.questions.length === 1 ? 'question' : 'questions'}`;
    case 'rate_limited':
      return 'usage limit';
    case 'failed':
      return task.error?.code ?? 'failed';
    case 'waiting': {
      const open = task.depends_on.filter(
        (id) => all.find((t) => t.id === id)?.state !== 'completed',
      );
      return open.length > 0 ? `waits for ${open.join(', ')}` : 'waiting';
    }
    case 'not_started':
      return 'a dependency did not finish';
    default:
      return 'queued';
  }
}

/** Seconds a task has been going: from its start to its end, or to now while it runs. */
export function taskElapsed(task: BatchTaskBody, now: number): number | null {
  if (!task.started_at) return null;
  const end = task.finished_at
    ? Date.parse(task.finished_at)
    : task.state === 'running'
      ? now
      : null;
  return end == null ? null : Math.max(0, (end - Date.parse(task.started_at)) / 1000);
}

export type EdgeState = 'done' | 'active' | 'waiting' | 'blocked';

/** The state of the edge from a dependency to the task that waits for it. */
export function edgeState(dep: BatchTaskBody | undefined, task: BatchTaskBody): EdgeState {
  if (!dep) return 'waiting';
  if (dep.state === 'completed') return task.state === 'running' ? 'active' : 'done';
  return dep.state === 'failed' ||
    dep.state === 'needs_input' ||
    dep.state === 'cancelled' ||
    dep.state === 'not_started'
    ? 'blocked'
    : 'waiting';
}

export interface GraphEdge {
  id: string;
  source: string;
  target: string;
  state: EdgeState;
}

/** The `depends_on` edges whose both ends are in the batch. */
export function graphEdges(tasks: readonly BatchTaskBody[]): GraphEdge[] {
  return tasks.flatMap((task) =>
    task.depends_on.flatMap((id) => {
      const dep = tasks.find((t) => t.id === id);
      return dep
        ? [{ id: `${id}->${task.id}`, source: id, target: task.id, state: edgeState(dep, task) }]
        : [];
    }),
  );
}

/** The graph's shape: the same string means the same layout, so a poll that only changes states does not lay the graph out again. */
export const graphShape = (tasks: readonly BatchTaskBody[]): string =>
  tasks.map((t) => `${t.id}<${[...t.depends_on].sort().join(',')}`).join('|');

export const NODE_WIDTH = 232;
export const NODE_HEIGHT = 104;
const GAP_X = 64;
const GAP_Y = 16;

export type Positions = Record<string, { x: number; y: number }>;

/**
 * The layout the graph falls back to when ELK is not available (no Web Worker): columns by the longest chain of dependencies, rows in
 * the order of the batch. Tasks with no link to another stack in a grid under the chains, as the design draws them.
 */
export function layerLayout(tasks: readonly BatchTaskBody[], columns = 3): Positions {
  const ids = new Set(tasks.map((t) => t.id));
  const layer = new Map<string, number>();
  const depth = (task: BatchTaskBody, seen: ReadonlySet<string> = new Set()): number => {
    const known = layer.get(task.id);
    if (known != null) return known;
    if (seen.has(task.id)) return 0;
    const next = new Set(seen).add(task.id);
    let l = 0;
    for (const id of task.depends_on) {
      const dep = tasks.find((t) => t.id === id);
      if (dep && ids.has(id)) l = Math.max(l, depth(dep, next) + 1);
    }
    layer.set(task.id, l);
    return l;
  };
  for (const task of tasks) depth(task);
  const linked = new Set(
    tasks
      .filter((t) => t.depends_on.some((id) => ids.has(id)))
      .flatMap((t) => [t.id, ...t.depends_on]),
  );
  const positions: Positions = {};
  const rows = new Map<number, number>();
  let bottom = 0;
  for (const task of tasks.filter((t) => linked.has(t.id))) {
    const l = layer.get(task.id) ?? 0;
    const row = rows.get(l) ?? 0;
    rows.set(l, row + 1);
    positions[task.id] = { x: l * (NODE_WIDTH + GAP_X), y: row * (NODE_HEIGHT + GAP_Y) };
    bottom = Math.max(bottom, (row + 1) * (NODE_HEIGHT + GAP_Y));
  }
  const alone = tasks.filter((t) => !linked.has(t.id));
  alone.forEach((task, i) => {
    positions[task.id] = {
      x: (i % columns) * (NODE_WIDTH + GAP_X),
      y: bottom + (bottom > 0 ? GAP_Y * 2 : 0) + Math.floor(i / columns) * (NODE_HEIGHT + GAP_Y),
    };
  });
  return positions;
}

/** The size of the area the positions fill. */
export function extent(positions: Positions): { width: number; height: number } {
  const all = Object.values(positions);
  return {
    width: Math.max(0, ...all.map((p) => p.x + NODE_WIDTH)),
    height: Math.max(0, ...all.map((p) => p.y + NODE_HEIGHT)),
  };
}

// ---------- the header ----------

export interface Cap {
  used: number | null;
  cap: number | null;
}

/** Tokens and dollars the batch has used, against its caps. A running task counts from its live counters. */
export function batchTotals(batch: BatchBody): { tokens: Cap; usd: Cap & { estimated: boolean } } {
  const tasks = batch.tasks;
  let tokens = 0;
  let any = false;
  let usd = 0;
  let anyUsd = false;
  for (const task of tasks) {
    const t = taskTokens(task);
    if (t != null) {
      tokens += t;
      any = true;
    }
    const u = taskUsd(task);
    if (u != null) {
      usd += u;
      anyUsd = true;
    }
  }
  const result = batch.result;
  if (result) {
    tokens = Math.max(tokens, result.input_tokens + result.output_tokens);
    any = true;
    if (result.usd != null && !anyUsd) {
      usd = result.usd;
      anyUsd = true;
    }
  }
  return {
    tokens: {
      used: any ? tokens : (batch.run.tokens ?? null),
      cap: batch.limits.max_tokens_total ?? null,
    },
    usd: {
      used: anyUsd ? usd : null,
      cap: batch.limits.max_usd_total ?? null,
      estimated: tasks.some((t) => (t.spend?.usd_estimate ?? t.live?.usd_estimate) != null),
    },
  };
}

export const progressOf = (batch: BatchBody) => batchProgress(batch.tasks);

/** How long a batch has run: to its end, or to now. */
export function batchElapsed(batch: BatchBody, now: number): number {
  const end = batch.run.finished_at ? Date.parse(batch.run.finished_at) : now;
  return Math.max(0, (end - Date.parse(batch.run.started_at)) / 1000);
}

export const isRunning = (batch: BatchBody) =>
  batch.run.status === 'running' || batch.run.status === 'queued';

// ---------- the timeline ----------

export interface Bar {
  id: string;
  task: string;
  session: number;
  start: number;
  end: number;
  status: Status;
  continuation: boolean;
  title: string;
}

export interface Lane {
  label: string;
  bars: Bar[];
}

export interface Tick {
  at: number;
  left: number;
  label: string;
}

export interface TimelineModel {
  lanes: Lane[];
  ticks: Tick[];
  t0: number;
  t1: number;
  pauses: Array<{ left: number; width: number; label: string }>;
  /** Percent from the left, or null for a batch that has ended. */
  nowLeft: number | null;
  /** The sessions come from the batch's events. Without them a task is one bar, whatever its continuations. */
  fromEvents: boolean;
  /** Slots busy at `now`, against the batch's `max_parallel`. */
  busy: number;
  slots: number;
}

const STEPS_MIN = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440];

function slotLabel(index: number): string {
  return `Slot ${String.fromCharCode(65 + (index % 26))}${index >= 26 ? Math.floor(index / 26) : ''}`;
}

/** A task's sessions: one per `container_started` event it has; the batch view alone gives one, from the task's start to its end. */
function sessionsOf(
  task: BatchTaskBody,
  events: readonly RunStatus[],
  now: number,
  all: readonly BatchTaskBody[],
): Bar[] {
  const mine = events
    .filter((e) => e.task_id === task.id && e.at)
    .map((e) => ({ ...e, ms: Date.parse(e.at as string) }))
    .sort((a, b) => a.ms - b.ms);
  const starts = mine.filter((e) => e.event === 'container_started').map((e) => e.ms);
  if (starts.length === 0 && task.started_at) starts.push(Date.parse(task.started_at));
  const word = taskWord(task, all);
  const end = task.finished_at
    ? Date.parse(task.finished_at)
    : task.state === 'running'
      ? now
      : (mine.at(-1)?.ms ?? starts[0] ?? now);
  return starts.map((start, i) => {
    const next = starts[i + 1];
    const last = i === starts.length - 1;
    // A session that was followed by another ended with the last thing the task did before the next one began.
    const stop = last
      ? Math.max(end, start)
      : Math.max(start, mine.filter((e) => e.ms < (next as number)).at(-1)?.ms ?? (next as number));
    const status: Status = last
      ? word === 'queued' || word === 'waiting'
        ? 'running'
        : word
      : 'partial';
    return {
      id: `${task.id}#${i + 1}`,
      task: task.id,
      session: i + 1,
      start,
      end: stop,
      status,
      continuation: i > 0,
      title: `${task.id}, session ${i + 1}: ${formatClock(start)} to ${last && task.state === 'running' ? 'now' : formatClock(stop)}${last ? '' : ', stopped, continued'}`,
    };
  });
}

/** Bars in parallel slots: each starts in the first slot that is free then. */
export function packLanes(bars: Bar[], min: number): Lane[] {
  const lanes: Lane[] = [];
  const freeAt: number[] = [];
  for (const bar of [...bars].sort((a, b) => a.start - b.start)) {
    let slot = freeAt.findIndex((t) => t <= bar.start);
    if (slot < 0) {
      slot = freeAt.length;
      lanes.push({ label: slotLabel(slot), bars: [] });
    }
    freeAt[slot] = bar.end;
    lanes[slot]?.bars.push(bar);
  }
  while (lanes.length < min) lanes.push({ label: slotLabel(lanes.length), bars: [] });
  return lanes;
}

function ticksFor(t0: number, t1: number): Tick[] {
  const span = t1 - t0;
  const stepMin = STEPS_MIN.find((m) => span / (m * 60_000) <= 10) ?? 1440;
  const step = stepMin * 60_000;
  const out: Tick[] = [];
  for (let at = Math.ceil(t0 / step) * step; at <= t1; at += step)
    out.push({ at, left: ((at - t0) / span) * 100, label: formatClock(at) });
  return out;
}

/** The gantt: the sessions of every task in parallel slots, rate-limit pauses as bands, and now. Null when no task has started. */
export function timelineModel(batch: BatchBody, now: number): TimelineModel | null {
  const events = batch.timeline.available ? batch.timeline.events : [];
  const bars = batch.tasks.flatMap((task) => sessionsOf(task, events, now, batch.tasks));
  if (bars.length === 0) return null;
  const running = isRunning(batch);
  const startedAt = Date.parse(batch.run.started_at);
  const pauses = batch.rate_limits.map((p) => ({
    from: Date.parse(p.observed_at),
    to: Date.parse(p.resets_at),
    task: p.task_id,
  }));
  const first = Math.min(startedAt, ...bars.map((b) => b.start));
  const last = Math.max(
    running
      ? now
      : batch.run.finished_at
        ? Date.parse(batch.run.finished_at)
        : Math.max(...bars.map((b) => b.end)),
    ...bars.map((b) => b.end),
    ...pauses.map((p) => p.to),
  );
  const pad = Math.max(60_000, (last - first) * 0.04);
  const t0 = first - pad / 4;
  const t1 = last + pad;
  const span = t1 - t0;
  const pct = (at: number) => ((at - t0) / span) * 100;
  const slots = Math.max(batch.limits.max_parallel ?? 0, 1);
  const lanes = packLanes(bars, Math.min(slots, 16)).map((lane) => ({
    ...lane,
    bars: lane.bars.map((bar) => ({ ...bar })),
  }));
  return {
    lanes,
    ticks: ticksFor(t0, t1),
    t0,
    t1,
    pauses: pauses.map((p) => ({
      left: pct(p.from),
      width: Math.max(0.5, ((p.to - p.from) / span) * 100),
      label: `new starts held until ${formatClock(p.to)}`,
    })),
    nowLeft: running ? pct(now) : null,
    fromEvents: events.length > 0,
    busy: batch.tasks.filter((t) => t.state === 'running').length,
    slots,
  };
}

/** The bar's position and width in percent of the track. */
export const barBox = (bar: Bar, model: Pick<TimelineModel, 't0' | 't1'>) => {
  const span = model.t1 - model.t0;
  return {
    left: ((bar.start - model.t0) / span) * 100,
    width: Math.max(1.2, ((bar.end - bar.start) / span) * 100),
  };
};

// ---------- events ----------

export type EventGroup = 'all' | 'progress' | 'governor' | 'needs' | 'prs';

export const EVENT_GROUPS: ReadonlyArray<{
  id: EventGroup;
  label: string;
  kinds: readonly string[] | null;
}> = [
  { id: 'all', label: 'All', kinds: null },
  {
    id: 'progress',
    label: 'Progress',
    kinds: [
      'session_progress',
      'container_started',
      'verify_finished',
      'plan',
      'started',
      'accepted',
      'intake',
    ],
  },
  { id: 'governor', label: 'Governor', kinds: ['governor'] },
  {
    id: 'needs',
    label: 'Questions and ends',
    kinds: ['question', 'task_finished', 'run_finished'],
  },
  { id: 'prs', label: 'PRs and merges', kinds: ['pushed', 'pr_opened', 'integrated'] },
];

export const eventKind = (e: RunStatus): string => e.event ?? e.status;

/** An event's text: what the server said, or the numbers it carried. */
export function eventDetail(e: RunStatus): string {
  if (e.detail) return e.detail;
  const parts: string[] = [];
  if (e.stage) parts.push(`stage ${e.stage}`);
  if (e.turns != null) parts.push(`${e.turns} turns`);
  if (e.tokens != null) parts.push(`${e.tokens.toLocaleString('en-US')} tokens`);
  if (e.usd_estimate != null) parts.push(`≈ $${e.usd_estimate.toFixed(2)}`);
  if (e.branch) parts.push(`branch ${e.branch}`);
  if (e.pr_url) parts.push(e.pr_url);
  return parts.join(' · ');
}

export function eventTone(e: RunStatus): 'warn' | 'danger' | undefined {
  const kind = eventKind(e);
  if (e.status === 'failed' || e.status === 'lost' || e.status === 'denied') return 'danger';
  if (kind === 'question') return 'warn';
  if (kind === 'governor' && /checkpoint|stop/.test(e.detail ?? '')) return 'warn';
  return undefined;
}

export const eventKey = (e: RunStatus) =>
  `${e.at}|${eventKind(e)}|${e.task_id ?? ''}|${e.detail ?? ''}|${e.turns ?? ''}|${e.tokens ?? ''}`;

/** The batch's recorded events and the live stream as one list, oldest first, an event that is in both once. */
export function mergeEvents(
  recorded: readonly RunStatus[],
  streamed: readonly RunStatus[],
): RunStatus[] {
  const seen = new Map<string, RunStatus>();
  for (const e of [...recorded, ...streamed]) if (!seen.has(eventKey(e))) seen.set(eventKey(e), e);
  return [...seen.values()].sort((a, b) => Date.parse(a.at ?? '') - Date.parse(b.at ?? ''));
}

export function filterEvents(
  events: readonly RunStatus[],
  group: EventGroup,
  task: string,
): RunStatus[] {
  const kinds = EVENT_GROUPS.find((g) => g.id === group)?.kinds ?? null;
  return events.filter(
    (e) => (!kinds || kinds.includes(eventKind(e))) && (task === 'all' || e.task_id === task),
  );
}

// ---------- governor ----------

export interface GovernorSummary {
  decisions: number;
  warn: number;
  checkpoint: number;
  stop: number;
  acted: number;
  modes: string[];
  checkpointedTasks: number;
  checkpointedCompleted: number;
  sources: Array<[source: string, count: number]>;
}

export function governorSummary(tasks: readonly BatchTaskBody[]): GovernorSummary {
  const all = tasks.flatMap((t) => t.governor?.decisions ?? []);
  const count = (action: GovernorCount['action']) =>
    all.filter((d) => d.action === action).reduce((n, d) => n + d.count, 0);
  const checkpointed = tasks.filter((t) =>
    (t.governor?.decisions ?? []).some((d) => d.action === 'checkpoint'),
  );
  const sources = new Map<string, number>();
  for (const t of tasks)
    if (t.budget) sources.set(t.budget.source.kind, (sources.get(t.budget.source.kind) ?? 0) + 1);
  return {
    decisions: all.reduce((n, d) => n + d.count, 0),
    warn: count('warn'),
    checkpoint: count('checkpoint'),
    stop: count('stop'),
    acted: all.reduce((n, d) => n + d.acted, 0),
    modes: [...new Set(tasks.map((t) => t.governor?.mode).filter((m): m is string => !!m))],
    checkpointedTasks: checkpointed.length,
    checkpointedCompleted: checkpointed.filter((t) => t.state === 'completed').length,
    sources: [...sources.entries()],
  };
}

export interface GovernorRow {
  task: string;
  mode: string;
  signal: string;
  action: GovernorCount['action'];
  count: number;
  acted: number;
  stopReason: string | null;
}

export function governorRows(tasks: readonly BatchTaskBody[]): GovernorRow[] {
  return tasks.flatMap((t) =>
    (t.governor?.decisions ?? []).map((d) => ({
      task: t.id,
      mode: t.governor?.mode ?? DASH,
      signal: d.signal,
      action: d.action,
      count: d.count,
      acted: d.acted,
      stopReason: t.governor?.stop_reason ?? null,
    })),
  );
}

// ---------- spend ----------

export interface SpendRow {
  id: string;
  input: number | null;
  output: number | null;
  cacheRead: number | null;
  cacheWrite: number | null;
  usd: number | null;
  /** The figure is the dollar equivalent of the tokens, not a priced cost. */
  estimate: boolean;
  partial: boolean;
}

export function spendRows(tasks: readonly BatchTaskBody[]): SpendRow[] {
  return tasks
    .filter((t) => t.spend || t.live)
    .map((t) => {
      const s = t.spend;
      return {
        id: t.id,
        input: s?.input_tokens ?? null,
        output: s?.output_tokens ?? null,
        cacheRead: s?.cache_read_tokens ?? t.live?.cache_read ?? null,
        cacheWrite: s?.cache_write_tokens ?? t.live?.cache_write ?? null,
        usd: taskUsd(t),
        estimate: s?.usd_estimate != null || (s == null && t.live?.usd_estimate != null),
        partial: s?.usd_estimate_partial ?? t.live?.usd_estimate_partial ?? false,
      };
    });
}

export function spendTotal(rows: readonly SpendRow[]): SpendRow {
  const sum = (pick: (r: SpendRow) => number | null) => {
    const values = rows.map(pick).filter((v): v is number => v != null);
    return values.length > 0 ? values.reduce((a, b) => a + b, 0) : null;
  };
  return {
    id: 'Total',
    input: sum((r) => r.input),
    output: sum((r) => r.output),
    cacheRead: sum((r) => r.cacheRead),
    cacheWrite: sum((r) => r.cacheWrite),
    usd: sum((r) => r.usd),
    estimate: rows.some((r) => r.estimate),
    partial: rows.some((r) => r.partial),
  };
}

/** Cache reads over everything read as input (cache reads and fresh input): the share the cache served. */
export function cacheShare(rows: readonly SpendRow[]): number | null {
  const total = spendTotal(rows);
  const read = total.cacheRead ?? 0;
  const input = (total.input ?? 0) + read;
  return input > 0 ? Math.round((read / input) * 100) : null;
}

/** The four costliest tasks and the rest, as the share-of-spend bar draws them. */
export function spendShares(
  rows: readonly SpendRow[],
  top = 4,
): Array<{ id: string; usd: number }> {
  const sorted = rows.filter((r) => (r.usd ?? 0) > 0).sort((a, b) => (b.usd ?? 0) - (a.usd ?? 0));
  const head = sorted.slice(0, top).map((r) => ({ id: r.id, usd: r.usd as number }));
  const rest = sorted.slice(top).reduce((n, r) => n + (r.usd ?? 0), 0);
  return rest > 0 ? [...head, { id: 'other tasks', usd: rest }] : head;
}

// ---------- pull requests ----------

/** The batch's pull requests: its own list, with the outcome `GET /v1/pulls` read back from the forge laid over it. Newest first. */
export function batchPulls(batch: BatchBody, pulls: readonly PullRequestBody[]): PullRequestBody[] {
  const byUrl = new Map<string, PullRequestBody>();
  for (const pr of batch.pull_requests) byUrl.set(pr.url, pr);
  for (const pr of pulls) {
    if (pr.batch_run_id !== batch.run.run_id) continue;
    const own = byUrl.get(pr.url);
    byUrl.set(
      pr.url,
      own ? { ...own, ...pr, integrator_last: pr.integrator_last ?? own.integrator_last } : pr,
    );
  }
  return [...byUrl.values()].sort((a, b) => (b.number ?? 0) - (a.number ?? 0));
}

export function prState(pr: PullRequestBody): { status: Status; label: string } {
  if (pr.state === 'merged_edited') return { status: 'merged', label: 'Merged, edited' };
  if (pr.state === 'merged' || pr.state === 'merged_unchanged')
    return { status: 'merged', label: 'Merged' };
  if (pr.state === 'closed') return { status: 'cancelled', label: 'Closed' };
  if (pr.partial) return { status: 'partial', label: 'Partial draft' };
  if (pr.state === 'open')
    return pr.awaiting_person
      ? { status: 'asked', label: 'Waits for you' }
      : { status: 'done', label: 'Open' };
  return { status: 'queued', label: 'Outcome unknown' };
}

export const AWAITING: Record<string, string> = {
  policy_ask: 'policy is ask',
  test_integrity: 'only tests or build files changed',
  partial_draft: 'a [partial] draft',
};

// ---------- the request ----------

export type RebuiltRequest = {
  contract_version: 'request/v1';
  text: string;
  context: { interactive: false; preset: string; repository?: { path: string; commit: string } };
  driven: {
    plan?: boolean;
    max_parallel?: number;
    max_tokens_total?: number;
    max_usd_total?: number;
    tasks: Array<Record<string, unknown>>;
  };
};

/**
 * The request as far as the batch view lets it be rebuilt: the server keeps the request in its run log and does not serve it, so this is the
 * batch's tasks, limits and repository. Text, caller blocks and inputs are not in it. `goals` holds whole goals for tasks the batch view cut.
 */
export function rebuildRequest(
  batch: BatchBody,
  goals: Readonly<Record<string, string>> = {},
): RebuiltRequest {
  return {
    contract_version: 'request/v1',
    text: `Clone of ${batch.run.run_id}`,
    context: {
      interactive: false,
      preset: batch.run.preset,
      ...(batch.repository
        ? { repository: { path: batch.repository.path, commit: batch.repository.commit } }
        : {}),
    },
    driven: {
      ...(batch.limits.plan != null ? { plan: batch.limits.plan } : {}),
      ...(batch.limits.max_parallel != null ? { max_parallel: batch.limits.max_parallel } : {}),
      ...(batch.limits.max_tokens_total != null
        ? { max_tokens_total: batch.limits.max_tokens_total }
        : {}),
      ...(batch.limits.max_usd_total != null ? { max_usd_total: batch.limits.max_usd_total } : {}),
      tasks: batch.tasks.map((t) => ({
        id: t.id,
        ...(t.ref ? { ref: t.ref } : {}),
        ...((goals[t.id] ?? t.goal) ? { goal: goals[t.id] ?? t.goal } : {}),
        ...(t.size && (t.size_from == null || t.size_from === 'caller') ? { size: t.size } : {}),
        ...(t.budget?.source.kind === 'task' && t.budget.usd != null
          ? { budget_usd: t.budget.usd }
          : {}),
        ...(t.depends_on.length > 0 ? { depends_on: t.depends_on } : {}),
        ...(t.continues
          ? { continue_from: { run_id: t.continues.run_id, task_id: t.continues.task_id } }
          : {}),
      })),
    },
  };
}

/** The ids of tasks whose goal the batch view cut at 500 characters. */
export const cutGoals = (batch: BatchBody): string[] =>
  batch.tasks.filter((t) => t.goal_truncated).map((t) => t.id);

// ---------- small facts ----------

export const when = (iso: string | null | undefined) => formatTime(iso);

export const durationText = (seconds: number | null | undefined) => formatDuration(seconds);

/** The sentences of `not_recorded` that mention one of the words. */
export const notesAbout = (notes: readonly string[], ...words: string[]): string[] =>
  notes.filter((note) => words.some((word) => note.toLowerCase().includes(word.toLowerCase())));
