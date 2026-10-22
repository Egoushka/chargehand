import { useNavigate } from '@tanstack/react-router';
import type { ReadinessBody } from '../../api/repositories';
import { Button } from '../../ui';
import { cloneAsNewBatch } from '../start/prefill';
import { CHECK_LABEL, CHECK_STATUS, signalLevel as levelOf, SIGNAL_LABEL } from './model';

/**
 * The repository's agent-readiness score and what it is made of. Under the threshold the gaps are listed and the proposed
 * "make this repository agent-ready" task opens in Start a batch, filled in; nothing starts until the person sends it.
 */
export function Readiness({ readiness, name }: { readiness: ReadinessBody; name: string }) {
  const navigate = useNavigate();
  return (
    <div className="repo-readiness" data-below={readiness.below || undefined}>
      <div className="row row--between">
        <div className="label-caps repo-label">Agent readiness</div>
        <span className="b">
          {readiness.score == null ? 'not rated' : `${readiness.score} / 100`}
        </span>
      </div>
      <ul className="list-reset">
        {readiness.signals.map((signal) => {
          const signalLevel = levelOf(signal.value);
          return (
            <li key={signal.name} className="repo-check">
              {signalLevel ? (
                <span
                  className="ch-pill ch-pill--dot"
                  data-status={CHECK_STATUS[signalLevel]}
                  role="img"
                  aria-label={CHECK_LABEL[signalLevel]}
                />
              ) : (
                <span className="ch-pill ch-pill--dot" role="img" aria-label="No data" />
              )}
              <span className={signalLevel ? undefined : 'dim'}>
                {SIGNAL_LABEL[signal.name] ?? signal.name}: {signal.detail}
              </span>
            </li>
          );
        })}
      </ul>
      {readiness.below ? (
        <p className="small repo-readiness__note">
          Under {readiness.threshold}: tasks here complete less often. Fix the gaps first; feature
          work is not refused.
        </p>
      ) : null}
      {readiness.proposal ? (
        <Button
          size="sm"
          variant={readiness.below ? 'primary' : undefined}
          icon="wrench"
          label="Propose agent-ready task"
          title={readiness.gaps.join('\n')}
          aria-label={`Propose a task that makes ${name} agent-ready`}
          onClick={() => void navigate(cloneAsNewBatch(readiness.proposal))}
        />
      ) : null}
    </div>
  );
}
