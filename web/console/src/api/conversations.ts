import { queryOptions } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { ApiError, call, getJson, problems } from './client';
import { streamEvents } from './sse';

export type ConversationState = 'starting' | 'idle' | 'running' | 'ended' | 'lost' | 'failed';

/** `conversation/v1`: what the server says of one conversation. */
export interface Conversation {
  contract_version: string;
  id: string;
  state: ConversationState;
  repository: { repository: string; commit: string };
  runtime: string;
  model?: string;
  preset?: string;
  actor?: string;
  created_at: string;
  updated_at: string;
  messages: number;
  queued: number;
  pending_approvals: string[];
  usage?: { input_tokens: number; output_tokens: number; usd?: number };
  caps: { max_messages: number };
  last_event_id: number;
  ended_reason?: string;
}

/** One `conversation-event/v1` envelope; `data` is the payload of the type. */
export interface ConversationEvent {
  id: number;
  at: string;
  type: string;
  data: Record<string, unknown>;
}

/** A conversation that can take no more messages. */
export const isOver = (c: Pick<Conversation, 'state'>) =>
  c.state === 'ended' || c.state === 'lost' || c.state === 'failed';

const base = (id: string) => `/v1/conversations/${encodeURIComponent(id)}`;

/** The caller's conversations, newest first. The list moves while an agent works, so it is read again every few seconds. */
export const conversationsQuery = () =>
  queryOptions({
    queryKey: ['conversations'],
    queryFn: ({ signal }) =>
      getJson<{ conversations: Conversation[] }>('/v1/conversations', signal),
    select: (body) => body.conversations,
    refetchInterval: 5_000,
  });

export const conversationQuery = (id: string) =>
  queryOptions({
    queryKey: ['conversations', id],
    queryFn: ({ signal }) => getJson<Conversation>(base(id), signal),
  });

async function send<T>(path: string, method: 'POST' | 'DELETE', body?: unknown): Promise<T> {
  const reply = await call<T>(path, { method, body });
  if (!reply.ok) throw new ApiError(reply.status, problems(reply));
  return reply.data;
}

export const sendMessage = (id: string, text: string) =>
  send<{ queued: boolean }>(`${base(id)}/messages`, 'POST', { text });

export const answerApproval = (id: string, call: string, decision: 'allow_once' | 'reject') =>
  send<{ call_id: string; decision: string }>(
    `${base(id)}/approvals/${encodeURIComponent(call)}`,
    'POST',
    { decision },
  );

export const endConversation = (id: string) => send<Conversation>(base(id), 'DELETE');

export interface ConversationEvents {
  /** In the order of their numbers, each once: a stream asked for again replays from the start. */
  events: ConversationEvent[];
  /** The server closed the stream: the conversation is over. */
  closed: boolean;
  error: string | null;
}

/** The live events of one conversation (`GET {id}/events`). A stream that breaks is asked for again after `retryMs`. */
export function useConversationEvents(
  id: string,
  onEvent?: (event: ConversationEvent) => void,
  retryMs = 3_000,
): ConversationEvents {
  const [state, setState] = useState<ConversationEvents>({
    events: [],
    closed: false,
    error: null,
  });
  const seen = useRef(onEvent);
  seen.current = onEvent;
  useEffect(() => {
    const abort = new AbortController();
    let timer: number | undefined;
    let last = 0;
    setState({ events: [], closed: false, error: null });
    const connect = () => {
      streamEvents<ConversationEvent>(
        `${base(id)}/events`,
        ({ data }) => {
          if (data.id <= last) return;
          last = data.id;
          setState((s) => ({ ...s, error: null, events: [...s.events, data] }));
          seen.current?.(data);
        },
        abort.signal,
      ).then(
        () => setState((s) => ({ ...s, closed: !abort.signal.aborted })),
        (error: unknown) => {
          if (abort.signal.aborted) return;
          setState((s) => ({
            ...s,
            error: error instanceof Error ? error.message : String(error),
          }));
          timer = window.setTimeout(connect, retryMs);
        },
      );
    };
    connect();
    return () => {
      abort.abort();
      window.clearTimeout(timer);
    };
  }, [id, retryMs]);
  return state;
}
