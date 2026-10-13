import { useQueries } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import type { BatchBody } from '../../api/batch';
import { batchTaskQuery } from '../../api/batch-task';
import { formatDateTime } from '../../lib/format';
import { useToast } from '../../shell/toasts';
import { Button, KeyValue, Panel, Skeleton } from '../../ui';
import { CodeBlock } from '../code/CodeBlock';
import { cloneAsNewBatch } from '../start/prefill';
import { cutGoals, rebuildRequest } from './model';

/** The request, as far as the batch view lets it be rebuilt, and the way to start a new batch from it. */
export function RequestTab({ batch }: { batch: BatchBody }) {
  const navigate = useNavigate();
  const toast = useToast();
  const cut = cutGoals(batch);
  // The batch view cuts a long goal at 500 characters; the task's own view has it whole.
  const whole = useQueries({ queries: cut.map((id) => batchTaskQuery(batch.run.run_id, id, 0)) });
  const goals = useMemo(
    () =>
      Object.fromEntries(
        cut.flatMap((id, i) => {
          const goal = whole[i]?.data?.task.goal;
          return goal ? [[id, goal]] : [];
        }),
      ),
    [cut, whole],
  );
  const loading = whole.some((q) => q.isPending);
  const request = useMemo(() => rebuildRequest(batch, goals), [batch, goals]);
  const json = JSON.stringify(request, null, 2);
  return (
    <div className="g-split request">
      <div className="stack stack--tight">
        <div className="row row--wrap">
          <span className="b">request/v1</span>
          <span className="dim">POST /v1/runs · Prefer: wait=0</span>
          <span className="spacer" />
          <Button
            size="sm"
            icon="copy"
            label="Copy JSON"
            onClick={() => {
              navigator.clipboard.writeText(json).then(
                () => toast.push({ tone: 'ok', icon: 'check', title: 'Request copied' }),
                () =>
                  toast.push({
                    tone: 'warn',
                    icon: 'alert-triangle',
                    title: 'Cannot copy',
                    body: 'The browser refused clipboard access. Select the text and copy it.',
                  }),
              );
            }}
          />
        </div>
        {loading ? <Skeleton width="100%" height={16} /> : null}
        <CodeBlock code={json} lang="json" label="The request as JSON" className="request__json" />
      </div>
      <div className="stack stack--tight">
        <Panel title="Use this request">
          <div className="stack stack--tight">
            <Button
              variant="primary"
              icon="copy"
              label="Clone as a new batch"
              disabled={loading}
              onClick={() => void navigate(cloneAsNewBatch(request, batch.run.run_id))}
            />
            <div className="dim small">
              A clone opens Start a batch with every field filled. Nothing runs until you press
              Start.
            </div>
          </div>
        </Panel>
        <Panel title="Rebuilt, not recorded">
          <p className="dim small">
            The server keeps the request in its run log and does not serve it. This one is rebuilt
            from the batch view: its tasks, goals, sizes set by the caller, dependencies, limits and
            repository. The request text, caller blocks and inputs are not in it.
          </p>
        </Panel>
        <Panel title="Sent by">
          <KeyValue
            stacked
            items={[
              ['Account', batch.actor ?? '—'],
              ['Time', `${formatDateTime(batch.run.started_at)} Kyiv`],
              ['Preset', batch.run.preset],
              [
                'Planning',
                batch.limits.plan === true
                  ? 'planned: sizes assigned'
                  : batch.limits.plan === false
                    ? 'no planning step'
                    : 'follows the profile',
              ],
            ]}
          />
        </Panel>
      </div>
    </div>
  );
}
