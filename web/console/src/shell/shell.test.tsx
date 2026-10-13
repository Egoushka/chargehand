import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { BATCH_A, me, overview, run } from '../testing/fixtures';
import { renderConsole } from '../testing/render';
import { serveApi } from '../testing/server';
import { viewport } from '../testing/viewport';
import { getPreferences } from './preferences';

const press = (key: string, init: KeyboardEventInit = {}) =>
  act(() => {
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, ...init }));
  });

afterEach(() => {
  document.documentElement.removeAttribute('data-theme');
});

describe('the rail (desktop)', () => {
  it('lists the screens in three groups, marks the open one, and shows what waits for you', async () => {
    serveApi();
    renderConsole('/console/runs');
    const rail = await screen.findByRole('navigation', { name: 'Main' });
    const links = within(rail)
      .getAllByRole('link')
      .map((a) => a.getAttribute('aria-label'));
    await waitFor(() =>
      expect(within(rail).getByRole('link', { name: 'Needs you, 3 waiting' })).toBeTruthy(),
    );
    expect(links.slice(0, 4)).toEqual([
      'Overview',
      expect.stringContaining('Needs you'),
      'Runs',
      'Start a batch',
    ]);
    expect(within(rail).getByText('Insight')).toBeTruthy();
    expect(within(rail).getByText('Configure')).toBeTruthy();
    expect(within(rail).getByRole('link', { name: 'Runs' }).getAttribute('aria-current')).toBe(
      'page',
    );
    expect(within(rail).getByRole('link', { name: 'Intake and automation' })).toBeTruthy();
  });

  it('collapses with [ and from its button, and remembers it', async () => {
    serveApi();
    renderConsole('/console/');
    const rail = await screen.findByRole('navigation', { name: 'Main' });
    expect(rail.className).not.toContain('ch-rail--collapsed');
    await press('[');
    expect(rail.className).toContain('ch-rail--collapsed');
    expect(getPreferences().railCollapsed).toBe(true);
    // Each item keeps its name when its label is hidden.
    expect(within(rail).getByRole('link', { name: 'Runs' })).toBeTruthy();
    fireEvent.click(within(rail).getByRole('button', { name: 'Expand the rail' }));
    expect(rail.className).not.toContain('ch-rail--collapsed');
  });
});

describe('the top bar', () => {
  it('names the screen and shows the live state with the time in Kyiv', async () => {
    serveApi();
    renderConsole('/console/needs-you');
    const crumb = await screen.findByRole('navigation', { name: 'Breadcrumb' });
    expect(within(crumb).getByText('Needs you').getAttribute('aria-current')).toBe('page');
    const live = await screen.findByText(/^Live · Kyiv \d\d:\d\d$/);
    expect(live.getAttribute('title')).toBe('Kyiv time');
  });

  it('shows a run under Runs, with a link back', async () => {
    serveApi();
    renderConsole(`/console/runs/${BATCH_A}`);
    const crumb = await screen.findByRole('navigation', { name: 'Breadcrumb' });
    expect(within(crumb).getByRole('link', { name: 'Runs' }).getAttribute('href')).toBe(
      '/console/runs',
    );
    expect(within(crumb).getByText(BATCH_A).getAttribute('aria-current')).toBe('page');
  });

  it('says Halted, Paused or Offline instead of Live when it is so', async () => {
    const base = overview();
    serveApi({
      overview: overview({ server: { ...base.server, halted: true } }),
      me: me({ halted: true }),
    });
    renderConsole('/console/runs');
    expect(await screen.findByText(/^Halted · Kyiv/)).toBeTruthy();
  });

  it('toggles dark and light, on the page and in storage', async () => {
    serveApi();
    renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    expect(document.documentElement.dataset.theme).toBe('dark');
    fireEvent.click(screen.getByRole('button', { name: 'Toggle dark and light theme' }));
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(JSON.parse(localStorage.getItem('chargehand.console') ?? '{}').theme).toBe('light');
    fireEvent.click(screen.getByRole('button', { name: 'Toggle dark and light theme' }));
    expect(document.documentElement.dataset.theme).toBe('dark');
  });

  it('cycles the row density: default, comfortable, compact', async () => {
    serveApi();
    renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    const cycle = () =>
      fireEvent.click(screen.getByRole('button', { name: /^Row density: .*Change it$/ }));
    expect(document.body.dataset.density).toBe('default');
    cycle();
    expect(document.body.dataset.density).toBe('comfortable');
    cycle();
    expect(document.body.dataset.density).toBe('compact');
    cycle();
    expect(document.body.dataset.density).toBe('default');
  });

  it('keeps who is signed in and today’s spend in the account menu', async () => {
    serveApi({ me: me({ name: 'ci-bot', owner: false, limits: { max_usd_per_day: 40 } }) });
    renderConsole('/console/');
    fireEvent.keyDown(await screen.findByRole('button', { name: 'Account' }), { key: 'Enter' });
    expect(await screen.findByText('ci-bot')).toBeTruthy();
    expect(screen.getByText(/Account key · today \$26\.14 of \$40\.00/)).toBeTruthy();
  });
});

