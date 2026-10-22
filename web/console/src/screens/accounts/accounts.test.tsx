import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import * as adm from '../../testing/admin';
import { me } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';
import { viewport } from '../../testing/viewport';
import {
  capsRows,
  credentialView,
  firstSelection,
  forecastView,
  keyViews,
  presenceText,
  repositoriesText,
  runsText,
  windowMeter,
} from './model';

const body = adm.accounts();
const byName = (name: string) => {
  const found = body.accounts.find((a) => a.name === name);
  if (!found) throw new Error(`no account ${name}`);
  return found;
};

/** The panel with this title. */
const panel = (title: string) => {
  const section = screen.getByRole('heading', { name: title }).closest('section');
  if (!section) throw new Error(`no panel ${title}`);
  return within(section as HTMLElement);
};

const rowOf = (name: string) =>
  within(screen.getByRole('table', { name: 'Accounts' }))
    .getAllByRole('row')
    .find((r) => within(r).queryByRole('button', { name })) as HTMLElement;

const open = async (extra: Parameters<typeof serveApi>[0] = {}) => {
  const api = serveApi(extra);
  renderConsole('/console/accounts');
  await screen.findByRole('table', { name: 'Accounts' });
  return api;
};

describe('accounts model', () => {
  it('measures spend in a window against the dollar cap, else the token cap, else says "no cap"', () => {
    const ci = byName('ci-bot');
    expect(windowMeter(ci, 'day')).toMatchObject({
      cap: 10,
      head: '$9.40 of $10.00',
      text: '94%',
      level: 'danger',
    });
    expect(windowMeter(ci, 'month')).toMatchObject({
      cap: 100,
      head: '$61.20 of $100.00',
      text: '61%',
    });
    const tokens = windowMeter({ ...ci, limits: { max_tokens_per_day: 8_000_000 } }, 'day');
    expect(tokens.head).toBe('1.88M of 8M tokens');
    expect(tokens.cap).toBe(8_000_000);
    expect(tokens.level).toBeUndefined();
    const none = windowMeter(byName('owner'), 'day');
    expect(none).toEqual({ value: 12.4, cap: null, head: '$12.40' });
  });

  it('names the repositories an account may use, or says any', () => {
    expect(repositoriesText(byName('ci-bot'))).toEqual({
      text: 'payments-api, web-shop',
      title: '/srv/work/payments-api\n/srv/work/web-shop',
    });
    expect(repositoriesText(byName('owner')).text).toBe('any');
    expect(repositoriesText(byName('unfunded')).text).toBe('any the server allows');
  });

  it('counts unfinished runs against the cap', () => {
    expect(runsText(byName('ci-bot'))).toBe('driven · 2 of 3');
    expect(runsText(byName('reviewer'))).toBe('read-only · 0 of 1');
    expect(runsText(byName('unfunded'))).toBe('driven · 0 of no cap');
  });

  it('reads the model credential as presence only, for every mode the server reports', () => {
    expect(credentialView(byName('ci-bot'))).toMatchObject({
      status: 'done',
      label: 'Own API key',
      presence: 'present',
      allowServer: null,
    });
    expect(credentialView(byName('gateway-user'))).toMatchObject({
      status: 'failed',
      label: 'Gateway key: item missing',
      presence: 'missing',
    });
    expect(credentialView(byName('reviewer'))).toMatchObject({
      status: 'asked',
      label: 'Server (allowed)',
      allowServer: true,
    });
    expect(credentialView(byName('unfunded'))).toMatchObject({
      status: 'failed',
      label: 'Missing',
      allowServer: false,
    });
    expect(credentialView(byName('owner')).label).toBe('Server credential');
    expect(credentialView(byName('owner')).allowServer).toBeNull();
    expect(credentialView({ ...byName('ci-bot'), credential: { mode: 'novel' } }).status).toBe(
      'queued',
    );
    expect(presenceText(credentialView(byName('ci-bot')))).toBe('The secret item resolves.');
    expect(presenceText(credentialView(byName('gateway-user')))).toBe(
      'The secret item does not resolve.',
    );
    expect(presenceText(credentialView(byName('reviewer')))).toBe('No secret item to check.');
  });

  it('shows a key by its label and public id, with its revocation, and nothing else', () => {
    expect(keyViews(byName('ci-bot').keys).map((k) => [k.id, k.label, k.revoked !== null])).toEqual(
      [
        ['k7f3a1', 'ci pipeline', false],
        ['k2b9c4', 'old runner', true],
      ],
    );
    expect(keyViews(byName('reviewer').keys)[0]?.label).toBe('unlabelled');
    expect(Object.keys(keyViews(byName('ci-bot').keys)[0] ?? {})).toEqual([
      'id',
      'label',
      'created',
      'revoked',
    ]);
  });

  it('opens on the first account that is not the owner', () => {
    expect(firstSelection(body.accounts)).toBe('ci-bot');
    expect(firstSelection([byName('owner')])).toBe('owner');
    expect(firstSelection([])).toBeUndefined();
  });

  it('lists every cap with the spend against it', () => {
    const rows = Object.fromEntries(capsRows(byName('ci-bot')));
    expect(rows['Dollars per day']).toBe('$9.40 of $10.00');
    expect(rows['Dollars per month']).toBe('$61.20 of $100.00');
    expect(rows['Tokens per day']).toBe('1.88M of 8M');
    expect(rows['Tokens per month']).toBe('12.2M of no cap');
    expect(rows['Concurrent runs']).toBe('2 of 3');
  });
});

