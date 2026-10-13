// Invented learning records, summed the way the server sums them (LearningSeries and LearningReport in Chargehand.Driven), so the
// fake server can answer `analytics/v1` and `/v1/learn` for any range, bucket and grouping with figures that agree with each
// other. Repositories, people and numbers are made up; times are relative to `now`.
import type {
  AnalyticsBody,
  AnalyticsBucket,
  AnalyticsGroupBy,
  AnalyticsParams,
  GovernorCount,
  PrCounts,
  RunPoint,
  SeriesPoint,
} from '../api/analytics';
import type { BatchBody, BatchTaskBody, BudgetSourceBody } from '../api/batch';
import type { LearnGroup, LearnReport, RunSummary } from '../api/types';
import { percentile } from '../charts/stats';
import * as fx from './fixtures';

const DAY = 86_400_000;

/** The first instant (UTC) of the day, week (Monday) or month holding `ms`: written out here so the fixture does not lean on the code it tests. */
function bucketStart(ms: number, bucket: AnalyticsBucket): number {
  const d = new Date(ms);
  const day = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
  if (bucket === 'day') return day;
  if (bucket === 'week') return day - ((d.getUTCDay() + 6) % 7) * DAY;
  return Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1);
}
export const REPOS = ['acme/payments-api', 'acme/storefront', 'acme/ledger'] as const;
type Size = 'S' | 'M' | 'L';
type PrOutcome = 'merged_unchanged' | 'merged_edited' | 'merged' | 'closed' | 'open';

const PR_OUTCOMES: PrOutcome[] = [
  'merged_unchanged',
  'merged_unchanged',
  'merged_unchanged',
  'merged_unchanged',
  'merged_unchanged',
  'merged_unchanged',
  'merged_edited',
  'merged_edited',
  'closed',
  'merged',
];

export interface Rec {
  at: number;
  repository: string;
  size: Size;
  actor: string | null;
  outcome: 'completed' | 'partial' | 'failed' | 'needs_input';
  usd: number | null;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  turns: number;
  continuations: number;
  governor: GovernorCount[];
  mode: string | null;
  pr: PrOutcome | null;
}

/** About 360 tasks over the last 60 days in three repositories; the same for the same `now`. */
export function records(now: number, days = 60): Rec[] {
  const out: Rec[] = [];
  let n = 0;
  for (let d = 0; d < days; d++) {
    REPOS.forEach((repository, r) => {
      const count = (d * 7 + r * 3) % 5;
      for (let k = 0; k < count; k++) {
        n += 1;
        const size: Size = (['S', 'M', 'M', 'L', 'S'] as const)[(n + r) % 5] as Size;
        const roll = (n * 37) % 100;
        const outcome =
          roll < 70 ? 'completed' : roll < 80 ? 'partial' : roll < 92 ? 'failed' : 'needs_input';
        const factor = 0.6 + ((n * 13) % 11) / 10;
        const usd =
          n % 17 === 0 ? null : Math.round({ S: 0.35, M: 0.9, L: 3.2 }[size] * factor * 100) / 100;
        const base = (usd ?? 0.5) * 1000;
        const checkpointed = n % 9 === 0;
        const at =
          d === 0
            ? now - (k + 1) * 3_600_000
            : bucketStart(now, 'day') - d * DAY + (8 + k * 3 + r) * 3_600_000;
        out.push({
          at,
          repository,
          size,
          actor: r === 1 ? 'ci-bot' : null,
          outcome,
          usd,
          input: Math.round(base * 40),
          output: Math.round(base * 9),
          cacheRead: Math.round(base * 2200),
          cacheWrite: Math.round(base * 150),
          turns: 12 + (n % 30),
          continuations: size === 'L' && n % 3 === 0 ? 1 : 0,
          governor: checkpointed
            ? [{ signal: 'spend', action: 'checkpoint', count: 1, acted: 0 }]
            : [],
          mode: checkpointed || n % 4 === 0 ? 'observe' : null,
          pr:
            outcome === 'completed' || outcome === 'partial'
              ? d < 2
                ? 'open'
                : (PR_OUTCOMES[(n * 7 + r) % 10] ?? 'merged')
              : null,
        });
      }
    });
  }
  return out.sort((a, b) => a.at - b.at);
}

const rate = (part: number, whole: number) =>
  whole === 0 ? null : Math.round((part / whole) * 1000) / 1000;

const prCounts = (rs: Rec[]): PrCounts => {
  const withPr = rs.filter((r) => r.pr);
  const count = (o: PrOutcome) => withPr.filter((r) => r.pr === o).length;
  return {
    total: withPr.length,
    merged_unchanged: count('merged_unchanged'),
    merged_edited: count('merged_edited'),
    merged: count('merged'),
    closed: count('closed'),
    open: count('open'),
  };
};

const sumOf = (rs: Rec[], pick: (r: Rec) => number) => rs.reduce((s, r) => s + pick(r), 0);

