import { useState } from 'react';
import { useMergePull } from '../../api/actions';
import { ApiError } from '../../api/client';
import { Banner, Button, ConfirmDialog } from '../../ui';

export interface MergeButtonProps {
  /** `owner/name`: the one shape the merge route can name. */
  repository?: string | null;
  number?: number | null;
  /** The button's words: "Approve merge" on the quality card, the option's label in the inbox. */
  label?: string;
  /** Why the button is disabled; absent when it may be pressed. */
  blocked?: string;
}

/** Merge a pull request: asks first, says what it does, merges once and shows the server's reason when it refuses. The one merge control of the quality card and the inbox card. */
export function MergeButton({
  repository,
  number,
  label = 'Approve merge',
  blocked,
}: MergeButtonProps) {
  const [asking, setAsking] = useState(false);
  const merge = useMergePull();
  const [owner, repo, extra] = (repository ?? '').split('/');
  if (number == null || !owner || !repo || extra !== undefined) return null;
  const done = merge.isSuccess;
  return (
    <div className="stack stack--tight">
      <div>
        <Button
          variant="primary"
          icon="pr"
          label={label}
          disabled={blocked !== undefined || done}
          title={blocked}
          onClick={() => setAsking(true)}
        />
      </div>
      {done ? <p className="dim small">Merged.</p> : null}
      {merge.isError ? (
        <Banner tone="warn" icon="alert-triangle" title="Not merged" role="alert">
          {merge.error instanceof ApiError ? merge.error.errors.join('; ') : 'The request failed.'}
        </Banner>
      ) : null}
      <ConfirmDialog
        open={asking}
        onOpenChange={setAsking}
        title={`Merge pull request ${number}?`}
        text={`This squash-merges ${repository} #${number} into its base branch. The repository's required checks and the integrator's policy are checked again first. A merge is not undone from here.`}
        keepLabel="Keep it open"
        confirmLabel={`Merge PR ${number}`}
        busy={merge.isPending}
        onConfirm={() => {
          merge.mutate({ owner, repo, pull: number }, { onSettled: () => setAsking(false) });
        }}
      />
    </div>
  );
}
