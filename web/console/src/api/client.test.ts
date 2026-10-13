import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, call, getJson, problems, Unauthorized } from './client';
import { getKey, getKeyState, setKey } from './key';

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers });

function stubFetch(response: Response) {
  const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => response);
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

afterEach(() => vi.unstubAllGlobals());

describe('call', () => {
  it('sends the stored key as the bearer header and nothing else that identifies the page', async () => {
    setKey('chk1.abc.secret');
    const fetchMock = stubFetch(json({ runs: [] }));

    await call('/v1/runs');

    const [path, init] = fetchMock.mock.calls[0] ?? [];
    expect(path).toBe('/v1/runs');
    expect(init?.headers).toMatchObject({ Authorization: 'Bearer chk1.abc.secret' });
    expect(init).toMatchObject({
      credentials: 'same-origin',
      referrerPolicy: 'no-referrer',
      cache: 'no-store',
    });
  });

  it('posts a JSON body with its content type', async () => {
    setKey('k');
    const fetchMock = stubFetch(json({ halted: true }));

    await call('/v1/halt', { method: 'POST', body: { reason: 'test' } });

    const init = fetchMock.mock.calls[0]?.[1];
    expect(init?.method).toBe('POST');
    expect(init?.headers).toMatchObject({ 'Content-Type': 'application/json' });
    expect(init?.body).toBe('{"reason":"test"}');
  });

  it('forgets the stored key and throws Unauthorized on a 401', async () => {
    setKey('stale');
    stubFetch(new Response('', { status: 401 }));

    await expect(call('/v1/runs')).rejects.toBeInstanceOf(Unauthorized);

    expect(getKey()).toBeNull();
    expect(getKeyState().notice).toMatch(/rejected/);
  });

  it('leaves the stored key alone when a key being tried is refused', async () => {
    setKey('current');
    stubFetch(new Response('', { status: 401 }));

    await expect(call('/v1/accounts/me', { key: 'wrong' })).rejects.toBeInstanceOf(Unauthorized);

    expect(getKey()).toBe('current');
  });

  it('turns a body that is not JSON into an error line', async () => {
    setKey('k');
    stubFetch(new Response('<html>bad gateway</html>', { status: 502 }));

    const reply = await call('/v1/runs');

    expect(reply.ok).toBe(false);
    expect(problems(reply)).toEqual(['HTTP 502: <html>bad gateway</html>']);
  });

  it('reads the Location header', async () => {
    setKey('k');
    stubFetch(json({}, 202, { Location: '/v1/runs/r1' }));
    expect((await call('/v1/runs', { method: 'POST' })).location).toBe('/v1/runs/r1');
  });
});

describe('getJson', () => {
  it("throws the server's error lines for a failed call", async () => {
    setKey('k');
    stubFetch(json({ errors: ['limit must be 1 to 500'] }, 400));

    const error = await getJson('/v1/runs?limit=0').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(400);
    expect((error as ApiError).message).toBe('limit must be 1 to 500');
  });
});