function point(start: number, group: string | null, rs: Rec[]): SeriesPoint {
  const completed = rs.filter((r) => r.outcome === 'completed').length;
  const checkpointed = rs.filter((r) => r.governor.some((g) => g.action === 'checkpoint'));
  const priced = rs.filter((r) => r.usd != null);
  return {
    bucket_start: new Date(start).toISOString(),
    group,
    tasks: rs.length,
    completed,
    partial: rs.filter((r) => r.outcome === 'partial').length,
    failed: rs.filter((r) => r.outcome === 'failed').length,
    needs_input: rs.filter((r) => r.outcome === 'needs_input').length,
    pass_rate: rate(completed, rs.length),
    usd_estimate: Math.round(sumOf(priced, (r) => r.usd ?? 0) * 100) / 100,
    usd_estimate_known: priced.length,
    input_tokens: sumOf(rs, (r) => r.input),
    output_tokens: sumOf(rs, (r) => r.output),
    cache_read_tokens: sumOf(rs, (r) => r.cacheRead),
    cache_write_tokens: sumOf(rs, (r) => r.cacheWrite),
    turns: sumOf(rs, (r) => r.turns),
    continuations: sumOf(rs, (r) => r.continuations),
    checkpointed: checkpointed.length,
    checkpointed_completed: checkpointed.filter((r) => r.outcome === 'completed').length,
    prs: prCounts(rs),
  };
}

const groupOf = (r: Rec, by: AnalyticsGroupBy): string | null =>
  by === 'repository'
    ? r.repository
    : by === 'account'
      ? (r.actor ?? 'owner')
      : by === 'size'
        ? r.size
        : null;

/** The days a `since` of `Nd` reaches back; anything else is the whole record. */
const daysOf = (since: string | undefined) => {
  const m = /^(\d+)d$/.exec(since ?? '30d');
  return m ? Number(m[1]) : 30;
};

export function analyticsBody(params: AnalyticsParams = {}, now = Date.now()): AnalyticsBody {
  const bucket: AnalyticsBucket = params.bucket ?? 'day';
  const groupBy: AnalyticsGroupBy = params.group_by ?? 'none';
  const since = now - daysOf(params.since) * DAY;
  const chosen = records(now).filter(
    (r) => r.at >= since && (!params.account || (r.actor ?? 'owner') === params.account),
  );
  const keyed = new Map<string, Rec[]>();
  for (const r of chosen) {
    const key = `${bucketStart(r.at, bucket)}|${groupOf(r, groupBy) ?? ''}`;
    keyed.set(key, [...(keyed.get(key) ?? []), r]);
  }
  const points = [...keyed.entries()]
    .map(([key, rs]) => point(Number(key.split('|')[0]), groupOf(rs[0] as Rec, groupBy), rs))
    .sort(
      (a, b) =>
        a.bucket_start.localeCompare(b.bucket_start) ||
        (a.group ?? '').localeCompare(b.group ?? ''),
    );
  const checkpointed = chosen.filter((r) => r.governor.length > 0);
  const modes = [...new Set(chosen.map((r) => r.mode ?? 'none'))].sort().map((mode) => {
    const inMode = chosen.filter((r) => (r.mode ?? 'none') === mode);
    const cp = inMode.filter((r) => r.governor.length > 0);
    const done = cp.filter((r) => r.outcome === 'completed').length;
    return {
      mode: mode as 'observe',
      tasks: inMode.length,
      checkpointed: cp.length,
      checkpointed_completed: done,
      false_stop_rate: rate(done, cp.length),
    };
  });
  const continued = chosen.filter((r) => r.continuations > 0);
  const single = chosen.filter((r) => r.continuations === 0);
  const runs: RunPoint[] = [...new Set(points.map((p) => p.bucket_start))].map((start) => ({
    bucket_start: start,
    group: null,
    runs: 2,
    completed: 1,
    failed: 1,
    needs_input: 0,
    denied: 0,
    usd: 1.5,
    tokens: 300_000,
  }));
  return {
    contract_version: 'analytics/v1',
    generated_at: new Date(now).toISOString(),
    not_recorded: [
      "The CI fixer's attempts and cost are not recorded apart: a task's spend includes its fix sessions, and the attempts are only in the live integrated events.",
      "Tasks that threw before their session ended (cancelled, a start that failed) write no learning record, so they are not in 'tasks'.",
      "'runs' counts finished top-level runs by their result's usage; a run still going, and the calls inside a batch, are left out.",
    ],
    since: new Date(since).toISOString(),
    bucket,
    group_by: groupBy,
    account: params.account ?? null,
    tasks: {
      since: new Date(since).toISOString(),
      bucket,
      group_by: groupBy,
      tasks: chosen.length,
      points,
      total: point(since, null, chosen),
      governor: {
        decisions: checkpointed.length
          ? [{ signal: 'spend', action: 'checkpoint', count: checkpointed.length, acted: 0 }]
          : [],
        modes,
      },
      continuations: {
        tasks_with_continuation: continued.length,
        continuations: sumOf(continued, (r) => r.continuations),
        completion_rate: rate(
          continued.filter((r) => r.outcome === 'completed').length,
          continued.length,
        ),
        completion_rate_without: rate(
          single.filter((r) => r.outcome === 'completed').length,
          single.length,
        ),
      },
    },
    runs,
  };
}

