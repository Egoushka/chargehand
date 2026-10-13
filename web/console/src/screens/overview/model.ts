import type { BatchBody, BatchTaskBody } from '../../api/batch';
import type { AccountBody, PullRequestBody } from '../../api/common';
import type { InboxItem } from '../../api/inbox';
import type { OverviewBody } from '../../api/overview';
import { isMerged } from '../../api/pulls';
import { capLevel, formatPercent, formatTokens, formatUsd } from '../../lib/format';
import type { MeterSegment } from '../../ui';

const DAY_MS = 24 * 3600 * 1000;

/** The batch's tasks grouped the way the progress bar draws them. */
export interface BatchProgress {
  total: number;
  done: number;
  running: number;
  question: number;
  partial: number;
  failed: number;
  waiting: number;
  segments: MeterSegment[];
  /** Counts for assistive technology: the bar is a picture. */
  label: string;
}

const isPartial = (task: BatchTaskBody) =>
  task.state === 'completed' && task.pull_request?.partial === true;

export function batchProgress(tasks: BatchTaskBody[]): BatchProgress {
  const count = (test: (t: BatchTaskBody) => boolean) => tasks.filter(test).length;
  const partial = count(isPartial);
  const done = count((t) => t.state === 'completed' && !isPartial(t));
  const running = count((t) => t.state === 'running');
  const question = count((t) => t.state === 'needs_input');
  const failed = count((t) => t.state === 'failed');
  const waiting = tasks.length - done - partial - running - question - failed;
  return {
    total: tasks.length,
    done,
    running,
    question,
    partial,
    failed,
    waiting,
    segments: [
      { n: done, tone: 'ok' },
      { n: running, tone: 'info', live: true },
      { n: question, tone: 'accent' },
      { n: partial, tone: 'warn' },
      { n: failed, tone: 'danger' },
      { n: waiting, tone: 'neutral' },
    ],
    label: `${done} done, ${running} running, ${question + partial + failed} need attention, ${waiting} waiting`,
  };
}

/** Tokens in and out over the batch, and its spend: the dollar equivalent when the tasks report it, else the priced cost. */
export function batchSpend(tasks: BatchTaskBody[]) {
  let tokens = 0;
  let estimate = 0;
  let priced = 0;
  let hasEstimate = false;
  let hasPriced = false;
  for (const task of tasks) {
    const s = task.spend;
    if (!s) continue;
    tokens += s.input_tokens + s.output_tokens;
    if (s.usd_estimate != null) {
      estimate += s.usd_estimate;
      hasEstimate = true;
    }
    if (s.usd != null) {
      priced += s.usd;
      hasPriced = true;
    }
  }
  return {
    tokens,
    usd: hasEstimate ? estimate : hasPriced ? priced : null,
    estimated: hasEstimate,
  };
}

export interface ActiveBatch {
  batch: BatchBody;
  progress: BatchProgress;
  slots: boolean[];
  usd: string;
  cap: string;
  tokens: string;
}

export function activeBatch(batch: BatchBody): ActiveBatch {
  const progress = batchProgress(batch.tasks);
  const spend = batchSpend(batch.tasks);
  const max = batch.limits.max_parallel ?? progress.running;
  return {
    batch,
    progress,
    slots: Array.from({ length: Math.max(max, progress.running) }, (_, i) => i < progress.running),
    usd: formatUsd(spend.usd, spend.estimated),
    cap: batch.limits.max_usd_total == null ? 'no cap' : formatUsd(batch.limits.max_usd_total),
    tokens: formatTokens(spend.tokens),
  };
}

export interface CapRow {
  name: string;
  tokens: string;
  value: number;
  cap: number | null;
  /** The text on the right of the meter's head: "$9.40 of $25.00 · 38%". */
  text: string;
  level?: 'warn' | 'danger';
}

