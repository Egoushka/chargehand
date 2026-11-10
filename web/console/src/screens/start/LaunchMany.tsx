import { useQueries } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { type ReactNode, useState } from 'react';
import { forecastQuery } from '../../api/forecast';
import type { RepositoryBody } from '../../api/repositories';
import { StartRefused, useStartBatch } from '../../api/start';
import type { Me } from '../../api/types';
import { formatUsd } from '../../lib/format';
import { Banner, Button, Check, Field, Input, Panel, Select, Textarea } from '../../ui';
import {
  type LaunchTemplate,
  launchRequest,
  loadLaunchTemplates,
  notReady,
  saveLaunchTemplate,
} from './launch';
import { SIZES } from './model';

interface Outcome {
  repo: string;
  runId?: string;
  error?: string;
}

/** Launch many: one goal started as one batch in each chosen repository, in order; a refusal in one does not stop the rest. */
export function LaunchMany({
  me,
  repositories,
  lead,
}: {
  me: Me;
  repositories: RepositoryBody[] | null;
  lead: ReactNode;
}) {
  const start = useStartBatch();
  const [goal, setGoal] = useState('');
  const [size, setSize] = useState('M');
  const [chosen, setChosen] = useState<string[]>([]);
  const [templates, setTemplates] = useState(loadLaunchTemplates);
  const [name, setName] = useState('');
  const [outcomes, setOutcomes] = useState<Outcome[]>([]);
  const [running, setRunning] = useState(false);

  const list = repositories ?? [];
  const ready = list.filter((r) => notReady(r) === null);
  const picked = list.filter((r) => chosen.includes(r.path) && notReady(r) === null);
  const forecasts = useQueries({
    queries: picked.map((r) => forecastQuery({ repository: r.path, goal: goal.trim(), size })),
  });
  const known = forecasts.filter((f) => f.data);
  const total = known.reduce((sum, f) => sum + (f.data?.usd_p50 ?? f.data?.usd_p90 ?? 0), 0);

  const toggle = (path: string) =>
    setChosen((c) => (c.includes(path) ? c.filter((p) => p !== path) : [...c, path]));

  async function launch() {
    setRunning(true);
    setOutcomes([]);
    for (const repo of picked) {
      let outcome: Outcome;
      try {
        const { runId } = await start.mutateAsync(launchRequest(goal, size, repo));
        outcome = { repo: repo.name, runId };
      } catch (e) {
        const code = e instanceof StartRefused ? [e.status, e.code].filter(Boolean).join(' ') : '';
        outcome = {
          repo: repo.name,
          error: `${code ? `${code}: ` : ''}${e instanceof Error ? e.message : 'failed'}`,
        };
      }
      setOutcomes((o) => [...o, outcome]);
    }
    setRunning(false);
  }

  function applyTemplate(value: string) {
    const t = templates.find((x) => x.name === value);
    if (!t) return;
    setGoal(t.goal);
    setSize(t.size);
    setName(t.name);
  }

  function save() {
    const t: LaunchTemplate = { name, goal, size };
    const next = saveLaunchTemplate(t);
    if (next) setTemplates(next);
  }

  const count = picked.length;
  return (
    <div className="screen start">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Launch many</h1>
          <p className="screen__sub">
            One goal, one batch in each repository you choose, started in order.
          </p>
        </div>
        <div className="screen__actions">
          {lead}
          <Button
            variant="primary"
            icon="play"
            label={`Start ${count} ${count === 1 ? 'batch' : 'batches'}`}
            loading={running}
            disabled={me.halted || count === 0 || goal.trim() === ''}
            onClick={() => void launch()}
          />
        </div>
      </div>
      {me.halted ? (
        <Banner tone="danger" icon="alert-circle" title="This server is halted" role="alert">
          It refuses new runs until the owner resumes it.
        </Banner>
      ) : null}
      <Panel title="Task">
        <div className="stack stack--tight">
          <div className="formgrid">
            <Field label="Template">
              {(control) => (
                <Select {...control} value="" onChange={(e) => applyTemplate(e.target.value)}>
                  <option value="">None</option>
                  {templates.map((t) => (
                    <option key={t.name} value={t.name}>
                      {t.name}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field label="Size">
              {(control) => (
                <Select {...control} value={size} onChange={(e) => setSize(e.target.value)}>
                  {SIZES.map((s) => (
                    <option key={s} value={s}>
                      {s}
                    </option>
                  ))}
                </Select>
              )}
            </Field>
            <Field label="Template name">
              {(control) => (
                <Input {...control} value={name} onChange={(e) => setName(e.target.value)} />
              )}
            </Field>
            <Button label="Save template" disabled={name.trim() === ''} onClick={save} />
          </div>
          <Field label="Goal">
            {(control) => (
              <Textarea
                {...control}
                rows={4}
                value={goal}
                placeholder="What should each batch change?"
                onChange={(e) => setGoal(e.target.value)}
              />
            )}
          </Field>
        </div>
      </Panel>
      <Panel title="Repositories">
        <div className="stack stack--tight">
          {list.map((r) => {
            const why = notReady(r);
            return (
              <div key={r.path} className="row">
                <Check
                  aria-label={r.name}
                  checked={why === null && chosen.includes(r.path)}
                  disabled={why !== null}
                  onChange={() => toggle(r.path)}
                />{' '}
                {r.name}
                {why ? <span className="dim"> not ready: {why}</span> : null}
              </div>
            );
          })}
          <p className="dim small">
            {ready.length} ready, {list.length - ready.length} not.
          </p>
        </div>
      </Panel>
      <Panel title="Forecast">
        <p data-testid="launch-forecast">
          {count} {count === 1 ? 'task' : 'tasks'}
          {known.length > 0 ? ` ${formatUsd(total, true)} typical` : ''}
          {known.length < count ? ' (forecast loading or unavailable for some)' : ''}
        </p>
        {picked.map((r, i) => {
          const f = forecasts[i]?.data;
          return f ? (
            <p key={r.path} className="dim small">
              {r.name}: {formatUsd(f.usd_p50 ?? f.usd_p90, true)}
            </p>
          ) : null;
        })}
      </Panel>
      {outcomes.length > 0 ? (
        <Panel title="Started">
          <ul className="stack stack--tight" aria-label="Results">
            {outcomes.map((o) => (
              <li key={o.repo}>
                {o.repo}:{' '}
                {o.runId ? (
                  <Link to="/runs/$runId" params={{ runId: o.runId }}>
                    {o.runId}
                  </Link>
                ) : (
                  <span role="alert">Refused: {o.error}</span>
                )}
              </li>
            ))}
          </ul>
        </Panel>
      ) : null}
    </div>
  );
}
