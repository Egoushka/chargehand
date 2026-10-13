// The API of the Runs, Batch, Task and Research screens, on top of `mockApi`: the routes added last answer first, so these take the paths
// they know and pass the rest on to the foundation's answers.
import type { Page } from '@playwright/test';
import { answerRuns } from '../src/testing/answer-runs';
import { LOGIN } from './mock.ts';

export interface RunsMock {
  /** Every call these routes answered, in order. */
  calls: Array<{ method: string; path: string; body: unknown }>;
}

export async function mockRuns(page: Page): Promise<RunsMock> {
  const mock: RunsMock = { calls: [] };
  await page.route('**/v1/**', async (route) => {
    const request = route.request();
    if (request.headers().authorization !== `Bearer ${LOGIN}`) return route.fallback();
    const url = new URL(request.url());
    const reply = answerRuns(request.method(), url.pathname, url.searchParams, Date.now());
    if (!reply) return route.fallback();
    const body = request.postData() ? JSON.parse(request.postData() as string) : undefined;
    mock.calls.push({ method: request.method(), path: url.pathname + url.search, body });
    if (reply.text !== undefined)
      return route.fulfill({
        status: reply.status,
        contentType: reply.contentType ?? 'text/plain',
        body: reply.text,
      });
    return route.fulfill({ status: reply.status, json: reply.json, headers: reply.headers });
  });
  return mock;
}
