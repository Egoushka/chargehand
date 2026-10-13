import { authHeaders, Unauthorized } from './client';
import { clearKey } from './key';

export interface ServerEvent<T = unknown> {
  name: string;
  data: T;
}

/** Turns the chunks of a server-sent-events stream into events. A frame that is a comment, has no data or has data that is not JSON is dropped. */
export class FrameReader {
  private buffer = '';

  push<T = unknown>(chunk: string): ServerEvent<T>[] {
    this.buffer += chunk;
    // A lone CR at the end may be the first half of CRLF: leave it for the next chunk.
    const hold = this.buffer.endsWith('\r') ? '\r' : '';
    this.buffer =
      this.buffer.slice(0, this.buffer.length - hold.length).replace(/\r\n?/g, '\n') + hold;
    const events: ServerEvent<T>[] = [];
    for (let end = this.buffer.indexOf('\n\n'); end >= 0; end = this.buffer.indexOf('\n\n')) {
      const event = parseFrame<T>(this.buffer.slice(0, end));
      this.buffer = this.buffer.slice(end + 2);
      if (event) events.push(event);
    }
    return events;
  }
}

export function parseFrame<T = unknown>(frame: string): ServerEvent<T> | null {
  let name = 'message';
  const data: string[] = [];
  for (const line of frame.split('\n')) {
    if (line.startsWith(':')) continue;
    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
    if (field === 'event') name = value;
    else if (field === 'data') data.push(value);
  }
  if (data.length === 0) return null;
  try {
    return { name, data: JSON.parse(data.join('\n')) as T };
  } catch {
    return null;
  }
}

/**
 * Server-sent events over fetch, because the browser's own EventSource cannot send an Authorization header. Calls onEvent per event and
 * resolves when the server closes the stream; rejects on a failed request. An abort ends it quietly.
 */
export async function streamEvents<T = unknown>(
  path: string,
  onEvent: (event: ServerEvent<T>) => void,
  signal?: AbortSignal,
): Promise<void> {
  try {
    const res = await fetch(path, {
      signal,
      cache: 'no-store',
      credentials: 'omit',
      referrerPolicy: 'no-referrer',
      headers: authHeaders(undefined, { Accept: 'text/event-stream' }),
    });
    if (res.status === 401) {
      clearKey('The server rejected the key. Sign in again.');
      throw new Unauthorized();
    }
    if (!res.ok || !res.body) throw new Error(`events: HTTP ${res.status}`);
    const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
    const frames = new FrameReader();
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return;
      for (const event of frames.push<T>(value)) onEvent(event);
    }
  } catch (error) {
    if (signal?.aborted) return;
    throw error;
  }
}
