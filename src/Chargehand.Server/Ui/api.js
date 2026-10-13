// The server's routes with the bearer key. The key lives in sessionStorage: it ends with the tab and is never sent anywhere but this origin.

const KEY = 'chargehand.key';
let memory = null; // when sessionStorage is unavailable

export function getKey() {
  try {
    return sessionStorage.getItem(KEY) ?? memory;
  } catch {
    return memory;
  }
}

export function setKey(key) {
  memory = key;
  try {
    sessionStorage.setItem(KEY, key);
  } catch { /* the page still works until it is reloaded */ }
}

export function clearKey() {
  memory = null;
  try {
    sessionStorage.removeItem(KEY);
  } catch { /* nothing to clear */ }
}

export class Unauthorized extends Error {}

const headers = extra => ({ Authorization: `Bearer ${getKey() ?? ''}`, ...extra });

/** {status, ok, data, location}: data is the parsed JSON body, or {errors: [...]} for a body that is not JSON. A 401 clears the key and throws Unauthorized. */
export async function call(path, { method = 'GET', body, extra, signal } = {}) {
  const res = await fetch(path, {
    method, signal, cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer',
    headers: headers({ ...(body ? { 'Content-Type': 'application/json' } : {}), ...extra }),
    body: body ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401) {
    clearKey();
    throw new Unauthorized();
  }
  const text = await res.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = { errors: [`HTTP ${res.status}: ${text.slice(0, 200)}`] };
  }
  return { status: res.status, ok: res.ok, data, location: res.headers.get('Location') };
}

/** The error lines of a failed call, for display. */
export function problems({ status, data }) {
  return data?.errors?.length ? data.errors : [`HTTP ${status}`];
}

/**
 * Server-sent events over fetch, because the browser's own SSE client cannot send an Authorization header. Calls onEvent({name, data}) per event and
 * resolves when the server closes the stream; rejects on a failed request. An abort ends it quietly.
 */
export async function stream(path, onEvent, signal) {
  const res = await fetch(path, { signal, cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer', headers: headers({ Accept: 'text/event-stream' }) });
  if (res.status === 401) {
    clearKey();
    throw new Unauthorized();
  }
  if (!res.ok || !res.body) throw new Error(`events: HTTP ${res.status}`);
  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) return;
    buffer += value;
    // A lone CR at the end may be the first half of CRLF: leave it for the next chunk.
    const hold = buffer.endsWith('\r') ? '\r' : '';
    buffer = buffer.slice(0, buffer.length - hold.length).replace(/\r\n?/g, '\n') + hold;
    for (let end = buffer.indexOf('\n\n'); end >= 0; end = buffer.indexOf('\n\n')) {
      const event = parseFrame(buffer.slice(0, end));
      buffer = buffer.slice(end + 2);
      if (event) onEvent(event);
    }
  }
}

function parseFrame(frame) {
  let name = 'message';
  const data = [];
  for (const line of frame.split('\n')) {
    if (line.startsWith(':')) continue;
    const colon = line.indexOf(':');
    const key = colon < 0 ? line : line.slice(0, colon);
    const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
    if (key === 'event') name = value;
    else if (key === 'data') data.push(value);
  }
  try {
    return data.length ? { name, data: JSON.parse(data.join('\n')) } : null;
  } catch {
    return null;
  }
}
