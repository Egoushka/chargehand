import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { batchQuery } from '../../api/batch';
import { ApiError } from '../../api/client';
import { meQuery, useCancelRuns } from '../../api/queries';
import type { RunSummary } from '../../api/types';
import {
  baseName,
  formatDateTime,
  formatDuration,
  formatTokens,
  formatUsd,
} from '../../lib/format';
import { runWord } from '../../lib/status';
import { useNow } from '../../lib/useNow';
import { useShell } from '../../shell/ShellContext';
import { useToast } from '../../shell/toasts';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Button,
  CapMeter,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  Icon,
  Panel,
  SegmentedMeter,
  Skeleton,
  StatusPill,
  TabPanel,
  Tabs,
} from '../../ui';
import { Elapsed } from '../overview/Elapsed';
import { TaskDrawer } from '../task/TaskDrawer';
import { ArtifactsTab } from './ArtifactsTab';
import { EventsTab } from './EventsTab';
import { GovernorTab } from './GovernorTab';
import { batchElapsed, batchTotals, governorSummary, progressOf } from './model';
import { PullsTab } from './PullsTab';
import { RequestTab } from './RequestTab';
import { SpendTab } from './SpendTab';
import { TasksTab } from './TasksTab';
import { TimelineTab } from './TimelineTab';
import { TABS, type TabId } from './tabs';
import './batch.css';

/** How many of the batch's newest events the batch view carries; the live stream brings the rest. */
const EVENTS = 500;

export interface BatchProps {
  /** The run as the list gave it: enough to draw the header before the batch view arrives. */
  run: RunSummary;
  tab: TabId;
  /** The task whose drawer is open. */
  taskId: string | null;
  onTab: (tab: TabId) => void;
  /** Open a task (null closes). On a phone a task is a page of its own. */
  onTask: (taskId: string | null) => void;
  /** Opens the task as a page of its own (the drawer's "open as a page"). */
  onOpenTaskPage: (taskId: string) => void;
}

