import { act, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BATCH_A, inbox, inboxItems, run } from '../testing/fixtures';
import { renderConsole } from '../testing/render';
import { serveApi } from '../testing/server';
import { updatePreferences } from './preferences';

const [question, pullRequest, failure] = inboxItems();
const settle = (
  client: { invalidateQueries: (f: { queryKey: string[] }) => Promise<void> },
  key: string,
) => act(() => client.invalidateQueries({ queryKey: [key] }));

afterEach(() => {
  Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
  Object.defineProperty(window, 'isSecureContext', { value: false, configurable: true });
});

describe('notices', () => {
  it('stays quiet about what was already waiting when the console opened', async () => {
    serveApi();
    renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 3 waiting' });
    expect(screen.queryByText(/asked a question|PR ready|failed/)).toBeNull();
  });

  it('says so when a task asks a question, with the action that answers it', async () => {
    serveApi({ inbox: inbox([pullRequest, failure] as never) });
    const { client, router } = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 2 waiting' });

    serveApi({ inbox: inbox([question, pullRequest, failure] as never) });
    await settle(client, 'inbox');

    expect(await screen.findByText('t3-memory-ttl asked a question')).toBeTruthy();
    expect(
      screen.getByText('Should the TTL come from the environment or from the container template?'),
    ).toBeTruthy();
    const toast = screen
      .getByText('t3-memory-ttl asked a question')
      .closest('.ch-toast') as HTMLElement;
    expect(toast.getAttribute('data-tone')).toBe('accent');
    toast.querySelector<HTMLButtonElement>('button.ch-btn--primary')?.click();
    await waitFor(() => expect(router.state.location.pathname).toBe('/needs-you'));
    await waitFor(() => expect(screen.queryByText('t3-memory-ttl asked a question')).toBeNull());
  });

  it('says so when a pull request is ready, and when a task fails, and says each once', async () => {
    serveApi({ inbox: inbox([question] as never) });
    const { client } = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 1 waiting' });

    serveApi({ inbox: inbox([pullRequest, failure, question] as never) });
    await settle(client, 'inbox');
    expect(await screen.findByText('PR ready on payments-api')).toBeTruthy();
    // The toast is also read out through a hidden live region, so its text is on the page twice.
    expect(
      screen.getAllByText(/Policy is ask: it waits for you to merge it\./).length,
    ).toBeGreaterThanOrEqual(1);
    const failed = await screen.findByText('t1-pii-scrub failed');
    expect(failed.closest('.ch-toast')?.getAttribute('role')).toBe('alert');
    expect(
      screen.getByText('verification_failed: 3 tests fail in a fresh container.'),
    ).toBeTruthy();

    await settle(client, 'inbox');
    expect(screen.getAllByText('PR ready on payments-api')).toHaveLength(1);
  });

  it('says so when a batch finishes or fails, from the runs the shell already reads', async () => {
    serveApi({ runs: [run(BATCH_A), run('run-20261013-124402-77bd0a')] });
    const { client } = renderConsole('/console/runs');
    await screen.findAllByText('Running', { exact: false });

    serveApi({
      runs: [
        run(BATCH_A, {
          status: 'completed',
          usd: 6.31,
          tokens: 3_840_000,
          finished_at: new Date().toISOString(),
        }),
        run('run-20261013-124402-77bd0a', {
          status: 'failed',
          error_code: 'tasks_incomplete',
          finished_at: new Date().toISOString(),
        }),
      ],
    });
    await settle(client, 'runs');

    expect(await screen.findByText('Batch finished: payments-api')).toBeTruthy();
    expect(screen.getByText('1fdf01: 3.84M tokens · $6.31')).toBeTruthy();
    const bad = await screen.findByText('Batch failed: payments-api');
    expect(bad.closest('.ch-toast')?.getAttribute('role')).toBe('alert');
    expect(screen.getAllByText(/77bd0a: .*tasks_incomplete/).length).toBeGreaterThanOrEqual(1);
  });

  it('does not raise a kind the person switched off', async () => {
    updatePreferences({
      notify: { question: false, pull_request: true, batch: true, failure: true },
    });
    serveApi({ inbox: inbox([pullRequest] as never) });
    const { client } = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 1 waiting' });
    serveApi({ inbox: inbox([question, pullRequest] as never) });
    await settle(client, 'inbox');
    await screen.findByRole('link', { name: 'Needs you, 2 waiting' });
    expect(screen.queryByText('t3-memory-ttl asked a question')).toBeNull();
  });
});

