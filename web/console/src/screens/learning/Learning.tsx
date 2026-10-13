import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { learnQuery } from '../../api/queries';
import { RECENT_BATCHES, useRecentBatches } from '../../api/recent';
import { systemQuery } from '../../api/system';
import { TightEmpty } from '../../charts/TightEmpty';
import { type BudgetInForce, budgetsInForce, describeBudgetSource, SIZES } from '../../lib/budgets';
import {
  DASH,
  DISPLAY_ZONE_LABEL,
  formatCount,
  formatTime,
  formatUsd,
  shortId,
} from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Banner,
  Cell,
  cssVars,
  EmptyState,
  ErrorState,
  HeadCell,
  Panel,
  Segmented,
  SizeBadge,
  Skeleton,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import {
  historyRule,
  LEARN_RANGES,
  type LearnRangeId,
  learnCellSource,
  learnRangeOf,
  learnRows,
  lessons,
  pickLessons,
  rankLessons,
  retros,
} from './model';
import './learning.css';

const COLS = 'minmax(104px, 1.1fr) 36px 44px 64px 64px 80px 52px 72px 64px 84px minmax(120px, 1fr)';

/** The budget a task of each size class was last held to, with the rule that chose it, read from the newest finished batches. */
function BudgetsInForce({
  budgets,
  pending,
  read,
}: {
  budgets: BudgetInForce[];
  pending: boolean;
  read: number;
}) {
  if (pending && budgets.length === 0) {
    return (
      <div className="stack stack--tight" aria-busy="true">
        <Skeleton width="100%" height={20} />
        <Skeleton width="100%" height={20} />
        <Skeleton width="100%" height={20} />
      </div>
    );
  }
  if (budgets.length === 0) {
    return (
      <TightEmpty
        icon="gauge"
        title="No budget seen yet"
        text={`None of the latest ${read} finished batches had a task with a budget. A task has one when the governor is on.`}
      />
    );
  }
  return (
    <div className="list">
      {SIZES.map((size) => {
        const b = budgets.find((x) => x.size === size);
        return (
          <div key={size} className="li" style={cssVars({ '--cols': 'auto minmax(0, 1fr) auto' })}>
            <SizeBadge size={size} />
            {b ? (
              <div className="li__main">
                <div className="b">
                  {formatUsd(b.usd, true)}{' '}
                  <span className="dim">· {describeBudgetSource(b.source)}</span>
                </div>
                <div
                  className="li__meta"
                  title={`GET /v1/batches/{id} · tasks[${b.taskId}].budget`}
                >
                  task {b.taskId} of batch {shortId(b.runId)} · {formatTime(b.at)}
                </div>
              </div>
            ) : (
              <span className="dim">no task of this size in the latest batches</span>
            )}
            {b ? (
              <Link to="/runs/$runId" params={{ runId: b.runId }} className="link">
                Open
              </Link>
            ) : (
              <span />
            )}
          </div>
        );
      })}
    </div>
  );
}