/** One account's spend today against its cap: dollars when it has a dollar cap, else tokens, else "no cap". */
export function capRow(account: Pick<AccountBody, 'name' | 'limits' | 'spend'>): CapRow {
  const { limits, spend } = account;
  const day = spend.day;
  const usdCap = limits.max_usd_per_day ?? null;
  const tokenCap = limits.max_tokens_per_day ?? null;
  const base = { name: account.name, tokens: formatTokens(day.tokens) };
  if (usdCap != null && usdCap > 0) {
    const pct = Math.round((day.usd / usdCap) * 100);
    return {
      ...base,
      value: day.usd,
      cap: usdCap,
      text: `${formatUsd(day.usd)} of ${formatUsd(usdCap)} · ${formatPercent(pct)}`,
      level: capLevel(pct),
    };
  }
  if (tokenCap != null && tokenCap > 0) {
    const pct = Math.round((day.tokens / tokenCap) * 100);
    return {
      ...base,
      value: day.tokens,
      cap: tokenCap,
      text: `${formatTokens(day.tokens)} of ${formatTokens(tokenCap)} tokens · ${formatPercent(pct)}`,
      level: capLevel(pct),
    };
  }
  return { ...base, value: day.usd, cap: null, text: `${formatUsd(day.usd)} · no cap` };
}

/** The accounts the spend panel lists: every account for the owner, the caller's own otherwise. */
export function spendRows(overview: OverviewBody): CapRow[] {
  return overview.accounts?.length ? overview.accounts.map(capRow) : [capRow(overview.standing)];
}

export interface Kpis {
  sessions: { running: number; max: number | null; sub: string };
  spend: { value: string; sub: string };
  needs: { value: number; sub: string };
  merged: { value: number; sub: string };
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

export function kpis(overview: OverviewBody, merges: PullRequestBody[]): Kpis {
  const { driven, standing, inbox, server } = overview;
  const waitingStarts = server.runs_queued;
  const accounts = overview.accounts?.length ?? 0;
  return {
    sessions: {
      running: driven.sessions.running,
      max: driven.sessions.max_parallel_total ?? null,
      sub: !driven.enabled
        ? 'driven sessions are off'
        : driven.sessions.running === 0 && waitingStarts === 0
          ? 'idle'
          : `${plural(driven.sessions.batches, 'batch', 'batches')}${waitingStarts > 0 ? ` · ${waitingStarts} queued` : ''}`,
    },
    spend: {
      value: formatUsd(standing.spend.day.usd),
      sub:
        standing.spend.day.tokens === 0 && standing.spend.day.usd === 0
          ? 'no runs today'
          : `${formatTokens(standing.spend.day.tokens)} tokens${accounts > 1 ? ` · ${plural(accounts, 'account')}` : ''}`,
    },
    needs: {
      value: inbox.total,
      sub:
        inbox.total === 0
          ? 'all clear'
          : `${plural(inbox.questions, 'question')} · ${plural(inbox.pull_requests, 'PR')} · ${plural(inbox.failures, 'failure')}`,
    },
    merged: { value: merges.length, sub: 'in the last 24 hours' },
  };
}

/** Pull requests merged in the last 24 hours, newest first. The time is when the server read the outcome back (about hourly). */
export function recentMerges(
  pulls: PullRequestBody[],
  now: number = Date.now(),
): PullRequestBody[] {
  const at = (pr: PullRequestBody) => Date.parse(pr.state_at ?? pr.opened_at ?? '');
  return pulls.filter((pr) => isMerged(pr) && now - at(pr) <= DAY_MS).sort((a, b) => at(b) - at(a));
}

/** The needs-you panel shows the newest of each; questions first, as the screen does. */
export function inboxPreview(items: InboxItem[], limit = 6): InboxItem[] {
  const order = { question: 0, pull_request: 1, failure: 2 } as const;
  return [...items]
    .sort((a, b) => order[a.kind] - order[b.kind] || b.at.localeCompare(a.at))
    .slice(0, limit);
}

/** GiB with one decimal, from MiB. */
export function gib(mb: number | null | undefined): string {
  return mb == null ? '—' : `${(mb / 1024).toFixed(1)} GiB`;
}
