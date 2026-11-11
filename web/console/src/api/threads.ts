import { queryOptions } from '@tanstack/react-query';
import { getJson } from './client';

/** One thread's card (`GET /v1/threads` leaves `text` out; `GET /v1/threads/{id}/card` carries it). */
export interface ThreadCard {
  thread: string;
  goal: string | null;
  state: 'open' | 'closed';
  scope: string;
  last_step: { seq: number; time: string; done: string; actor: { id?: string; name?: string } };
  next: string | null;
  owner: { id?: string; name?: string };
  last_activity: string;
  steps: number;
  refs: string[];
  /** Markdown the server composed; shown as text, never as markup. */
  text?: string;
}

/** The open threads, newest activity first. The list is read again every few seconds, as the conversations are. */
export const threadsQuery = () =>
  queryOptions({
    queryKey: ['threads'],
    queryFn: ({ signal }) => getJson<{ threads: ThreadCard[] }>('/v1/threads?state=open', signal),
    select: (body) =>
      body.threads
        .filter((t) => t.state === 'open')
        .sort((a, b) => b.last_activity.localeCompare(a.last_activity)),
    refetchInterval: 15_000,
  });

export const threadCardQuery = (id: string) =>
  queryOptions({
    queryKey: ['threads', id, 'card'],
    queryFn: ({ signal }) =>
      getJson<ThreadCard>(`/v1/threads/${encodeURIComponent(id)}/card`, signal),
  });

/** Who made a step: the actor's name, else its id. */
export const actorName = (actor: { id?: string; name?: string } | null | undefined) =>
  actor?.name ?? actor?.id ?? 'unknown';
