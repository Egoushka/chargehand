import { useParams } from '@tanstack/react-router';
import { TaskView } from '../task/TaskView';
import '../task/task.css';

/** `/runs/<batch>/tasks/<task>`: the task as a page, for a link or a phone. */
export function TaskPage() {
  const { runId, taskId } = useParams({ from: '/runs/$runId/tasks/$taskId' });
  return (
    <div className="screen">
      <div className="card page-task">
        <TaskView batchId={runId} taskId={taskId} mode="page" />
      </div>
    </div>
  );
}
