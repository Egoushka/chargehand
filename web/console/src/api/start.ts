import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, problems } from './client';

/** The server said no to a start: its status, the lines it sent, and the code of an account refusal (`repository_not_allowed`, `budget_exceeded`, ...). */
export class StartRefused extends ApiError {
  readonly code: string | null;
  constructor(status: number, errors: string[], code: string | null) {
    super(status, errors);
    this.name = 'StartRefused';
    this.code = code;
  }
}

/**
 * Starts a batch: `POST /v1/runs` with `Prefer: wait=0`, so the answer is the new run's id (a `202` with its `Location`) and the
 * batch goes on in the background. A `400` names what the request got wrong, a `403` or `429` is the account's refusal, a `503` is
 * a halted server.
 */
export function useStartBatch() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: unknown): Promise<{ runId: string }> => {
      const reply = await call<{ run_id?: string; error?: { code?: string } }>('/v1/runs', {
        method: 'POST',
        body: request,
        headers: { Prefer: 'wait=0' },
      });
      if (!reply.ok)
        throw new StartRefused(reply.status, problems(reply), reply.data?.error?.code ?? null);
      const runId = reply.location?.split('/').pop() || reply.data?.run_id;
      if (!runId)
        throw new ApiError(reply.status, ['The server accepted the batch but sent no run id.']);
      return { runId };
    },
    onSuccess: () => {
      for (const key of ['runs', 'overview', 'inbox', 'me'])
        void queryClient.invalidateQueries({ queryKey: [key] });
    },
  });
}