/** What past tasks cost, how they ended, and the budgets chargehand derives from them. */
export function Learning() {
  const phone = useIsPhone();
  const [rangeId, setRangeId] = useState<LearnRangeId>('60d');
  const range = learnRangeOf(rangeId);
  const learn = useQuery({ ...learnQuery(range.since), placeholderData: keepPreviousData });
  const system = useQuery({ ...systemQuery(), refetchInterval: false });
  const recent = useRecentBatches();

  const rule = useMemo(() => historyRule(system.data), [system.data]);
  const groups = learn.data?.groups;
  const rows = useMemo(() => learnRows(groups ?? [], rule), [groups, rule]);
  const budgets = useMemo(() => budgetsInForce(recent.batches), [recent.batches]);
  const found = useMemo(() => retros(recent.batches), [recent.batches]);
  const patterns = useMemo(
    () => pickLessons(rankLessons(lessons(groups ?? [], rule))),
    [groups, rule],
  );
  const retroOn = system.data?.profile?.driven?.learning?.retro;
  const empty = learn.data != null && learn.data.tasks === 0;
  const stale = learn.isPlaceholderData;

  return (
    <div className="screen ln-screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Learning</h1>
          <p className="screen__sub">
            {learn.data
              ? `${formatCount(learn.data.tasks)} finished tasks in ${range.phrase} · what they cost and how they ended, and the budgets chargehand derives from them`
              : learn.isError
                ? 'Not loaded'
                : 'Loading the learning report'}
          </p>
        </div>
        <div className="screen__actions">
          <Segmented
            label="Range"
            touch={phone}
            value={rangeId}
            items={LEARN_RANGES.map((r) => ({ id: r.id, label: r.label }))}
            onValueChange={setRangeId}
          />
        </div>
      </div>

      {learn.isError ? (
        <ErrorState
          title="Cannot read the learning report"
          call={`GET /v1/learn?since=${range.since}`}
          detail={learn.error.message}
          age={
            learn.data
              ? `Showing the last data, from ${formatTime(learn.dataUpdatedAt, { seconds: true })}.`
              : undefined
          }
          onRetry={() => void learn.refetch()}
        />
      ) : null}

      {!learn.data && !learn.isError ? (
        <Panel title="Budgets and outcomes by size class" busy>
          <div className="stack stack--tight">
            <Skeleton width="30%" />
            <Skeleton width="100%" />
            <Skeleton width="100%" />
            <Skeleton width="100%" />
          </div>
        </Panel>
      ) : null}

      {empty ? (
        <Panel title="Budgets and outcomes by size class">
          <EmptyState
            icon="book"
            title="Nothing learned yet"
            text={`The server appends one record per task that ends. A size class needs ${rule.minTasks} finished tasks in ${rule.windowDays} days before its budget comes from history; until then the flat budget of its size class applies.`}
          />
        </Panel>
      ) : null}

      {learn.data && !empty ? (
        <>
          <Banner tone="info" icon="info" title="How budgets are chosen">
            The task's own budget wins; else the p{rule.percentile} of past spend of its size class
            in its repository, when at least {rule.minTasks} tasks finished in the last{' '}
            {rule.windowDays} days; else the same over all repositories; else the flat budget.{' '}
            {rule.from === 'profile'
              ? 'Settings from the profile'
              : 'The documented defaults (only the owner can read the profile)'}
            : min_tasks {rule.minTasks}, window_days {rule.windowDays}, percentile {rule.percentile}
            .
          </Banner>

          <Panel
            title="Budgets and outcomes by size class"
            flush
            aside={`${range.phrase} · dollar equivalent`}
            className={stale ? 'is-stale' : undefined}
          >
            {phone ? (
              <div className="list">
                {rows.map((r) => (
                  <div
                    key={r.key}
                    className="li"
                    style={cssVars({ '--cols': 'minmax(0, 1fr) auto' })}
                  >
                    <div className="li__main">
                      <div className="row">
                        <SizeBadge size={r.size as 'S' | 'M' | 'L'} />
                        <span className="b li__title">{r.repository}</span>
                      </div>
                      <div className="li__meta ln-wrap">
                        {formatCount(r.tasks)} tasks · median {r.median} · completed {r.completed}
                      </div>
                      <div className="li__meta ln-wrap">
                        then done {r.thenDone} · merged as is {r.merged}
                      </div>
                      <div className="li__meta ln-wrap">{r.history}</div>
                    </div>
                    <div className="b ln-right">
                      {r.p80}
                      <div className="dim ln-cap">p80</div>
                    </div>
                  </div>
                ))}
              </div>
            ) : (
              <div className="table-scroll">
                <Table
                  cols={COLS}
                  label="Budgets and outcomes by repository and size class"
                  className="ln-table"
                >
                  <TableHead>
                    <HeadCell>Repository</HeadCell>
                    <HeadCell>Size</HeadCell>
                    <HeadCell num>Tasks</HeadCell>
                    <HeadCell num>Median</HeadCell>
                    <HeadCell num>p80 spend</HeadCell>
                    <HeadCell num>Completed</HeadCell>
                    <HeadCell num>Partial</HeadCell>
                    <HeadCell num>Checkpoint</HeadCell>
                    <HeadCell num>Then done</HeadCell>
                    <HeadCell num>Merged as is</HeadCell>
                    <HeadCell>History rule</HeadCell>
                  </TableHead>
                  {rows.map((r) => (
                    <TableRow key={r.key}>
                      <Cell strong title={r.repository}>
                        {r.repository}
                      </Cell>
                      <Cell>
                        <SizeBadge size={r.size as 'S' | 'M' | 'L'} />
                      </Cell>
                      <Cell num title={learnCellSource(r.group, 'tasks')}>
                        {formatCount(r.tasks)}
                      </Cell>
                      <Cell num title={learnCellSource(r.group, 'spend_median_usd')}>
                        {r.median}
                      </Cell>
                      <Cell num strong title={learnCellSource(r.group, 'spend_p80_usd')}>
                        {r.p80}
                      </Cell>
                      <Cell num title={learnCellSource(r.group, 'completion_rate, completed')}>
                        {r.completed}
                      </Cell>
                      <Cell num title={learnCellSource(r.group, 'partial_rate')}>
                        {r.partial}
                      </Cell>
                      <Cell num title={learnCellSource(r.group, 'checkpoint_rate')}>
                        {r.checkpoint}
                      </Cell>
                      <Cell
                        num
                        title={learnCellSource(r.group, 'checkpointed_completed, checkpointed')}
                      >
                        {r.thenDone}
                      </Cell>
                      <Cell num title={learnCellSource(r.group, 'merged_unchanged_rate, prs')}>
                        {r.merged}
                      </Cell>
                      <Cell title={learnCellSource(r.group, 'spend_known')}>
                        <span className={r.enough ? undefined : 'dim'}>{r.history}</span>
                      </Cell>
                    </TableRow>
                  ))}
                </Table>
              </div>
            )}
            <div className="ln-foot">
              Spend is the dollar equivalent of a task's tokens (nearest-rank percentiles over the
              tasks that could be priced). "Then done" is the false-stop proxy: of the tasks that
              hit a governor checkpoint, how many still completed. "Merged as is" is the share of
              resolved pull requests merged with the head chargehand left; the count is in brackets.
              Small groups are noisy: read the counts first. The server counts finished tasks
              (completed, or stopped at the budget) in the window, and the table counts every priced
              task, so a group can fall a few short of what its row suggests.
            </div>
          </Panel>

          <div className="ln-grid">
            <Panel
              title="Budgets in force"
              aside={`from the latest ${RECENT_BATCHES} finished batches`}
            >
              <BudgetsInForce
                budgets={budgets}
                pending={recent.pending}
                read={recent.runs.length}
              />
            </Panel>

            <Panel title="Lessons" aside="read off the table, not stored by the server">
              {patterns.shown.length === 0 ? (
                <TightEmpty
                  icon="book"
                  title="No pattern stands out"
                  text="No group has enough tasks, or a rate far enough from the rest, to say anything yet."
                />
              ) : (
                <div className="list">
                  {patterns.shown.map((l) => (
                    <article
                      key={l.id}
                      className="li ln-lesson"
                      style={cssVars({ '--cols': 'minmax(0, 1fr)' })}
                    >
                      <div className="ln-lesson__text">{l.text}</div>
                      <div className="dim small">Evidence: {l.evidence}</div>
                      <div className="small">
                        <b>Try:</b> {l.act}
                      </div>
                      <div className="ln-src">{l.source}</div>
                    </article>
                  ))}
                  {patterns.more > 0 ? (
                    <div className="li dim small">
                      {patterns.more} more patterns of the same kinds: the table above holds the
                      counts they stand on.
                    </div>
                  ) : null}
                </div>
              )}
            </Panel>

            <Panel
              className="ln-wide"
              title="Retros"
              aside={<Badge size="s" tone="warn" label="a model's advice" />}
            >
              {recent.pending && found.written.length === 0 ? (
                <Skeleton width="100%" height={60} />
              ) : found.written.length === 0 ? (
                <TightEmpty
                  icon="history"
                  title="No retro in the latest batches"
                  text={
                    <>
                      {retroOn === true
                        ? 'The retro is on (driven.learning.retro): one is written after each batch that ends from now on.'
                        : retroOn === false
                          ? 'The retro is off (driven.learning.retro, the default): turn it on in the profile and one read-only run after each batch writes at most 12 lines.'
                          : 'A retro is written only when the profile turns driven.learning.retro on.'}
                      {found.skipped.length > 0
                        ? ` ${found.skipped.length} of the latest batches skipped it: ${found.skipped[0]?.reason}.`
                        : ''}
                    </>
                  }
                />
              ) : (
                <div className="list">
                  {found.written.map((r) => (
                    <article
                      key={r.runId}
                      className="li ln-retro"
                      style={cssVars({ '--cols': 'minmax(0, 1fr)' })}
                    >
                      <div className="row row--wrap">
                        <Link to="/runs/$runId" params={{ runId: r.runId }} className="mono-b">
                          {shortId(r.runId)}
                        </Link>
                        {r.repository ? <Badge size="s" label={r.repository} /> : null}
                        <span className="dim">
                          {r.at ? `${formatTime(r.at)} ${DISPLAY_ZONE_LABEL}` : DASH}
                        </span>
                        <span className="dim">{formatCount(r.tasks)} task records</span>
                      </div>
                      <ul className="ln-lines">
                        {r.lines.map((line) => (
                          <li key={line}>{line}</li>
                        ))}
                      </ul>
                      <div className="ln-src">batch/v1 · retro.text</div>
                    </article>
                  ))}
                </div>
              )}
              <div className="ln-foot">
                A retro is one read-only run after a batch (off by default). It costs a run outside
                the batch's caps, can be wrong, and never changes the batch's status.
              </div>
            </Panel>
          </div>
        </>
      ) : null}
    </div>
  );
}