export function learnReport(since: string | undefined, now = Date.now()): LearnReport {
  const from = now - daysOf(since) * DAY;
  const chosen = records(now).filter((r) => r.at >= from);
  const groups: LearnGroup[] = [];
  for (const repository of REPOS) {
    for (const size of ['S', 'M', 'L'] as const) {
      const rs = chosen.filter((r) => r.repository === repository && r.size === size);
      if (rs.length === 0) continue;
      const spends = rs.flatMap((r) => (r.usd == null ? [] : [r.usd]));
      const completed = rs.filter((r) => r.outcome === 'completed').length;
      const partial = rs.filter((r) => r.outcome === 'partial').length;
      const cp = rs.filter((r) => r.governor.length > 0);
      const cpDone = cp.filter((r) => r.outcome === 'completed').length;
      const prs = prCounts(rs);
      const resolved = prs.merged_unchanged + prs.merged_edited + prs.merged + prs.closed;
      groups.push({
        repository,
        size_class: size,
        tasks: rs.length,
        spend_known: spends.length,
        spend_median_usd: percentile(spends, 50),
        spend_p80_usd: percentile(spends, 80),
        completed,
        partial,
        completion_rate: rate(completed, rs.length) ?? 0,
        partial_rate: rate(partial, rs.length) ?? 0,
        checkpointed: cp.length,
        checkpoint_rate: rate(cp.length, rs.length) ?? 0,
        checkpointed_completed: cpDone,
        checkpointed_completed_rate: rate(cpDone, cp.length),
        prs,
        merged_unchanged_rate: rate(prs.merged_unchanged, resolved),
      });
    }
  }
  return { since: new Date(from).toISOString(), tasks: chosen.length, groups };
}

// ---------- finished batches: durations, budgets in force, a retro ----------

const BUDGET_SOURCES: Record<Size, BudgetSourceBody> = {
  S: { kind: 'flat' },
  M: { kind: 'history', n: 23, percentile: 80, scope: 'repository' },
  L: { kind: 'history', n: 31, percentile: 80, scope: 'all' },
};
const BUDGET_USD: Record<Size, number> = { S: 1.5, M: 1.12, L: 4.9 };

export const RETRO_TEXT = [
  't2-retry-policy cost 1.4x its budget and stopped on checkpoint_spend; it still completed after the continuation.',
  'The spend signal fired on 3 of 6 tasks; two of them completed, so the checkpoint was early.',
  'Size M looks right for the spend; t4-metrics (L) used a third of its budget and could have been M.',
  'Try: lower the L budget for this repository, and split goals that name more than two modules.',
].join('\n');

/** A finished batch of three tasks (S, M, L) with their budgets, as `GET /v1/batches/{id}` carries them. */
export function finishedBatch(
  id: string,
  now: number,
  over: {
    retro?: BatchBody['retro'];
    minutesAgo?: number;
    sources?: Partial<Record<Size, BudgetSourceBody>>;
  } = {},
): BatchBody {
  const base = fx.batch(id, now);
  const end = now - (over.minutesAgo ?? 90) * 60_000;
  const tasks: BatchTaskBody[] = (['S', 'M', 'L'] as const).map((size, i) => ({
    ...(base.tasks[0] as BatchTaskBody),
    id: `t${i + 1}-${size.toLowerCase()}`,
    size,
    state: 'completed',
    started_at: new Date(end - 40 * 60_000).toISOString(),
    finished_at: new Date(end - (10 - i) * 60_000).toISOString(),
    budget: { usd: BUDGET_USD[size], source: over.sources?.[size] ?? BUDGET_SOURCES[size] },
  }));
  return {
    ...base,
    run: fx.run(
      id,
      {
        status: 'completed',
        started_at: new Date(end - 45 * 60_000).toISOString(),
        finished_at: new Date(end).toISOString(),
      },
      now,
    ),
    tasks,
    retro: over.retro,
  };
}

/** Finished batch runs of 4 to 190 minutes, newest first. */
export function finishedRuns(now: number): RunSummary[] {
  const minutes = [4, 9, 12, 18, 25, 33, 41, 58, 75, 96, 130, 190];
  return minutes.map((m, i) => {
    const end = now - (i + 1) * 3 * 3_600_000;
    return fx.run(
      `run-20261012-${String(100000 + i)}-aa${String(i).padStart(4, '0')}`,
      {
        status: i % 5 === 4 ? 'failed' : 'completed',
        started_at: new Date(end - m * 60_000).toISOString(),
        finished_at: new Date(end).toISOString(),
      },
      now,
    );
  });
}
