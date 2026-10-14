// What the Analytics screen computes from analytics/v1, /v1/learn and the run list: buckets, stacks, rates, bins. Pure, so it is
// tested without a canvas or a DOM. Nothing here invents a number: each figure is a sum, a share or a bin of what the server sent,
// and every one carries the route and field it came from.

import type {
  AnalyticsBody,
  AnalyticsBucket,
  AnalyticsGroupBy,
  GovernorCount,
  GovernorModeStats,
  PrCounts,
  SeriesPoint,
} from '../../api/analytics';
import type { LearnGroup, RunSummary } from '../../api/types';
import type { Slot } from '../../charts/colors';
import { SERIES_SLOTS, type SlotMemory } from '../../charts/colors';
import { type Bin, histogram } from '../../charts/scales';
import { formatInterval, type Proportion, percentile, wilson } from '../../charts/stats';
import type { TimeSeries } from '../../charts/TimeChart';
import type { TipData } from '../../charts/Tip';
import type { BudgetInForce, SizeClass } from '../../lib/budgets';
import { SIZES } from '../../lib/budgets';
import {
  baseName,
  DASH,
  formatCount,
  formatDuration,
  formatPercent,
  formatTokens,
  formatUsd,
} from '../../lib/format';

// ---------- the controls ----------

export type RangeId = '7d' | '30d' | '90d' | 'all';

export interface Range {
  id: RangeId;
  label: string;
  /** The `since` the server takes: a number of days. */
  since: string;
  /** Null: from the first record. */
  days: number | null;
  /** The bucket that fits the range when the person has not picked one. */
  bucket: AnalyticsBucket;
  phrase: string;
}

export const RANGES: readonly Range[] = [
  { id: '7d', label: '7 d', since: '7d', days: 7, bucket: 'day', phrase: 'the last 7 days' },
  { id: '30d', label: '30 d', since: '30d', days: 30, bucket: 'day', phrase: 'the last 30 days' },
  { id: '90d', label: '90 d', since: '90d', days: 90, bucket: 'week', phrase: 'the last 90 days' },
  // The server takes days or a time, not "all": a hundred years reaches the first record.
  { id: 'all', label: 'All', since: '36500d', days: null, bucket: 'month', phrase: 'all time' },
];

export const rangeOf = (id: RangeId): Range =>
  RANGES.find((r) => r.id === id) ?? (RANGES[1] as Range);

export const BUCKETS: ReadonlyArray<{ id: AnalyticsBucket; label: string }> = [
  { id: 'day', label: 'Day' },
  { id: 'week', label: 'Week' },
  { id: 'month', label: 'Month' },
];

export const GROUPINGS: ReadonlyArray<{
  id: AnalyticsGroupBy;
  label: string;
  ownerOnly?: boolean;
}> = [
  { id: 'none', label: 'None' },
  { id: 'repository', label: 'Repository' },
  { id: 'size', label: 'Size' },
  { id: 'account', label: 'Account', ownerOnly: true },
];

// ---------- time (buckets are UTC, as the server cuts them) ----------

const DAY = 86_400_000;

/** The first instant (UTC) of the day, week (Monday) or month that holds `ms`. */
export function bucketStart(ms: number, bucket: AnalyticsBucket): number {
  const d = new Date(ms);
  const day = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
  if (bucket === 'day') return day;
  if (bucket === 'week') return day - ((d.getUTCDay() + 6) % 7) * DAY;
  return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1);
}

function nextBucket(ms: number, bucket: AnalyticsBucket): number {
  if (bucket === 'day') return ms + DAY;
  if (bucket === 'week') return ms + 7 * DAY;
  const d = new Date(ms);
  return Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 1);
}

/**
 * Every bucket from the start of the range to the one holding "now", so a day with no task is a gap of zero and not a missing bar.
 * With no days (all time) it starts at the first record. Empty when there is no record at all.
 */
