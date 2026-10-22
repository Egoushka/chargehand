import type { RepositoryBody } from '../../api/repositories';
import { Banner, Button } from '../../ui';

/** The id of the task the server proposes for a repository under the readiness threshold. */
export const AGENT_READY_TASK = 'agent-ready';

/**
 * A warning above the form when the chosen repository scores under the readiness threshold and the draft is not already the
 * task that fixes it. It never blocks the start: the person may send the batch as it is, or swap in the proposed task.
 */
export function ReadinessWarning({
  repo,
  taskIds,
  onUseProposal,
}: {
  repo: RepositoryBody | undefined;
  taskIds: string[];
  onUseProposal: (request: Record<string, unknown>) => void;
}) {
  const readiness = repo?.readiness;
  if (!repo || !readiness?.below || taskIds.includes(AGENT_READY_TASK)) return null;
  return (
    <Banner
      tone="warn"
      icon="alert-triangle"
      title={`${repo.name} scores ${readiness.score ?? 0} of 100 for agent readiness`}
      role="status"
    >
      <p>
        Tasks in a repository under {readiness.threshold} complete less often. Consider making it
        agent-ready first; this batch can still start as it is.
      </p>
      {readiness.gaps.length > 0 ? (
        <ul className="start-gaps">
          {readiness.gaps.map((gap) => (
            <li key={gap}>{gap}</li>
          ))}
        </ul>
      ) : null}
      {readiness.proposal ? (
        <Button
          size="sm"
          icon="wrench"
          label="Use the agent-ready task instead"
          onClick={() => readiness.proposal && onUseProposal(readiness.proposal)}
        />
      ) : null}
    </Banner>
  );
}
