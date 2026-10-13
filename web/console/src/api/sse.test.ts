import { afterEach, describe, expect, it, vi } from 'vitest';
import { Unauthorized } from './client';
import { getKey, setKey } from './key';
import { FrameReader, parseFrame, streamEvents } from './sse';

afterEach(() => vi.unstubAllGlobals());

describe('FrameReader', () => {
  it('reads events that arrive whole', () => {
    const events = new FrameReader().push('event: started\ndata: {"run_id":"r1"}\n\n');
    expect(events).toEqual([{ name: 'started', data: { run_id: 'r1' } }]);
  });

  it('joins a frame split across chunks, including a CRLF split in the middle', () => {
    const reader = new FrameReader();
    expect(reader.push('event: node_started\r')).toEqual([]);
    expect(reader.push('\ndata: {"node_id":')).toEqual([]);
    expect(reader.push('"n1"}\r\n\r\n')).toEqual([
      { name: 'node_started', data: { node_id: 'n1' } },
    ]);
  });

  it('reads several events from one chunk and keeps the unfinished one', () => {
    const reader = new FrameReader();
    const first = reader.push('data: {"a":1}\n\ndata: {"a":2}\n\ndata: {"a');
    expect(first.map((e) => e.data)).toEqual([{ a: 1 }, { a: 2 }]);
    expect(reader.push('":3}\n\n').map((e) => e.data)).toEqual([{ a: 3 }]);
  });

  it('drops comments, frames without data and data that is not JSON', () => {
    expect(parseFrame(': keep-alive')).toBeNull();
    expect(parseFrame('event: x')).toBeNull();
    expect(parseFrame('data: not json')).toBeNull();
    expect(parseFrame('data: {"a":\ndata: 1}')).toEqual({ name: 'message', data: { a: 1 } });
  });
});

function streamOf(...chunks: string[]) {
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(new TextEncoder().encode(chunk));
      controller.close();
    },
  });
}

describe('streamEvents', () => {
  it('sends the bearer header and delivers each event until the server closes the stream', async () => {
    setKey('chk1.abc.secret');
    const fetchMock = vi.fn(
      async () =>
        new Response(
          streamOf(
            'event: accepted\ndata: {"status":"queued"}\n\n',
            'event: run_finished\ndata: {"status":"completed"}\n\n',
          ),
          { status: 200 },
        ),
    );
    vi.stubGlobal('fetch', fetchMock);
    const seen: string[] = [];

    await streamEvents('/v1/runs/r1/events', (e) => seen.push(e.name));

    expect(seen).toEqual(['accepted', 'run_finished']);
    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(init.headers).toMatchObject({
      Authorization: 'Bearer chk1.abc.secret',
      Accept: 'text/event-stream',
    });
  });

  it('forgets the key on a 401', async () => {
    setKey('stale');
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 401 })),
    );

    await expect(streamEvents('/v1/runs/r1/events', () => {})).rejects.toBeInstanceOf(Unauthorized);

    expect(getKey()).toBeNull();
  });

  it('rejects on another failed status', async () => {
    setKey('k');
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 404 })),
    );
    await expect(streamEvents('/v1/runs/nope/events', () => {})).rejects.toThrow('HTTP 404');
  });

  it('ends quietly when aborted', async () => {
    setKey('k');
    const abort = new AbortController();
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        abort.abort();
        throw new DOMException('aborted', 'AbortError');
      }),
    );
    await expect(
      streamEvents('/v1/runs/r1/events', () => {}, abort.signal),
    ).resolves.toBeUndefined();
  });
});
