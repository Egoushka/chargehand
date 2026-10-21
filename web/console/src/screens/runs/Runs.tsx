import { Link, useNavigate } from '@tanstack/react-router';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useCancelRuns } from '../../api/queries';
import type { RunSummary } from '../../api/types';
import { formatTime } from '../../lib/format';
import { keysBelongElsewhere } from '../../shell/hotkeys';
import { useToast } from '../../shell/toasts';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Button,
  buttonClass,
  Chip,
  ConfirmDialog,
  Dialog,
  EmptyState,
  ErrorState,
  Field,
  Icon,
  IconButton,
  Input,
  InputGroup,
  Select,
  Skeleton,
} from '../../ui';
import {
  ANY,
  activeCount,
  BUILTIN_VIEWS,
  DEFAULT_FILTERS,
  distinct,
  isCancelable,
  MAX_WINDOW,
  options,
  RANGE_OPTIONS,
  type RangeFilter,
  type RunFilters,
  STATE_OPTIONS,
  type StateFilter,
  sameFilters,
} from './model';
import { RunCard } from './RunCard';
import { RunsTable } from './RunsTable';
import { useRunList } from './useRunList';
import { newViewId, readViews, writeViews } from './views';
import './runs.css';

const baseOf = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;

/** Every run, newest first, with the filters the server knows, search, saved views and a bulk cancel. */
export function Runs() {
  const phone = useIsPhone();
  const navigate = useNavigate();
  const toast = useToast();
  const [filters, setFilters] = useState<RunFilters>(DEFAULT_FILTERS);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [saved, setSaved] = useState(readViews);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [cursor, setCursor] = useState(0);
  const [scrollTo, setScrollTo] = useState<{ index: number; at: number } | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [saving, setSaving] = useState(false);
  const list = useRunList(filters);
  const cancel = useCancelRuns();
  const { query, rows, loaded } = list;

  const update = useCallback(
    (patch: Partial<RunFilters>) => {
      setFilters((f) => ({ ...f, ...patch }));
      setCursor(0);
      list.reset();
    },
    [list.reset],
  );
  const apply = (next: RunFilters) => {
    setFilters(next);
    setCursor(0);
    list.reset();
  };

  const views = useMemo(() => [...BUILTIN_VIEWS, ...saved], [saved]);
  const currentView = views.find((v) => sameFilters(v.filters, filters));
  const savedView = saved.find((v) => v.id === currentView?.id);

  const chosen = useMemo(() => rows.filter((r) => selected.has(r.run_id)), [rows, selected]);
  const cancelable = chosen.filter(isCancelable);
  const at = Math.min(cursor, Math.max(0, rows.length - 1));

  const toggle = useCallback((runId: string) => {
    setSelected((s) => {
      const next = new Set(s);
      if (!next.delete(runId)) next.add(runId);
      return next;
    });
  }, []);
  const toggleAll = () =>
    setSelected(chosen.length === rows.length ? new Set() : new Set(rows.map((r) => r.run_id)));

  // j and k move the cursor, Enter opens the run, x selects it.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.metaKey || event.ctrlKey || event.altKey || keysBelongElsewhere(event.target))
        return;
      const row = rows[at];
      const move = (by: number) => {
        const next = Math.max(0, Math.min(rows.length - 1, at + by));
        setCursor(next);
        setScrollTo({ index: next, at: Date.now() });
      };
      if (event.key === 'j') move(1);
      else if (event.key === 'k') move(-1);
      else if (event.key === 'x' && row) toggle(row.run_id);
      else if (event.key === 'Enter' && row && !(event.target instanceof HTMLButtonElement))
        void navigate({ to: '/runs/$runId', params: { runId: row.run_id } });
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [rows, at, toggle, navigate]);

  const runCancel = () => {
    const ids = cancelable.map((r) => r.run_id);
    setConfirming(false);
    cancel.mutate(ids, {
      onSuccess: (outcomes) => {
        const started = outcomes.filter((o) => o.result === 'cancelling').length;
        const rest = outcomes.length - started;
        toast.push({
          tone: rest > 0 ? 'warn' : 'ok',
          icon: rest > 0 ? 'alert-triangle' : 'check',
          title: `Cancelling ${started} of ${outcomes.length} ${outcomes.length === 1 ? 'run' : 'runs'}`,
          body:
            rest > 0
              ? `${rest} had finished, belong to another process or could not be cancelled.`
              : 'Each ends failed with the code cancelled. Nothing from a stopped task is pushed.',
        });
        setSelected(new Set());
      },
      onError: (error) =>
        toast.push({
          tone: 'danger',
          icon: 'alert-triangle',
          title: 'Cannot cancel',
          body: error.message,
        }),
    });
  };

  const repositories = options(
    distinct(loaded, (r) => r.repository),
    filters.repository,
  );
  const accounts = options(
    distinct(loaded, (r) => r.actor),
    filters.account,
  );
  const presets = options(
    distinct(loaded, (r) => r.preset),
    filters.preset,
  );
  const count = activeCount(filters);
  const noRuns = loaded.length === 0 && count === 0;
  const subtitle = query.isPending
    ? 'Loading'
    : query.isError && !query.data
      ? 'Not loaded'
      : `${rows.length === loaded.length ? `${rows.length} runs` : `${rows.length} of ${loaded.length} loaded runs match`}, newest first · Kyiv time`;

  const filterFields = (
    <>
      <Select
        aria-label="State"
        value={filters.state}
        touch={phone}
        onChange={(e) => update({ state: e.target.value as StateFilter })}
      >
        {STATE_OPTIONS.map((o) => (
          <option key={o.id} value={o.id}>
            {o.label}
          </option>
        ))}
      </Select>
      <Select
        aria-label="Kind"
        value={filters.kind}
        touch={phone}
        onChange={(e) => update({ kind: e.target.value as RunFilters['kind'] })}
      >
        <option value={ANY}>Kind: any</option>
        <option value="batch">Batch</option>
        <option value="single">Single</option>
      </Select>
      <Select
        aria-label="Repository"
        value={filters.repository}
        touch={phone}
        onChange={(e) => update({ repository: e.target.value })}
      >
        <option value={ANY}>Repository: any</option>
        {repositories.map((r) => (
          <option key={r} value={r} title={r}>
            {baseOf(r)}
          </option>
        ))}
      </Select>
      <Select
        aria-label="Account"
        value={filters.account}
        touch={phone}
        onChange={(e) => update({ account: e.target.value })}
      >
        <option value={ANY}>Account: any</option>
        {accounts.map((a) => (
          <option key={a} value={a}>
            {a}
          </option>
        ))}
      </Select>
      <Select
        aria-label="Preset"
        value={filters.preset}
        touch={phone}
        onChange={(e) => update({ preset: e.target.value })}
      >
        <option value={ANY}>Preset: any</option>
        {presets.map((p) => (
          <option key={p} value={p}>
            {p}
          </option>
        ))}
      </Select>
      <Select
        aria-label="Date"
        value={filters.range}
        touch={phone}
        onChange={(e) => update({ range: e.target.value as RangeFilter })}
      >
        {RANGE_OPTIONS.map((o) => (
          <option key={o.id} value={o.id}>
            {o.label}
          </option>
        ))}
      </Select>
      <Select
        aria-label="Session calls"
        value={filters.calls ? 'show' : 'hide'}
        touch={phone}
        onChange={(e) => update({ calls: e.target.value === 'show' })}
      >
        <option value="hide">Session calls: hidden</option>
        <option value="show">Session calls: shown</option>
      </Select>
      {count > 0 ? (
        <Button
          label="Clear filters"
          variant="quiet"
          size="sm"
          onClick={() => apply(DEFAULT_FILTERS)}
        />
      ) : null}
    </>
  );

  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Runs</h1>
          <p className="screen__sub">{subtitle}</p>
        </div>
        <div className="screen__actions">
          <Link to="/new" className={buttonClass({ variant: 'primary' }, 'hide-phone')}>
            <Icon name="play" />
            Start a batch
          </Link>
        </div>
      </div>

      <div className="toolbar">
        <InputGroup icon="search">
          <Input
            type="search"
            aria-label="Search runs"
            placeholder="Search run id, repository, branch, PR"
            data-filter-input
            touch={phone}
            value={filters.q}
            onChange={(e) => update({ q: e.target.value })}
          />
          {phone ? null : <kbd className="ch-kbd">/</kbd>}
        </InputGroup>
        <Select
          aria-label="Saved views"
          value={currentView?.id ?? 'custom'}
          touch={phone}
          onChange={(e) => {
            const view = views.find((v) => v.id === e.target.value);
            if (view) apply(view.filters);
          }}
        >
          {currentView ? null : <option value="custom">View: custom</option>}
          {BUILTIN_VIEWS.map((v) => (
            <option key={v.id} value={v.id}>
              View: {v.name}
            </option>
          ))}
          {saved.map((v) => (
            <option key={v.id} value={v.id}>
              View: {v.name}
            </option>
          ))}
        </Select>
        {savedView ? (
          <IconButton
            icon="trash"
            label={`Delete the view ${savedView.name}`}
            onClick={() => {
              const next = saved.filter((v) => v.id !== savedView.id);
              setSaved(next);
              writeViews(next);
              apply(DEFAULT_FILTERS);
            }}
          />
        ) : !currentView ? (
          <Button label="Save view" icon="plus" size="sm" onClick={() => setSaving(true)} />
        ) : null}
        {phone ? (
          <Button
            label={
              <>
                Filters
                {count > 0 ? <span className="ch-count">{count}</span> : null}
              </>
            }
            icon="filter"
            aria-pressed={filtersOpen}
            onClick={() => setFiltersOpen((o) => !o)}
          />
        ) : null}
      </div>
      {!phone || filtersOpen ? (
        <div className="toolbar toolbar--filters">{filterFields}</div>
      ) : null}
      {count > 0 && !phone ? (
        <div className="row row--wrap">
          {filters.q.trim() ? (
            <Chip label="Search" value={filters.q.trim()} onRemove={() => update({ q: '' })} />
          ) : null}
          {filters.state !== 'all' ? (
            <Chip
              label="State"
              value={STATE_OPTIONS.find((o) => o.id === filters.state)?.label ?? filters.state}
              onRemove={() => update({ state: 'all' })}
            />
          ) : null}
          {filters.kind !== 'all' ? (
            <Chip label="Kind" value={filters.kind} onRemove={() => update({ kind: 'all' })} />
          ) : null}
          {filters.repository !== ANY ? (
            <Chip
              label="Repository"
              value={baseOf(filters.repository)}
              onRemove={() => update({ repository: ANY })}
            />
          ) : null}
          {filters.account !== ANY ? (
            <Chip
              label="Account"
              value={filters.account}
              onRemove={() => update({ account: ANY })}
            />
          ) : null}
          {filters.preset !== ANY ? (
            <Chip label="Preset" value={filters.preset} onRemove={() => update({ preset: ANY })} />
          ) : null}
          {filters.calls ? (
            <Chip label="Session calls" value="shown" onRemove={() => update({ calls: false })} />
          ) : null}
          {filters.range !== DEFAULT_FILTERS.range ? (
            <Chip
              label="Date"
              value={RANGE_OPTIONS.find((o) => o.id === filters.range)?.label ?? filters.range}
              onRemove={() => update({ range: DEFAULT_FILTERS.range })}
            />
          ) : null}
        </div>
      ) : null}

      {chosen.length > 0 && !phone ? (
        <div className="bulkbar" role="status">
          <b>{chosen.length} selected</b>
          <span className="dim">
            {cancelable.length === chosen.length
              ? 'all still running'
              : `${chosen.length - cancelable.length} finished: skipped by cancel`}
          </span>
          <span className="spacer" />
          <Button
            variant="danger-quiet"
            size="sm"
            label={`Cancel ${cancelable.length} running ${cancelable.length === 1 ? 'run' : 'runs'}`}
            disabled={cancelable.length === 0}
            loading={cancel.isPending}
            onClick={() => setConfirming(true)}
          />
          <Button label="Clear" variant="quiet" size="sm" onClick={() => setSelected(new Set())} />
        </div>
      ) : null}

      {query.isError ? (
        <ErrorState
          title="Cannot load runs"
          call="GET /v1/runs"
          detail={query.error.message}
          age={
            query.data
              ? `The list below is the data from ${formatTime(query.dataUpdatedAt, { seconds: true })}.`
              : undefined
          }
          onRetry={() => void query.refetch()}
        />
      ) : null}

      <div className="tbl" aria-busy={query.isPending || undefined}>
        {query.isPending ? (
          <div className="pad stack stack--tight">
            {[80, 60, 90, 70, 85, 65].map((w) => (
              <Skeleton key={w} width={`${w}%`} height={20} />
            ))}
          </div>
        ) : rows.length === 0 && query.data ? (
          <EmptyState
            icon={noRuns ? 'list' : 'search'}
            title={noRuns ? 'No runs yet' : 'No run matches these filters'}
            text={
              noRuns
                ? 'Start a run or a batch and it appears here.'
                : 'Change a filter, or widen the date range. A saved view keeps a set of filters.'
            }
            action={
              noRuns ? (
                <Link to="/new" className={buttonClass({ variant: 'primary' })}>
                  Start a batch
                </Link>
              ) : (
                <Button label="Clear filters" onClick={() => apply(DEFAULT_FILTERS)} />
              )
            }
          />
        ) : phone ? (
          <ul className="list-reset">
            {rows.map((run: RunSummary) => (
              <RunCard key={run.run_id} run={run} />
            ))}
          </ul>
        ) : (
          <RunsTable
            rows={rows}
            selected={selected}
            cursor={at}
            onToggle={toggle}
            onToggleAll={toggleAll}
            onNearEnd={list.canLoadMore && !query.isFetching ? list.loadMore : undefined}
            scrollTo={scrollTo}
          />
        )}
        {query.data ? (
          <div className="tbl__foot">
            <span>
              Showing {rows.length} of {loaded.length} loaded
              {list.atCap
                ? `. The server returns at most ${MAX_WINDOW} runs per request: narrow the date range or a filter to reach older ones`
                : ''}
            </span>
            <span className="spacer" />
            {list.canLoadMore ? (
              <Button
                label="Load the next 100"
                size="sm"
                loading={query.isFetching}
                onClick={list.loadMore}
              />
            ) : null}
            <span className="ch-live" data-state={query.isError ? 'off' : ''}>
              {query.isError ? 'Not refreshing' : 'Auto-refresh'}
            </span>
          </div>
        ) : null}
      </div>

      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={`Cancel ${cancelable.length} running ${cancelable.length === 1 ? 'run' : 'runs'}?`}
        text={`A batch stops its running tasks and ends failed with the code cancelled. Nothing from a stopped task is pushed.${
          chosen.length > cancelable.length
            ? ` ${chosen.length - cancelable.length} selected ${chosen.length - cancelable.length === 1 ? 'run has' : 'runs have'} finished and ${chosen.length - cancelable.length === 1 ? 'is' : 'are'} skipped.`
            : ''
        }`}
        keepLabel="Keep running"
        confirmLabel={`Cancel ${cancelable.length} ${cancelable.length === 1 ? 'run' : 'runs'}`}
        onConfirm={runCancel}
      />
      <SaveViewDialog
        open={saving}
        onOpenChange={setSaving}
        taken={views.map((v) => v.name.toLowerCase())}
        onSave={(name) => {
          const next = [...saved, { id: newViewId(), name, filters }];
          setSaved(next);
          writeViews(next);
          setSaving(false);
        }}
      />
    </div>
  );
}

function SaveViewDialog({
  open,
  onOpenChange,
  taken,
  onSave,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  taken: string[];
  onSave: (name: string) => void;
}) {
  const [name, setName] = useState('');
  const trimmed = name.trim();
  const clash = taken.includes(trimmed.toLowerCase());
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Save this view">
      <form
        className="dialog__body stack stack--tight"
        onSubmit={(e) => {
          e.preventDefault();
          if (trimmed && !clash) {
            onSave(trimmed);
            setName('');
          }
        }}
      >
        <Field
          label="Name"
          hint="The filters are kept in this browser, not on the server."
          error={clash ? 'A view with this name exists. Pick another.' : undefined}
        >
          {(control) => (
            <Input
              {...control}
              data-autofocus
              maxLength={40}
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
          )}
        </Field>
        <div className="row row--end">
          <Button label="Cancel" onClick={() => onOpenChange(false)} />
          <Button label="Save view" variant="primary" type="submit" disabled={!trimmed || clash} />
        </div>
      </form>
    </Dialog>
  );
}
