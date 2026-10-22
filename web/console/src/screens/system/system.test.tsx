import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import * as adm from '../../testing/admin';
import { me } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  containerCounts,
  containerRows,
  imageRows,
  presetRows,
  profileText,
  runnerStatusView,
  runnerView,
  uptime,
} from './model';

const full = adm.systemFull();

/** The panel with this title. */
const panel = (title: string) => {
  const section = screen.getByRole('heading', { name: title }).closest('section');
  if (!section) throw new Error(`no panel ${title}`);
  return within(section as HTMLElement);
};

/** The skeletons are gone: the screen holds the answer. */
const loaded = () =>
  waitFor(() => {
    expect(screen.getByRole('heading', { name: 'Version and engine' })).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).toBeNull();
  });

const open = async (extra: Parameters<typeof serveApi>[0] = {}) => {
  const api = serveApi(extra);
  renderConsole('/console/system');
  await loaded();
  return api;
};

describe('system model', () => {
  it('reads how long the server has been up', () => {
    const now = Date.parse('2026-10-13T12:00:00Z');
    expect(uptime('2026-10-10T10:00:00Z', now)).toBe('3d 2h');
    expect(uptime('2026-10-13T11:59:00Z', now)).toBe('1m');
  });

  it('says whether the runner answered, did not, or is not used', () => {
    expect(runnerView(full.runner)).toEqual({
      status: 'done',
      text: 'reachable in 38 ms',
      short: 'reachable',
    });
    expect(runnerView({ ...full.runner, reachable: false, error: 'no answer within 3 s' })).toEqual(
      {
        status: 'failed',
        text: 'unreachable: no answer within 3 s',
        short: 'unreachable',
        tone: 'danger',
      },
    );
    expect(
      runnerView({ ...full.runner, configured: true, reachable: null, elapsed_ms: null }).text,
    ).toBe('driven sessions are off');
    expect(runnerView({ ...full.runner, configured: false, reachable: null })).toEqual({
      status: 'off',
      text: 'not configured',
      short: 'not configured',
      tone: 'off',
    });
  });

  it('flags a runner whose allowlist is missing a configured session image', () => {
    const status = {
      name: 'laptop',
      host: 'http://laptop.example.test:4301',
      reachable: true,
      elapsed_ms: 12,
      draining: false,
      image_mismatch: 'image session@sha256:new is not on its allowlist',
    };
    expect(runnerStatusView(status)).toEqual({
      status: 'failed',
      text: 'image session@sha256:new is not on its allowlist',
      short: 'image mismatch',
      tone: 'danger',
    });
    // Draining is reported first: it is why the runner is unusable right now, same as the mismatch would be.
    expect(runnerStatusView({ ...status, draining: true }).short).toBe('draining');
  });

  it('ties a container to its task or batch, and only a session can be stopped', () => {
    const rows = containerRows(full.runner);
    expect(rows).toHaveLength(8);
    expect(rows[0]).toMatchObject({
      kind: 'session',
      owner: 't3-memory-ttl · 1fdf01',
      actor: 'owner',
      session: true,
    });
    expect(rows.find((r) => r.kind === 'egress')).toMatchObject({
      owner: 'batch 1fdf01',
      session: false,
    });
    expect(rows.find((r) => r.name === 'chargehand-stray')).toMatchObject({
      kind: 'other',
      owner: '—',
      actor: '—',
    });
    expect(containerCounts(full.runner)).toBe(
      '3 session · 2 egress · 1 service · 1 workspace · 1 other',
    );
  });

  it('lists presets with their version and kind, and a preset that cannot load with its error', () => {
    const rows = presetRows(full.presets);
    expect(rows.map((r) => [r.name, r.version, r.mode])).toEqual([
      ['default', '1.4.0', 'read-only'],
      ['driven', '2.1.0', 'driven'],
      ['review', '1.0.2', 'approval'],
      ['draft', '—', 'read-only'],
    ]);
    expect(rows[3]?.error).toBe('preset file is not valid YAML');
  });

  it('shortens a digest in the middle and tells a pinned image from one that is not', () => {
    const [pinned, loose] = imageRows(full.images);
    expect(pinned?.pinned).toBe(true);
    expect(pinned?.digest).toMatch(/^sha256:6f3a1c9….*b4c5d$/);
    expect(pinned?.digest.length).toBeLessThanOrEqual(28);
    expect(loose).toMatchObject({ pinned: false, digest: 'no digest' });
  });

  it('prints the profile as the route returns it, and nothing when there is none', () => {
    const text = profileText(full);
    expect(JSON.parse(text)).toEqual(full.profile);
    expect(profileText({ ...full, profile: null })).toBe('');
  });
});

