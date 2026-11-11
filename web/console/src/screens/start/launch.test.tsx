import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { OTHER_REPO, startReplies } from '../../testing/start';

const NEW_RUN = 'run-20261013-150000-abc123';

describe('Start: launch many', () => {
  it('starts one batch per repository, shows a refusal and the forecast sum, and goes on', async () => {
    const api = serveApi({ replies: startReplies() });
    const inner = globalThis.fetch;
    globalThis.fetch = ((input: string, init?: RequestInit) => {
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : {};
      if (
        init?.method === 'POST' &&
        input === '/v1/runs' &&
        body.context?.repository?.path === OTHER_REPO
      ) {
        api.calls.push({ path: input, method: 'POST', body, headers: {} });
        return Promise.resolve(
          new Response(
            JSON.stringify({ error: { code: 'budget_exceeded', message: 'Over budget.' } }),
            { status: 409, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }
      return inner(input, init);
    }) as typeof fetch;
    try {
      renderConsole('/console/new');
      fireEvent.click(await screen.findByRole('button', { name: 'Launch many' }));
      await screen.findByRole('heading', { level: 1, name: 'Launch many' });
      fireEvent.change(screen.getByLabelText('Goal'), {
        target: { value: 'Bump the logging library.' },
      });
      fireEvent.click(await screen.findByRole('checkbox', { name: /payments-api/ }));
      fireEvent.click(screen.getByRole('checkbox', { name: /billing-worker/ }));
      await waitFor(() =>
        expect(screen.getByTestId('launch-forecast').textContent).toContain('2 tasks ≈ $8.00'),
      );
      fireEvent.click(screen.getByRole('button', { name: 'Start 2 batches' }));
      await screen.findByText(/Refused: 409 budget_exceeded/);
      expect(await screen.findByRole('link', { name: NEW_RUN })).toBeTruthy();
      expect(api.calls.filter((c) => c.method === 'POST' && c.path === '/v1/runs')).toHaveLength(2);
    } finally {
      globalThis.fetch = inner;
    }
  });
});
