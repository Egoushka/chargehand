import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ThreadCard } from '../../api/threads';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';

const thread = (id: string, over: Partial<ThreadCard> = {}): ThreadCard => ({
  thread: id,
  goal: `Goal of ${id}`,
  state: 'open',
  scope: 'acme/widgets',
  last_step: { seq: 1, time: '2026-11-10T10:00:00Z', done: 'did it', actor: { name: 'agent-x' } },
  next: `Next of ${id}`,
  owner: { name: 'agent-x' },
  last_activity: '2026-11-10T10:00:00Z',
  steps: 2,
  refs: [],
  text: `Thread ${id} (open)\nGoal: <b>bold</b>`,
  ...over,
});

const panel = () => {
  const section = screen.getByRole('heading', { name: 'Open threads' }).closest('section');
  if (!section) throw new Error('no panel');
  return within(section as HTMLElement);
};

describe('open threads on the overview', () => {
  it('lists three threads newest activity first and leaves a closed one out', async () => {
    serveApi({
      threads: [
        thread('old', { last_activity: '2026-11-10T08:00:00Z' }),
        thread('new', { last_activity: '2026-11-10T11:00:00Z' }),
        thread('mid', { last_activity: '2026-11-10T09:00:00Z' }),
        thread('done', { state: 'closed', last_activity: '2026-11-10T12:00:00Z' }),
      ],
    });
    renderConsole('/console/');
    await waitFor(() => expect(panel().getAllByRole('listitem')).toHaveLength(3));
    const goals = panel()
      .getAllByRole('button')
      .map((b) => b.textContent);
    expect(goals).toEqual(['Goal of new', 'Goal of mid', 'Goal of old']);
    expect(panel().queryByText('Goal of done')).toBeNull();
    expect(panel().getAllByText(/agent-x/)).toHaveLength(3);
    expect(panel().getByText(/Next of new/)).toBeTruthy();
  });

  it('opens a card in a sheet and shows its text as text, not markup', async () => {
    const { calls } = serveApi({ threads: [thread('t1')] });
    renderConsole('/console/');
    fireEvent.click(await screen.findByRole('button', { name: 'Goal of t1' }));
    const text = await screen.findByText(/Goal: <b>bold<\/b>/);
    expect(text.querySelector('b')).toBeNull();
    expect(calls.some((c) => c.path === '/v1/threads/t1/card')).toBe(true);
  });

  it('says so when no thread is open', async () => {
    serveApi({ threads: [] });
    renderConsole('/console/');
    expect(await screen.findByText('No open thread')).toBeTruthy();
  });
});
