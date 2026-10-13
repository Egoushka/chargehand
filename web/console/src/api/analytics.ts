import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta } from './common';

export type AnalyticsBucket = 'day' | 'week' | 'month';
export type AnalyticsGroupBy = 'none' | 'repository' | 'account' | 'size';

/** Pull requests of a group by what became of them; `open` includes those whose outcome was never read. */
export interface PrCounts {
  total: number;
  merged_unchanged: number;
  merged_edited: number;
  merged: number;
  closed: number;
  open: number;
}

/** One bucket of finished driven tasks for one group; `group` is null when not grouped, `pass_rate` null for no tasks. */
export interface SeriesPoint {
  bucket_start: string;
  group?: string | null;
  tasks: number;
  completed: number;
  partial: number;
  failed: number;
  needs_input: number;
  pass_rate?: number | null;
  /** The sum of the dollar equivalents that are known, for `usd_estimate_known` tasks. */
  usd_estimate: number;
  usd_estimate_known: number;
  input_tokens: number;
  output_tokens: number;
  cache_read_tokens: number;
  cache_write_tokens: number;
  turns: number;
  continuations: number;
  /** Tasks with a checkpoint decision, acted on or only logged. */
  checkpointed: number;
  /** Of those, the ones that completed (the governor's false-stop proxy). */
  checkpointed_completed: number;
  prs: PrCounts;
}

export interface GovernorCount {
  signal: string;
  action: 'warn' | 'checkpoint' | 'stop';
  count: number;
  acted: number;
}

/** `false_stop_rate`: checkpointed tasks that still completed, over checkpointed tasks; null when none. */
export interface GovernorModeStats {
  /** `none` is a record with no governor. */
  mode: 'off' | 'observe' | 'enforce' | 'none';
  tasks: number;
  checkpointed: number;
  checkpointed_completed: number;
  false_stop_rate?: number | null;
}

/** How the governor decided over the period, per signal and rung, and per mode. */
export interface GovernorSeries {
  decisions: GovernorCount[];
  modes: GovernorModeStats[];
}

/** Tasks that went on in a fresh session after one was stopped; a rate is null for none. */
export interface ContinuationStats {
  tasks_with_continuation: number;
  continuations: number;
  completion_rate?: number | null;
  completion_rate_without?: number | null;
}

export interface LearningSeriesReport {
  since?: string | null;
  bucket: AnalyticsBucket;
  group_by: AnalyticsGroupBy;
  tasks: number;
  points: SeriesPoint[];
  total: SeriesPoint;
  governor: GovernorSeries;
  continuations: ContinuationStats;
}

/** Finished top-level runs (a batch counts as one) in one bucket, for one group. `usd` is the priced cost: a subscription run adds none. */
export interface RunPoint {
  bucket_start: string;
  group?: string | null;
  runs: number;
  completed: number;
  failed: number;
  needs_input: number;
  denied: number;
  usd: number;
  tokens: number;
}

/** `analytics/v1`: finished driven tasks over time (the learning records) and finished runs over time (the run log). */
export interface AnalyticsBody extends ContractMeta {
  contract_version: 'analytics/v1';
  since?: string | null;
  bucket: AnalyticsBucket;
  group_by: AnalyticsGroupBy;
  /** The account the body is limited to; absent: every run the caller may see. */
  account?: string | null;
  /** Spend, tokens (cache included), pass rate, continuations and the governor's false-stop proxy per bucket and group. */
  tasks: LearningSeriesReport;
  runs: RunPoint[];
}

export interface AnalyticsParams {
  /** `30d` or an ISO 8601 time; the server's default is 30 days. */
  since?: string;
  bucket?: AnalyticsBucket;
  group_by?: AnalyticsGroupBy;
  /** Owner only: one account (`owner` for runs with no account). */
  account?: string;
}

const analyticsPath = (params: AnalyticsParams) => {
  const query = new URLSearchParams();
  for (const key of ['since', 'bucket', 'group_by', 'account'] as const) {
    const value = params[key];
    if (value) query.set(key, value);
  }
  const text = query.toString();
  return text ? `/v1/analytics?${text}` : '/v1/analytics';
};

export const analyticsQuery = (params: AnalyticsParams = {}) =>
  queryOptions({
    queryKey: ['analytics', params],
    queryFn: ({ signal }) => getJson<AnalyticsBody>(analyticsPath(params), signal),
  });

export const useAnalytics = (params: AnalyticsParams = {}) => useQuery(analyticsQuery(params));