describe('the command palette', () => {
  const open = async () => {
    await screen.findByRole('navigation', { name: 'Main' });
    await press('k', { metaKey: true });
    return screen.findByRole('dialog', { name: 'Command palette', hidden: true });
  };

  it('opens with ⌘K or Ctrl+K from anywhere, and with the search button', async () => {
    serveApi();
    renderConsole('/console/');
    expect(await open()).toBeTruthy();
    fireEvent.keyDown(document.body, { key: 'Escape' });
    (screen.getByRole('dialog', { hidden: true }) as HTMLDialogElement).close();
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    await press('k', { ctrlKey: true });
    expect(
      await screen.findByRole('dialog', { name: 'Command palette', hidden: true }),
    ).toBeTruthy();
  });

  it('lists the screens with their shortcuts, then actions; the destructive one says it asks first', async () => {
    serveApi();
    renderConsole('/console/');
    const palette = within(await open());
    const options = await palette.findAllByRole('option', { hidden: true });
    const labels = options.map((o) => o.querySelector('.ch-cmd__label')?.textContent);
    expect(labels).toContain('Overview');
    expect(labels).toContain('Halt server');
    const halt = options.find((o) => o.textContent?.includes('Halt server'));
    expect(halt?.querySelector('.ch-cmd__meta')?.textContent).toBe('asks first');
    const needs = options.find((o) => o.textContent?.startsWith('Needs you'));
    expect([...(needs?.querySelectorAll('kbd') ?? [])].map((k) => k.textContent)).toEqual([
      'g',
      'n',
    ]);
  });

  it('finds a screen by typing, shows the match, and goes there', async () => {
    serveApi();
    const { router } = renderConsole('/console/');
    const palette = within(await open());
    fireEvent.change(palette.getByRole('combobox', { hidden: true }), {
      target: { value: 'anal' },
    });
    const option = await palette.findByRole('option', { name: /Anal\s?ytics/, hidden: true });
    expect(option.querySelector('mark')?.textContent).toBe('Anal');
    fireEvent.click(option);
    await waitFor(() => expect(router.state.location.pathname).toBe('/analytics'));
    expect(screen.queryByRole('dialog', { hidden: true })).toBeNull();
  });

  it('finds a run by the six-hex suffix of its id', async () => {
    serveApi({ runs: [run(BATCH_A), run('run-20261013-124402-77bd0a')] });
    const { router } = renderConsole('/console/');
    const palette = within(await open());
    fireEvent.change(palette.getByRole('combobox', { hidden: true }), {
      target: { value: '77bd0a' },
    });
    const option = await palette.findByRole('option', { name: /Batch 77bd0a/, hidden: true });
    expect(option.querySelector('.ch-cmd__meta')?.textContent).toBe('payments-api · running');
    fireEvent.click(option);
    await waitFor(() =>
      expect(router.state.location.pathname).toBe('/runs/run-20261013-124402-77bd0a'),
    );
  });

  it('finds the tasks that wait for you', async () => {
    serveApi();
    renderConsole('/console/');
    const palette = within(await open());
    fireEvent.change(palette.getByRole('combobox', { hidden: true }), {
      target: { value: 'memory-ttl' },
    });
    const option = await palette.findByRole('option', { name: /t3-\s?memory-ttl/, hidden: true });
    expect(option.querySelector('.ch-cmd__meta')?.textContent).toBe('payments-api · question');
  });

  it('says so when nothing matches, and tells how to find a run', async () => {
    serveApi();
    renderConsole('/console/');
    const palette = within(await open());
    fireEvent.change(palette.getByRole('combobox', { hidden: true }), {
      target: { value: 'zzzzqq' },
    });
    expect(
      await palette.findByText('No match for this text. Try a run id suffix such as 1fdf01.'),
    ).toBeTruthy();
  });

  it('asks before halting from the palette, and offers Resume instead when halted', async () => {
    serveApi();
    const { unmount } = renderConsole('/console/');
    const palette = within(await open());
    fireEvent.click(await palette.findByRole('option', { name: /Halt server/, hidden: true }));
    expect(
      await screen.findByRole('alertdialog', { name: 'Halt the server?', hidden: true }),
    ).toBeTruthy();
    unmount();

    const base = overview();
    serveApi({
      overview: overview({ server: { ...base.server, halted: true } }),
      me: me({ halted: true }),
    });
    renderConsole('/console/');
    const again = within(await open());
    expect(await again.findByRole('option', { name: /Resume server/, hidden: true })).toBeTruthy();
    expect(again.queryByRole('option', { name: /Halt server/, hidden: true })).toBeNull();
  });

  it('leaves halt and the owner’s screens out for an account', async () => {
    serveApi({ me: me({ name: 'ci-bot', owner: false }) });
    renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    await waitFor(() => expect(screen.queryByRole('link', { name: 'System' })).toBeNull());
    await press('k', { metaKey: true });
    const palette = within(
      await screen.findByRole('dialog', { name: 'Command palette', hidden: true }),
    );
    await palette.findAllByRole('option', { hidden: true });
    expect(palette.queryByRole('option', { name: /Halt server|^System/, hidden: true })).toBeNull();
  });
});

