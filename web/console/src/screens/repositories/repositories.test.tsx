import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import * as adm from '../../testing/admin';
import { me } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  checkoutChecks,
  commandLine,
  prLabel,
  repoHistory,
  shortHead,
  testCommands,
} from './model';

const history = () => ({ '/v1/analytics': [200, adm.repositoryHistory()] as [number, unknown] });

/** The card of this repository. */
const card = (name: string) => {
  const section = screen.getByRole('heading', { name }).closest('section');
  if (!section) throw new Error(`no card ${name}`);
  return within(section as HTMLElement);
};

const open = async (extra: Parameters<typeof serveApi>[0] = {}) => {
  const api = serveApi({ replies: history(), ...extra });
  renderConsole('/console/repositories');
  await screen.findByRole('heading', { name: 'payments-api' });
  return api;
};

describe('repositories model', () => {
  const [payments, shop, infra] = adm.repositories().repositories;
  if (!payments || !shop || !infra) throw new Error('fixture');

  it('shortens a commit to seven characters and says nothing for none', () => {
    expect(shortHead(payments.head)).toBe('a91c0ff');
    expect(shortHead(null)).toBeNull();
  });

  it('reads the test command and the candidates it did not propose', () => {
    expect(commandLine({ command: ['npm', 'run', 'test'], source: 'package.json' })).toBe(
      'npm run test',
    );
    expect(testCommands(shop)).toEqual({ best: 'npm test', others: ['pytest'] });
    expect(testCommands(payments)).toEqual({ best: 'dotnet test', others: [] });
    expect(testCommands(infra)).toEqual({ best: null, others: [] });
  });

  it('checks what the read found: all well, and what is missing', () => {
    expect(checkoutChecks(payments).map((c) => c.level)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok']);
    const bad = checkoutChecks(infra);
    expect(bad.map((c) => c.level)).toEqual(['fail', 'warn', 'warn', 'warn', 'warn']);
    expect(bad[0]?.text).toBe('git could not read HEAD');
    expect(bad[2]?.text).toMatch(/No origin remote/);
    const unparsed = checkoutChecks({ ...payments, repository: null });
    expect(unparsed[2]).toMatchObject({ level: 'warn' });
  });

  it('adds up the weekly points of one repository by its path or its forge name', () => {
    const points = adm.repositoryHistory().tasks.points;
    const byPath = repoHistory(points, payments);
    expect(byPath.tasks).toBe(18);
    expect(byPath.completed).toBe(14);
    expect(byPath.rate).toBe(78);
    expect(byPath.weeks).toEqual([67, 75, 80, 100, 50]);
    expect(repoHistory(points, shop).rate).toBe(50);
    expect(repoHistory(points, infra)).toEqual({ weeks: [], tasks: 0, completed: 0, rate: null });
    const byName = repoHistory(
      points.map((p) => ({ ...p, group: 'example/payments-api' })),
      payments,
    );
    expect(byName.tasks).toBe(adm.repositoryHistory().tasks.tasks);
  });

  it('says what a pull request waits for', () => {
    const [pr] = payments.open_pull_requests;
    if (!pr) throw new Error('fixture');
    expect(prLabel(pr)).toEqual({ status: 'asked', text: 'Waiting for you' });
    expect(prLabel({ ...pr, awaiting_reason: 'partial_draft' }).text).toBe('Partial draft');
    expect(prLabel({ ...pr, awaiting_reason: 'test_integrity' }).text).toBe(
      'Test-integrity flagged',
    );
    expect(prLabel({ ...pr, awaiting_person: false })).toEqual({ status: 'waiting', text: 'Open' });
  });
});

describe('Repositories with data', () => {
  it('shows a card per checkout: branch and HEAD, remote, what was detected, and the test command', async () => {
    await open();
    const payments = card('payments-api');
    expect(payments.getByText('main @ a91c0ff')).toBeTruthy();
    expect(payments.getByText('example/payments-api').getAttribute('title')).toBe(
      'https://github.com/example/payments-api.git',
    );
    expect(payments.getByText('dotnet').getAttribute('title')).toBe('Payments.sln, global.json');
    expect(payments.getByText('dotnet test')).toBeTruthy();
    expect(payments.getByText('from Payments.sln')).toBeTruthy();
    expect(payments.getByText('merge').closest('.ch-badge')?.getAttribute('data-mode')).toBe(
      'merge',
    );
    expect(payments.getByText('watched by intake')).toBeTruthy();

    const shop = card('web-shop');
    expect(shop.getByText('node')).toBeTruthy();
    expect(shop.getByText('python')).toBeTruthy();
    expect(shop.getByText('npm test')).toBeTruthy();
    expect(shop.getByText('also found: pytest')).toBeTruthy();
    expect(shop.getByText('watched by intake')).toBeTruthy();
    expect(card('infra').queryByText('watched by intake')).toBeNull();
    expect(screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual([
      'Conflict radar',
      'Merge train',
      'payments-api',
      'web-shop',
      'infra',
      'Not recorded by the server',
    ]);
  });

  it('lists the checks of this read and says the doctor result is not recorded', async () => {
    await open();
    const payments = card('payments-api');
    expect(payments.getByText('HEAD a91c0ff is readable')).toBeTruthy();
    expect(payments.getByText('origin is example/payments-api')).toBeTruthy();
    expect(
      payments.getByText(/result not recorded\. The server does not run or keep it\./),
    ).toBeTruthy();
    expect(payments.getAllByRole('img', { name: 'OK' })).toHaveLength(5);

    const infra = card('infra');
    expect(infra.getByRole('img', { name: 'Failure' })).toBeTruthy();
    expect(infra.getByText('git could not read HEAD')).toBeTruthy();
    expect(infra.getAllByRole('img', { name: 'Warning' })).toHaveLength(4);
    expect(infra.getByText(/No origin remote/)).toBeTruthy();
    expect(infra.getByText('nothing detected')).toBeTruthy();
    expect(infra.getByText('none proposed')).toBeTruthy();
    expect(infra.getByText('off').closest('.ch-badge')?.getAttribute('data-mode')).toBe('off');
  });

  it('shows the success of the last ten weeks from the analytics, a sparkline with a text name, and an honest "no task"', async () => {
    await open();
    const payments = card('payments-api');
    expect(await payments.findByText('78% of 18 tasks')).toBeTruthy();
    expect(
      payments.getByRole('img', {
        name: /Share of tasks completed, per week: 67%, 75%, 80%, 100%, 50%/,
      }),
    ).toBeTruthy();
    expect(card('web-shop').getByText('50% of 2 tasks')).toBeTruthy();
    expect(card('infra').getByText('No finished task')).toBeTruthy();
    expect(card('infra').queryByRole('img', { name: /Share of tasks/ })).toBeNull();
  });

  it('opens the pull requests chargehand has open, links an http address and says what each waits for', async () => {
    await open();
    const payments = card('payments-api');
    const toggle = payments.getByRole('button', { name: '2 open PRs' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const link = payments.getByRole('link', { name: '#412' });
    expect(link.getAttribute('href')).toBe('https://github.com/example/payments-api/pull/412');
    expect(link.getAttribute('rel')).toBe('noreferrer noopener');
    expect(payments.getByText('Waiting for you')).toBeTruthy();
    expect(payments.getByText('Open')).toBeTruthy();
    fireEvent.click(toggle);
    expect(payments.queryByRole('link', { name: '#412' })).toBeNull();
    expect(card('web-shop').getByRole('button', { name: 'No open PR' })).toHaveProperty(
      'disabled',
      true,
    );
  });

  it('never links a pull request whose address is not http or https', async () => {
    const body = adm.repositories();
    const [first, ...rest] = body.repositories;
    if (!first) throw new Error('fixture');
    await open({
      repositories: {
        ...body,
        repositories: [
          {
            ...first,
            open_pull_requests: first.open_pull_requests.map((p) => ({
              ...p,
              url: 'data:text/html,x',
            })),
          },
          ...rest,
        ],
      },
    });
    fireEvent.click(card('payments-api').getByRole('button', { name: '2 open PRs' }));
    expect(card('payments-api').queryByRole('link', { name: '#412' })).toBeNull();
    expect(card('payments-api').getByText('#412')).toBeTruthy();
  });

  it('offers the doctor as a later affordance, disabled and labelled', async () => {
    await open();
    const button = screen.getByRole('button', { name: 'Run doctor on all' });
    expect(button).toHaveProperty('disabled', true);
    expect(button.parentElement?.querySelector('.later')?.textContent).toBe('later');
  });

  it('lists what the server does not record', async () => {
    await open();
    const section = screen
      .getByRole('heading', { name: 'Not recorded by the server' })
      .closest('section');
    expect(within(section as HTMLElement).getAllByRole('listitem')).toHaveLength(3);
    expect(within(section as HTMLElement).getByText(/the test command is a proposal/)).toBeTruthy();
  });

  it('warns of a repository root that is not a directory on the server', async () => {
    const body = adm.repositories();
    await open({
      repositories: {
        ...body,
        roots: [...body.roots, { path: '/srv/gone/checkouts', exists: false }],
      },
    });
    const banner = screen.getByText('A repository root is missing').closest('.ch-banner');
    expect(banner?.getAttribute('data-tone')).toBe('warn');
    expect(banner?.textContent).toContain('/srv/gone/checkouts');
  });

  it('stacks the cards in one column on a phone', async () => {
    viewport(true);
    await open();
    expect(screen.getAllByRole('heading', { level: 3 }).length).toBeGreaterThanOrEqual(3);
  });
});

describe('Repositories states', () => {
  it('shows a card-shaped skeleton while the first answer is on its way', async () => {
    serveApi({ hang: ['/v1/repositories'] });
    renderConsole('/console/repositories');
    expect(await screen.findByRole('heading', { name: 'Repositories', level: 1 })).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(5);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('names the failing call and offers Retry', async () => {
    const { calls } = serveApi({
      replies: { '/v1/repositories': [500, { errors: ['git is not installed'] }] },
    });
    renderConsole('/console/repositories');
    const box = (await screen.findByText('Cannot read the checkouts')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/repositories')).toBeTruthy();
    expect(box.textContent).toContain('git is not installed');
    const before = calls.filter((c) => c.path === '/v1/repositories').length;
    fireEvent.click(within(box).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/v1/repositories').length).toBeGreaterThan(before),
    );
  });

  it('says why there is nothing to show, and how to fill it', async () => {
    serveApi({ repositories: { ...adm.repositories(), repositories: [] } });
    renderConsole('/console/repositories');
    expect(await screen.findByText('No repository to show')).toBeTruthy();
    expect(screen.getByText(/chargehand init/)).toBeTruthy();
  });

  it('keeps the cards when the history cannot be read, and says so', async () => {
    serveApi({ replies: { '/v1/analytics': [503, { errors: ['learning log is locked'] }] } });
    renderConsole('/console/repositories');
    await screen.findByRole('heading', { name: 'payments-api' });
    const box = (await screen.findByText('Cannot read the success history')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/analytics')).toBeTruthy();
    expect(card('payments-api').getByText('Not loaded')).toBeTruthy();
    expect(card('payments-api').getByText('main @ a91c0ff')).toBeTruthy();
  });
});

describe('Repositories for an account', () => {
  it('shows the checkouts the key may use, with no owner-only state', async () => {
    const { calls } = await open({ me: me({ name: 'ci-bot', owner: false }) });
    expect(screen.queryByText('This screen needs the owner key')).toBeNull();
    expect(calls.some((c) => c.path === '/v1/repositories')).toBe(true);
    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(within(nav).getByRole('link', { name: 'Repositories' })).toBeTruthy();
  });
});

describe('Conflict radar', () => {
  const radar = async () => {
    const heading = await screen.findByRole('heading', { name: 'Conflict radar' });
    const section = heading.closest('section') as HTMLElement;
    await within(section)
      .findByRole('list', {
        name: 'Files both change in example/payments-api',
      })
      .catch(() => undefined);
    return within(section);
  };

  it('pairs open pull requests that change the same files, most shared first, with links to both', async () => {
    await open();
    const panel = await radar();
    const items = await panel.findAllByRole('list', { name: /Files both change in/ });
    expect(items).toHaveLength(2);
    expect(within(items[0] as HTMLElement).getAllByRole('listitem')).toHaveLength(5);
    expect(panel.getByText('and 2 more')).toBeTruthy();
    expect(panel.getByText('7 shared files')).toBeTruthy();
    expect(panel.getByText('1 shared file')).toBeTruthy();
    expect(panel.getAllByRole('link', { name: '#412' })[0]?.getAttribute('href')).toBe(
      'https://github.com/example/payments-api/pull/412',
    );
    expect(panel.getByRole('link', { name: '#412 issue-412' }).getAttribute('href')).toBe(
      '/console/runs/run-20261013-124239-1fdf01',
    );
    expect(panel.getByText(/#415 changed 900 files and its result kept fewer/)).toBeTruthy();
    expect(panel.getByText('Not compared, no changed paths recorded: #401.')).toBeTruthy();
  });

  it('says when no open pull requests share a file, and how many it compared', async () => {
    await open({ conflicts: adm.conflicts({ conflicts: [], open_pulls: 3, unread: [] }) });
    const panel = await radar();
    expect(await panel.findByText('No open pull requests share a file')).toBeTruthy();
    expect(panel.getByText('Compared 3 of 3 open pull requests chargehand opened.')).toBeTruthy();
  });

  it('never links a pull request whose address is not http or https', async () => {
    const body = adm.conflicts();
    const [first] = body.conflicts;
    if (first?.pulls[0]) first.pulls[0].url = 'javascript:alert(1)';
    await open({ conflicts: body });
    const panel = await radar();
    await panel.findByText('7 shared files');
    expect(panel.queryAllByRole('link', { name: '#412' })).toHaveLength(0);
  });

  it('names the failing call and keeps the checkouts', async () => {
    await open({
      replies: {
        ...history(),
        '/v1/pulls/conflicts': [503, { errors: ['the run log is locked'] }],
      },
    });
    const panel = await radar();
    expect(await panel.findByText('Cannot read the conflict radar')).toBeTruthy();
    expect(panel.getByText('GET /v1/pulls/conflicts')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'payments-api' })).toBeTruthy();
  });
});

describe('Merge train', () => {
  const train = async () => {
    const heading = await screen.findByRole('heading', { name: 'Merge train' });
    return within(heading.closest('section') as HTMLElement);
  };

  it('orders each repository oldest first and says which earlier cars a pull request must follow', async () => {
    await open();
    const panel = await train();
    const payments = await panel.findByRole('list', {
      name: 'Merge train of example/payments-api',
    });
    expect(
      within(payments)
        .getAllByRole('listitem')
        .map((li) => within(li).getByRole('link').textContent),
    ).toEqual(['#409', '#412', '#415']);
    expect(within(payments).getByText(/After 1 and 2: shares 7 files/)).toBeTruthy();
    const shop = panel.getByRole('list', { name: 'Merge train of example/web-shop' });
    expect(within(shop).getByText('waits for you to merge')).toBeTruthy();
    expect(
      panel.getByText(/Held, never merged: #401 \(unfinished work, stays a draft\)/),
    ).toBeTruthy();
  });

  it('says when nothing waits to merge', async () => {
    await open({ train: adm.train({ trains: [], held: [] }) });
    const panel = await train();
    expect(await panel.findByText('Nothing to merge')).toBeTruthy();
  });

  it('names the failing call', async () => {
    await open({ replies: { ...history(), '/v1/pulls/train': [503, { errors: ['locked'] }] } });
    const panel = await train();
    expect(await panel.findByText('Cannot read the merge train')).toBeTruthy();
    expect(panel.getByText('GET /v1/pulls/train')).toBeTruthy();
  });
});