/** One batch: where it stands, what it has used against its caps, and its tasks, timeline, events, governor, spend, pull requests, artifacts and request. */
export function Batch({ run, tab, taskId, onTab, onTask, onOpenTaskPage }: BatchProps) {
  const phone = useIsPhone();
  const toast = useToast();
  const owner = useQuery(meQuery()).data?.owner === true;
  const cancel = useCancelRuns();
  const [confirming, setConfirming] = useState(false);
  const tabsTop = useRef<HTMLDivElement>(null);
  const shownTab = useRef(tab);
  // On a phone the header fills the first screen: when the tab changes, bring the tab bar to the top so the section shows.
  useEffect(() => {
    if (shownTab.current === tab) return;
    shownTab.current = tab;
    if (phone) tabsTop.current?.scrollIntoView({ block: 'start' });
  }, [tab, phone]);
  const now = useNow(1000);
  const query = useQuery({
    ...batchQuery(run.run_id, EVENTS),
    placeholderData: keepPreviousData,
    // A batch the server accepted and has not started has no view yet: ask again until it has.
    refetchInterval: (q) =>
      q.state.error && (run.status === 'queued' || run.status === 'running')
        ? 3_000
        : q.state.data?.run.status === 'running' || q.state.data?.run.status === 'queued'
          ? 5_000
          : false,
  });
  const batch = query.data;
  const live = batch?.run ?? run;
  const word = runWord(live);
  const running = live.status === 'running' || live.status === 'queued';
  const runningTasks = batch?.tasks.filter((t) => t.state === 'running').length ?? 0;

  const doCancel = () => {
    setConfirming(false);
    cancel.mutate([run.run_id], {
      onSuccess: ([outcome]) =>
        toast.push(
          outcome?.result === 'cancelling'
            ? {
                tone: 'ok',
                icon: 'check',
                title: 'Cancelling the batch',
                body: 'It ends failed with the code cancelled. Nothing from a stopped task is pushed.',
              }
            : {
                tone: 'warn',
                icon: 'alert-triangle',
                title: 'The batch was not cancelled',
                body:
                  outcome?.result === 'finished'
                    ? 'It had finished, or another process runs it.'
                    : (outcome?.message ?? 'The server could not cancel it.'),
              },
        ),
      onError: (error) =>
        toast.push({
          tone: 'danger',
          icon: 'alert-triangle',
          title: 'Cannot cancel',
          body: error.message,
        }),
    });
  };

  const header = (
    <div className="screen__head">
      <div className="stack stack--tight head-main">
        <div className="row row--wrap">
          <StatusPill status={word} size="lg" />
          <h1 className="screen__title mono-b batch-id">{run.run_id}</h1>
        </div>
        <div className="row row--wrap dim">
          {live.repository ? (
            <Badge label={baseName(live.repository)} title={live.repository} />
          ) : null}
          {live.commit ? <span className="mono">@{live.commit.slice(0, 7)}</span> : null}
          <span>·</span>
          <span>{live.preset}</span>
          {live.actor ? (
            <>
              <span>·</span>
              <span>account {live.actor}</span>
            </>
          ) : null}
          <span>·</span>
          <span title={formatDateTime(live.started_at)}>
            started {formatDateTime(live.started_at)}
          </span>
          <span>·</span>
          <span>
            elapsed{' '}
            <b className="elapsed">
              {running ? (
                <Elapsed since={live.started_at} />
              ) : batch ? (
                formatDuration(batchElapsed(batch, now))
              ) : (
                '—'
              )}
            </b>
          </span>
          {live.error_code ? <Badge size="s" tone="danger" label={live.error_code} /> : null}
        </div>
      </div>
      <div className="screen__actions">
        {batch ? (
          <Button
            variant="quiet"
            icon="copy"
            label="Clone"
            className="hide-phone"
            onClick={() => onTab('request')}
          />
        ) : null}
        {running && owner ? <HaltButton /> : null}
        {running ? (
          <Button
            variant="danger-quiet"
            label="Cancel batch"
            loading={cancel.isPending}
            onClick={() => setConfirming(true)}
          />
        ) : null}
      </div>
    </div>
  );

  if (!batch) {
    // The view is not here yet: loading, a batch not started, or a read that failed.
    return (
      <div className="screen">
        {header}
        {query.isError ? (
          query.error instanceof ApiError && query.error.status === 404 ? (
            <Panel title="Waiting to start">
              <EmptyState
                icon="clock"
                title="The batch has not started"
                text="The server accepted it and has not begun its tasks, so there is no view of them yet. This page asks again every few seconds."
              />
            </Panel>
          ) : (
            <ErrorState
              title="Cannot read the batch"
              call={`GET /v1/batches/${run.run_id}`}
              detail={query.error.message}
              onRetry={() => void query.refetch()}
            />
          )
        ) : (
          <div className="stack" aria-busy="true">
            <Skeleton width="100%" height={48} />
            <Skeleton width="100%" height={32} />
            <Skeleton width="100%" height={240} />
          </div>
        )}
      </div>
    );
  }

  const totals = batchTotals(batch);
  const progress = progressOf(batch);
  const governor = governorSummary(batch.tasks);
  const tokenPct = totals.tokens.cap
    ? Math.round(((totals.tokens.used ?? 0) / totals.tokens.cap) * 100)
    : null;
  const usdPct = totals.usd.cap
    ? Math.round(((totals.usd.used ?? 0) / totals.usd.cap) * 100)
    : null;
  const counts: Partial<Record<TabId, number>> = {
    tasks: batch.tasks.length,
    governor: governor.decisions,
    prs: batch.pull_requests.length,
  };
  const content: Record<TabId, () => React.ReactNode> = {
    tasks: () => <TasksTab batch={batch} selected={taskId} onSelect={(id) => onTask(id)} />,
    timeline: () => <TimelineTab batch={batch} onOpenTask={(id) => onTask(id)} />,
    events: () => <EventsTab batch={batch} />,
    governor: () => <GovernorTab batch={batch} />,
    spend: () => <SpendTab batch={batch} />,
    prs: () => <PullsTab batch={batch} />,
    artifacts: () => <ArtifactsTab batch={batch} initialTask={taskId} />,
    request: () => <RequestTab batch={batch} />,
  };
  return (
    <div className="screen">
      {header}
      {query.isError ? (
        <ErrorState
          title="Cannot refresh the batch"
          call={`GET /v1/batches/${run.run_id}`}
          detail={query.error.message}
          age={`The tabs show the data from ${formatDateTime(query.dataUpdatedAt)}.`}
          onRetry={() => void query.refetch()}
        />
      ) : null}
      {batch.result?.error ? (
        <div className="errbox" role="alert">
          <Icon name="alert-triangle" />
          <div className="stack stack--tight">
            <div className="b">{batch.result.error.code}</div>
            <div className="dim">
              {batch.result.error.message}
              {batch.result.error.action ? ` ${batch.result.error.action}` : ''}
            </div>
          </div>
        </div>
      ) : null}
      <div className="g2e caps">
        <CapMeter
          label="Tokens, input plus output"
          value={totals.tokens.used ?? 0}
          cap={totals.tokens.cap}
          text={
            totals.tokens.cap
              ? `${formatTokens(totals.tokens.used)} of ${formatTokens(totals.tokens.cap)} · ${tokenPct}%`
              : `${formatTokens(totals.tokens.used)} · no cap`
          }
        />
        <CapMeter
          label="Dollar equivalent"
          value={totals.usd.used ?? 0}
          cap={totals.usd.cap}
          text={
            totals.usd.cap
              ? `${formatUsd(totals.usd.used, totals.usd.estimated)} of ${formatUsd(totals.usd.cap)} · ${usdPct}%`
              : `${formatUsd(totals.usd.used, totals.usd.estimated)} · no cap`
          }
        />
        <div className="stack stack--tight">
          <div className="meter-head">
            <span>Tasks</span>
            <span className="b">
              {progress.done} of {progress.total} done
            </span>
          </div>
          <SegmentedMeter segments={progress.segments} label={progress.label} />
        </div>
      </div>
      <div ref={tabsTop} className="tabs-top">
        <Tabs
          label="Batch sections"
          touch={phone}
          value={tab}
          onValueChange={(id) => {
            onTab(id as TabId);
            // On a phone the header fills the first screen: bring the tab bar to the top so the chosen section shows.
            if (phone) tabsTop.current?.scrollIntoView({ block: 'start' });
          }}
          items={TABS.map((t) => ({ id: t.id, label: t.label, count: counts[t.id] }))}
        >
          {TABS.map((t) => (
            <TabPanel key={t.id} value={t.id}>
              <div className="tab-body">{t.id === tab ? content[t.id]() : null}</div>
            </TabPanel>
          ))}
        </Tabs>
      </div>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Cancel ${live.run_id.slice(-6)}?`}
        text={`${runningTasks} running ${runningTasks === 1 ? 'task stops' : 'tasks stop'}. Tasks that have not started never do, and nothing from a stopped task is pushed. The batch ends failed with the code cancelled.`}
        keepLabel="Keep running"
        confirmLabel="Cancel batch"
        onConfirm={doCancel}
      />
      {taskId && !phone ? (
        <TaskDrawer
          batchId={run.run_id}
          taskId={taskId}
          onClose={() => onTask(null)}
          onOpenPage={() => onOpenTaskPage(taskId)}
        />
      ) : null}
    </div>
  );
}

function HaltButton() {
  const shell = useShell();
  return (
    <Button
      variant="quiet"
      label="Halt server"
      loading={shell.haltBusy}
      onClick={shell.requestHalt}
    />
  );
}
