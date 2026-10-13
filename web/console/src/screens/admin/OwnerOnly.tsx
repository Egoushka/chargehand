import { Link } from '@tanstack/react-router';
import { buttonClass, EmptyState, Panel } from '../../ui';

/** What an account sees on a screen about the server itself: a plain statement, not an error, and the way back. */
export function OwnerOnly({
  title,
  route,
  account,
}: {
  title: string;
  /** The route the screen reads, as the server names it. */
  route: string;
  account?: string;
}) {
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">{title}</h1>
          <p className="screen__sub">Owner only</p>
        </div>
      </div>
      <Panel title="Owner only">
        <EmptyState
          icon="lock"
          title="This screen needs the owner key"
          text={
            <>
              <span className="mono">{route}</span> describes the server itself, so it answers{' '}
              <span className="mono">403</span> to an account key
              {account ? (
                <>
                  {' '}
                  such as <b>{account}</b>
                </>
              ) : null}
              . An account sees its own runs, spend and repositories; sign in with the owner key to
              see this.
            </>
          }
          action={
            <Link to="/" className={buttonClass({ size: 'sm' })}>
              Go to Overview
            </Link>
          }
        />
      </Panel>
    </div>
  );
}
