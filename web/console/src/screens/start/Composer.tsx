import { useNavigate } from '@tanstack/react-router';
import { type ReactNode, useEffect, useMemo, useRef, useState } from 'react';
import type { RepositoryBody } from '../../api/repositories';
import { StartRefused, useStartBatch } from '../../api/start';
import type { SystemBody } from '../../api/system';
import type { LearnReport, Me } from '../../api/types';
import { shortId } from '../../lib/format';
import { useToast } from '../../shell/toasts';
import {
  Banner,
  Button,
  ErrorState,
  Field,
  Icon,
  Input,
  Panel,
  Segmented,
  Select,
  Skeleton,
} from '../../ui';
import { CycleNotice, DependencyGraph } from './DependencyGraph';
import { ImportDialog, TemplatesDialog } from './Dialogs';
import { JsonView } from './JsonView';
import {
  type CheckEnv,
  check,
  DEFAULT_FLAT_BUDGETS,
  DEFAULT_RULES,
  type Draft,
  type EstimateRules,
  emptyTask,
  errorsOf,
  estimateBatch,
  fromRequest,
  type Issue,
  issuesAt,
  layoutGraph,
  MAX_PARALLEL,
  MAX_TASKS,
  nextTaskId,
  type PlanChoice,
  parseDraftJson,
  toJson,
  toRequest,
} from './model';
import { AccountPanel, EstimatePanel } from './Panels';
import { ReadinessWarning } from './ReadinessWarning';
import { TaskEditor } from './TaskEditor';
import { applyTemplate } from './templates';

export interface ComposerProps {
  /** The switch to a single run, drawn in the header. */
  lead?: ReactNode;
  initial: Draft;
  /** The run this was cloned from. */
  from?: string;
  notes: Issue[];
  me: Me;
  phone: boolean;
  repositories: {
    list: RepositoryBody[] | null;
    pending: boolean;
    error: Error | null;
    retry: () => void;
  };
  learn: LearnReport | undefined;
  /** The owner's view of the profile; null for an account, which cannot read it. */
  system: SystemBody | null;
}

const FIELD_LABELS: Record<string, string> = {
  text: 'Label',
  preset: 'Preset',
  repository: 'Repository',
  commit: 'Commit',
  tasks: 'Tasks',
  max_parallel: 'max_parallel',
  max_tokens_total: 'max_tokens_total',
  max_usd_total: 'max_usd_total',
  caps: 'Caps',
  account: 'Account',
  draft_pr: 'draft_pr',
  json: 'JSON',
};

/** `tasks.2.goal` as "Task 3 goal". */
function describe(path: string): string {
  const task = /^tasks\.(\d+)\.(\w+)$/.exec(path);
  if (task) return `Task ${Number(task[1]) + 1} ${(task[2] ?? '').replace('_', ' ')}`;
  return FIELD_LABELS[path] ?? path;
}

/** The control that takes the focus for a problem: the cap fields stand for "caps", and the JSON view has one box. */
function focusField(path: string, view: 'form' | 'json') {
  const target = view === 'json' ? 'json' : path === 'caps' ? 'max_tokens_total' : path;
  const element = document.querySelector<HTMLElement>(`[data-field="${target}"]`);
  element?.focus();
  element?.scrollIntoView({ block: 'center' });
}

export function rulesFrom(system: SystemBody | null): EstimateRules {
  const governor = system?.profile?.driven?.governor;
  if (!governor) return DEFAULT_RULES;
  return {
    minTasks: governor.history?.min_tasks ?? DEFAULT_RULES.minTasks,
    percentile: governor.history?.percentile ?? DEFAULT_RULES.percentile,
    windowDays: governor.history?.window_days ?? DEFAULT_RULES.windowDays,
    flat: { ...DEFAULT_FLAT_BUDGETS, ...governor.budgets },
    fromProfile: true,
  };
}

