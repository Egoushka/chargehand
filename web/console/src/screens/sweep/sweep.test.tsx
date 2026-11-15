import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { sweep, sweepSettings } from '../../testing/sweep';
import { changeOf, draftOf, holdProblem, isUnchanged, timeProblem } from './model';

const open = async (scenario = {}) => {
  const api = serveApi(scenario);
  renderConsole('/console/sweep');
  await screen.findByRole('table', { name: 'Repositories of the last sweep' });
  return api;
};

const posts = (api: ReturnType<typeof serveApi>) =>
  api.calls.filter((c) => c.method === 'POST' && c.path === '/v1/sweep');

describe('the Sweep screen', () => {
  it('shows a row per repository from the last sweep', async () => {
    await open();
    const table = screen.getByRole('table', { name: 'Repositories of the last sweep' });
    const app = within(table).getByText('example/app').closest('[role=row]') as HTMLElement;
    expect(
      within(app)
        .getAllByRole('cell')
        .map((c) => c.textContent),
    ).toEqual(['example/app', '3', '2', '1', '0', '0', '0', '—']);
  });

  it('shows a problem as its one line, once, in its row', async () => {
    await open();
    expect(
      screen.getAllByText('checkout refused: tracks a denied file (fix: public_files)'),
    ).toHaveLength(1);
  });

  it('posts once on Scan now, also when the schedule is off', async () => {
    const api = await open({ sweep: sweep({ schedule_on: false, next: undefined }) });
    expect(screen.getByText(/schedule off/)).toBeTruthy();
    const button = screen.getByRole('button', { name: 'Scan now' }) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    fireEvent.click(button);
    await waitFor(() => expect(posts(api)).toHaveLength(1));
    fireEvent.click(button);
    expect(posts(api)).toHaveLength(1);
  });

  it('while a sweep runs shows scanning and its id, and Scan now is off', async () => {
    const api = await open({
      sweep: sweep({
        running: {
          id: 'sweep-20261114-160000-d4e5f6',
          trigger: 'manual',
          started: new Date().toISOString(),
        },
      }),
    });
    const button = (await screen.findByRole('button', { name: /scanning/ })) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByText('sweep-20261114-160000-d4e5f6')).toBeTruthy();
    fireEvent.click(button);
    expect(posts(api)).toHaveLength(0);
  });
});

describe('the sweep settings model', () => {
  it('sends only what is overridden', () => {
    const settings = sweepSettings();
    expect(changeOf(draftOf(settings))).toEqual({});
    const draft = { ...draftOf(settings), days: ['monday'], hold: '80' };
    expect(changeOf(draft)).toEqual({ days: ['monday'], subscription_hold_percent: 80 });
    expect(isUnchanged(draft, settings)).toBe(false);
  });

  it('checks a time and the hold percent', () => {
    expect(timeProblem('24:00', [])).not.toBeNull();
    expect(timeProblem('08:00', ['08:00'])).toBe('That time is already there.');
    expect(timeProblem('09:30', ['08:00'])).toBeNull();
    expect(holdProblem('')).toBeNull();
    expect(holdProblem('0')).toBe('From 1 to 100.');
    expect(holdProblem('101')).toBe('From 1 to 100.');
    expect(holdProblem('50')).toBeNull();
  });
});

describe('the Sweep settings', () => {
  const settingsScreen = async (scenario = {}) => {
    const api = serveApi(scenario);
    renderConsole('/console/settings');
    await screen.findByRole('button', { name: 'Mon' });
    return api;
  };
  const put = (api: ReturnType<typeof serveApi>) =>
    api.calls.filter((c) => c.method === 'PUT' && c.path === '/v1/sweep/settings');

  it('saves a day toggled off and a time added', async () => {
    const api = await settingsScreen();
    fireEvent.click(screen.getByRole('button', { name: 'Sat' }));
    fireEvent.click(screen.getByRole('button', { name: 'Sun' }));
    fireEvent.change(screen.getByLabelText('Add a time'), { target: { value: '12:30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add time' }));
    expect(screen.getByText('12:30')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save sweep' }));
    await waitFor(() => expect(put(api)).toHaveLength(1));
    expect(put(api)[0]?.body).toEqual({
      at: ['08:00', '12:30', '20:00'],
      days: ['monday', 'tuesday', 'wednesday', 'thursday', 'friday'],
    });
  });

  it('shows a refusal inline and saves nothing', async () => {
    const api = await settingsScreen({ sweepRefusal: ['unknown repository example/gone'] });
    fireEvent.click(screen.getByRole('button', { name: 'Mon' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save sweep' }));
    expect((await screen.findByRole('alert')).textContent).toContain(
      'unknown repository example/gone',
    );
    expect(
      (screen.getByRole('button', { name: 'Mon' }) as HTMLElement).getAttribute('aria-pressed'),
    ).toBe('false');
    expect(put(api)).toHaveLength(1);
  });
});