describe('keyboard shortcuts', () => {
  it('goes to a screen with g and a letter', async () => {
    serveApi();
    const { router } = renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    await press('g');
    await press('n');
    await waitFor(() => expect(router.state.location.pathname).toBe('/needs-you'));
    await press('g');
    await press('r');
    await waitFor(() => expect(router.state.location.pathname).toBe('/runs'));
    await press('g');
    await press('s');
    await waitFor(() => expect(router.state.location.pathname).toBe('/new'));
    await press('g');
    await press('a');
    await waitFor(() => expect(router.state.location.pathname).toBe('/analytics'));
    await press('g');
    await press('o');
    await waitFor(() => expect(router.state.location.pathname).toBe('/'));
  });

  it('does nothing for g and a letter that goes nowhere, or while typing', async () => {
    serveApi();
    const { router } = renderConsole('/console/needs-you');
    await screen.findAllByRole('article');
    await press('g');
    await press('z');
    expect(router.state.location.pathname).toBe('/needs-you');
    const box = screen.getByLabelText('Your answer');
    box.focus();
    act(() => {
      box.dispatchEvent(new KeyboardEvent('keydown', { key: 'g', bubbles: true }));
      box.dispatchEvent(new KeyboardEvent('keydown', { key: 'o', bubbles: true }));
    });
    expect(router.state.location.pathname).toBe('/needs-you');
  });

  it('opens the sheet with ?, lists the keys that work, and says halt has no bare key', async () => {
    serveApi();
    renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    await press('?');
    const sheet = within(
      await screen.findByRole('dialog', { name: 'Keyboard shortcuts', hidden: true }),
    );
    for (const group of ['Anywhere', 'Go to', 'Lists', 'On a task'])
      expect(sheet.getByRole('region', { name: group, hidden: true })).toBeTruthy();
    expect(sheet.getByText('Command palette')).toBeTruthy();
    expect(sheet.getByText('Start a batch')).toBeTruthy();
    expect(sheet.getByText(/Cancel and halt have no bare shortcut/)).toBeTruthy();
  });

  it('ignores a bare key while a dialog holds the keyboard', async () => {
    serveApi();
    const { router } = renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    await press('?');
    await screen.findByRole('dialog', { name: 'Keyboard shortcuts', hidden: true });
    await press('g');
    await press('r');
    expect(router.state.location.pathname).toBe('/');
  });

  it('focuses the filter on / when the screen has one, and lets the key be when it has none', async () => {
    serveApi();
    renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    const event = new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true });
    act(() => {
      document.body.dispatchEvent(event);
    });
    expect(event.defaultPrevented).toBe(false);
    const input = document.createElement('input');
    input.setAttribute('data-filter-input', '');
    document.body.append(input);
    const second = new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true });
    act(() => {
      document.body.dispatchEvent(second);
    });
    expect(second.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(input);
    input.remove();
  });
});

