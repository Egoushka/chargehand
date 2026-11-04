import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { type IntakeLimitsBody, intakeLimitsQuery, useSetLimits } from '../../api/limits';
import { formatUsd } from '../../lib/format';
import { Banner, Button, EmptyState, ErrorState, Field, Input, Panel, Switch } from '../../ui';
import { isForbidden, useOwnerGate } from '../admin/gate';
import { OwnerOnly } from '../admin/OwnerOnly';
import { PanelSkeleton } from '../admin/parts';
import {
  changeOf,
  type Draft,
  draftOf,
  hasProblem,
  isUnchanged,
  problemWith,
  RANGES,
} from './model';

/** The owner's intake caps and pause switch: set here, they win over the profile at intake's next poll, with no restart. */
export function Settings() {
  const gate = useOwnerGate();
  const limits = useQuery({ ...intakeLimitsQuery(), enabled: gate.allowed });
  if (gate.denied || isForbidden(limits.error))
    return <OwnerOnly title="Settings" route="GET /v1/intake/limits" account={gate.name} />;
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Settings</h1>
          <p className="screen__sub">
            What intake may start. A box left empty uses the profile's value; a change applies at
            the next poll.
          </p>
        </div>
      </div>
      {limits.isError ? (
        <ErrorState
          title="Cannot read the settings"
          call="GET /v1/intake/limits"
          detail={limits.error.message}
          onRetry={() => limits.refetch()}
        />
      ) : limits.data === undefined ? (
        <PanelSkeleton title="Intake limits" lines={[100, 90, 95]} />
      ) : !limits.data.configured ? (
        <div className="card">
          <EmptyState
            icon="bolt"
            title="Intake is not configured"
            text="The profile has no automation.github, so there is nothing to limit."
          />
        </div>
      ) : (
        <LimitsForm key={JSON.stringify(limits.data)} limits={limits.data} />
      )}
    </div>
  );
}

const CAPS = [
  {
    key: 'max_concurrent',
    label: 'Triggered batches at once',
    hint: 'A trigger over this waits for a slot.',
    step: 1,
  },
  {
    key: 'max_review_rounds',
    label: 'Review rounds per pull request',
    hint: '0 turns review rounds off.',
    step: 1,
  },
  {
    key: 'max_usd_total',
    label: 'Dollar cap per task',
    hint: 'What one triggered task may spend.',
    step: 0.5,
  },
] as const;

function LimitsForm({ limits }: { limits: IntakeLimitsBody }) {
  const [draft, setDraft] = useState<Draft>(() => draftOf(limits));
  const save = useSetLimits();
  const shown = (key: (typeof CAPS)[number]['key']) => {
    const { profile } = limits[key];
    return key === 'max_usd_total' ? formatUsd(profile) : String(profile);
  };
  return (
    <Panel title="Intake limits" aside="applies at the next poll">
      <form
        className="pad stack"
        onSubmit={(event) => {
          event.preventDefault();
          if (!hasProblem(draft)) save.mutate(changeOf(draft));
        }}
      >
        {save.isError ? (
          <Banner tone="danger" icon="alert-circle" title="Not saved" role="alert">
            {save.error.message}
          </Banner>
        ) : null}
        {draft.paused ? (
          <Banner tone="warn" icon="alert-circle" title="Intake is paused">
            No new batch or review starts until this is switched off. Running ones finish.
          </Banner>
        ) : null}
        {CAPS.map(({ key, label, hint, step }) => (
          <Field
            key={key}
            label={label}
            hint={`${hint} Profile: ${shown(key)}.`}
            error={problemWith(key, draft[key])}
          >
            {(control) => (
              <Input
                {...control}
                type="number"
                inputMode="decimal"
                min={RANGES[key].min}
                max={RANGES[key].max}
                step={step}
                placeholder={String(limits[key].profile)}
                value={draft[key]}
                onChange={(event) => setDraft({ ...draft, [key]: event.target.value })}
              />
            )}
          </Field>
        ))}
        <div className="row">
          <Switch
            aria-label="Pause intake"
            checked={draft.paused}
            onChange={(event) => setDraft({ ...draft, paused: event.target.checked })}
          />
          <span aria-hidden="true">Pause intake</span>
        </div>
        <div className="row">
          <Button
            type="submit"
            label="Save"
            loading={save.isPending}
            disabled={hasProblem(draft) || isUnchanged(draft, limits)}
          />
          <Button
            variant="quiet"
            label="Use the profile's values"
            onClick={() =>
              setDraft({
                ...draft,
                max_concurrent: '',
                max_review_rounds: '',
                max_usd_total: '',
              })
            }
          />
        </div>
      </form>
    </Panel>
  );
}
