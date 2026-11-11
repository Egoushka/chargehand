import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { BatchTaskDetailBody } from '../../api/batch-task';
import { BATCH_A, run } from '../../testing/fixtures';
import { richBatch, taskDetail } from '../../testing/fixtures-runs';
import { renderConsole } from '../../testing/render';
import { type Scenario, serveApi } from '../../testing/server';
import { qualityPassed } from './quality';

const MERGE = '/v1/pulls/example/payments-api/412/merge';
const page = (task: string) => `/console/runs/${BATCH_A}/tasks/${task}`;

function serve(extra: Scenario = {}, over: Partial<BatchTaskDetailBody> = {}) {
  const tasks = {
    [`${BATCH_A}/t2-retry-policy`]: { ...taskDetail(BATCH_A, 't2-retry-policy'), ...over },
  };
  return serveApi({ runs: [run(BATCH_A)], batches: { [BATCH_A]: richBatch() }, tasks, ...extra });
}

const merge = () => screen.findByRole('button', { name: 'Approve merge' });
const asked = (calls: { path: string; method: string }[]) =>
  calls.filter((c) => c.path === MERGE && c.method === 'POST');
const confirm = async () =>
  fireEvent.click(
    within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Merge PR 412' }),
  );

describe('qualityPassed', () => {
  const passed = { verification: { passed: true, exit_code: 0 } };

  it('needs verification to have passed and nothing to have failed or been flagged', () => {
    expect(qualityPassed(passed)).toBe(true);
    expect(qualityPassed({})).toBe(false);
    expect(qualityPassed({ verification: { passed: false, exit_code: 1 } })).toBe(false);
    expect(
      qualityPassed({
        ...passed,
        mutation: { ran: true, score: 0.2, threshold: 0.6, passed: false },
      }),
    ).toBe(false);
    expect(qualityPassed({ ...passed, mutation: { ran: false, not_run: 'timed out' } })).toBe(
      false,
    );
  });
});

describe('merge from the quality card', () => {
  it('asks before it merges, then merges once and says so', async () => {
    const { calls } = serve({ replies: { [MERGE]: [202, { action: 'merge', changed: true }] } });
    renderConsole(page('t2-retry-policy'));
    fireEvent.click(await merge());
    const dialog = await screen.findByRole('alertdialog');
    expect(asked(calls)).toHaveLength(0);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep it open' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(asked(calls)).toHaveLength(0);
    fireEvent.click(await merge());
    await confirm();
    expect(await screen.findByText('Merged.')).toBeTruthy();
    expect(asked(calls)).toHaveLength(1);
  });

  it('shows the reason when the server refuses', async () => {
    serve({ replies: { [MERGE]: [409, { errors: ['required checks have not passed: build'] }] } });
    renderConsole(page('t2-retry-policy'));
    fireEvent.click(await merge());
    await confirm();
    expect(await screen.findByText(/required checks have not passed: build/)).toBeTruthy();
  });

  it('is disabled when the card did not pass, and nothing is sent', async () => {
    const { calls } = serve({}, { verification: { passed: false, exit_code: 1 } });
    renderConsole(page('t2-retry-policy'));
    const button = (await merge()) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fireEvent.click(button);
    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(asked(calls)).toHaveLength(0);
  });

  it('is not offered for a task whose pull request is merged', async () => {
    serveApi({ runs: [run(BATCH_A)], batches: { [BATCH_A]: richBatch() } });
    renderConsole(page('t1-schema'));
    await screen.findByText(/verification passed/);
    expect(screen.queryByRole('button', { name: 'Approve merge' })).toBeNull();
  });
});
