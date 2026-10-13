import { renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { setKey } from './key';
import type { RunStatus } from './types';
import { useRunEvents } from './useRunEvents';

const event = (detail: string, at = '2026-10-13T10:00:00Z'): RunStatus => ({
  contract_version: 'run-status/v1',
  run_id: 'r-1',
  status: 'running',
  event: 'session_progress',
  at,
  detail,
});
const frame = (e: RunStatus) => `event: status\ndata: ${JSON.stringify(e)}\n\n`;
const stream = (text: string) =>
  new Response(text, { status: 200, headers: { 'content-type': 'text/event-stream' } });

describe('the live events of a run', () => {
  it('lists the events in the order they came, once each, and is closed when the server closes the stream', async () => {
    setKey('k');
    const a = event('first');
    const fetchMock = vi.fn(async () =>
      stream(`${frame(a)}${frame(a)}${frame(event('second', '2026-10-13T10:01:00Z'))}`),
    );
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useRunEvents('r-1'));
    await waitFor(() => expect(result.current.closed).toBe(true));
    expect(result.current.events.map((e) => e.data.detail)).toEqual(['first', 'second']);
    expect(result.current.events.map((e) => e.id)).toEqual([0, 1]);
    expect(result.current.error).toBeNull();
    expect(fetchMock).toHaveBeenCalledWith(
      '/v1/runs/r-1/events',
      expect.objectContaining({ headers: expect.objectContaining({ Authorization: 'Bearer k' }) }),
    );
  });

  it('stops at the first failure unless it is told to ask again', async () => {
    setKey('k');
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 500 })),
    );
    const { result } = renderHook(() => useRunEvents('r-1'));
    await waitFor(() => expect(result.current.closed).toBe(true));
    expect(result.current.error).toContain('HTTP 500');
    expect(result.current.reconnecting).toBe(false);
  });

  it('asks again after a stream that broke, and does not list an event it already had', async () => {
    setKey('k');
    const a = event('first');
    let calls = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        calls += 1;
        if (calls === 1)
          // The first stream delivers one event and then breaks.
          return new Response(
            new ReadableStream({
              start(controller) {
                controller.enqueue(new TextEncoder().encode(frame(a)));
                controller.error(new Error('connection reset'));
              },
            }),
            { status: 200, headers: { 'content-type': 'text/event-stream' } },
          );
        return stream(`${frame(a)}${frame(event('later', '2026-10-13T10:02:00Z'))}`);
      }),
    );
    const { result } = renderHook(() => useRunEvents('r-1', { retryMs: 10 }));
    await waitFor(() => expect(result.current.closed).toBe(true));
    expect(calls).toBe(2);
    expect(result.current.events.map((e) => e.data.detail)).toEqual(['first', 'later']);
  });

  it('starts over for another run', async () => {
    setKey('k');
    vi.stubGlobal(
      'fetch',
      vi.fn(async (path: string) => stream(frame(event(path.includes('r-1') ? 'one' : 'two')))),
    );
    const { result, rerender } = renderHook(({ id }) => useRunEvents(id), {
      initialProps: { id: 'r-1' },
    });
    await waitFor(() => expect(result.current.events[0]?.data.detail).toBe('one'));
    rerender({ id: 'r-2' });
    await waitFor(() => expect(result.current.events.map((e) => e.data.detail)).toEqual(['two']));
  });
});
