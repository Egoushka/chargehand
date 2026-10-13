import type { BatchBody } from '../../api/batch';
import { describeBudgetSource } from '../../lib/budgets';
import { formatTime, formatUsd } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Banner,
  CapMeter,
  Cell,
  EmptyState,
  HeadCell,
  Kpi,
  LogLine,
  Panel,
  SizeBadge,
  Table,
  TableHead,
  TableRow,
  TableScroll,
} from '../../ui';
import {
  eventDetail,
  eventKind,
  governorRows,
  governorSummary,
  notesAbout,
  taskShare,
  taskUsd,
} from './model';
import { NotRecorded } from './NotRecorded';

const COLS = 'minmax(160px,1.4fr) 90px 110px minmax(150px,1fr) 80px 80px minmax(140px,1fr)';

/** What the governor saw: its mode, the budget each task was held to, and the decisions it took, as the server keeps them (counts per signal and rung). */
export function GovernorTab({ batch }: { batch: BatchBody }) {
  const phone = useIsPhone();
  const summary = governorSummary(batch.tasks);
  const rows = governorRows(batch.tasks);
  const budgeted = batch.tasks.filter((t) => t.budget?.usd != null && taskUsd(t) != null);
  const decisionEvents = batch.timeline.available
    ? batch.timeline.events.filter((e) => eventKind(e) === 'governor')
    : [];
  const recorded = batch.tasks.some((t) => t.governor);
  const mode = summary.modes.length === 1 ? summary.modes[0] : null;
  const falseStop =
    summary.checkpointedTasks > 0
      ? `${summary.checkpointedCompleted} of ${summary.checkpointedTasks}`
      : '—';
  return (
    <div className="stack">
      {!recorded ? (
        <Banner tone="info" icon="info" title="No governor record yet">
          The governor's record is written when a task ends. Until one has, the live events below
          are all there is.
        </Banner>
      ) : mode === 'observe' ? (
        <Banner tone="info" icon="info" title="Governor mode: observe">
          Decisions are logged and none is acted on. A false stop is a task that was checkpointed
          and still completed; the proxy below counts them.
        </Banner>
      ) : mode === 'enforce' ? (
        <Banner tone="danger" icon="shield" title="Governor mode: enforce">
          A stop decision ends the session; a checkpoint hands the task to a fresh session with what
          it had done.
        </Banner>
      ) : summary.modes.length > 1 ? (
        <Banner
          tone="warn"
          icon="info"
          title={`Governor modes differ: ${summary.modes.join(', ')}`}
        >
          Tasks of this batch ran under different modes.
        </Banner>
      ) : null}
      <div className="g4">
        <Kpi
          label="Decisions"
          value={summary.decisions}
          sub={`${summary.warn} warn, ${summary.checkpoint} checkpoint, ${summary.stop} stop`}
        />
        <Kpi
          label="Tasks with a checkpoint"
          value={summary.checkpointedTasks}
          sub={`${summary.acted} acted on`}
        />
        <Kpi label="False-stop proxy" value={falseStop} sub="checkpointed, then completed" />
        <Kpi
          label="Budget sources"
          value={summary.sources.length}
          sub={summary.sources.map(([source, n]) => `${source} ${n}`).join(', ') || 'none yet'}
        />
      </div>
      <Panel title="Budget per task" count={budgeted.length}>
        {budgeted.length === 0 ? (
          <EmptyState
            icon="gauge"
            title="No task has a budget and spend yet"
            text="A task's budget is chosen once, when it starts; its spend shows as it runs."
          />
        ) : (
          <div className="stack">
            {budgeted.map((task) => {
              const share = Math.round(taskShare(task) ?? 0);
              return (
                <CapMeter
                  key={task.id}
                  label={`Budget of ${task.id}`}
                  value={taskUsd(task) ?? 0}
                  cap={task.budget?.usd}
                  ticks={[85]}
                  text={`${formatUsd(taskUsd(task), true)} of ${formatUsd(task.budget?.usd)} · ${share}%`}
                  head={
                    <>
                      <b className="mono-b meter-name">{task.id}</b>{' '}
                      {task.size ? <SizeBadge size={task.size} /> : null} ·{' '}
                      {task.budget ? describeBudgetSource(task.budget.source) : 'no budget'}
                    </>
                  }
                />
              );
            })}
            <div className="dim small">
              The tick marks 85% (checkpoint) and the end of the bar marks 100% (stop). The budget
              is a dollar equivalent chosen once, when the task starts.
            </div>
          </div>
        )}
      </Panel>
      <Panel title="Decisions" count={rows.length} flush>
        {rows.length === 0 ? (
          <EmptyState
            icon="shield-check"
            title="The governor took no decision"
            text={
              recorded
                ? 'No task of this batch reached a warning, a checkpoint or a stop.'
                : 'Nothing has been recorded yet.'
            }
          />
        ) : phone ? (
          <ul className="list-reset">
            {rows.map((r) => (
              <li key={`${r.task}|${r.signal}|${r.action}`} className="li">
                <div className="row row--wrap">
                  <Badge size="s" tone={r.action === 'stop' ? 'danger' : 'warn'} label={r.action} />
                  <span className="mono">{r.signal}</span>
                  <span className="dim">× {r.count}</span>
                </div>
                <div className="li__title">{r.task}</div>
                <div className="li__meta">
                  {r.mode} · {r.acted} acted on{r.stopReason ? ` · ${r.stopReason}` : ''}
                </div>
              </li>
            ))}
          </ul>
        ) : (
          <TableScroll label="Governor decisions">
            <Table cols={COLS} label="Governor decisions">
              <TableHead>
                <HeadCell>Task</HeadCell>
                <HeadCell>Mode</HeadCell>
                <HeadCell>Action</HeadCell>
                <HeadCell>Signal</HeadCell>
                <HeadCell num>Count</HeadCell>
                <HeadCell num>Acted on</HeadCell>
                <HeadCell>Stop reason</HeadCell>
              </TableHead>
              {rows.map((r) => (
                <TableRow key={`${r.task}|${r.signal}|${r.action}`}>
                  <Cell mono title={r.task}>
                    {r.task}
                  </Cell>
                  <Cell>{r.mode}</Cell>
                  <Cell>
                    <Badge
                      size="s"
                      tone={r.action === 'stop' ? 'danger' : 'warn'}
                      label={r.action}
                    />
                  </Cell>
                  <Cell mono>{r.signal}</Cell>
                  <Cell num>{r.count}</Cell>
                  <Cell num>{r.acted}</Cell>
                  <Cell className="dim">{r.stopReason ?? '—'}</Cell>
                </TableRow>
              ))}
            </Table>
          </TableScroll>
        )}
      </Panel>
      {decisionEvents.length > 0 ? (
        <Panel title="Live decision events" count={decisionEvents.length} flush>
          {/* biome-ignore lint/a11y/noNoninteractiveTabindex: a log that scrolls must be reachable by keyboard */}
          <div className="ch-stream events-pane events-pane--short" tabIndex={0}>
            {decisionEvents.map((e) => (
              <LogLine
                key={`${e.at}|${e.task_id}|${e.detail}`}
                time={formatTime(e.at, { seconds: true })}
                kind="governor"
                task={e.task_id}
                detail={eventDetail(e)}
                tone={/checkpoint|stop/.test(e.detail ?? '') ? 'warn' : undefined}
                wrap={phone}
              />
            ))}
          </div>
        </Panel>
      ) : null}
      <NotRecorded notes={notesAbout(batch.not_recorded, 'governor')} />
    </div>
  );
}
