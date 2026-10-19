import { useQuery } from '@tanstack/react-query';
import {
  Outlet,
  useMatchRoute,
  useNavigate,
  useRouter,
  useRouterState,
} from '@tanstack/react-router';
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useInbox } from '../api/inbox';
import { signOut } from '../api/key';
import { useOverview } from '../api/overview';
import { meQuery, useHaltToggle } from '../api/queries';
import { DISPLAY_ZONE_LABEL, formatTime, formatUsd } from '../lib/format';
import {
  Banner,
  Brand,
  Button,
  ConfirmDialog,
  IconButton,
  Menu,
  NavItem,
  Rail,
  RailSection,
  TabBar,
  TabBarItem,
  Tooltip,
  TopBar,
} from '../ui';
import { CommandPalette } from './CommandPalette';
import { useHotkeys } from './hotkeys';
import { browserPermission, requestBrowserPermission } from './notifications';
import {
  cycleDensity,
  toggleRail,
  toggleTheme,
  updatePreferences,
  usePreferences,
} from './preferences';
import { MoreSheet, ShortcutsSheet } from './Sheets';
import { type ShellApi, ShellContext } from './ShellContext';
import { GROUP_LABELS, SCREENS, type Screen, TAB_BAR } from './screens';
import { useToast } from './toasts';
import { useAttention } from './useAttention';
import { useIsPhone } from './useIsPhone';

/** The current time of day in the display zone, refreshed each half minute for the top bar. */
function useClock(): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 30_000);
    return () => window.clearInterval(timer);
  }, []);
  return now;
}