describe('a phone', () => {
  it('has a tab bar of Overview, Needs you with its count, Runs, Start and More, and no rail', async () => {
    viewport(true);
    serveApi();
    renderConsole('/console/');
    const bar = await screen.findByRole('navigation', { name: 'Main' });
    expect(bar.className).toBe('ch-tabbar');
    expect(document.querySelector('.ch-rail')).toBeNull();
    await waitFor(() =>
      expect(within(bar).getByRole('link', { name: 'Needs you, 3 waiting' })).toBeTruthy(),
    );
    expect(
      within(bar)
        .getAllByRole('link')
        .map((a) => a.textContent),
    ).toEqual(['Overview', 'Needs you3', 'Runs', 'Start']);
    expect(within(bar).getByRole('button', { name: 'More' })).toBeTruthy();
    expect(within(bar).getByRole('link', { name: 'Overview' }).getAttribute('aria-current')).toBe(
      'page',
    );
  });

  it('opens the rest from More, with the theme and sign out', async () => {
    viewport(true);
    serveApi();
    const { router } = renderConsole('/console/');
    fireEvent.click(await screen.findByRole('button', { name: 'More' }));
    const sheet = within(await screen.findByRole('dialog', { name: 'More', hidden: true }));
    const labels = sheet.getAllByRole('button', { hidden: true }).map((b) => b.textContent);
    expect(labels).toEqual([
      'Analytics',
      'Learning',
      'Intake and automation',
      'Repositories',
      'Accounts and keys',
      'System',
      'Toggle dark and light theme',
      'Sign out',
    ]);
    fireEvent.click(sheet.getByRole('button', { name: 'Learning', hidden: true }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/learning'));
    expect(screen.queryByRole('dialog', { hidden: true })).toBeNull();
  });

  it('shows a back button on a run, and a search button instead of the search box', async () => {
    viewport(true);
    serveApi();
    const { router } = renderConsole(`/console/runs/${BATCH_A}`);
    fireEvent.click(await screen.findByRole('button', { name: 'Back to runs' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/runs'));
    expect(screen.getByRole('button', { name: 'Search' })).toBeTruthy();
    expect(screen.queryByText('Search or jump to')).toBeNull();
  });
});

describe('theme', () => {
  it('follows a browser that asks for light until the person chooses, and dark otherwise', async () => {
    viewport(false, 'light');
    localStorage.clear();
    const { resetPreferences } = await import('./preferences');
    resetPreferences();
    expect(getPreferences().theme).toBe('light');
    expect(document.documentElement.dataset.theme).toBe('light');
    viewport(false, 'dark');
    resetPreferences();
    expect(getPreferences().theme).toBe('dark');
  });

  it('prefers what the person chose to what the browser asks', async () => {
    viewport(false, 'light');
    localStorage.setItem('chargehand.console', JSON.stringify({ theme: 'dark' }));
    const { resetPreferences } = await import('./preferences');
    resetPreferences();
    expect(getPreferences().theme).toBe('dark');
  });

  it('survives storage that is broken, blocked or has strangers in it', async () => {
    const { resetPreferences } = await import('./preferences');
    localStorage.setItem('chargehand.console', '{not json');
    resetPreferences();
    expect(getPreferences()).toMatchObject({
      theme: 'dark',
      density: 'default',
      railCollapsed: false,
    });
    localStorage.setItem(
      'chargehand.console',
      JSON.stringify({ theme: 'purple', density: 'huge', notify: { batch: false } }),
    );
    resetPreferences();
    expect(getPreferences()).toMatchObject({ theme: 'dark', density: 'default' });
    expect(getPreferences().notify).toEqual({
      question: true,
      pull_request: true,
      batch: false,
      failure: true,
    });
  });
});

describe('focus', () => {
  it('moves to the main area when the screen changes, and the skip link goes there', async () => {
    serveApi();
    const { router } = renderConsole('/console/');
    await screen.findByRole('navigation', { name: 'Main' });
    const main = document.getElementById('main') as HTMLElement;
    fireEvent.click(screen.getByRole('button', { name: 'Skip to content' }));
    expect(document.activeElement).toBe(main);
    (document.activeElement as HTMLElement).blur();
    await act(() => router.navigate({ to: '/runs' }));
    await waitFor(() => expect(document.activeElement).toBe(main));
  });
});
