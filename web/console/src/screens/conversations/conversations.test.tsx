import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { Conversation, ConversationEvent } from '../../api/conversations';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import { pending, transcript } from './model';

const AT = '2026-11-10T10:00:00Z';

const conversation = (id: string, over: Partial<Conversation> = {}): Conversation => ({
  contract_version: 'conversation/v1',
  id,
  state: 'running',
  repository: { repository: 'acme/widgets', commit: '0123456789abcdef0123456789abcdef01234567' },
  runtime: 'fake',
  created_at: AT,
  updated_at: AT,
  messages: 1,
  queued: 0,
  pending_approvals: [],
  usage: { input_tokens: 1200, output_tokens: 300, usd: 0.42 },
  caps: { max_messages: 50 },
  last_event_id: 4,
  ...over,
});

const event = (id: number, type: string, data: Record<string, unknown>): ConversationEvent => ({
  id,
  at: AT,
  type,
  data,
});

const sse = (events: ConversationEvent[]) =>
  events.map((e) => `id: ${e.id}\nevent: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');

const EVENTS = [
  event(1, 'message_chunk', { role: 'assistant', text: 'Reading ' }),
  event(2, 'message_chunk', { role: 'assistant', text: 'the <b>repository</b>' }),
  event(3, 'tool_call', {
    call_id: 'c1',
    title: 'Run the tests',
    kind: 'execute',
    status: 'pending',
  }),
  event(4, 'approval_requested', { call_id: 'c1', title: 'Run the tests', kind: 'execute' }),
];

const A = 'conv-20261110-100000-aaaaaa';
const B = 'conv-20261110-090000-bbbbbb';

describe('the transcript', () => {
  it('joins chunks of one speaker, folds a tool call update in and marks an approval answered', () => {
    const items = transcript([
      ...EVENTS,
      event(5, 'approval_answered', { call_id: 'c1', decision: 'reject' }),
      event(6, 'tool_call_update', { call_id: 'c1', status: 'failed' }),
    ]);
    expect(items.map((i) => i.kind)).toEqual(['message', 'tool', 'approval']);
    expect(items[0]).toMatchObject({ text: 'Reading the <b>repository</b>' });
    expect(items[1]).toMatchObject({ status: 'failed' });
    expect(pending(items)).toEqual([]);
    expect(pending(transcript(EVENTS))).toHaveLength(1);
  });
});

describe('Conversations', () => {
  it('lists the conversations with their state and opens one', async () => {
    serveApi({
      conversations: [
        conversation(A, { pending_approvals: ['c1'] }),
        conversation(B, { state: 'ended', ended_reason: 'user' }),
      ],
      streams: { [`/v1/conversations/${A}/events`]: sse(EVENTS) },
    });
    const { router } = renderConsole('/console/conversations');
    const list = await screen.findByRole('navigation', { name: 'Conversations' });
    expect(await within(list).findAllByText('acme/widgets')).toHaveLength(2);
    expect(within(list).getByText('Needs approval')).toBeTruthy();
    fireEvent.click(within(list).getAllByRole('link')[0] as HTMLElement);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ id: A }));
  });

  it('streams the transcript into the screen and shows agent text as text', async () => {
    serveApi({
      conversations: [conversation(A)],
      streams: { [`/v1/conversations/${A}/events`]: sse(EVENTS) },
    });
    renderConsole(`/console/conversations?id=${A}`);
    expect(await screen.findByText('Reading the <b>repository</b>')).toBeTruthy();
    expect(document.querySelector('.cv-msg__text b')).toBeNull();
    expect(screen.getAllByText('Run the tests').length).toBeGreaterThan(0);
  });

  it('approves and declines a tool call through the approvals route', async () => {
    const api = serveApi({
      conversations: [conversation(A, { pending_approvals: ['c1'] })],
      streams: { [`/v1/conversations/${A}/events`]: sse(EVENTS) },
    });
    renderConsole(`/console/conversations?id=${A}`);
    fireEvent.click(await screen.findByRole('button', { name: 'Approve' }));
    await waitFor(() =>
      expect(api.last(`/v1/conversations/${A}/approvals/c1`)?.body).toEqual({
        decision: 'allow_once',
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Decline' }));
    await waitFor(() =>
      expect(api.last(`/v1/conversations/${A}/approvals/c1`)?.body).toEqual({ decision: 'reject' }),
    );
  });

  it('sends a message and ends the conversation after asking', async () => {
    const api = serveApi({
      conversations: [conversation(A, { state: 'idle' })],
      streams: { [`/v1/conversations/${A}/events`]: sse(EVENTS.slice(0, 2)) },
    });
    renderConsole(`/console/conversations?id=${A}`);
    const box = await screen.findByRole('textbox', { name: 'Message' });
    fireEvent.change(box, { target: { value: 'Add a test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    await waitFor(() =>
      expect(api.last(`/v1/conversations/${A}/messages`)?.body).toEqual({ text: 'Add a test' }),
    );
    await waitFor(() => expect((box as HTMLTextAreaElement).value).toBe(''));

    fireEvent.click(screen.getByRole('button', { name: 'End conversation' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(api.calls.some((c) => c.method === 'DELETE')).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'End conversation' }));
    await waitFor(() =>
      expect(api.calls.find((c) => c.method === 'DELETE')?.path).toBe(`/v1/conversations/${A}`),
    );
  });

  it('is usable at phone width: the list, then one conversation with a way back', async () => {
    viewport(true);
    serveApi({
      conversations: [conversation(A)],
      streams: { [`/v1/conversations/${A}/events`]: sse(EVENTS) },
    });
    const first = renderConsole('/console/conversations');
    expect(await screen.findByRole('navigation', { name: 'Conversations' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: 'Conversation' })).toBeNull();
    first.unmount();

    renderConsole(`/console/conversations?id=${A}`);
    expect(await screen.findByRole('link', { name: /All conversations/ })).toBeTruthy();
    expect(screen.queryByRole('navigation', { name: 'Conversations' })).toBeNull();
    expect(await screen.findByRole('textbox', { name: 'Message' })).toBeTruthy();
  });

  it('says so when there are none', async () => {
    serveApi({ conversations: [] });
    renderConsole('/console/conversations');
    expect(await screen.findByText('No conversations')).toBeTruthy();
  });
});
