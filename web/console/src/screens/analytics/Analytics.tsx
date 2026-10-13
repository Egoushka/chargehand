import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useMemo, useRef, useState } from 'react';
import { type AnalyticsBucket, type AnalyticsGroupBy, analyticsQuery } from '../../api/analytics';
import { learnQuery, meQuery } from '../../api/queries';
import { batchDurationsQuery, useRecentBatches } from '../../api/recent';
import { ChartFrame } from '../../charts/ChartFrame';
import { SlotMemory } from '../../charts/colors';
import { TightEmpty } from '../../charts/TightEmpty';
import { TimeChart } from '../../charts/TimeChart';
import { budgetsInForce } from '../../lib/budgets';
import {
  DASH,
  DISPLAY_ZONE_LABEL,
  formatCount,
  formatTime,
  formatTokens,
  formatUsd,
} from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import { EmptyState, ErrorState, Kpi, Panel, Segmented, Skeleton } from '../../ui';
import { BudgetChart } from './BudgetChart';
import { Continuations } from './Continuations';
import { DurationChart } from './DurationChart';
import { DecisionBars, FalseStopChart } from './GovernorPanel';
import {
  axisShare,
  axisUsd,
  BUCKETS,
  budgetRows,
  budgetTable,
  chartsModel,
  ciFixerNote,
  durations,
  durationTable,
  figures,
  GROUPINGS,
  modeRows,
  modeTable,
  passRows,
  passTable,
  RANGES,
  type RangeId,
  rangeOf,
  runLog,
  topDecisions,
} from './model';
import { PassRateChart } from './PassRateChart';
import './analytics.css';

/** How old a query's data is, for the error box: "Showing the last data, from 13:14." */
function lastSeen(updatedAt: number): string | undefined {
  return updatedAt > 0
    ? `Showing the last data, from ${formatTime(updatedAt, { seconds: true })}.`
    : undefined;
}

function PanelSkeleton({ title, height = 160 }: { title: string; height?: number }) {
  return (
    <Panel title={title} busy>
      <div className="stack stack--tight">
        <Skeleton width="40%" />
        <Skeleton width="100%" height={height} />
      </div>
    </Panel>
  );
}

/** A panel whose source failed: the call and what it said, in the panel, so the others stay readable. */
function PanelError({ title, call, message }: { title: string; call: string; message: string }) {
  return (
    <Panel title={title}>
      <ErrorState title={`Cannot read ${title.toLowerCase()}`} call={call} detail={message} />
    </Panel>
  );
}