describe('browser notifications', () => {
  const sent: Array<{ title: string; options?: NotificationOptions }> = [];
  class FakeNotification {
    static permission: NotificationPermission = 'granted';
    static requestPermission = async () => 'granted' as const;
    onclick: (() => void) | null = null;
    constructor(
      public title: string,
      public options?: NotificationOptions,
    ) {
      sent.push({ title, options });
    }
    close() {}
  }

  function arrange(
    visibility: 'hidden' | 'visible',
    permission: NotificationPermission,
    secure = true,
  ) {
    sent.length = 0;
    FakeNotification.permission = permission;
    vi.stubGlobal('Notification', FakeNotification);
    Object.defineProperty(window, 'isSecureContext', { value: secure, configurable: true });
    Object.defineProperty(document, 'visibilityState', { value: visibility, configurable: true });
    updatePreferences({ browserNotifications: true });
  }

  it('shows one, with the notice’s title, when the page is in the background and the person allowed it', async () => {
    arrange('hidden', 'granted');
    serveApi({ inbox: inbox([pullRequest] as never) });
    const { client } = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 1 waiting' });
    serveApi({ inbox: inbox([question, pullRequest] as never) });
    await settle(client, 'inbox');

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]?.title).toBe('t3-memory-ttl asked a question');
    expect(sent[0]?.options?.body).toBe(
      'Should the TTL come from the environment or from the container template?',
    );
    expect(sent[0]?.options?.tag).toBe(question?.id);
  });

  it('shows none while the page is in front: the notice on the page is enough', async () => {
    arrange('visible', 'granted');
    serveApi({ inbox: inbox([pullRequest] as never) });
    const { client } = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 1 waiting' });
    serveApi({ inbox: inbox([question, pullRequest] as never) });
    await settle(client, 'inbox');
    expect(await screen.findByText('t3-memory-ttl asked a question')).toBeTruthy();
    expect(sent).toHaveLength(0);
  });

  it('shows none without permission, and does nothing at all on plain http', async () => {
    arrange('hidden', 'denied');
    serveApi({ inbox: inbox([pullRequest] as never) });
    const first = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 1 waiting' });
    serveApi({ inbox: inbox([question, pullRequest] as never) });
    await settle(first.client, 'inbox');
    await screen.findByText('t3-memory-ttl asked a question');
    expect(sent).toHaveLength(0);
    first.unmount();

    arrange('hidden', 'granted', false);
    serveApi({ inbox: inbox([pullRequest] as never) });
    const second = renderConsole('/console/runs');
    await screen.findByRole('link', { name: 'Needs you, 1 waiting' });
    serveApi({ inbox: inbox([question, pullRequest] as never) });
    await settle(second.client, 'inbox');
    await screen.findByText('t3-memory-ttl asked a question');
    expect(sent).toHaveLength(0);
  });

  it('offers to turn them on from a click, once, and not at all on plain http', async () => {
    Object.defineProperty(window, 'isSecureContext', { value: false, configurable: true });
    serveApi();
    const plain = renderConsole('/console/');
    await screen.findByRole('link', { name: 'Needs you, 3 waiting' });
    expect(screen.queryByRole('button', { name: 'Enable' })).toBeNull();
    plain.unmount();

    vi.stubGlobal('Notification', FakeNotification);
    FakeNotification.permission = 'default';
    Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
    serveApi();
    renderConsole('/console/');
    const enable = await screen.findByRole('button', { name: 'Enable' });
    expect(screen.getByText(/Get a browser notification when a task asks a question/)).toBeTruthy();
    FakeNotification.permission = 'granted';
    enable.click();
    expect(await screen.findByText('Notifications on')).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Enable' })).toBeNull());
  });
});
