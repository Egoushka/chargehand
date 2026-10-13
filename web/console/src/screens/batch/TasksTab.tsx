import { lazy, Suspense } from 'react';
import type { BatchBody, BatchTaskBody } from '../../api/batch';
import { capLevel, DASH, formatDuration, formatTokens, formatUsd } from '../../lib/format';
import { safeHttpUrl } from '../../lib/url';
import { useNow } from '../../lib/useNow';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Cell,
  cssVars,
  EmptyState,
  HeadCell,
  Panel,
  SizeBadge,
  Skeleton,
  StatusPill,
  Table,
  TableHead,
  TableRow,
  TableScroll,
} from '../../ui';
import { Elapsed } from '../overview/Elapsed';
import { taskElapsed, taskMeta, taskShare, taskTokens, taskUsd, taskWord } from './model';

// Loaded when the Tasks tab opens: the graph brings React Flow and its layout with it.
const TaskGraph = lazy(() => import('./graph/TaskGraph'));

const COLS = '28px minmax(160px,1.4fr) 112px minmax(100px,1fr) 128px 68px 150px 72px 76px';

export function TasksTab({
  batch,
  selected,
  onSelect,
}: {
  batch: BatchBody;
  selected: string | null;
  onSelect: (taskId: string) => void;
}) {
  const tasks = batch.tasks;
  const phone = useIsPhone();
  const planning =
    batch.limits.plan === true
      ? 'planned: sizes assigned'
      : batch.limits.plan === false
        ? 'no planning step'
        : 'planning follows the profile';
  return (
    <div className="stack">
      <Panel
        title="Task graph"
        count={tasks.length}
        flush
        aside={<span className="dim">edges are depends_on</span>}
      >
        {tasks.length === 0 ? (
          <EmptyState
            icon="layers"
            title="No tasks yet"
            text="The batch has not named its tasks."
          />
        ) : (
          <Suspense fallback={<Skeleton width="100%" height={220} />}>
            <TaskGraph tasks={tasks} selected={selected} onSelect={onSelect} />
          </Suspense>
        )}
      </Panel>
      <Panel title="Tasks" flush aside={<Badge size="s" tone="info" label={planning} />}>
        {phone ? (
          <ul className="list-reset">
            {tasks.map((task) => (
              <TaskCard key={task.id} task={task} all={tasks} onSelect={onSelect} />
            ))}
          </ul>
        ) : (
          <TableScroll label="Tasks">
            <Table cols={COLS} label="Tasks" className="tasks-table">
              <TableHead>
                <HeadCell>Sz</HeadCell>
                <HeadCell>Task</HeadCell>
                <HeadCell>State</HeadCell>
                <HeadCell>Depends on</HeadCell>
                <HeadCell>Stage</HeadCell>
                <HeadCell num>Tokens</HeadCell>
                <HeadCell>Spend of budget</HeadCell>
                <HeadCell>PR</HeadCell>
                <HeadCell num>Elapsed</HeadCell>
              </TableHead>
              {tasks.map((task) => (
                <TaskRow
                  key={task.id}
                  task={task}
                  all={tasks}
                  selected={task.id === selected}
                  onSelect={onSelect}
                />
              ))}
            </Table>
          </TableScroll>
        )}
      </Panel>
    </div>
  );
}

function PrLink({ task }: { task: BatchTaskBody }) {
  const url = safeHttpUrl(task.pr_url ?? task.pull_request?.url);
  const number = task.pull_request?.number;
  if (!url) return <span className="dim">{DASH}</span>;
  return (
    <a href={url} target="_blank" rel="noreferrer noopener" title={url}>
      {number ? `#${number}` : 'PR'}
    </a>
  );
}

function TaskRow({
  task,
  all,
  selected,
  onSelect,
}: {
  task: BatchTaskBody;
  all: readonly BatchTaskBody[];
  selected: boolean;
  onSelect: (id: string) => void;
}) {
  const word = taskWord(task, all);
  const share = taskShare(task);
  const usd = taskUsd(task);
  const now = useNow(5000);
  const elapsed = taskElapsed(task, now);
  return (
    <TableRow
      selected={selected}
      onClick={(event) => {
        if (!(event.target as HTMLElement).closest('a, button')) onSelect(task.id);
      }}
    >
      <Cell>{task.size ? <SizeBadge size={task.size} /> : DASH}</Cell>
      <Cell strong title={task.id}>
        <button type="button" className="link mono-b ch-trunc" onClick={() => onSelect(task.id)}>
          {task.id}
        </button>
      </Cell>
      <Cell>
        <StatusPill status={word} bare />
      </Cell>
      <Cell className="dim" title={task.depends_on.join(', ')}>
        {task.depends_on.length > 0 ? task.depends_on.join(', ') : DASH}
      </Cell>
      <Cell className="dim">{taskMeta(task, all)}</Cell>
      <Cell num>{formatTokens(taskTokens(task))}</Cell>
      <Cell>
        {usd == null ? (
          <span className="dim">{DASH}</span>
        ) : (
          <div className="budget">
            <div
              className="ch-meter ch-meter--sm"
              data-level={share == null ? undefined : capLevel(Math.round(share))}
              aria-hidden="true"
            >
              <div className="ch-meter__track">
                <div
                  className="ch-meter__fill"
                  style={cssVars({ '--p': Math.min(100, Math.round(share ?? 0)) })}
                />
              </div>
            </div>
            <span className="dim small">
              {formatUsd(usd)}
              {task.budget?.usd != null ? ` of ${formatUsd(task.budget.usd)}` : ''}
            </span>
          </div>
        )}
      </Cell>
      <Cell>
        <PrLink task={task} />
      </Cell>
      <Cell num>
        {task.state === 'running' && task.started_at ? (
          <Elapsed since={task.started_at} />
        ) : elapsed != null ? (
          formatDuration(elapsed)
        ) : (
          DASH
        )}
      </Cell>
    </TableRow>
  );
}

function TaskCard({
  task,
  all,
  onSelect,
}: {
  task: BatchTaskBody;
  all: readonly BatchTaskBody[];
  onSelect: (id: string) => void;
}) {
  const usd = taskUsd(task);
  return (
    <li className="li task-card">
      <div className="row row--wrap">
        {task.size ? <SizeBadge size={task.size} /> : null}
        <button
          type="button"
          className="link mono-b ch-trunc"
          onClick={() => onSelect(task.id)}
          aria-label={`Open task ${task.id}`}
        >
          {task.id}
        </button>
        <span className="spacer" />
        <StatusPill status={taskWord(task, all)} bare />
      </div>
      <div className="li__meta task-card__meta">
        {taskMeta(task, all)} · {formatTokens(taskTokens(task))} tokens ·{' '}
        {usd == null ? DASH : formatUsd(usd)}
        {task.pr_url ? (
          <>
            {' · '}
            <PrLink task={task} />
          </>
        ) : null}
      </div>
    </li>
  );
}