/** The form, its JSON twin, the checks, and the start. All of its state is one Draft; the form and the JSON are two ways to edit it. */
export function Composer({
  lead,
  initial,
  from,
  notes: initialNotes,
  me,
  phone,
  repositories,
  learn,
  system,
}: ComposerProps) {
  const navigate = useNavigate();
  const toast = useToast();
  const startBatch = useStartBatch();
  const [draft, setDraft] = useState(initial);
  const [notes, setNotes] = useState(initialNotes);
  const [view, setView] = useState<'form' | 'json'>('form');
  const [jsonText, setJsonText] = useState('');
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [attempted, setAttempted] = useState(false);
  const [checked, setChecked] = useState(false);
  const [dialog, setDialog] = useState<'import' | 'templates' | null>(null);

  const repos = repositories.list;
  const repo = repos?.find((r) => r.path === draft.repository);
  // The owner can tell which presets run batches; an account is told only the names.
  const presets = useMemo(() => {
    const batch = (system?.presets ?? []).filter((p) => p.driven && !p.error).map((p) => p.name);
    return batch.length > 0 ? batch : me.presets;
  }, [system, me.presets]);

  // Once the checkouts are known: the only one is chosen, and a repository with no commit gets its head.
  const filled = useRef(false);
  useEffect(() => {
    if (filled.current || !repos) return;
    filled.current = true;
    setDraft((d) => {
      const only = d.repository === '' && repos.length === 1 ? repos[0] : undefined;
      const chosen = only ?? repos.find((r) => r.path === d.repository);
      if (only) return { ...d, repository: only.path, commit: d.commit || (only.head ?? '') };
      return chosen && d.commit === '' ? { ...d, commit: chosen.head ?? '' } : d;
    });
  }, [repos]);

  const env: CheckEnv = useMemo(() => ({ me, repositories: repos, presets }), [me, repos, presets]);
  const issues = useMemo(
    () => [
      ...check(draft, env),
      ...(me.halted
        ? [
            {
              path: 'account',
              level: 'error' as const,
              message: 'The server is halted. It refuses new runs until the owner resumes it.',
            },
          ]
        : []),
      ...notes,
    ],
    [draft, env, me.halted, notes],
  );
  const errors = errorsOf(issues);
  const showLate = attempted;
  const at = (path: string) => issuesAt(issues, path, showLate);
  const firstError = (path: string) => at(path).find((i) => i.level === 'error')?.message;
  /** The problem with the list of tasks as a whole (none, or too many), not with one task. */
  const listIssue = issues.find((i) => i.path === 'tasks' && (showLate || !i.late));
  const firstWarning = (path: string) => at(path).find((i) => i.level === 'warning')?.message;

  const rules = useMemo(() => rulesFrom(system), [system]);
  // The learning records name a repository by its forge name or its path; both are tried.
  const repositoryKeys = [repo?.path, repo?.repository, draft.repository]
    .filter((name): name is string => Boolean(name))
    .join('\n');
  const estimate = useMemo(
    () => estimateBatch(draft, learn, rules, repositoryKeys.split('\n')),
    [draft, learn, rules, repositoryKeys],
  );
  const layout = useMemo(() => layoutGraph(draft.tasks), [draft.tasks]);

  function edit(patch: Partial<Draft>) {
    setDraft((d) => ({ ...d, ...patch }));
    setNotes([]);
    setChecked(false);
  }

  function pickRepository(path: string) {
    const next = repos?.find((r) => r.path === path);
    const previous = repos?.find((r) => r.path === draft.repository);
    edit({
      repository: path,
      commit:
        draft.commit === '' || draft.commit === previous?.head ? (next?.head ?? '') : draft.commit,
    });
  }

  function load(template: Parameters<typeof applyTemplate>[0]) {
    replace(applyTemplate(template), `Template ${template.name} loaded`);
  }

  /** The form filled from another request: a template, or the agent-ready task the server proposes. */
  function replace(next: Draft, title: string) {
    const head = repos?.find((r) => r.path === next.repository)?.head;
    setDraft(head && next.commit === '' ? { ...next, commit: head } : next);
    setNotes([]);
    setChecked(false);
    setAttempted(false);
    startBatch.reset();
    if (view === 'json') setView('form');
    toast.push({
      tone: 'info',
      icon: 'layers',
      title,
      body: 'Check the commit and the caps, then start.',
    });
  }

  function openJson() {
    setJsonText(toJson(draft));
    setJsonError(null);
    setView('json');
  }

  function onJson(text: string) {
    setJsonText(text);
    const parsed = parseDraftJson(text);
    if (parsed.ok) {
      setDraft(parsed.draft);
      setNotes(parsed.notes);
      setJsonError(null);
    } else {
      setJsonError(parsed.error);
    }
    setChecked(false);
  }

  function openForm() {
    if (jsonError) {
      focusField('json', 'json');
      return;
    }
    setView('form');
  }

  /** Reveals every problem. Returns whether the request can go. */
  function validate(): boolean {
    setAttempted(true);
    if (jsonError) {
      focusField('json', 'json');
      return false;
    }
    const first = errors[0];
    if (first) {
      focusField(first.path, view);
      return false;
    }
    return true;
  }

  function submit() {
    setChecked(false);
    if (!validate()) return;
    startBatch.mutate(toRequest(draft), {
      onSuccess: ({ runId }) => {
        toast.push({
          tone: 'ok',
          icon: 'play',
          title: 'Batch accepted',
          body: `${draft.tasks.length} ${draft.tasks.length === 1 ? 'task' : 'tasks'} queued. It runs in the background.`,
        });
        void navigate({ to: '/runs/$runId', params: { runId } });
      },
    });
  }

  function runCheck() {
    setChecked(validate());
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(jsonText);
      toast.push({
        tone: 'ok',
        icon: 'copy',
        title: 'JSON copied',
        body: 'The request is on your clipboard.',
      });
    } catch {
      toast.push({
        tone: 'warn',
        icon: 'copy',
        title: 'Could not copy',
        body: 'The browser did not allow it. Select the text and copy it.',
      });
    }
  }

  const blocked = me.halted || !me.driven;
  const actions = (
    <>
      <Button label="Check" icon="check" onClick={runCheck} disabled={startBatch.isPending} />
      <Button
        variant="primary"
        label="Start batch"
        icon="play"
        loading={startBatch.isPending}
        disabled={blocked}
        onClick={submit}
      />
    </>
  );

  const accountIssues = issues.filter((i) => i.path === 'account');
  const refusal = startBatch.error;
  const planChoice: PlanChoice = draft.plan;
  const planProfile = system?.profile?.driven?.planning;

  return (
    <div className="screen start">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Start a batch</h1>
          <p className="screen__sub">
            A list of tasks that run as headless sessions in containers, each ending in a draft pull
            request.
          </p>
        </div>
        <div className="screen__actions">
          {lead}
          <Segmented<'form' | 'json'>
            label="View"
            touch={phone}
            value={view}
            onValueChange={(next) => (next === 'json' ? openJson() : openForm())}
            items={[
              { id: 'form', label: 'Form' },
              { id: 'json', label: 'JSON' },
            ]}
          />
          <Button label="Templates" icon="layers" onClick={() => setDialog('templates')} />
          {phone ? null : actions}
        </div>
      </div>

      {from ? (
        <Banner tone="info" icon="repeat" title={`Cloned from batch ${shortId(from)}`}>
          Review the tasks and the caps, then start it as a new batch. Nothing has been sent.
        </Banner>
      ) : null}
      {view === 'form' && notes.length > 0 ? (
        <Banner
          tone="warn"
          icon="alert-triangle"
          title="Part of this request could not be carried over"
        >
          {notes.map((note) => note.message).join(' ')}
        </Banner>
      ) : null}
      <ReadinessWarning
        repo={repo}
        taskIds={draft.tasks.map((t) => t.id)}
        onUseProposal={(request) =>
          replace(fromRequest(request).draft, 'Agent-ready task filled in')
        }
      />
      {accountIssues.map((issue) => (
        <Banner
          key={issue.message}
          tone={issue.level === 'error' ? 'danger' : 'warn'}
          icon={issue.level === 'error' ? 'alert-circle' : 'alert-triangle'}
          title={issue.level === 'error' ? 'This account cannot start a batch now' : 'Heads up'}
          role={issue.level === 'error' ? 'alert' : 'status'}
        >
          {issue.message}
        </Banner>
      ))}
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
      {attempted && errors.length > 0 ? (
        <div className="errbox" role="alert">
          <Icon name="alert-circle" />
          <div className="stack stack--tight">
            <div className="b">
              {errors.length === 1 ? 'One thing to fix' : `${errors.length} things to fix`} before
              this can start
            </div>
            <ul className="start-todo list-reset">
              {errors.map((issue) => (
                <li key={`${issue.path}|${issue.message}`}>
                  <button
                    type="button"
                    className="link"
                    onClick={() => focusField(issue.path, view)}
                  >
                    {describe(issue.path)}
                  </button>
                  <span className="dim">: {issue.message}</span>
                </li>
              ))}
            </ul>
          </div>
        </div>
      ) : null}
      {checked ? (
        <Banner tone="ok" icon="check" title="No problems found" role="status">
          The request matches request/v1 and the rules the server adds. Start batch sends it.
        </Banner>
      ) : null}

      {view === 'json' ? (
        <JsonView
          text={jsonText}
          error={jsonError}
          issues={issues}
          onChange={onJson}
          onCopy={() => void copy()}
        />
      ) : (
        <div className="start-split">
          <div className="stack">
            <Panel title="Request">
              <div className="stack stack--tight">
                {repositories.error ? (
                  <ErrorState
                    title="Cannot list the repositories"
                    call="GET /v1/repositories"
                    detail={repositories.error.message}
                    onRetry={repositories.retry}
                  />
                ) : null}
                <div className="formgrid">
                  <Field
                    label="Preset"
                    error={firstError('preset')}
                    hint="The preset a batch of tasks runs with."
                  >
                    {(control) =>
                      presets.length > 0 ? (
                        <Select
                          {...control}
                          value={draft.preset}
                          data-field="preset"
                          onChange={(e) => edit({ preset: e.target.value })}
                        >
                          {presets.includes(draft.preset) ? null : (
                            <option value={draft.preset}>
                              {draft.preset || 'Choose a preset'}
                            </option>
                          )}
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
                          value={draft.preset}
                          data-field="preset"
                          onChange={(e) => edit({ preset: e.target.value })}
                        />
                      )
                    }
                  </Field>
                  <Field
                    label="Repository"
                    error={firstError('repository')}
                    hint={
                      firstWarning('repository') ??
                      (repo
                        ? repo.ecosystems.length > 0
                          ? `Detected: ${repo.ecosystems.map((e) => e.name).join(', ')}.`
                          : 'Under the server’s repository roots.'
                        : 'A checkout under the server’s repository_roots.')
                    }
                  >
                    {(control) =>
                      repositories.pending && !repos ? (
                        <Skeleton height={32} />
                      ) : repos && repos.length > 0 ? (
                        <Select
                          {...control}
                          value={draft.repository}
                          data-field="repository"
                          onChange={(e) => pickRepository(e.target.value)}
                        >
                          <option value="">Choose a repository</option>
                          {repos.some((r) => r.path === draft.repository) ||
                          draft.repository === '' ? null : (
                            <option value={draft.repository}>{draft.repository}</option>
                          )}
                          {repos.map((r) => (
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
                          value={draft.repository}
                          placeholder="/path/to/the/checkout"
                          autoComplete="off"
                          data-field="repository"
                          onChange={(e) => edit({ repository: e.target.value })}
                        />
                      )
                    }
                  </Field>
                  <Field
                    label="Commit"
                    error={firstError('commit')}
                    hint={
                      repo?.head ? (
                        <>
                          Sessions start here; the branch is chargehand/&lt;run&gt;. HEAD of{' '}
                          {repo.default_branch ?? 'the checkout'} is{' '}
                          <button
                            type="button"
                            className="link mono"
                            onClick={() => edit({ commit: repo.head ?? '' })}
                          >
                            {repo.head.slice(0, 12)}
                          </button>
                          .
                        </>
                      ) : (
                        'Sessions start from this commit; the branch is chargehand/<run>.'
                      )
                    }
                  >
                    {(control) => (
                      <Input
                        {...control}
                        mono
                        value={draft.commit}
                        placeholder="a91c0ffe21b7"
                        autoComplete="off"
                        spellCheck={false}
                        data-field="commit"
                        onChange={(e) => edit({ commit: e.target.value })}
                      />
                    )}
                  </Field>
                  <Field
                    label="Label"
                    error={firstError('text')}
                    hint="Shown in lists; the request’s text."
                  >
                    {(control) => (
                      <Input
                        {...control}
                        value={draft.text}
                        placeholder="Intake aliases and peak memory"
                        data-field="text"
                        onChange={(e) => edit({ text: e.target.value })}
                      />
                    )}
                  </Field>
                </div>
              </div>
            </Panel>

            <Panel
              title="Tasks"
              count={draft.tasks.length}
              flush
              aside={
                <>
                  <Button
                    label="Import tasks"
                    size="sm"
                    icon="download"
                    onClick={() => setDialog('import')}
                  />
                  <Button
                    label="Add task"
                    size="sm"
                    icon="plus"
                    disabled={draft.tasks.length >= MAX_TASKS}
                    onClick={() =>
                      edit({ tasks: [...draft.tasks, emptyTask({ id: nextTaskId(draft.tasks) })] })
                    }
                  />
                </>
              }
            >
              {listIssue ? (
                <div className="ch-error pad" role="alert">
                  {listIssue.message}
                </div>
              ) : null}
              <TaskEditor
                tasks={draft.tasks}
                estimates={estimate.tasks}
                issues={issues}
                showLate={showLate}
                phone={phone}
                onChange={(tasks) => edit({ tasks })}
                onAdd={() =>
                  edit({ tasks: [...draft.tasks, emptyTask({ id: nextTaskId(draft.tasks) })] })
                }
                onImport={() => setDialog('import')}
              />
            </Panel>

            <Panel title="Options and caps">
              <div className="stack">
                <div className="ch-field">
                  <span className="ch-label" id="plan-label">
                    Planning
                  </span>
                  <Segmented<PlanChoice>
                    label="Planning"
                    touch={phone}
                    value={planChoice}
                    onValueChange={(plan) => edit({ plan })}
                    items={[
                      { id: 'on', label: 'Plan first' },
                      { id: 'off', label: 'Skip' },
                      { id: 'default', label: 'Server default' },
                    ]}
                  />
                  <div className="ch-hint">
                    One cheap read-only run sizes tasks that have no size, splits an L task into 2
                    to 4 ordered parts and assigns shared numbers. It is not part of the caps.
                    {planChoice === 'default' && planProfile
                      ? ` The profile has planning ${planProfile.enabled ? 'on' : 'off'}.`
                      : ''}
                  </div>
                </div>
                <div className="formgrid">
                  <Field
                    label="max_parallel"
                    error={firstError('max_parallel')}
                    hint={`Sessions at once, 1 to ${MAX_PARALLEL}. The server also admits by free memory${
                      system?.profile?.driven
                        ? ` and runs at most ${system.profile.driven.max_parallel}`
                        : ''
                    }.`}
                  >
                    {(control) => (
                      <Input
                        {...control}
                        mono
                        inputMode="numeric"
                        value={draft.maxParallel}
                        placeholder="the profile’s"
                        data-field="max_parallel"
                        onChange={(e) => edit({ maxParallel: e.target.value })}
                      />
                    )}
                  </Field>
                  <Field
                    label="Token cap (input plus output)"
                    error={firstError('max_tokens_total') ?? firstError('caps')}
                    hint={
                      firstWarning('max_tokens_total') ??
                      'max_tokens_total. Binds on a subscription and with a key.'
                    }
                  >
                    {(control) => (
                      <Input
                        {...control}
                        mono
                        inputMode="numeric"
                        value={draft.maxTokens}
                        placeholder="2,000,000 or 2m"
                        data-field="max_tokens_total"
                        onChange={(e) => edit({ maxTokens: e.target.value })}
                      />
                    )}
                  </Field>
                  <Field
                    label="Dollar cap"
                    error={firstError('max_usd_total')}
                    hint={
                      firstWarning('max_usd_total') ??
                      'max_usd_total. Binds with an API key; a subscription has no price.'
                    }
                  >
                    {(control) => (
                      <Input
                        {...control}
                        mono
                        inputMode="decimal"
                        value={draft.maxUsd}
                        placeholder="12.00"
                        data-field="max_usd_total"
                        onChange={(e) => edit({ maxUsd: e.target.value })}
                      />
                    )}
                  </Field>
                  <div className="ch-field">
                    <span className="ch-label">Pull requests</span>
                    <div className="start-fixed">
                      <Icon name="shield-check" />
                      <span>Always drafts (draft_pr). Never merged by this batch.</span>
                    </div>
                  </div>
                </div>
                {system?.profile?.driven?.governor ? (
                  <p className="dim small">
                    Governor: {system.profile.driven.governor.mode}, set in the profile.
                  </p>
                ) : null}
              </div>
            </Panel>
          </div>

          <div className="stack">
            <Panel title="Dependencies" aside="edges are depends_on" flush>
              <DependencyGraph layout={layout} />
              {layout.cycle.length > 0 ? (
                <div className="pad">
                  <CycleNotice cycle={layout.cycle} />
                </div>
              ) : null}
            </Panel>
            <EstimatePanel
              estimate={estimate}
              rules={rules}
              maxParallel={draft.maxParallel}
              maxUsd={draft.maxUsd}
              serverParallel={system?.profile?.driven?.max_parallel ?? null}
            />
            <AccountPanel me={me} />
          </div>
        </div>
      )}

      {phone ? (
        <div className="start-bar">
          <Button label="Check" size="touch" onClick={runCheck} disabled={startBatch.isPending} />
          <Button
            variant="primary"
            size="touch"
            label="Start batch"
            icon="play"
            loading={startBatch.isPending}
            disabled={blocked}
            onClick={submit}
          />
        </div>
      ) : null}

      <ImportDialog
        open={dialog === 'import'}
        onOpenChange={(open) => setDialog(open ? 'import' : null)}
        existingIds={draft.tasks.map((t) => t.id)}
        onAdd={(tasks) => edit({ tasks: [...draft.tasks, ...tasks] })}
      />
      <TemplatesDialog
        open={dialog === 'templates'}
        onOpenChange={(open) => setDialog(open ? 'templates' : null)}
        draft={draft}
        onLoad={load}
      />
    </div>
  );
}