/** The frame of every screen once signed in: navigation, the top bar, banners for server-wide states, and the layers. */
export function Shell() {
  const router = useRouter();
  const navigate = useNavigate();
  const matchRoute = useMatchRoute();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const phone = useIsPhone();
  const prefs = usePreferences();
  const toast = useToast();
  const me = useQuery(meQuery());
  const overview = useOverview();
  const inbox = useInbox();
  const halt = useHaltToggle();
  const now = useClock();
  const main = useRef<HTMLElement>(null);

  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [haltAsk, setHaltAsk] = useState(false);

  useAttention();

  const account = me.data;
  const owner = account?.owner === true;
  const halted = overview.data?.server.halted ?? account?.halted ?? false;
  const pause = overview.data?.driven.rate_limit;
  const waiting = inbox.data?.counts.total ?? 0;
  const sessions = overview.data?.driven.sessions.running ?? 0;

  // A new screen takes the focus, so a keyboard or screen-reader user starts at its top, not where the last link was.
  const firstPath = useRef(pathname);
  useEffect(() => {
    if (pathname !== firstPath.current) main.current?.focus({ preventScroll: true });
  }, [pathname]);

  const enableNotifications = useCallback(async () => {
    const result = await requestBrowserPermission();
    if (result === 'granted') {
      updatePreferences({ browserNotifications: true, offerDismissed: true });
      toast.push({
        tone: 'ok',
        icon: 'bell',
        title: 'Notifications on',
        body: 'You get a notice for questions, ready PRs, and finished or failed batches.',
      });
    } else {
      updatePreferences({ offerDismissed: true });
      toast.push({
        tone: 'warn',
        icon: 'bell',
        title: 'Notifications stay off',
        body: 'The browser did not allow them. Notices still appear on this page.',
      });
    }
  }, [toast]);

  const api = useMemo<ShellApi>(
    () => ({
      openPalette: () => setPaletteOpen(true),
      openShortcuts: () => setShortcutsOpen(true),
      requestHalt: () => setHaltAsk(true),
      resumeServer: () =>
        halt.mutate(false, {
          onSuccess: () =>
            toast.push({
              tone: 'ok',
              icon: 'play',
              title: 'Server resumed',
              body: 'New runs are accepted again.',
            }),
        }),
      haltBusy: halt.isPending,
      enableNotifications: () => void enableNotifications(),
    }),
    [halt.mutate, halt.isPending, toast, enableNotifications],
  );

  const keys = useMemo(
    () => ({
      palette: () => setPaletteOpen((open) => !open),
      shortcuts: () => setShortcutsOpen(true),
      focusFilter: () => {
        const filter = document.querySelector<HTMLElement>('[data-filter-input]');
        filter?.focus();
        return filter !== null;
      },
      toggleRail,
      go: (letter: string) => {
        const screen = SCREENS.find((s) => s.go === letter && (owner || !s.ownerOnly));
        if (screen) void navigate({ to: screen.to });
        return screen !== undefined;
      },
    }),
    [navigate, owner],
  );
  useHotkeys(keys);

  const link = (screen: Screen) => ({
    href: router.buildLocation({ to: screen.to }).href,
    current: matchRoute({ to: screen.to, fuzzy: screen.to !== '/' }) !== false,
    onNavigate: () => void navigate({ to: screen.to }),
  });
  const countFor = (screen: Screen) => (screen.to === '/needs-you' ? waiting : undefined);
  const visible = SCREENS.filter((screen) => owner || !screen.ownerOnly);

  const crumbs = useMemo(() => {
    const screen = [...SCREENS]
      .sort((a, b) => b.to.length - a.to.length)
      .find((s) =>
        s.to === '/'
          ? pathname.replace(/\/$/, '') === '' || pathname === '/'
          : pathname.startsWith(s.to),
      );
    const detail = /\/runs\/([^/]+)/.exec(pathname)?.[1];
    return { screen, detail: detail ? decodeURIComponent(detail) : null };
  }, [pathname]);

  const railRow = (screen: Screen) => {
    const item = (
      <NavItem
        key={screen.to}
        icon={screen.icon}
        label={screen.label}
        count={countFor(screen)}
        tone="accent"
        {...link(screen)}
      />
    );
    return (
      <Tooltip key={screen.to} content={screen.label} disabled={!prefs.railCollapsed}>
        {item}
      </Tooltip>
    );
  };

  const moreActions = [
    {
      id: 'theme',
      label: 'Toggle dark and light theme',
      icon: prefs.theme === 'dark' ? ('sun' as const) : ('moon' as const),
      run: toggleTheme,
    },
    ...(browserPermission() === 'default' && !prefs.browserNotifications
      ? [
          {
            id: 'notify',
            label: 'Turn on browser notifications',
            icon: 'bell' as const,
            run: () => void enableNotifications(),
          },
        ]
      : []),
    { id: 'out', label: 'Sign out', icon: 'lock' as const, run: () => signOut() },
  ];

  const live = halted ? 'off' : pause?.paused ? 'paused' : overview.isError ? 'off' : undefined;
  const clock = formatTime(now);
  const liveText = `${halted ? 'Halted' : pause?.paused ? `Paused ${formatTime(pause.resets_at)}` : overview.isError ? 'Offline' : 'Live'} · ${DISPLAY_ZONE_LABEL} ${clock}`;
  const offer =
    browserPermission() === 'default' && !prefs.browserNotifications && !prefs.offerDismissed;
  const lastPause = pause?.pauses.reduce<string | null>(
    (latest, p) => (!latest || p.observed_at > latest ? p.observed_at : latest),
    null,
  );

  return (
    <ShellContext.Provider value={api}>
      <div className="app" data-rail={prefs.railCollapsed && !phone ? 'collapsed' : 'expanded'}>
        <button type="button" className="ch-btn skip" onClick={() => main.current?.focus()}>
          Skip to content
        </button>
        {phone ? null : (
          <Rail collapsed={prefs.railCollapsed} label="Main" className="app__rail">
            <Brand />
            {(['main', 'insight', 'configure'] as const).map((group) => {
              const screens = visible.filter((s) => s.group === group);
              return screens.length === 0 ? null : (
                <Fragment key={group}>
                  {GROUP_LABELS[group] ? <RailSection>{GROUP_LABELS[group]}</RailSection> : null}
                  {screens.map(railRow)}
                </Fragment>
              );
            })}
            <div className="ch-rail__foot">
              <Tooltip content="Keyboard shortcuts" disabled={!prefs.railCollapsed}>
                <button
                  type="button"
                  className="ch-nav"
                  aria-label="Keyboard shortcuts"
                  onClick={() => setShortcutsOpen(true)}
                >
                  <span className="ch-i ch-i-keyboard" aria-hidden="true" />
                  <span className="ch-nav__label">Shortcuts</span>
                </button>
              </Tooltip>
              <Tooltip content="Collapse the rail" disabled={!prefs.railCollapsed}>
                <button
                  type="button"
                  className="ch-nav"
                  aria-label={prefs.railCollapsed ? 'Expand the rail' : 'Collapse the rail'}
                  aria-pressed={prefs.railCollapsed}
                  onClick={toggleRail}
                >
                  <span className="ch-i ch-i-panel-left" aria-hidden="true" />
                  <span className="ch-nav__label">Collapse</span>
                </button>
              </Tooltip>
            </div>
          </Rail>
        )}

        <div className="main">
          <TopBar>
            {phone && crumbs.detail ? (
              <IconButton
                icon="chevron-left"
                label="Back to runs"
                onClick={() => void navigate({ to: '/runs' })}
              />
            ) : null}
            <nav className="crumbs" aria-label="Breadcrumb">
              {crumbs.detail ? (
                <>
                  {phone ? null : (
                    <>
                      <a
                        href={router.buildLocation({ to: '/runs' }).href}
                        onClick={(e) => {
                          e.preventDefault();
                          void navigate({ to: '/runs' });
                        }}
                      >
                        Runs
                      </a>
                      <span className="sep" aria-hidden="true">
                        /
                      </span>
                    </>
                  )}
                  <span className="ch-trunc" aria-current="page">
                    {crumbs.detail}
                  </span>
                </>
              ) : (
                <span className="ch-trunc" aria-current="page">
                  {crumbs.screen?.label ?? 'Not found'}
                </span>
              )}
            </nav>
            <span className="spacer" />
            {phone ? (
              <IconButton icon="search" label="Search" onClick={() => setPaletteOpen(true)} />
            ) : (
              <>
                <button type="button" className="topbar__find" onClick={() => setPaletteOpen(true)}>
                  <span className="ch-i ch-i-search" aria-hidden="true" />
                  Search or jump to
                  <kbd className="ch-kbd">⌘K</kbd>
                </button>
                <span className="ch-live" data-state={live} title={`${DISPLAY_ZONE_LABEL} time`}>
                  {liveText}
                </span>
                <IconButton
                  icon={prefs.theme === 'dark' ? 'sun' : 'moon'}
                  label="Toggle dark and light theme"
                  onClick={toggleTheme}
                />
                <IconButton
                  icon="menu"
                  label={`Row density: ${prefs.density}. Change it`}
                  onClick={cycleDensity}
                />
                <Menu
                  trigger={<IconButton icon="user" label="Account" />}
                  header={
                    account ? (
                      <div className="stack stack--tight">
                        <div className="b">{account.name}</div>
                        <div className="dim small">
                          {account.owner ? 'Owner key' : 'Account key'} · today{' '}
                          {formatUsd(account.spend.day.usd)}
                          {account.limits.max_usd_per_day == null
                            ? ''
                            : ` of ${formatUsd(account.limits.max_usd_per_day)}`}
                        </div>
                      </div>
                    ) : undefined
                  }
                  items={[
                    ...(browserPermission() === 'default' && !prefs.browserNotifications
                      ? [
                          {
                            id: 'notify',
                            label: 'Turn on browser notifications',
                            icon: 'bell' as const,
                            onSelect: () => void enableNotifications(),
                          },
                        ]
                      : []),
                    { id: 'out', label: 'Sign out', icon: 'lock', onSelect: () => signOut() },
                  ]}
                />
              </>
            )}
          </TopBar>

          <div className="banner-stack">
            {halted ? (
              <Banner
                tone="danger"
                icon="alert-circle"
                title="Server is halted"
                role="alert"
                action={
                  owner ? (
                    <Button
                      label="Resume server"
                      size="sm"
                      onClick={api.resumeServer}
                      loading={halt.isPending}
                    />
                  ) : undefined
                }
              >
                Every run was cancelled and nothing starts until the owner resumes.
              </Banner>
            ) : null}
            {!halted && pause?.paused ? (
              <Banner
                tone="warn"
                icon="pause"
                title={`New starts are paused until about ${formatTime(pause.resets_at)} ${DISPLAY_ZONE_LABEL}`}
              >
                {lastPause ? `The model reported a usage limit at ${formatTime(lastPause)}. ` : ''}
                Running tasks go on; queued tasks wait.
              </Banner>
            ) : null}
            {offer ? (
              <Banner
                tone="info"
                icon="bell"
                title="Notifications"
                action={
                  <>
                    <Button label="Enable" size="sm" onClick={() => void enableNotifications()} />
                    <IconButton
                      icon="x"
                      size="sm"
                      label="Dismiss"
                      onClick={() => updatePreferences({ offerDismissed: true })}
                    />
                  </>
                }
              >
                Get a browser notification when a task asks a question, a PR is ready, or a batch
                finishes or fails.
              </Banner>
            ) : null}
            {halt.error ? (
              <Banner
                tone="danger"
                icon="alert-circle"
                title="The server did not answer the request"
                role="alert"
              >
                {halt.error.message}
              </Banner>
            ) : null}
          </div>

          <main id="main" ref={main} tabIndex={-1}>
            <Outlet />
          </main>
        </div>

        {phone ? (
          <TabBar label="Main">
            {TAB_BAR.map((to) => {
              const screen = SCREENS.find((s) => s.to === to);
              return screen ? (
                <TabBarItem
                  key={to}
                  icon={screen.icon}
                  label={screen.short ?? screen.label}
                  count={countFor(screen)}
                  {...link(screen)}
                />
              ) : null;
            })}
            <TabBarItem
              icon="menu"
              label="More"
              current={visible.some(
                (s) => s.group !== 'main' && matchRoute({ to: s.to }) !== false,
              )}
              onNavigate={() => setMoreOpen(true)}
            />
          </TabBar>
        ) : null}
      </div>

      <CommandPalette
        open={paletteOpen}
        onOpenChange={setPaletteOpen}
        owner={owner}
        halted={halted}
        onHalt={() => setHaltAsk(true)}
        onResume={api.resumeServer}
        onShortcuts={() => setShortcutsOpen(true)}
        onSignOut={() => signOut()}
        onEnableNotifications={() => void enableNotifications()}
      />
      <ShortcutsSheet open={shortcutsOpen} onOpenChange={setShortcutsOpen} />
      <MoreSheet
        open={moreOpen && phone}
        onOpenChange={setMoreOpen}
        owner={owner}
        actions={moreActions}
      />
      <ConfirmDialog
        open={haltAsk}
        onOpenChange={setHaltAsk}
        title="Halt the server?"
        text={`This cancels every run${sessions > 0 ? ` (${sessions} running ${sessions === 1 ? 'session stops' : 'sessions stop'})` : ''} and refuses new ones until you resume. Nothing is pushed.`}
        keepLabel="Keep running"
        confirmLabel="Halt server"
        busy={halt.isPending}
        onConfirm={() =>
          halt.mutate(true, {
            onSuccess: () => {
              setHaltAsk(false);
              toast.push({
                tone: 'danger',
                icon: 'stop',
                title: 'Server halted',
                body: 'Every run was cancelled. Resume from the banner or System.',
              });
            },
            onError: () => setHaltAsk(false),
          })
        }
      />
    </ShellContext.Provider>
  );
}
