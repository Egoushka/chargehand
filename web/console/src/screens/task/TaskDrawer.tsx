import { Dialog } from '../../ui';
import { TaskView } from './TaskView';

/** The task over the batch, on the right: modal, so the page behind is inert, Esc closes it and the focus goes back to what opened it. */
export function TaskDrawer({
  batchId,
  taskId,
  onClose,
  onOpenPage,
}: {
  batchId: string;
  taskId: string;
  onClose: () => void;
  onOpenPage: () => void;
}) {
  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      title={`Task ${taskId}`}
      hideTitle
      placement="end"
    >
      <TaskView batchId={batchId} taskId={taskId} mode="drawer" onOpenPage={onOpenPage} />
    </Dialog>
  );
}
