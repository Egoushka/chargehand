import { useQuery } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';
import { type SweepSettingsBody, sweepSettingsQuery, useSetSweepSettings } from '../../api/sweep';
import {
  Banner,
  Button,
  EmptyState,
  ErrorState,
  Field,
  IconButton,
  Input,
  Panel,
  Switch,
} from '../../ui';
import { isForbidden } from '../admin/gate';
import { PanelSkeleton } from '../admin/parts';
import {
  addTime,
  changeOf,
  DAYS,
  dayNames,
  draftOf,
  hasProblem,
  holdProblem,
  inForce,
  isUnchanged,
  onOff,
  type SweepDraft,
  timeProblem,
} from '../sweep/model';
import '../sweep/sweep.css';

/** The sweep's schedule, its days, its repositories and the subscription hold: overrides over the profile, applied at the next tick. */
export function SweepSettings({ allowed }: { allowed: boolean }) {
  const settings = useQuery({ ...sweepSettingsQuery(), enabled: allowed });
  if (isForbidden(settings.error)) return null;
  if (settings.isError)
    return (
      <ErrorState
        title="Cannot read the sweep settings"
        call="GET /v1/sweep/settings"
        detail={settings.error.message}
        onRetry={() => settings.refetch()}
      />
    );
  if (settings.data === undefined) return <PanelSkeleton title="Sweep" lines={[100, 90, 95]} />;
  if (!settings.data.configured)
    return (
      <div className="card">
        <EmptyState
          icon="bolt"
          title="The sweep is not configured"
          text="The profile has no automation.github, so there is nothing to schedule."
        />
      </div>
    );
  return <SweepForm key={JSON.stringify(settings.data)} settings={settings.data} />;
}

function SweepForm({ settings }: { settings: SweepSettingsBody }) {
  const [draft, setDraft] = useState<SweepDraft>(() => draftOf(settings));
  const [time, setTime] = useState('');
  const save = useSetSweepSettings();
  const set = (patch: Partial<SweepDraft>) => setDraft({ ...draft, ...patch });
  const enabled = draft.enabled ?? settings.enabled.profile;
  const at = draft.at ?? settings.at.profile;
  const days = draft.days ?? settings.days.profile;
  const timeError = time === '' ? null : timeProblem(time, at);
  const addable = time !== '' && timeError === null;
  const add = (event: FormEvent) => {
    event.preventDefault();
    if (!addable) return;
    set({ at: addTime(at, time) });
    setTime('');
  };
  const toggleDay = (id: string) =>
    set({
      days: DAYS.map(([day]) => day).filter((day) =>
        day === id ? !days.includes(day) : days.includes(day),
      ),
    });
  return (
    <Panel title="Sweep" aside={`times in ${settings.time_zone}`}>
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
        <div className="stack--tight">
          <div className="row">
            <Switch
              aria-label="Run the sweep on its schedule"
              checked={enabled}
              onChange={(event) => set({ enabled: event.target.checked })}
            />
            <span aria-hidden="true">Run the sweep on its schedule</span>
          </div>
          <p className="dim small">
            {inForce(settings.enabled, onOff)}. Scan now works when this is off.
          </p>
        </div>
        <fieldset className="sweep-set">
          <legend className="b">Times</legend>
          <div className="sweep-times">
            {at.map((t) => (
              <span key={t} className="sweep-time">
                {t}
                <IconButton
                  icon="x"
                  size="sm"
                  label={`Remove ${t}`}
                  onClick={() => set({ at: at.filter((x) => x !== t) })}
                />
              </span>
            ))}
            {at.length === 0 ? (
              <span className="dim">No time: the schedule never fires.</span>
            ) : null}
          </div>
          <div className="row">
            <Field label="Add a time" hint="HH:mm, 24 hours." error={timeError}>
              {(control) => (
                <Input
                  {...control}
                  mono
                  placeholder="08:30"
                  value={time}
                  onChange={(event) => setTime(event.target.value)}
                  onKeyDown={(event) => event.key === 'Enter' && add(event)}
                />
              )}
            </Field>
            <Button variant="quiet" label="Add time" disabled={!addable} onClick={add} />
          </div>
          <p className="dim small">{inForce(settings.at, (v) => v.join(', ') || 'none')}</p>
        </fieldset>
        <fieldset className="sweep-set">
          <legend className="b">Days</legend>
          <div className="sweep-days">
            {DAYS.map(([id, short]) => (
              <Button
                key={id}
                variant="quiet"
                label={short}
                aria-pressed={days.includes(id)}
                onClick={() => toggleDay(id)}
              />
            ))}
          </div>
          <p className="dim small">{inForce(settings.days, dayNames)}</p>
        </fieldset>
        <fieldset className="sweep-set">
          <legend className="b">Repositories</legend>
          {Object.entries(settings.repositories).map(([name, setting]) => (
            <div key={name} className="stack--tight">
              <div className="row">
                <Switch
                  aria-label={`Sweep ${name}`}
                  checked={draft.repositories[name] ?? setting.profile}
                  onChange={(event) =>
                    set({ repositories: { ...draft.repositories, [name]: event.target.checked } })
                  }
                />
                <span aria-hidden="true" className="mono">
                  {name}
                </span>
              </div>
              <p className="dim small">{inForce(setting, onOff)}</p>
            </div>
          ))}
        </fieldset>
        <Field
          label="Subscription hold, percent"
          hint={`Share of a subscription window, in percent, after which driven work moves to the API key. Empty uses the profile's value. ${inForce(settings.subscription_hold_percent, (v) => (v === 0 ? 'none' : `${v}%`))}.`}
          error={holdProblem(draft.hold)}
        >
          {(control) => (
            <Input
              {...control}
              type="number"
              inputMode="numeric"
              min={1}
              max={100}
              step={1}
              placeholder={
                settings.subscription_hold_percent.profile === 0
                  ? 'none'
                  : String(settings.subscription_hold_percent.profile)
              }
              value={draft.hold}
              onChange={(event) => set({ hold: event.target.value })}
            />
          )}
        </Field>
        <div className="row">
          <Button
            type="submit"
            label="Save sweep"
            loading={save.isPending}
            disabled={hasProblem(draft) || isUnchanged(draft, settings)}
          />
          <Button
            variant="quiet"
            label="Use the profile's sweep values"
            onClick={() =>
              setDraft({
                enabled: null,
                at: null,
                days: null,
                repositories: Object.fromEntries(
                  Object.keys(settings.repositories).map((n) => [n, null]),
                ),
                hold: '',
              })
            }
          />
        </div>
      </form>
    </Panel>
  );
}
