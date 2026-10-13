import { useQuery } from '@tanstack/react-query';
import { useLocation } from '@tanstack/react-router';
import { useMemo } from 'react';
import { learnQuery, meQuery } from '../../api/queries';
import { useRepositories } from '../../api/repositories';
import { systemQuery } from '../../api/system';
import { useIsPhone } from '../../shell/useIsPhone';
import { ErrorState, Skeleton } from '../../ui';
import { Composer } from './Composer';
import { emptyDraft } from './model';
import { readPrefill } from './prefill';
import './start.css';

/** The history the server's own budgets are chosen from: its default window of 60 days (an owner can set another in the profile). */
const HISTORY = '60d';

/**
 * Start a batch: choose a repository and a commit, write the tasks, see what they will cost and how they depend on each other,
 * check the request, and send it. It reads the account (what it may do), the repositories (what it may work in), the learning
 * report (what tasks of each size class have cost) and, for the owner, the profile's budget rules.
 */
export function Start() {
  const me = useQuery(meQuery());
  const phone = useIsPhone();
  const repos = useRepositories();
  const learn = useQuery(learnQuery(HISTORY));
  const owner = me.data?.owner === true;
  const system = useQuery({ ...systemQuery(), enabled: owner });
  const state = useLocation({ select: (l) => l.state });
  const visit = state.key ?? String(state.__TSR_index);
  const prefill = useMemo(() => readPrefill(state), [state]);

  if (me.isPending)
    return (
      <div className="screen" aria-busy="true">
        <div>
          <h1 className="screen__title">Start a batch</h1>
          <p className="screen__sub">Loading</p>
        </div>
        {[1, 2].map((n) => (
          <div key={n} className="card pad-lg stack stack--tight">
            <Skeleton width="30%" />
            <Skeleton height={32} />
            <Skeleton height={32} />
          </div>
        ))}
      </div>
    );
  if (me.isError)
    return (
      <div className="screen">
        <div>
          <h1 className="screen__title">Start a batch</h1>
        </div>
        <ErrorState
          title="Cannot read your account"
          call="GET /v1/accounts/me"
          detail={me.error.message}
          onRetry={() => void me.refetch()}
        />
      </div>
    );

  return (
    <Composer
      // A new visit with another request (a clone) starts the form over.
      key={visit}
      initial={prefill?.draft ?? emptyDraft()}
      from={prefill?.from}
      notes={prefill?.notes ?? []}
      me={me.data}
      phone={phone}
      repositories={{
        list: repos.data?.repositories ?? null,
        pending: repos.isPending,
        error: repos.isError ? repos.error : null,
        retry: () => void repos.refetch(),
      }}
      learn={learn.data}
      system={system.data ?? null}
    />
  );
}
