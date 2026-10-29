import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError } from '../../api/client';
import { approveHost, type LearnedHostBody, useLearnedHosts } from '../../api/egress';
import { Button } from '../../ui';

/**
 * The hosts this repository's sessions were refused by the batch's egress proxy (ADR 0078, decision 4). Approving one puts it on
 * the allowlist of the repository's later batches, and no other repository's. Owner only: for an account the list does not load
 * and nothing is shown.
 */
export function LearnedHosts({ repository }: { repository?: string | null }) {
  const learned = useLearnedHosts();
  const hosts = learned.data?.repositories.find((r) => r.repository === repository)?.hosts ?? [];
  if (!repository || hosts.length === 0) return null;
  return (
    <div className="repo-learned">
      <div className="label-caps repo-label">Refused hosts</div>
      <ul className="list-reset">
        {hosts.map((host) => (
          <HostRow key={host.host} repository={repository} host={host} />
        ))}
      </ul>
    </div>
  );
}

function HostRow({ repository, host }: { repository: string; host: LearnedHostBody }) {
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const approved = host.approved != null;
  const change = async () => {
    setBusy(true);
    setError(null);
    try {
      await approveHost(repository, host.host, approved);
      await queryClient.invalidateQueries({ queryKey: ['egress-learned'] });
    } catch (e) {
      setError(e instanceof ApiError ? e.errors.join('; ') : 'The change was not saved.');
    } finally {
      setBusy(false);
    }
  };
  return (
    <li className="repo-check row row--between">
      <span>
        <span className="mono">{host.host}</span>{' '}
        <span className="dim small">
          {host.refusals} refused in {host.batches} {host.batches === 1 ? 'batch' : 'batches'}
          {approved ? ' · approved' : ''}
        </span>
        {error ? <span className="small"> {error}</span> : null}
      </span>
      <Button
        size="sm"
        variant={approved ? undefined : 'primary'}
        label={approved ? 'Revoke' : 'Approve'}
        aria-label={`${approved ? 'Revoke' : 'Approve'} ${host.host} for ${repository}`}
        loading={busy}
        disabled={busy}
        onClick={() => void change()}
      />
    </li>
  );
}
