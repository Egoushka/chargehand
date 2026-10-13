import { useQuery } from '@tanstack/react-query';
import { Link, Outlet } from '@tanstack/react-router';
import { clearKey } from '../api/key';
import { meQuery, useHaltToggle } from '../api/queries';
import { formatUsd } from '../lib/format';
import { SCREENS } from './screens';

/** The frame of every screen once signed in: navigation, who is signed in, and the owner's halt switch. */
export function Shell() {
  const me = useQuery(meQuery());
  const halt = useHaltToggle();
  const account = me.data;

  function toggleHalt() {
    if (!account) return;
    const halting = !account.halted;
    if (
      halting &&
      !window.confirm(
        'Halt the server? Every unfinished run is cancelled and new runs are refused.',
      )
    )
      return;
    halt.mutate(halting);
  }

  return (
    <div className="shell">
      <header className="top">
        <span className="brand">chargehand</span>
        <span className="who">
          {account ? (
            <>
              {account.name} · today {formatUsd(account.spend.day.usd)}
              {account.limits.max_usd_per_day == null
                ? ''
                : ` of ${formatUsd(account.limits.max_usd_per_day)}`}
            </>
          ) : null}
          {account?.owner ? (
            <button
              type="button"
              className={account.halted ? '' : 'danger'}
              onClick={toggleHalt}
              disabled={halt.isPending}
            >
              {account.halted ? 'Resume server' : 'Halt server'}
            </button>
          ) : null}
          <button type="button" onClick={() => clearKey()}>
            Sign out
          </button>
        </span>
      </header>
      {account?.halted ? (
        <p role="alert" className="notice error banner">
          The server is halted: new runs are refused until it is resumed.
        </p>
      ) : null}
      {halt.error ? (
        <p role="alert" className="notice error banner">
          {halt.error.message}
        </p>
      ) : null}
      <div className="body">
        <nav aria-label="Screens">
          <ul>
            {SCREENS.map((screen) => (
              <li key={screen.to}>
                <Link
                  to={screen.to}
                  activeOptions={{ exact: screen.to === '/' }}
                  activeProps={{ 'aria-current': 'page' }}
                >
                  {screen.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <main>
          <Outlet />
        </main>
      </div>
    </div>
  );
}
