import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import * as adm from '../../testing/admin';
import { me } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { changeOf, draftOf, hasProblem, isUnchanged, problemWith } from './model';

const open = async (scenario = {}) => {
  const api = serveApi(scenario);
  renderConsole('/console/settings');
  await screen.findByLabelText('Triggered batches at once');
  return api;
};

describe('settings model', () => {
  it('turns an empty box into "no override" and a number into that number', () => {
    const draft = { ...draftOf(adm.intakeLimits()), max_concurrent: '1', max_usd_total: '2.5' };
    expect(changeOf(draft)).toEqual({
      max_concurrent: 1,
      max_review_rounds: null,
      max_usd_total: 2.5,
      paused: false,
    });
  });

  it('checks the ranges the server accepts', () => {
    expect(problemWith('max_concurrent', '')).toBeNull();
    expect(problemWith('max_concurrent', '11')).toBe('From 1 to 10.');
    expect(problemWith('max_concurrent', '1.5')).toBe('A whole number.');
    expect(problemWith('max_review_rounds', '0')).toBeNull();
    expect(problemWith('max_usd_total', '0')).toBe('From 0.01 to 100.');
    expect(problemWith('max_usd_total', 'abc')).toBe('A number.');
    expect(hasProblem({ ...draftOf(adm.intakeLimits()), max_concurrent: '0' })).toBe(true);
  });

  it('knows a draft that equals what is saved', () => {
    const limits = adm.intakeLimits();
    expect(isUnchanged(draftOf(limits), limits)).toBe(true);
    expect(isUnchanged({ ...draftOf(limits), paused: true }, limits)).toBe(false);
  });
});

describe('the Settings screen', () => {
  it('shows the profile value as the placeholder and saves an override and the pause switch', async () => {
    const api = await open();
    const concurrent = screen.getByLabelText('Triggered batches at once') as HTMLInputElement;
    expect(concurrent.placeholder).toBe('2');
    const save = screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.change(concurrent, { target: { value: '1' } });
    fireEvent.click(screen.getByRole('switch', { name: 'Pause intake' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(api.calls.find((c) => c.method === 'PUT')?.body).toEqual({
        max_concurrent: 1,
        max_review_rounds: null,
        max_usd_total: null,
        paused: true,
      }),
    );
    await screen.findByText('Intake is paused');
  });

  it('refuses a value out of range before asking the server', async () => {
    const api = await open();
    fireEvent.change(screen.getByLabelText('Triggered batches at once'), {
      target: { value: '11' },
    });
    expect(await screen.findByText('From 1 to 10.')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
    expect(api.calls.some((c) => c.method === 'PUT')).toBe(false);
  });

  it('says so when the profile has no intake', async () => {
    serveApi({ intakeLimits: adm.intakeLimits({ configured: false }) });
    renderConsole('/console/settings');
    expect(await screen.findByText('Intake is not configured')).toBeTruthy();
  });

  it('shows the error and a retry when the limits cannot be read', async () => {
    serveApi({ replies: { '/v1/intake/limits': [500, { errors: ['disk'] }] } });
    renderConsole('/console/settings');
    expect(await screen.findByText('Cannot read the settings')).toBeTruthy();
  });

  it('is for the owner: an account sees why, and the call is not made', async () => {
    const api = serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/settings');
    await screen.findByRole('heading', { level: 1, name: 'Settings' });
    expect(api.calls.some((c) => c.path === '/v1/intake/limits')).toBe(false);
  });
});
