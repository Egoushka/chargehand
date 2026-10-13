import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { runSummaryQuery } from '../../api/queries';
import { useIsPhone } from '../../shell/useIsPhone';
import { buttonClass, EmptyState, ErrorState, Skeleton } from '../../ui';
import { Batch } from '../batch/Batch';
import type { TabId } from '../batch/tabs';
import { Research } from '../research/Research';
import { TaskView } from '../task/TaskView';
import '../task/task.css';

/** `/runs/<id>`: a batch has its own screen, any other run is a research result. The run list says which. */
export function RunPage() {
  const { runId } = useParams({ from: '/runs/$runId' });
  const search = useSearch({ from: '/runs/$runId' });
  const navigate = useNavigate();
  const phone = useIsPhone();
  const summary = useQuery(runSummaryQuery(runId));
  const tab: TabId = search.tab ?? 'tasks';

  if (summary.isPending)
    return (
      <div className="screen" aria-busy="true">
        <Skeleton width="50%" height={28} />
        <Skeleton width="100%" height={48} />
        <Skeleton width="100%" height={240} />
      </div>
    );
  if (summary.isError)
    return (
      <div className="screen">
        <ErrorState
          title="Cannot read the run"
          call={`GET /v1/runs?run_id=${runId}`}
          detail={summary.error.message}
          onRetry={() => void summary.refetch()}
        />
      </div>
    );
  const run = summary.data;
  if (!run)
    return (
      <div className="screen">
        <div className="card">
          <EmptyState
            icon="search"
            title="No such run"
            text="The server has no run with this id, or it belongs to another account."
            action={
              <Link to="/runs" className={buttonClass()}>
                Back to Runs
              </Link>
            }
          />
        </div>
      </div>
    );
  if (run.kind !== 'batch') return <Research run={run} />;
  // On a phone a task is a page of its own, not a drawer over the batch.
  if (search.task && phone)
    return (
      <div className="screen">
        <div className="card">
          <TaskView batchId={runId} taskId={search.task} mode="page" />
        </div>
      </div>
    );
  const taskPage = (taskId: string) =>
    void navigate({ to: '/runs/$runId/tasks/$taskId', params: { runId, taskId } });
  return (
    <Batch
      run={run}
      tab={tab}
      taskId={search.task ?? null}
      onTab={(next) =>
        void navigate({
          to: '/runs/$runId',
          params: { runId },
          search: (prev) => ({ ...prev, tab: next }),
          replace: true,
        })
      }
      onTask={(task) => {
        if (task && phone) return taskPage(task);
        void navigate({
          to: '/runs/$runId',
          params: { runId },
          search: (prev) => ({ ...prev, task: task ?? undefined }),
          replace: task == null,
        });
      }}
      onOpenTaskPage={taskPage}
    />
  );
}