export function bucketAxis(
  points: ReadonlyArray<Pick<SeriesPoint, 'bucket_start'>>,
  bucket: AnalyticsBucket,
  nowIso: string,
  days: number | null,
): number[] {
  const starts = points.map((p) => Date.parse(p.bucket_start)).filter((t) => !Number.isNaN(t));
  if (starts.length === 0) return [];
  const now = Date.parse(nowIso);
  const first = Math.min(...starts);
  const from = days != null ? Math.min(bucketStart(now - days * DAY, bucket), first) : first;
  const to = Math.max(bucketStart(now, bucket), ...starts);
  const axis: number[] = [];
  for (let t = from; t <= to; t = nextBucket(t, bucket)) axis.push(t);
  return axis;
}

const utc = (ms: number, options: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', ...options }).format(ms);

/** `12 Oct` for a day or a week (the Monday it starts on), `Oct 2026` for a month. */
export function bucketLabel(ms: number, bucket: AnalyticsBucket): string {
  return bucket === 'month'
    ? utc(ms, { month: 'short', year: 'numeric' })
    : utc(ms, { day: 'numeric', month: 'short' });
}

/** The long form for a hover: `Mon 12 Oct 2026`, `Week of Mon 12 Oct 2026`, `October 2026`. */
export function bucketTitle(ms: number, bucket: AnalyticsBucket): string {
  if (bucket === 'month') return utc(ms, { month: 'long', year: 'numeric' });
  const day = utc(ms, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
  return bucket === 'week' ? `Week of ${day}` : day;
}

const isoDay = (ms: number) => new Date(ms).toISOString().slice(0, 10);

// ---------- series ----------

export const MAX_SERIES = SERIES_SLOTS;

export interface StackedSeries {
  id: string;
  label: string;
  /** The server's group (a repository, an account, a size); null when the report is not grouped. */
  group: string | null;
  slot: Slot;
  /** One value per bucket of the axis. */
  values: number[];
  total: number;
}

/** A group's name for a legend: a repository path by its last segment, a size as `Size S`, the ungrouped total as "All tasks". */
export function groupLabel(group: string | null | undefined, groupBy: AnalyticsGroupBy): string {
  if (group == null || group === '') return groupBy === 'none' ? 'All tasks' : 'Unnamed';
  if (groupBy === 'size') return `Size ${group}`;
  if (groupBy === 'repository' && group.startsWith('/')) return baseName(group);
  return group;
}

/**
 * The points of a metric as stacked series, one per group, aligned to the axis. A group that adds nothing to the metric is left
 * out. More than eight groups: the seven largest keep a hue and the rest fold into one grey "Other" (a ninth hue would look like
 * one of the eight). Hues come from `memory`, so a group keeps its colour when a filter drops and returns it.
 */
export function stackSeries(
  points: readonly SeriesPoint[],
  axis: readonly number[],
  groupBy: AnalyticsGroupBy,
  value: (p: SeriesPoint) => number,
  memory: SlotMemory,
): StackedSeries[] {
  const index = new Map(axis.map((t, i) => [t, i]));
  const byGroup = new Map<string, number[]>();
  for (const p of points) {
    const i = index.get(Date.parse(p.bucket_start));
    if (i === undefined) continue;
    const key = p.group ?? '';
    const values = byGroup.get(key) ?? axis.map(() => 0);
    values[i] = (values[i] ?? 0) + value(p);
    byGroup.set(key, values);
  }
  const totalOf = (values: number[]) => values.reduce((a, b) => a + b, 0);
  const ranked = [...byGroup.entries()]
    .map(([group, values]) => ({ group, values, total: totalOf(values) }))
    .filter((g) => g.total > 0)
    .sort((a, b) => b.total - a.total || a.group.localeCompare(b.group));

  const keep = ranked.length > MAX_SERIES ? ranked.slice(0, MAX_SERIES - 1) : ranked;
  const tail = ranked.length > MAX_SERIES ? ranked.slice(MAX_SERIES - 1) : [];
  const slots = memory.assign(keep.map((g) => g.group));
  const shown: StackedSeries[] = keep
    .map((g) => ({
      id: `g:${g.group}`,
      label: groupLabel(g.group, groupBy),
      group: g.group,
      slot: slots.get(g.group) ?? 0,
      values: g.values,
      total: g.total,
    }))
    .sort((a, b) => a.slot - b.slot);
  if (tail.length > 0) {
    const values = axis.map((_, i) => tail.reduce((sum, g) => sum + (g.values[i] ?? 0), 0));
    shown.push({
      id: 'other',
      label: `Other (${tail.length} more)`,
      group: null,
      slot: 0,
      values,
      total: totalOf(values),
    });
  }
  return shown;
}

/** The sum over every group of a metric, per bucket. */
export function sumByBucket(
  points: readonly SeriesPoint[],
  axis: readonly number[],
  value: (p: SeriesPoint) => number,
): number[] {
  const index = new Map(axis.map((t, i) => [t, i]));
  const sums = axis.map(() => 0);
  for (const p of points) {
    const i = index.get(Date.parse(p.bucket_start));
    if (i !== undefined) sums[i] = (sums[i] ?? 0) + value(p);
  }
  return sums;
}

export const spendOf = (p: SeriesPoint) => p.usd_estimate;
export const tokensOf = (p: SeriesPoint) => p.input_tokens + p.output_tokens;

/** The share of the input side served from the cache: cache reads over input, cache reads and cache writes. Null for no input. */
export function cacheShare(input: number, read: number, write: number): number | null {
  const whole = input + read + write;
  return whole > 0 ? read / whole : null;
}

/** The cache-read share per bucket, over all groups; null where a bucket had no input. */
export function cacheSeries(
  points: readonly SeriesPoint[],
  axis: readonly number[],
): Array<number | null> {
  const input = sumByBucket(points, axis, (p) => p.input_tokens);
  const read = sumByBucket(points, axis, (p) => p.cache_read_tokens);
  const write = sumByBucket(points, axis, (p) => p.cache_write_tokens);
  return axis.map((_, i) => cacheShare(input[i] ?? 0, read[i] ?? 0, write[i] ?? 0));
}

export interface PrSeries {
  unchanged: number[];
  edited: number[];
  /** Merged, with no commit kept to compare the head with. */
  merged: number[];
  closed: number[];
  open: number[];
}

export function prSeries(points: readonly SeriesPoint[], axis: readonly number[]): PrSeries {
  const per = (pick: (p: PrCounts) => number) => sumByBucket(points, axis, (p) => pick(p.prs));
  return {
    unchanged: per((p) => p.merged_unchanged),
    edited: per((p) => p.merged_edited),
    merged: per((p) => p.merged),
    closed: per((p) => p.closed),
    open: per((p) => p.open),
  };
}

export const mergedCount = (p: PrCounts) => p.merged_unchanged + p.merged_edited + p.merged;
const resolvedCount = (p: PrCounts) => mergedCount(p) + p.closed;

// ---------- hover and table builders ----------

export interface StackView {
  tip: (index: number) => TipData;
  table: { label: string; columns: string[]; rows: string[][] };
}

/** The hover and the table of a stacked chart: every series at the bucket, the sum, and the field they are read from. */
export function stackView(options: {
  axis: readonly number[];
  bucket: AnalyticsBucket;
  series: readonly StackedSeries[];
  format: (value: number) => string;
  field: string;
  name: string;
  /** The word for the sum, when the series add up to something that means a thing. */
  totalLabel?: string;
}): StackView {
  const { axis, bucket, series, format, field, name, totalLabel = 'Total' } = options;
  const source = (i: number) =>
    `analytics/v1 tasks.points · ${field} · bucket_start ${isoDay(axis[i] ?? 0)}`;
  const sum = (i: number) => series.reduce((s, x) => s + (x.values[i] ?? 0), 0);
  return {
    tip: (i) => ({
      title: `${bucketTitle(axis[i] ?? 0, bucket)} (UTC)`,
      rows: series
        .filter((s) => (s.values[i] ?? 0) > 0)
        .map((s) => ({ label: s.label, value: format(s.values[i] ?? 0), slot: s.slot })),
      total: series.length > 1 ? { label: totalLabel, value: format(sum(i)) } : undefined,
      source: source(i),
    }),
    table: {
      label: `${name} per ${bucket}`,
      columns: [
        'Bucket (UTC)',
        ...series.map((s) => s.label),
        ...(series.length > 1 ? [totalLabel] : []),
      ],
      rows: axis.map((t, i) => [
        bucketLabel(t, bucket),
        ...series.map((s) => format(s.values[i] ?? 0)),
        ...(series.length > 1 ? [format(sum(i))] : []),
      ]),
    },
  };
}

// ---------- the four figures ----------

export interface Figure {
  label: string;
  value: string;
  sub: string;
  /** The route and field the figure is read from. */
  source: string;
}

export function figures(
  body: AnalyticsBody,
): Record<'spend' | 'pass' | 'falseStop' | 'merged', Figure> {
  const t = body.tasks.total;
  const tokens = t.input_tokens + t.output_tokens;
  const pass = wilson(t.completed, t.tasks);
  const merged = mergedCount(t.prs);
  const resolved = resolvedCount(t.prs);
  return {
    spend: {
      label: 'Spend',
      value: t.usd_estimate_known > 0 ? formatUsd(t.usd_estimate, true) : DASH,
      sub:
        t.tasks === 0
          ? 'no finished task'
          : t.usd_estimate_known === 0
            ? `${formatTokens(tokens)} tokens in and out · no task could be priced`
            : `${formatTokens(tokens)} tokens in and out · ${formatCount(t.usd_estimate_known)} of ${formatCount(t.tasks)} tasks priced`,
      source: 'tasks.total.usd_estimate',
    },
    pass: {
      label: 'Pass rate',
      value: pass ? formatPercent(pass.rate * 100) : DASH,
      sub: pass
        ? `${formatCount(pass.k)} of ${formatCount(pass.n)} tasks completed · 95% CI ${formatInterval(pass)}`
        : 'no finished task',
      source: 'tasks.total.completed / tasks',
    },
    falseStop: {
      label: 'Governor false stops',
      value:
        t.checkpointed > 0
          ? formatPercent((t.checkpointed_completed / t.checkpointed) * 100)
          : DASH,
      sub:
        t.checkpointed > 0
          ? `${formatCount(t.checkpointed_completed)} of ${formatCount(t.checkpointed)} checkpointed tasks still completed`
          : 'no task hit a checkpoint',
      source: 'tasks.total.checkpointed_completed / checkpointed',
    },
    merged: {
      label: 'Pull requests merged',
      value: t.prs.total > 0 ? formatCount(merged) : DASH,
      sub:
        t.prs.total === 0
          ? 'no pull request opened'
          : resolved > 0
            ? `${formatPercent((t.prs.merged_unchanged / resolved) * 100)} of resolved merged as chargehand left them · ${formatCount(t.prs.open)} open`
            : `${formatCount(t.prs.open)} open, none resolved yet`,
      source: 'tasks.total.prs',
    },
  };
}

// ---------- pass rate by repository and size (from /v1/learn) ----------

export interface PassRow {
  key: string;
  repository: string;
  size: string;
  /** Null for a group with no task: not drawn. */
  p: Proportion;
  group: LearnGroup;
}

/** Rows for the whisker chart: the groups with the most tasks first, then by name; `more` counts the groups past the limit. */
export function passRows(
  groups: readonly LearnGroup[],
  limit = 12,
): { rows: PassRow[]; more: number } {
  const all = groups
    .flatMap((g) => {
      const p = wilson(g.completed, g.tasks);
      return p
        ? [
            {
              key: `${g.repository}\u0000${g.size_class}`,
              repository: g.repository,
              size: g.size_class,
              p,
              group: g,
            },
          ]
        : [];
    })
    .sort(
      (a, b) =>
        b.p.n - a.p.n || a.repository.localeCompare(b.repository) || a.size.localeCompare(b.size),
    );
  return { rows: all.slice(0, limit), more: Math.max(0, all.length - limit) };
}

export const learnSource = (g: LearnGroup) =>
  `GET /v1/learn · groups[${g.repository}, ${g.size_class}] · completed over tasks`;

// ---------- duration (from the run list) ----------

/** Edges in minutes: under 5, 5 to 15, 15 to 30, 30 to 60, 1 to 2 h, 2 to 4 h, 4 h and more. */
export const DURATION_EDGES = [0, 5, 15, 30, 60, 120, 240] as const;

export function durationBinLabel(bin: Bin): string {
  if (bin.from === 0) return `<${bin.to} m`;
  if (!Number.isFinite(bin.to)) return bin.from >= 60 ? `${bin.from / 60} h+` : `${bin.from} m+`;
  return bin.to <= 60 ? `${bin.from}–${bin.to} m` : `${bin.from / 60}–${bin.to / 60} h`;
}

export interface Durations {
  n: number;
  bins: Bin[];
  median: number | null;
  p80: number | null;
}

/** The wall time of finished batch runs, in minutes, binned. `from` (ms) drops the runs that ended before the range began. */
export function durations(runs: readonly RunSummary[], from: number | null): Durations {
  const minutes = runs.flatMap((r) => {
    if (!r.finished_at) return [];
    const end = Date.parse(r.finished_at);
    const start = Date.parse(r.started_at);
    if (Number.isNaN(end) || Number.isNaN(start) || end < start) return [];
    if (from != null && end < from) return [];
    return [(end - start) / 60_000];
  });
  return {
    n: minutes.length,
    bins: histogram(minutes, DURATION_EDGES),
    median: percentile(minutes, 50),
    p80: percentile(minutes, 80),
  };
}

export const minutesText = (m: number | null) => (m == null ? DASH : formatDuration(m * 60));

// ---------- budget against actual ----------

export interface RepoSpend {
  repository: string;
  tasks: number;
  spendKnown: number;
  median: number | null;
  p80: number | null;
  group: LearnGroup;
}

export interface BudgetRow {
  size: SizeClass;
  budget: BudgetInForce | null;
  repos: RepoSpend[];
}

/** Per size class: the budget a task was last held to, and the 80th-percentile spend of each repository's tasks of that size. */
export function budgetRows(
  groups: readonly LearnGroup[],
  budgets: readonly BudgetInForce[],
): BudgetRow[] {
  return SIZES.flatMap((size) => {
    const budget = budgets.find((b) => b.size === size) ?? null;
    const repos = groups
      .filter((g) => g.size_class === size && g.spend_p80_usd != null)
      .map((g) => ({
        repository: g.repository,
        tasks: g.tasks,
        spendKnown: g.spend_known,
        median: g.spend_median_usd,
        p80: g.spend_p80_usd,
        group: g,
      }))
      .sort((a, b) => (b.p80 ?? 0) - (a.p80 ?? 0) || a.repository.localeCompare(b.repository));
    return budget || repos.length > 0 ? [{ size, budget, repos }] : [];
  });
}

/** The largest p80 spend of any repository as a share of its size class's budget, as a fraction (1 is the budget). */
export function budgetAxisMax(rows: readonly BudgetRow[]): number {
  let max = 1;
  for (const row of rows) {
    if (!row.budget || row.budget.usd <= 0) continue;
    for (const r of row.repos) max = Math.max(max, (r.p80 ?? 0) / row.budget.usd);
  }
  return max;
}

// ---------- governor ----------

export interface ModeRow {
  mode: GovernorModeStats['mode'];
  tasks: number;
  checkpointed: number;
  completed: number;
  p: Proportion;
}

/** Governor modes that had a checkpointed task, with the share that completed and its Wilson interval. */
export function modeRows(modes: readonly GovernorModeStats[]): ModeRow[] {
  return modes.flatMap((m) => {
    const p = wilson(m.checkpointed_completed, m.checkpointed);
    return p
      ? [
          {
            mode: m.mode,
            tasks: m.tasks,
            checkpointed: m.checkpointed,
            completed: m.checkpointed_completed,
            p,
          },
        ]
      : [];
  });
}

/** The decisions the governor took, most frequent first. */
export function topDecisions(decisions: readonly GovernorCount[], limit = 6): GovernorCount[] {
  return [...decisions]
    .filter((d) => d.count > 0)
    .sort(
      (a, b) =>
        b.count - a.count || a.signal.localeCompare(b.signal) || a.action.localeCompare(b.action),
    )
    .slice(0, limit);
}

/** ADR 0045 switches the governor to enforce when false stops stay under this share. */
export const ENFORCE_THRESHOLD = 0.05;

// ---------- continuations ----------

export interface ContinuationView {
  withCount: number;
  withoutCount: number;
  sessions: number;
  withRate: Proportion | null;
  withoutRate: Proportion | null;
}

/** Completion of tasks that went on in a fresh session against those that did not, with the counts the rates stand on. */
export function continuationView(body: AnalyticsBody): ContinuationView {
  const c = body.tasks.continuations;
  const withCount = c.tasks_with_continuation;
  const withoutCount = Math.max(0, body.tasks.tasks - withCount);
  const k = (rate: number | null | undefined, n: number) =>
    rate == null ? 0 : Math.round(rate * n);
  return {
    withCount,
    withoutCount,
    sessions: c.continuations,
    withRate: wilson(k(c.completion_rate, withCount), withCount),
    withoutRate: wilson(k(c.completion_rate_without, withoutCount), withoutCount),
  };
}

/** The CI fixer line of `not_recorded`, as the server says it. */
export function ciFixerNote(notRecorded: readonly string[]): string {
  return (
    notRecorded.find((s) => /ci fix/i.test(s)) ??
    "The CI fixer's attempts are not recorded apart from the task's spend."
  );
}

// ---------- axis formats ----------

/** `$0`, `$2.50`, `$12`, `$1.5k`: short, for an axis. */
export function axisUsd(value: number): string {
  if (value >= 1000) return `$${formatTokens(value)}`;
  return Number.isInteger(value) || value >= 10 ? `$${Math.round(value)}` : `$${value.toFixed(2)}`;
}

export const axisShare = (value: number) => `${Math.round(value * 100)}%`;

// ---------- cache read share ----------

export interface CacheView {
  /** The one series the line chart draws. */
  series: TimeSeries[];
  tip: (index: number) => TipData;
  table: { label: string; columns: string[]; rows: string[][] };
}

export function cacheView(
  points: readonly SeriesPoint[],
  axis: readonly number[],
  bucket: AnalyticsBucket,
): CacheView {
  const input = sumByBucket(points, axis, (p) => p.input_tokens);
  const read = sumByBucket(points, axis, (p) => p.cache_read_tokens);
  const write = sumByBucket(points, axis, (p) => p.cache_write_tokens);
  const values = cacheSeries(points, axis);
  const share = (i: number) =>
    values[i] == null ? DASH : formatPercent((values[i] as number) * 100);
  return {
    series: [{ id: 'cache', label: 'Cache read share', slot: 1, values }],
    tip: (i) => ({
      title: `${bucketTitle(axis[i] ?? 0, bucket)} (UTC)`,
      rows: [
        { label: 'cache read share', value: share(i), slot: 1 },
        { label: 'input tokens', value: formatTokens(input[i]) },
        { label: 'cache read', value: formatTokens(read[i]) },
        { label: 'cache write', value: formatTokens(write[i]) },
      ],
      source: `analytics/v1 tasks.points · cache_read_tokens over input + cache_read + cache_write · bucket_start ${isoDay(axis[i] ?? 0)}`,
    }),
    table: {
      label: `Cache read share per ${bucket}`,
      columns: ['Bucket (UTC)', 'Cache read share', 'Input', 'Cache read', 'Cache write'],
      rows: axis.map((t, i) => [
        bucketLabel(t, bucket),
        share(i),
        formatTokens(input[i]),
        formatTokens(read[i]),
        formatTokens(write[i]),
      ]),
    },
  };
}

// ---------- everything the time charts draw ----------

export interface ChartsModel {
  axis: number[];
  bucket: AnalyticsBucket;
  labels: string[];
  spend: { series: StackedSeries[]; view: StackView };
  tokens: { series: StackedSeries[]; view: StackView };
  cache: CacheView;
  prs: { series: StackedSeries[]; view: StackView };
  /** Continuation sessions per bucket. */
  continuations: number[];
}

const PR_KINDS = [
  { key: 'unchanged', label: 'Merged as chargehand left it', slot: 1 },
  { key: 'edited', label: 'Merged after a person edited it', slot: 2 },
  { key: 'merged', label: 'Merged, no commit to compare', slot: 3 },
] as const;

export function chartsModel(body: AnalyticsBody, range: Range, memory: SlotMemory): ChartsModel {
  const { points } = body.tasks;
  const bucket = body.bucket;
  const groupBy = body.group_by;
  const axis = bucketAxis(points, bucket, body.generated_at, range.days);
  const spend = stackSeries(points, axis, groupBy, spendOf, memory);
  const tokens = stackSeries(points, axis, groupBy, tokensOf, memory);
  const pr = prSeries(points, axis);
  const prStack: StackedSeries[] = PR_KINDS.flatMap((k) => {
    const values = pr[k.key];
    const total = values.reduce((a, b) => a + b, 0);
    return total > 0
      ? [{ id: k.key, label: k.label, group: null, slot: k.slot, values, total }]
      : [];
  });
  return {
    axis,
    bucket,
    labels: axis.map((t) => bucketLabel(t, bucket)),
    spend: {
      series: spend,
      view: stackView({
        axis,
        bucket,
        series: spend,
        format: (v) => formatUsd(v, true),
        field: 'usd_estimate',
        name: 'Spend',
      }),
    },
    tokens: {
      series: tokens,
      view: stackView({
        axis,
        bucket,
        series: tokens,
        format: formatTokens,
        field: 'input_tokens + output_tokens',
        name: 'Tokens',
      }),
    },
    cache: cacheView(points, axis, bucket),
    prs: {
      series: prStack,
      view: stackView({
        axis,
        bucket,
        series: prStack,
        format: formatCount,
        field: 'prs',
        name: 'Pull requests merged',
        totalLabel: 'Merged',
      }),
    },
    continuations: sumByBucket(points, axis, (p) => p.continuations),
  };
}

/** The finished runs of the run log in the range: how many, and the priced cost they add (API-key runs only). */
export function runLog(runs: AnalyticsBody['runs']): { runs: number; usd: number } {
  return { runs: runs.reduce((s, r) => s + r.runs, 0), usd: runs.reduce((s, r) => s + r.usd, 0) };
}

// ---------- the numbers behind the SVG charts, as tables ----------

export interface TableData {
  label: string;
  columns: string[];
  rows: string[][];
}

export function passTable(rows: readonly PassRow[]): TableData {
  return {
    label: 'Pass rate by repository and size',
    columns: ['Repository', 'Size', 'Tasks', 'Completed', 'Pass rate', '95% interval'],
    rows: rows.map((r) => [
      r.repository,
      r.size,
      formatCount(r.p.n),
      formatCount(r.p.k),
      formatPercent(r.p.rate * 100),
      formatInterval(r.p),
    ]),
  };
}

export function durationTable(d: Durations): TableData {
  return {
    label: 'Duration of finished batches',
    columns: ['Duration', 'Batches'],
    rows: d.bins.map((b) => [durationBinLabel(b), String(b.count)]),
  };
}

export function budgetTable(rows: readonly BudgetRow[]): TableData {
  return {
    label: 'Budget against actual spend',
    columns: ['Size', 'Repository', 'Priced tasks', 'Median', 'p80', 'Budget'],
    rows: rows.flatMap((row) =>
      row.repos.length > 0
        ? row.repos.map((r) => [
            row.size,
            r.repository,
            `${formatCount(r.spendKnown)} of ${formatCount(r.tasks)}`,
            formatUsd(r.median, true),
            formatUsd(r.p80, true),
            row.budget ? formatUsd(row.budget.usd, true) : DASH,
          ])
        : [[row.size, DASH, DASH, DASH, DASH, row.budget ? formatUsd(row.budget.usd, true) : DASH]],
    ),
  };
}

export function modeTable(rows: readonly ModeRow[]): TableData {
  return {
    label: 'Governor false-stop rate by mode',
    columns: ['Mode', 'Tasks', 'Checkpointed', 'Completed', 'Rate', '95% interval'],
    rows: rows.map((r) => [
      r.mode,
      formatCount(r.tasks),
      formatCount(r.checkpointed),
      formatCount(r.completed),
      formatPercent(r.p.rate * 100),
      formatInterval(r.p),
    ]),
  };
}
