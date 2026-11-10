import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { BatchTaskDetailBody } from '../../api/batch-task';
import { BATCH_A, run } from '../../testing/fixtures';
import { richBatch, taskDetail } from '../../testing/fixtures-runs';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { parseRef, qualityCard, verdict } from './model';

const strong = taskDetail(BATCH_A, 't2-retry-policy');
const weak: BatchTaskDetailBody = {
  ...taskDetail(BATCH_A, 't1-schema'),
  verification: {
    ...(strong.verification as object),
    passed: false,
    exit_code: 1,
  },
};

describe('the compare model', () => {
  it('reads both sides from the address and refuses a malformed one', () => {
    expect(parseRef('b-1/t-1')).toEqual({ batch: 'b-1', task: 't-1' });
    for (const bad of [undefined, '', 'b-1', '/t', 'b/', 'a/b/c']) expect(parseRef(bad)).toBeNull();
  });

  it('marks the first difference: verification, then a smaller change', () => {
    const a = qualityCard(strong);
    const b = qualityCard(weak);
    expect(verdict(a, b).winner).toBe('a');
    expect(verdict(b, a).winner).toBe('b');
    expect(verdict(a, { ...a, lines: a.lines + 5 }).winner).toBe('a');
    expect(verdict(a, a).winner).toBeNull();
  });
});

describe('the compare screen', () => {
  it('renders two quality cards with their diffs from the two task views, the stronger marked', async () => {
    serveApi({
      runs: [run(BATCH_A)],
      batches: { [BATCH_A]: richBatch() },
      tasks: {
        [`${BATCH_A}/t1-schema`]: weak,
        [`${BATCH_A}/t2-retry-policy`]: strong,
      },
    });
    renderConsole(`/console/compare?a=${BATCH_A}/t1-schema&b=${BATCH_A}/t2-retry-policy`);
    const b = await screen.findByRole('region', { name: 'Task B' });
    const a = screen.getByRole('region', { name: 'Task A' });
    expect((await screen.findByRole('status')).textContent).toContain('Task B is stronger');
    expect(within(b).getByText('Stronger')).toBeTruthy();
    expect(within(a).queryByText('Stronger')).toBeNull();
    expect(await within(b).findByText('Verification')).toBeTruthy();
    expect(within(a).getByText('failed')).toBeTruthy();
    expect(within(b).getByText('passed')).toBeTruthy();
  });

  it('asks for both tasks when the address holds fewer', async () => {
    serveApi({});
    renderConsole(`/console/compare?a=${BATCH_A}/t1-schema`);
    expect(await screen.findByText('Pick two tasks to compare')).toBeTruthy();
  });
});
