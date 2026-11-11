import { useMutation, useQueryClient } from '@tanstack/react-query';
import { postJson } from './client';

/** What the action routes answer (`202`, or `200` with `changed: false` when a repeat changed nothing). A refusal is a `409` problem body. */
export interface ActionResult {
  batch_run_id?: string;
  task_id?: string;
  action: 'cancel' | 'retry' | 'session_stop' | 'checks_rerun' | 'merge';
  changed: boolean;
  state?: string;
  /** `retry`: the new batch. */
  run_id?: string;
  continue_from?: boolean;
  /** `checks_rerun`. */
  checks?: string[];
  rerun_runs?: number;
}

const task = (batch: string, id: string) =>
  `/v1/batches/${encodeURIComponent(batch)}/tasks/${encodeURIComponent(id)}`;

export const cancelTask = (batch: string, id: string) =>
  postJson<ActionResult>(`${task(batch, id)}/cancel`);
export const retryTask = (batch: string, id: string) =>
  postJson<ActionResult>(`${task(batch, id)}/retry`);
export const stopSession = (batch: string, id: string) =>
  postJson<ActionResult>(`${task(batch, id)}/session/stop`);
export const rerunChecks = (owner: string, repo: string, pull: number) =>
  postJson<ActionResult>(
    `/v1/pulls/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/${pull}/checks/rerun`,
  );

/** Merge a pull request chargehand opened (the owner's key only). A `409` carries the reason the integrator's policy or the forge's rules refuse. */
export const mergePull = (owner: string, repo: string, pull: number) =>
  postJson<ActionResult>(
    `/v1/pulls/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/${pull}/merge`,
  );

export function useMergePull() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (p: { owner: string; repo: string; pull: number }) =>
      mergePull(p.owner, p.repo, p.pull),
    onSettled: () =>
      Promise.all(
        ['batch-task', 'batch', 'inbox', 'overview', 'pulls'].map((key) =>
          queryClient.invalidateQueries({ queryKey: [key] }),
        ),
      ),
  });
}