/** Cost, tokens and outcomes over time, from the learning records and the run log. Every mark says where its number comes from. */
export function Analytics() {
  const phone = useIsPhone();
  const me = useQuery(meQuery());
  const [rangeId, setRangeId] = useState<RangeId>('30d');
  const [bucketPick, setBucketPick] = useState<AnalyticsBucket | null>(null);
  const [groupBy, setGroupBy] = useState<AnalyticsGroupBy>('repository');
  const range = rangeOf(rangeId);
  const bucket = bucketPick ?? range.bucket;
  // A group keeps its hue while the screen is open, per kind of group (a repository is not a size).
  const memories = useRef<Partial<Record<AnalyticsGroupBy, SlotMemory>>>({});
  const memory = memories.current[groupBy] ?? new SlotMemory();
  memories.current[groupBy] = memory;

  const analytics = useQuery({
    ...analyticsQuery({ since: range.since, bucket, group_by: groupBy }),
    placeholderData: keepPreviousData,
  });
  const learn = useQuery({ ...learnQuery(range.since), placeholderData: keepPreviousData });
  const batchRuns = useQuery(batchDurationsQuery());
  const recent = useRecentBatches();

  const body = analytics.data;
  const stale = analytics.isPlaceholderData;
  // biome-ignore lint/correctness/useExhaustiveDependencies: `memory` is a ref's value and only remembers hues
  const charts = useMemo(() => (body ? chartsModel(body, range, memory) : null), [body, range]);
  const groups = learn.data?.groups ?? [];
  const pass = useMemo(() => passRows(groups), [groups]);
  const budgets = useMemo(() => budgetsInForce(recent.batches), [recent.batches]);
  const budget = useMemo(() => budgetRows(groups, budgets), [groups, budgets]);
  const duration = useMemo(
    () =>
      durations(
        batchRuns.data ?? [],
        range.days != null ? Date.now() - range.days * 86_400_000 : null,
      ),
    [batchRuns.data, range],
  );
  const figs = body ? figures(body) : null;
  const height = phone ? 180 : 220;
  const owner = me.data?.owner !== false;
  const groupings = GROUPINGS.filter((g) => owner || !g.ownerOnly);
  const groupName = groupBy === 'none' ? 'all tasks' : groupBy === 'size' ? 'size' : groupBy;
  const empty = body != null && body.tasks.tasks === 0;
  const governor = body?.tasks.governor;
  const modes = modeRows(governor?.modes ?? []);
  const decisions = topDecisions(governor?.decisions ?? []);
  const log = body ? runLog(body.runs) : null;

  return (
    <div className="screen an-screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Analytics</h1>
          <p className="screen__sub">
            {body
              ? `${formatCount(body.tasks.tasks)} finished tasks in ${range.phrase} · buckets are UTC ${bucket}s · ${formatTime(body.generated_at)} ${DISPLAY_ZONE_LABEL}${me.data?.owner === false ? ' · your own tasks' : ''}`
              : analytics.isError
                ? 'Not loaded'
                : 'Loading the learning records'}
          </p>
        </div>
        <div className="screen__actions">
          <Segmented
            label="Range"
            touch={phone}
            value={rangeId}
            items={RANGES.map((r) => ({ id: r.id, label: r.label }))}
            onValueChange={(id) => {
              setRangeId(id);
              setBucketPick(null);
            }}
          />
          <Segmented
            label="Bucket"
            touch={phone}
            value={bucket}
            items={BUCKETS.map((b) => ({ id: b.id, label: b.label }))}
            onValueChange={setBucketPick}
          />
          <Segmented
            label="Group by"
            touch={phone}
            value={groupBy}
            items={groupings.map((g) => ({ id: g.id, label: g.label }))}
            onValueChange={setGroupBy}
          />
        </div>
      </div>

      {analytics.isError ? (
        <ErrorState
          title="Cannot read the learning records"
          call="GET /v1/analytics"
          detail={analytics.error.message}
          age={body ? lastSeen(analytics.dataUpdatedAt) : undefined}
          onRetry={() => void analytics.refetch()}
        />
      ) : null}

      {!body && !analytics.isError ? (
        <div className="an-grid" aria-busy="true">
          {['Spend over time', 'Tokens over time', 'Cache read share', 'Pull requests merged'].map(
            (t) => (
              <PanelSkeleton key={t} title={t} />
            ),
          )}
        </div>
      ) : null}

      {empty ? (
        <Panel title="Analytics">
          <EmptyState
            icon="chart"
            title="No finished tasks in this range"
            text={
              <>
                Analytics reads the learning records: one per task that ends. Run a batch, or widen
                the range
                {range.id === 'all' ? '' : ' to all time'}.
              </>
            }
          />
        </Panel>
      ) : null}

      {body && charts && figs && !empty ? (
        <>
          <div className="g4">
            {(['spend', 'pass', 'falseStop', 'merged'] as const).map((key) => {
              const f = figs[key];
              return (
                <Kpi
                  key={key}
                  label={f.label}
                  value={f.value}
                  sub={
                    <>
                      {f.sub}
                      <span className="kpi-src" title={`GET /v1/analytics · ${f.source}`}>
                        {f.source}
                      </span>
                    </>
                  }
                />
              );
            })}
          </div>

          <div className="an-grid">
            <Panel
              className="an-wide"
              title="Spend over time"
              aside={`dollar equivalent per ${bucket}, stacked by ${groupName}`}
            >
              {charts.spend.series.length === 0 ? (
                <TightEmpty
                  icon="chart"
                  title="No task could be priced"
                  text="Spend is the dollar equivalent of a task's tokens. It needs a price table in the profile (a subscription has tokens but no price)."
                />
              ) : (
                <ChartFrame
                  legend={charts.spend.series}
                  table={charts.spend.view.table}
                  note={
                    <>
                      ≈ the dollar equivalent of the tokens, not a bill.{' '}
                      {formatCount(body.tasks.total.usd_estimate_known)} of{' '}
                      {formatCount(body.tasks.total.tasks)} tasks could be priced.
                      {log && log.runs > 0
                        ? ` The run log counts ${formatCount(log.runs)} finished runs, ${formatUsd(log.usd)} priced (runs on an API key only).`
                        : ''}
                    </>
                  }
                >
                  <TimeChart
                    labels={charts.labels}
                    series={charts.spend.series}
                    mode="stacked-bars"
                    format={axisUsd}
                    tip={charts.spend.view.tip}
                    label={`Spend per ${bucket}, stacked by ${groupName}`}
                    height={height}
                    stale={stale}
                  />
                </ChartFrame>
              )}
            </Panel>

            <Panel title="Tokens over time" aside={`input plus output per ${bucket}`}>
              <ChartFrame
                legend={charts.tokens.series}
                table={charts.tokens.view.table}
                note="Cache tokens are counted apart, in the next chart."
              >
                <TimeChart
                  labels={charts.labels}
                  series={charts.tokens.series}
                  mode="stacked-bars"
                  format={formatTokens}
                  tip={charts.tokens.view.tip}
                  label={`Tokens per ${bucket}, stacked by ${groupName}`}
                  height={height}
                  stale={stale}
                />
              </ChartFrame>
            </Panel>

            <Panel title="Cache read share of input" aside="higher is cheaper">
              <ChartFrame
                table={charts.cache.table}
                note="Cache reads over input, cache reads and cache writes, all groups together. A bucket with no input is a gap."
              >
                <TimeChart
                  labels={charts.labels}
                  series={charts.cache.series}
                  mode="line"
                  format={axisShare}
                  max={1}
                  tip={charts.cache.tip}
                  label={`Cache read share of input per ${bucket}`}
                  height={height}
                  stale={stale}
                />
              </ChartFrame>
            </Panel>

            {learn.isError && !learn.data ? (
              <PanelError
                title="Pass rate by repository and size"
                call="GET /v1/learn"
                message={learn.error.message}
              />
            ) : !learn.data ? (
              <PanelSkeleton title="Pass rate by repository and size" />
            ) : (
              <Panel
                className="an-wide"
                title="Pass rate by repository and size"
                aside="point and 95% Wilson interval; the counts beside each"
              >
                {pass.rows.length === 0 ? (
                  <TightEmpty
                    icon="chart"
                    title="No finished task"
                    text="No repository has a finished task in this range."
                  />
                ) : (
                  <ChartFrame
                    table={passTable(pass.rows)}
                    className={learn.isPlaceholderData ? 'is-stale' : undefined}
                    note={
                      pass.more > 0
                        ? `The ${pass.rows.length} groups with the most tasks; ${pass.more} more are in Learning. Few tasks, a wide interval: read the count before the percentage.`
                        : 'Few tasks, a wide interval: read the count before the percentage.'
                    }
                  >
                    <PassRateChart rows={pass.rows} />
                  </ChartFrame>
                )}
              </Panel>
            )}

            <Panel title="Duration of finished batches" aside="wall time, started to finished">
              {batchRuns.isError && !batchRuns.data ? (
                <ErrorState
                  title="Cannot read the run list"
                  call="GET /v1/runs"
                  detail={batchRuns.error.message}
                />
              ) : !batchRuns.data ? (
                <Skeleton width="100%" height={140} />
              ) : duration.n === 0 ? (
                <TightEmpty
                  icon="clock"
                  title="No finished batch in this range"
                  text="Durations come from the run list."
                />
              ) : (
                <ChartFrame
                  table={durationTable(duration)}
                  note={
                    <>
                      {formatCount(duration.n)} batches · median {durationText(duration.median)} ·
                      p80 {durationText(duration.p80)}. A task's own duration, and a split by size
                      class, are not in the analytics route.
                    </>
                  }
                >
                  <DurationChart durations={duration} />
                </ChartFrame>
              )}
            </Panel>

            <Panel title="Budget against actual spend" aside="p80 spend as a share of budget">
              {learn.isError && !learn.data ? (
                <ErrorState
                  title="Cannot read the learning report"
                  call="GET /v1/learn"
                  detail={learn.error.message}
                />
              ) : !learn.data || (recent.pending && budgets.length === 0) ? (
                <Skeleton width="100%" height={140} />
              ) : budget.length === 0 ? (
                <TightEmpty
                  icon="gauge"
                  title="No spend or budget to compare"
                  text="No task in this range could be priced, and the latest batches show no budget."
                />
              ) : (
                <ChartFrame
                  table={budgetTable(budget)}
                  note={`Each dot is a repository's 80th-percentile spend for tasks of that size, as a share of the budget a task of that size was last held to (the rule), read from the latest ${recent.runs.length} finished batches.${recent.failed > 0 ? ` ${recent.failed} could not be read.` : ''}`}
                >
                  <BudgetChart rows={budget} />
                </ChartFrame>
              )}
            </Panel>

            <Panel title="Governor false-stop rate" aside="checkpointed tasks that still completed">
              {modes.length === 0 ? (
                <TightEmpty
                  icon="gauge"
                  title="No task hit a checkpoint"
                  text="The governor has nothing to be wrong about yet in this range."
                />
              ) : (
                <ChartFrame
                  table={modeTable(modes)}
                  note="ADR 0045 switches the governor to enforce when false stops stay under 5%. With few checkpointed tasks the interval is far too wide to say: read the count first."
                >
                  <FalseStopChart rows={modes} />
                </ChartFrame>
              )}
              {decisions.length > 0 ? <DecisionBars decisions={decisions} /> : null}
            </Panel>

            <Panel title="Continuations and CI fixer" aside="what happened after a stop">
              <Continuations
                body={body}
                values={charts.continuations}
                note={ciFixerNote(body.not_recorded)}
              />
            </Panel>

            <Panel
              className="an-wide"
              title="Pull requests merged"
              aside={`by the ${bucket} their task ended`}
            >
              {charts.prs.series.length === 0 ? (
                <TightEmpty
                  icon="merge"
                  title="No pull request merged in this range"
                  text={
                    body.tasks.total.prs.total > 0
                      ? `${formatCount(body.tasks.total.prs.total)} opened, none merged yet.`
                      : 'No finished task opened a pull request.'
                  }
                />
              ) : (
                <ChartFrame
                  legend={charts.prs.series}
                  table={charts.prs.view.table}
                  note="The outcome is read back from the forge about hourly for 14 days, so a recent pull request may still show as open. A person merging or editing it is counted under the day its task ended."
                >
                  <TimeChart
                    labels={charts.labels}
                    series={charts.prs.series}
                    mode="stacked-bars"
                    format={formatCount}
                    tip={charts.prs.view.tip}
                    label={`Pull requests merged per ${bucket}, by outcome`}
                    height={height}
                    stale={stale}
                  />
                </ChartFrame>
              )}
            </Panel>

            <Panel className="an-wide" title="What is not recorded">
              <ul className="an-notes">
                {[
                  ...body.not_recorded,
                  'A task’s own duration is not in the learning record, so Analytics shows the wall time of finished batches instead.',
                  'No route serves benchmark results (chargehand bench writes a results file on the machine that ran it), so there is no benchmark comparison here.',
                ].map((line) => (
                  <li key={line}>{line}</li>
                ))}
              </ul>
            </Panel>
          </div>
        </>
      ) : null}
    </div>
  );
}

const durationText = (minutes: number | null) =>
  minutes == null
    ? DASH
    : minutes < 1
      ? '<1 m'
      : minutes < 60
        ? `${Math.round(minutes)} m`
        : `${(minutes / 60).toFixed(1)} h`;