describe('Accounts with data', () => {
  it('lists the owner and every account with role, repositories, spend against the cap, runs and credential', async () => {
    await open();
    const table = screen.getByRole('table', { name: 'Accounts' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(5);
    expect(panel('Accounts').getByText('5', { selector: '.ch-count' })).toBeTruthy();

    const ci = within(rowOf('ci-bot')).getAllByRole('cell');
    expect(ci[1]?.textContent).toBe('account');
    expect(ci[2]?.textContent).toBe('payments-api, web-shop');
    expect(ci[2]?.getAttribute('title')).toBe('/srv/work/payments-api\n/srv/work/web-shop');
    const day = within(ci[3] as HTMLElement).getByRole('meter');
    expect(day.getAttribute('aria-label')).toBe('ci-bot, spend today');
    expect(day.getAttribute('data-level')).toBe('danger');
    expect(within(day).getByText('$9.40 of $10.00')).toBeTruthy();
    expect(within(day).getByText('94%')).toBeTruthy();
    expect(within(ci[4] as HTMLElement).getByText('$61.20 of $100.00')).toBeTruthy();
    expect(ci[5]?.textContent).toBe('driven · 2 of 3');
    expect(ci[6]?.textContent).toBe('Own API key');
    expect(ci[7]?.textContent).toBe('later');

    const owner = within(rowOf('owner')).getAllByRole('cell');
    expect(owner[1]?.textContent).toBe('owner');
    const ownerMeter = within(owner[3] as HTMLElement).getByRole('meter');
    expect(ownerMeter.getAttribute('data-empty')).toBe('true');
    expect(within(ownerMeter).getByText('$12.40')).toBeTruthy();
    expect(within(ownerMeter).getByText('no cap')).toBeTruthy();
    expect(owner[6]?.textContent).toBe('Server credential');

    expect(within(rowOf('reviewer')).getAllByRole('cell')[5]?.textContent).toBe(
      'read-only · 0 of 1',
    );
    expect(within(rowOf('gateway-user')).getAllByRole('cell')[6]?.textContent).toBe(
      'Gateway key: item missing',
    );
    expect(within(rowOf('unfunded')).getAllByRole('cell')[6]?.textContent).toBe('Missing');
  });

  it('opens on the first account that is not the owner and switches its keys, credential and limits with the choice', async () => {
    await open();
    expect(screen.getByRole('heading', { name: 'Keys of ci-bot' })).toBeTruthy();
    expect(panel('Keys of ci-bot').getByText('ci pipeline')).toBeTruthy();
    expect(panel('Keys of ci-bot').getByText('k7f3a1')).toBeTruthy();
    expect(panel('Keys of ci-bot').getByText(/Revoked/)).toBeTruthy();
    expect(panel('Limits of ci-bot').getByText('$9.40 of $10.00')).toBeTruthy();
    expect(
      within(rowOf('ci-bot')).getByRole('button', { name: 'ci-bot' }).getAttribute('aria-pressed'),
    ).toBe('true');

    fireEvent.click(within(rowOf('reviewer')).getByRole('button', { name: 'reviewer' }));
    expect(screen.getByRole('heading', { name: 'Keys of reviewer' })).toBeTruthy();
    expect(panel('Keys of reviewer').getByText('unlabelled')).toBeTruthy();
    expect(panel('Keys of reviewer').getByText('k5d1e8')).toBeTruthy();
    expect(panel('Model credential').getByText('Server (allowed)')).toBeTruthy();
    expect(
      within(rowOf('reviewer'))
        .getByRole('button', { name: 'reviewer' })
        .getAttribute('aria-pressed'),
    ).toBe('true');
    expect(
      within(rowOf('ci-bot')).getByRole('button', { name: 'ci-bot' }).getAttribute('aria-pressed'),
    ).toBe('false');
  });

  it('selects an account by clicking anywhere on its row', async () => {
    await open();
    fireEvent.click(rowOf('gateway-user'));
    expect(screen.getByRole('heading', { name: 'Keys of gateway-user' })).toBeTruthy();
    expect(panel('Keys of gateway-user').getByText('No stored key')).toBeTruthy();
  });

  it('shows presence of the model credential and never a value, a key or a hash', async () => {
    await open();
    const cred = panel('Model credential');
    expect(cred.getByText('Own API key')).toBeTruthy();
    expect(cred.getByText('The secret item resolves.')).toBeTruthy();
    expect(cred.getByText(/the server credential is never spent/)).toBeTruthy();
    expect(cred.getByText('not reported for an account with its own key')).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/chk1\./);
    expect(document.body.textContent).not.toMatch(/sk-|sha256/);
  });

  it('says what a missing secret item means for the account, and shows allow_server_credential as a disabled switch', async () => {
    await open();
    fireEvent.click(within(rowOf('gateway-user')).getByRole('button', { name: 'gateway-user' }));
    expect(panel('Model credential').getByText('The secret item does not resolve.')).toBeTruthy();
    expect(
      panel('Model credential').getByText(
        /does not resolve, so a request that needs it is refused/,
      ),
    ).toBeTruthy();

    fireEvent.click(within(rowOf('unfunded')).getByRole('button', { name: 'unfunded' }));
    const toggle = panel('Model credential').getByRole('switch', {
      name: 'allow_server_credential',
    });
    expect(toggle).toHaveProperty('checked', false);
    expect(toggle).toHaveProperty('disabled', true);
    expect(
      panel('Model credential').getByText(/403 model_credential_missing/, { selector: 'p' }),
    ).toBeTruthy();

    fireEvent.click(within(rowOf('reviewer')).getByRole('button', { name: 'reviewer' }));
    expect(
      panel('Model credential').getByRole('switch', { name: 'allow_server_credential' }),
    ).toHaveProperty('checked', true);
  });

  it('offers no edit: every affordance is disabled and labelled "later"', async () => {
    await open();
    const add = screen.getByRole('button', { name: 'Add account' });
    expect(add).toHaveProperty('disabled', true);
    expect(add.parentElement?.querySelector('.later')?.textContent).toBe('later');
    const newKey = within(
      screen.getByRole('heading', { name: 'Keys of ci-bot' }).closest('section') as HTMLElement,
    ).getByRole('button', { name: 'New key' });
    expect(newKey).toHaveProperty('disabled', true);
    expect(newKey.parentElement?.querySelector('.later')).not.toBeNull();
    expect(panel('Limits of ci-bot').getByText('edit later')).toBeTruthy();
    const editCells = within(screen.getByRole('table', { name: 'Accounts' })).getAllByText('later');
    expect(editCells).toHaveLength(5);
    expect(screen.queryByRole('button', { name: /revoke|delete|save/i })).toBeNull();
  });

  it('says a key’s last use is not recorded, with the server’s own sentence', async () => {
    await open();
    expect(panel('Keys of ci-bot').getByText(/a key’s last use is not recorded/)).toBeTruthy();
    const nr = panel('Not recorded by the server');
    expect(nr.getAllByRole('listitem')).toHaveLength(2);
    expect(nr.getByText('A key’s last use is not recorded.')).toBeTruthy();
  });

  it('explains that the owner has no stored key', async () => {
    await open();
    fireEvent.click(within(rowOf('owner')).getByRole('button', { name: 'owner' }));
    expect(
      panel('Keys of owner').getByText(/The owner’s key is the profile’s api_key_secret/),
    ).toBeTruthy();
    expect(panel('Model credential').getByText('not an owner setting')).toBeTruthy();
  });

  it('puts an account on each card on a phone, with its spend meter and credential', async () => {
    viewport(true);
    serveApi();
    renderConsole('/console/accounts');
    await screen.findByRole('heading', { name: 'Keys of ci-bot' });
    const list = screen
      .getByRole('heading', { name: 'Accounts' })
      .closest('section') as HTMLElement;
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(5);
    expect(screen.queryByRole('table', { name: 'Accounts' })).toBeNull();
    expect(within(items[1] as HTMLElement).getByText('Own API key')).toBeTruthy();
    expect(within(items[1] as HTMLElement).getByRole('meter')).toBeTruthy();
    fireEvent.click(items[2] as HTMLElement);
    expect(screen.getByRole('heading', { name: 'Keys of reviewer' })).toBeTruthy();
  });
});

describe('Accounts states', () => {
  it('shows skeletons while the first answer is on its way', async () => {
    serveApi({ hang: ['/v1/accounts'] });
    renderConsole('/console/accounts');
    expect(
      await screen.findByRole('heading', { name: 'Accounts and keys', level: 1 }),
    ).toBeTruthy();
    expect(document.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(document.querySelectorAll('.ch-skel').length).toBeGreaterThan(2);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('names the failing call and offers Retry', async () => {
    const { calls } = serveApi({
      replies: { '/v1/accounts': [502, { errors: ['run log unreadable'] }] },
    });
    renderConsole('/console/accounts');
    const box = (await screen.findByText('Cannot read the accounts')).closest(
      '[role="alert"]',
    ) as HTMLElement;
    expect(within(box).getByText('GET /v1/accounts')).toBeTruthy();
    expect(box.textContent).toContain('run log unreadable');
    const before = calls.filter((c) => c.path === '/v1/accounts').length;
    fireEvent.click(within(box).getByRole('button', { name: 'Retry' }));
    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/v1/accounts').length).toBeGreaterThan(before),
    );
  });

  it('says only the owner exists when no account is declared, and how to declare one', async () => {
    serveApi({ accounts: { ...body, accounts: body.accounts.slice(0, 1) } });
    renderConsole('/console/accounts');
    expect(await screen.findByText('Only the owner exists')).toBeTruthy();
    expect(screen.getByText('http.accounts')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Keys of owner' })).toBeTruthy();
  });

  it('wraps a long list of repositories instead of cutting the table', async () => {
    const long = byName('ci-bot');
    serveApi({
      accounts: {
        ...body,
        accounts: [
          ...body.accounts,
          {
            ...long,
            name: 'ci-review-for-the-open-source-mirror',
            repositories: Array.from(
              { length: 6 },
              (_, i) => `/srv/work/a-very-long-repository-name-${i}`,
            ),
          },
        ],
      },
    });
    renderConsole('/console/accounts');
    await screen.findByRole('table', { name: 'Accounts' });
    expect(
      screen.getByRole('button', { name: 'ci-review-for-the-open-source-mirror' }),
    ).toBeTruthy();
  });
});

describe('Accounts for an account', () => {
  it('says the screen is owner only, and never calls the route that would answer 403', async () => {
    const { calls } = serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/accounts');
    expect(await screen.findByText('This screen needs the owner key')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Accounts and keys', level: 1 })).toBeTruthy();
    expect(screen.getByText('GET /v1/accounts')).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Accounts' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add account' })).toBeNull();
    expect(calls.some((c) => c.path === '/v1/accounts')).toBe(false);
  });

  it('shows the same state when the server answers 403', async () => {
    serveApi({ replies: { '/v1/accounts': [403, { errors: ['owner only'] }] } });
    renderConsole('/console/accounts');
    expect(await screen.findByText('This screen needs the owner key')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('Month forecast', () => {
  it('says the projection against the cap, the rate and what is spent so far', () => {
    expect(forecastView(byName('ci-bot'))).toMatchObject({
      over: true,
      text: '≈ $125.70 of $100.00',
      rate: '$6.50 a day over the last 7 days; $61.20 so far this month.',
    });
    expect(forecastView(byName('gateway-user'))?.over).toBe(false);
    expect(forecastView({ ...byName('ci-bot'), forecast: undefined })).toBeNull();
  });

  it('warns on the selected account when the forecast passes its cap, and not under it', async () => {
    await open();
    const forecast = panel('Month forecast');
    expect(forecast.getByRole('meter').getAttribute('aria-valuetext')).toBe('≈ $125.70 of $100.00');
    expect(forecast.getByText('The forecast passes the monthly cap')).toBeTruthy();

    fireEvent.click(within(rowOf('gateway-user')).getByRole('button', { name: 'gateway-user' }));
    await waitFor(() =>
      expect(panel('Month forecast').queryByText('The forecast passes the monthly cap')).toBeNull(),
    );
    expect(panel('Month forecast').getByText(/\$0\.20 a day/)).toBeTruthy();
  });

  it('leaves the panel out for a server that sends no forecast', async () => {
    const plain = adm.accounts();
    await open({
      accounts: { ...plain, accounts: plain.accounts.map((a) => ({ ...a, forecast: undefined })) },
    });
    expect(screen.queryByRole('heading', { name: 'Month forecast' })).toBeNull();
  });

  it('says automated starts are held on the account that carries them, and only warns without the opt-in', async () => {
    await open();
    const forecast = panel('Month forecast');
    expect(forecast.getByText('Automated starts are held')).toBeTruthy();
    expect(forecast.getByText(/tried again at the next poll/)).toBeTruthy();

    expect(forecastView({ ...byName('ci-bot'), pause_on_forecast: false })?.automation).toMatch(
      /pause_on_forecast is off: the forecast only warns/,
    );
    expect(forecastView({ ...byName('ci-bot'), pause_on_forecast: false })?.held).toBe(false);
    expect(forecastView(byName('gateway-user'))?.automation).toBeNull();
  });
});
