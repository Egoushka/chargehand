import { useNavigate } from '@tanstack/react-router';
import { type ReactNode, useMemo, useState } from 'react';
import type { RepositoryBody } from '../../api/repositories';
import { StartRefused, useStartBatch } from '../../api/start';
import type { SystemBody } from '../../api/system';
import type { Me } from '../../api/types';
import { useToast } from '../../shell/toasts';
import { Banner, Button, Field, Icon, Input, Panel, Select, Textarea } from '../../ui';
import { COMMIT_PATTERN, PRESET_PATTERN } from './model';

interface SingleRunProps {
  me: Me;
  system: SystemBody | null;
  repositories: RepositoryBody[] | null;
  /** The switch between this form and the batch composer, drawn in the header. */
  lead: ReactNode;
}

/** The request/v1 of one run: a goal, a preset and, for the presets that read code, a repository at a commit. */
export function singleRequest(goal: string, preset: string, repository: string, commit: string) {
  return {
    contract_version: 'request/v1',
    text: goal.trim(),
    context: {
      interactive: false,
      preset: preset.trim(),
      ...(repository.trim()
        ? { repository: { path: repository.trim(), commit: commit.trim() } }
        : {}),
    },
  };
}

/** Start one run (a question about code, or a change that is not a batch): `POST /v1/runs`, then the run's own page. */
export function SingleRun({ me, system, repositories, lead }: SingleRunProps) {
  const navigate = useNavigate();
  const toast = useToast();
  const start = useStartBatch();
  // The owner can tell which presets run batches; an account is told only the names, so `driven` is left out by name.
  const presets = useMemo(() => {
    const known = system?.presets;
    const names = known
      ? known.filter((p) => !p.driven && !p.error).map((p) => p.name)
      : me.presets;
    return names.filter((n) => known || n !== 'driven');
  }, [system, me.presets]);
  const [goal, setGoal] = useState('');
  const [preset, setPreset] = useState(
    me.presets.includes('default') ? 'default' : (presets[0] ?? ''),
  );
  const [repository, setRepository] = useState('');
  const [commit, setCommit] = useState('');
  const [attempted, setAttempted] = useState(false);

  const problem = {
    goal: goal.trim() === '' ? 'Write what the run should find out or change.' : undefined,
    preset: PRESET_PATTERN.test(preset.trim())
      ? undefined
      : 'A preset name is lower-case words joined by hyphens.',
    commit:
      repository && !COMMIT_PATTERN.test(commit.trim())
        ? 'A repository needs the commit to read it at: 7 to 40 hex digits.'
        : undefined,
  };
  const invalid = Object.values(problem).some(Boolean);
  const show = (key: keyof typeof problem) => (attempted ? problem[key] : undefined);

  function pickRepository(path: string) {
    setRepository(path);
    setCommit(repositories?.find((r) => r.path === path)?.head ?? '');
  }

  function submit() {
    setAttempted(true);
    if (invalid) return;
    start.mutate(singleRequest(goal, preset, repository, commit), {
      onSuccess: ({ runId }) => {
        toast.push({
          tone: 'ok',
          icon: 'play',
          title: 'Run accepted',
          body: 'It runs in the background; its page follows it.',
        });
        void navigate({ to: '/runs/$runId', params: { runId } });
      },
    });
  }

  const refusal = start.error;
  return (
    <div className="screen start">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Start a run</h1>
          <p className="screen__sub">
            One request to a preset: a question answered with cited claims, or a change. Most
            presets need a repository on the server and the commit to read it at.
          </p>
        </div>
        <div className="screen__actions">
          {lead}
          <Button
            variant="primary"
            label="Start run"
            icon="play"
            loading={start.isPending}
            disabled={me.halted}
            onClick={submit}
          />
        </div>
      </div>
      {me.halted ? (
        <Banner tone="danger" icon="alert-circle" title="This server is halted" role="alert">
          It refuses new runs until the owner resumes it.
        </Banner>
      ) : null}
      {refusal ? (
        <div className="errbox" role="alert">
          <Icon name="alert-triangle" />
          <div className="stack stack--tight">
            <div className="b">
              {refusal instanceof StartRefused
                ? 'The server refused the request'
                : 'Could not reach the server'}
            </div>
            <div className="dim">
              <span className="mono">
                {refusal instanceof StartRefused
                  ? [refusal.status, refusal.code].filter(Boolean).join(' ')
                  : 'POST /v1/runs'}
              </span>
              : {refusal.message}
            </div>
          </div>
        </div>
      ) : null}
      <Panel title="Request">
        <div className="stack stack--tight">
          <Field label="Goal" error={show('goal')}>
            {(control) => (
              <Textarea
                {...control}
                rows={5}
                value={goal}
                placeholder="What should chargehand find out, or change?"
                onChange={(e) => setGoal(e.target.value)}
              />
            )}
          </Field>
          <div className="formgrid">
            <Field label="Preset" error={show('preset')}>
              {(control) =>
                presets.length > 0 ? (
                  <Select {...control} value={preset} onChange={(e) => setPreset(e.target.value)}>
                    {presets.includes(preset) ? null : <option value={preset}>{preset}</option>}
                    {presets.map((name) => (
                      <option key={name} value={name}>
                        {name}
                      </option>
                    ))}
                  </Select>
                ) : (
                  <Input
                    {...control}
                    mono
                    value={preset}
                    onChange={(e) => setPreset(e.target.value)}
                  />
                )
              }
            </Field>
            <Field label="Repository">
              {(control) =>
                repositories && repositories.length > 0 ? (
                  <Select
                    {...control}
                    value={repository}
                    onChange={(e) => pickRepository(e.target.value)}
                  >
                    <option value="">None</option>
                    {repositories.map((r) => (
                      <option key={r.path} value={r.path}>
                        {r.name}
                        {r.repository ? ` (${r.repository})` : ''}
                      </option>
                    ))}
                  </Select>
                ) : (
                  <Input
                    {...control}
                    mono
                    value={repository}
                    placeholder="/path/to/checkout on the server"
                    onChange={(e) => setRepository(e.target.value)}
                  />
                )
              }
            </Field>
            <Field label="Commit" error={show('commit')} hint="Filled with the checkout's head.">
              {(control) => (
                <Input
                  {...control}
                  mono
                  value={commit}
                  disabled={!repository}
                  onChange={(e) => setCommit(e.target.value)}
                />
              )}
            </Field>
          </div>
        </div>
      </Panel>
    </div>
  );
}
