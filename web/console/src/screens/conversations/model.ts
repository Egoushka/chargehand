// What the Conversations screen makes of a conversation's events: a transcript of messages, tool calls and approvals. Pure.

import type { Conversation, ConversationEvent, ConversationState } from '../../api/conversations';
import type { Status } from '../../lib/status';

export type Item =
  | { kind: 'message'; key: string; role: string; text: string }
  | {
      kind: 'tool';
      key: string;
      callId: string;
      title: string;
      toolKind: string;
      status: string;
      paths: string[];
    }
  | {
      kind: 'approval';
      key: string;
      callId: string;
      title: string;
      decision: string | null;
      paths: string[];
    }
  | { kind: 'error'; key: string; text: string }
  | { kind: 'ended'; key: string; reason: string };

const text = (v: unknown) => (typeof v === 'string' ? v : '');

function pathsOf(v: unknown): string[] {
  if (!Array.isArray(v)) return [];
  return v.flatMap((l: { path?: unknown; line?: unknown }) =>
    typeof l?.path === 'string'
      ? [typeof l.line === 'number' ? `${l.path}:${l.line}` : l.path]
      : [],
  );
}

/** The transcript the events tell, in order: chunks of one speaker join into one message; a tool call and its updates are one line; an approval carries its answer once given. */
export function transcript(events: readonly ConversationEvent[]): Item[] {
  const items: Item[] = [];
  const find = (callId: string, kind: Item['kind']) =>
    items.find((i) => i.kind === kind && 'callId' in i && i.callId === callId);
  for (const e of events) {
    const d = e.data;
    const key = `e${e.id}`;
    switch (e.type) {
      case 'message_chunk': {
        const last = items.at(-1);
        if (last?.kind === 'message' && last.role === text(d.role)) last.text += text(d.text);
        else items.push({ kind: 'message', key, role: text(d.role), text: text(d.text) });
        break;
      }
      case 'tool_call':
        items.push({
          kind: 'tool',
          key,
          callId: text(d.call_id),
          title: text(d.title),
          toolKind: text(d.kind),
          status: text(d.status),
          paths: pathsOf(d.locations),
        });
        break;
      case 'tool_call_update': {
        const call = find(text(d.call_id), 'tool');
        if (call?.kind === 'tool') {
          if (typeof d.status === 'string') call.status = d.status;
          if (typeof d.title === 'string') call.title = d.title;
        }
        break;
      }
      case 'approval_requested':
        items.push({
          kind: 'approval',
          key,
          callId: text(d.call_id),
          title: text(d.title),
          decision: null,
          paths: pathsOf(d.locations),
        });
        break;
      case 'approval_answered': {
        const ask = find(text(d.call_id), 'approval');
        if (ask?.kind === 'approval') ask.decision = text(d.decision);
        break;
      }
      case 'error':
        items.push({ kind: 'error', key, text: text(d.message) });
        break;
      case 'ended':
        items.push({ kind: 'ended', key, reason: text(d.reason) });
        break;
    }
  }
  return items;
}

/** The tool approvals still waiting for a person. */
export const pending = (items: readonly Item[]) =>
  items.filter((i) => i.kind === 'approval' && i.decision === null);

const STATUS: Record<ConversationState, Status> = {
  starting: 'queued',
  idle: 'waiting',
  running: 'running',
  ended: 'done',
  lost: 'lost',
  failed: 'failed',
};

export const statusOf = (c: Pick<Conversation, 'state'>): Status => STATUS[c.state];
