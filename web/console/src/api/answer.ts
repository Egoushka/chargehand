import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError, call, problems } from './client';
import type { AnswerHint } from './common';

/**
 * The request that answers a question: a driven task that continues the one that asked, with the answer as sent (ADR 0045).
 * The route, repository and preset are the ones the inbox item carried; nothing here guesses them.
 */
export function answerRequest(hint: AnswerHint, answer: string) {
  const { run_id, task_id } = hint.continue_from;
  return {
    contract_version: 'request/v1',
    text: `Answer for task ${task_id} of ${run_id}`,
    context: {
      interactive: false,
      preset: hint.preset,
      ...(hint.repository ? { repository: hint.repository } : {}),
    },
    driven: { tasks: [{ id: task_id, continue_from: hint.continue_from, answers: [answer] }] },
  };
}

/** Sends an answer. The batch it starts is the reply's run id; the question leaves the inbox once that batch has begun. */
export function useAnswer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ hint, answer }: { hint: AnswerHint; answer: string }) => {
      // The hint is data from the server: it may name a route of the API and nothing else.
      if (hint.method !== 'POST' || !hint.path.startsWith('/v1/'))
        throw new ApiError(0, ['This question carries a request the console will not send.']);
      const reply = await call(hint.path, {
        method: hint.method,
        body: answerRequest(hint, answer.trim()),
        headers: { Prefer: 'wait=0' },
      });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return { runId: reply.location?.split('/').pop() ?? null };
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['inbox'] });
      void queryClient.invalidateQueries({ queryKey: ['overview'] });
      void queryClient.invalidateQueries({ queryKey: ['runs'] });
    },
  });
}