describe('System with data', () => {
  it('shows the version, uptime, runner, governor and halt state', async () => {
    await open();
    const engine = panel('Version and engine');
    expect(engine.getByText('0.9.1')).toBeTruthy();
    expect(engine.getByText(/up 3d 2h/)).toBeTruthy();
    expect(engine.getByText(/reachable in 38 ms/)).toBeTruthy();
    expect(engine.getByText(/runner-a\.example\.test:4301/)).toBeTruthy();
    expect(engine.getByText('11')).toBeTruthy();
    expect(engine.getByText('observe').closest('.ch-badge')?.getAttribute('data-tone')).toBe(
      'info',
    );
    expect(engine.getByText('Accepting runs')).toBeTruthy();
    expect(engine.getByText('2')).toBeTruthy();
  });

  it('lists the live containers with their kind and task, and offers Stop for a session only, disabled and labelled', async () => {
    await open();
    const table = screen.getByRole('table', { name: 'Live containers' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(8);
    const first = within(rows[0] as HTMLElement).getAllByRole('cell');
    expect(first[0]?.textContent).toBe('chargehand-run-20261013-124239-1fdf01-t3');
    expect(first[1]?.textContent).toBe('session');
    expect(first[2]?.textContent).toBe('t3-memory-ttl · 1fdf01');
    expect(first[3]?.textContent).toBe('owner');
    const stops = within(table).getAllByRole('button', { name: 'Stop' });
    expect(stops).toHaveLength(3);
    for (const stop of stops) expect(stop).toHaveProperty('disabled', true);
    expect(
      within(within(table).getAllByRole('row')[0] as HTMLElement).getByText('later'),
    ).toBeTruthy();
    expect(panel('Live containers').getByText('11 on the runner')).toBeTruthy();
    expect(
      panel('Live containers').getByText(/Stopping one container has no route yet/),
    ).toBeTruthy();
  });

  it('lists presets with their versions, and a preset that cannot be loaded as a failure', async () => {
    await open();
    const table = screen.getByRole('table', { name: 'Presets and prompt versions' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(4);
    const driven = within(rows[1] as HTMLElement).getAllByRole('cell');
    expect(driven.map((c) => c.textContent)).toEqual([
      'driven',
      '2.1.0',
      'driven',
      'read, write, test',
    ]);
    const draft = within(rows[3] as HTMLElement).getAllByRole('cell');
    expect(draft[2]?.textContent).toBe('Cannot load');
    expect(draft[3]?.textContent).toBe('preset file is not valid YAML');
    expect(
      panel('Presets and prompt versions').getByText(/Prompt CI is not recorded here/),
    ).toBeTruthy();
  });

  it('lists each runner the profile names with its state, draining included', async () => {
    await open();
    const rows = within(screen.getByRole('table', { name: 'Runners' }))
      .getAllByRole('row')
      .slice(1);
    expect(rows).toHaveLength(2);
    const [local, second] = rows.map((r) =>
      within(r)
        .getAllByRole('cell')
        .map((c) => c.textContent),
    );
    expect(local).toEqual([
      'local',
      'http://runner-a.example.test:4301',
      'linux/amd64',
      '1',
      '3 / 4',
      'reachable',
    ]);
    expect(second).toEqual([
      'second',
      'http://runner-b.example.test:4301',
      'linux/arm64',
      '0.5',
      '1 / 2',
      'draining',
    ]);
    expect(within(rows[1] as HTMLElement).getByTitle('draining: host update')).toBeTruthy();
  });

  it("shows a runner's allowlist mismatch and the reason", async () => {
    await open({
      system: {
        ...full,
        runners: [
          {
            name: 'local',
            host: 'http://runner-a.example.test:4301',
            reachable: true,
            elapsed_ms: 38,
            draining: false,
            running: 3,
            max_sessions: 4,
            container_count: 11,
            image_mismatch: 'image session@sha256:new is not on its allowlist',
          },
        ],
      },
    });
    const rows = within(screen.getByRole('table', { name: 'Runners' }))
      .getAllByRole('row')
      .slice(1);
    expect(within(rows[0] as HTMLElement).getByText('image mismatch')).toBeTruthy();
    expect(
      within(rows[0] as HTMLElement).getByTitle('image session@sha256:new is not on its allowlist'),
    ).toBeTruthy();
  });

  it("falls back to the first runner's probe when the server does not serve runners", async () => {
    await open({ system: { ...full, runners: null } });
    const rows = within(screen.getByRole('table', { name: 'Runners' }))
      .getAllByRole('row')
      .slice(1);
    expect(within(rows[0] as HTMLElement).getByText('reachable')).toBeTruthy();
    expect(within(rows[1] as HTMLElement).getByText('unknown')).toBeTruthy();
  });

  it('shows the host the runner reads for admission: memory against the reserve, and load', async () => {
    await open();
    const host = panel('Runner host');
    const meter = host.getByRole('meter');
    expect(meter.getAttribute('aria-label')).toBe('Memory available');
    expect(within(meter).getByText('14.0 GiB of 32.0 GiB')).toBeTruthy();
    expect(host.getByText('4.0 GiB')).toBeTruthy();
    expect(host.getByText('10.0 GiB')).toBeTruthy();
    expect(host.getByText('2.4 on 8 CPUs')).toBeTruthy();
  });

  it('shows pinned images by digest and flags one that is not pinned', async () => {
    await open();
    const images = panel('Pinned images');
    const items = images.getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(within(items[0] as HTMLElement).getByText('Pinned')).toBeTruthy();
    expect(within(items[0] as HTMLElement).getByText(/^sha256:6f3a/)).toBeTruthy();
    expect(within(items[1] as HTMLElement).getByText('Not pinned')).toBeTruthy();
    expect(within(items[1] as HTMLElement).getByText('no digest')).toBeTruthy();
  });

  it('prints the redacted profile: secret items as booleans, addresses as scheme, host and port', async () => {
    await open();
    const profile = panel('Profile');
    const text = profile.getByRole('region', { name: 'Redacted profile' }).textContent ?? '';
    expect(profile.getByText('redacted')).toBeTruthy();
    expect(JSON.parse(text)).toEqual(full.profile);
    expect(text).toContain('"api_key_set": true');
    expect(text).toContain('"push_secret_set": true');
    expect(text).not.toMatch(/sk-|password|token_secret|api_key_secret/);
    expect(profile.getByText(/a secret item is set or not set/)).toBeTruthy();
  });

  it('lists what the server does not record', async () => {
    await open();
    const nr = panel('Not recorded by the server');
    expect(nr.getAllByRole('listitem')).toHaveLength(3);
    expect(nr.getByText(/start time, state and memory use are not served/)).toBeTruthy();
  });

  it('shows the lists as cards on a phone, without the tables', async () => {
    viewport(true);
    serveApi();
    renderConsole('/console/system');
    await loaded();
    expect(screen.queryByRole('table', { name: 'Live containers' })).toBeNull();
    expect(panel('Live containers').getAllByRole('listitem')).toHaveLength(8);
    expect(panel('Presets and prompt versions').getAllByRole('listitem')).toHaveLength(4);
    expect(panel('Runners').getByText('draining')).toBeTruthy();
    expect(panel('Live containers').getAllByRole('button', { name: 'Stop' })).toHaveLength(3);
  });
});

describe('System halt and resume', () => {
  it('asks before halting, and halts only on the confirm', async () => {
    const { calls } = await open();
    fireEvent.click(
      screen.getByRole('main').querySelector('.screen__actions button') as HTMLElement,
    );
    const dialog = await screen.findByRole('alertdialog', { name: 'Halt the server?' });
    expect(calls.some((c) => c.path === '/v1/halt')).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep running' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(calls.some((c) => c.path === '/v1/halt')).toBe(false);
  });

  it('shows Resume server instead of Halt when the server is halted, and resumes', async () => {
    const { calls } = await open({ system: { ...full, halted: true }, me: me({ halted: true }) });
    expect(screen.queryByRole('button', { name: 'Halt server' })).toBeNull();
    expect(panel('Version and engine').getByText('Halted: nothing starts')).toBeTruthy();
    const actions = screen.getByRole('main').querySelector('.screen__actions') as HTMLElement;
    fireEvent.click(within(actions).getByRole('button', { name: 'Resume server' }));
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/v1/resume' && c.method === 'POST')).toBe(true),
    );
  });
});

describe('System states', () => {
  it('shows skeletons while the first answer is on its way', async () => {
    serveApi({ hang: ['/v1/system'] });
    renderConsole('/console/system');
    expect(await screen.findByRole('heading', { name: 'System', level: 1 })).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(4);
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Halt server' })).toBeNull();
  });

  it('names the failing call and offers Retry', async () => {
    const { calls } = serveApi({
      replies: { '/v1/system': [500, { errors: ['profile unreadable'] }] },
    });
    renderConsole('/console/system');
    const box = (await screen.findByText('Cannot read the system')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/system')).toBeTruthy();
    expect(box.textContent).toContain('profile unreadable');
    const before = calls.filter((c) => c.path === '/v1/system').length;
    fireEvent.click(within(box).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/v1/system').length).toBeGreaterThan(before),
    );
  });

  it('says the runner does not answer, with what it said, and keeps the rest of the screen', async () => {
    await open({
      system: {
        ...full,
        runner: {
          ...full.runner,
          reachable: false,
          error: 'no answer within 3 s',
          container_count: null,
          containers: [],
          host_snapshot: null,
        },
      },
    });
    const box = panel('Live containers');
    expect(box.getByText('The runner does not answer')).toBeTruthy();
    expect(box.getByText('no answer within 3 s')).toBeTruthy();
    expect(panel('Version and engine').getByText(/unreachable: no answer within 3 s/)).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Runner host' })).toBeNull();
    expect(panel('Presets and prompt versions')).toBeTruthy();
  });

  it('says driven sessions are off when the server has no runner to ask', async () => {
    await open({
      system: {
        ...full,
        runner: {
          ...full.runner,
          configured: false,
          reachable: null,
          containers: [],
          container_count: null,
        },
        profile: full.profile && { ...full.profile, driven: null },
      },
    });
    expect(screen.getByText('Driven sessions are off')).toBeTruthy();
    expect(screen.getByText('No runner service listed')).toBeTruthy();
    expect(panel('Version and engine').getByText(/not configured/)).toBeTruthy();
  });

  it('says no container is running when the runner answers with none', async () => {
    await open({
      system: { ...full, runner: { ...full.runner, containers: [], container_count: 0 } },
    });
    expect(screen.getByText('No container is running')).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Live containers' })).toBeNull();
  });

  it('says there is no profile when the server runs without one', async () => {
    await open({ system: { ...full, profile: null } });
    expect(screen.getByText('No profile loaded')).toBeTruthy();
  });

  it('keeps a long container name and a long preset error readable', async () => {
    const longName = `chargehand-svc-${'x'.repeat(80)}-run-20261013-124239-1fdf01`;
    await open({
      system: {
        ...full,
        runner: {
          ...full.runner,
          containers: [
            { name: longName, kind: 'service', batch_run_id: 'run-20261013-124239-1fdf01' },
          ],
        },
      },
    });
    expect(screen.getByText(longName).getAttribute('title')).toBe(longName);
  });
});

describe('System for an account', () => {
  it('says the screen is owner only, and never calls the route that would answer 403', async () => {
    const { calls } = serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/system');
    expect(await screen.findByText('This screen needs the owner key')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'System', level: 1 })).toBeTruthy();
    expect(screen.getByText('GET /v1/system')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Halt server' })).toBeNull();
    expect(calls.some((c) => c.path === '/v1/system')).toBe(false);
  });

  it('shows the same state when the server answers 403', async () => {
    serveApi({ system: null });
    renderConsole('/console/system');
    expect(await screen.findByText('This screen needs the owner key')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
