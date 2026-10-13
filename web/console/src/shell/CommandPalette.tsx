import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Command } from 'cmdk';
import { useMemo, useState } from 'react';
import type { BatchBody } from '../api/batch';
import { useInbox } from '../api/inbox';
import { runsQuery } from '../api/queries';
import { baseName, shortId } from '../lib/format';
import { runStatus, STATUS, taskStatus } from '../lib/status';
import { CommandItem, Dialog, Icon, type IconName } from '../ui';
import { browserNotificationsAvailable } from './notifications';
import { cycleDensity, toggleRail, toggleTheme, usePreferences } from './preferences';
import { SCREENS } from './screens';

export interface PaletteProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  owner: boolean;
  halted: boolean;
  onHalt: () => void;
  onResume: () => void;
  onShortcuts: () => void;
  onSignOut: () => void;
  onEnableNotifications: () => void;
}

interface Action {
  id: string;
  label: string;
  icon: IconName;
  meta?: string;
  keys?: string[];
  run: () => void;
}

/** How many runs the palette offers at once; a longer history is the Runs screen's. */
const RUN_LIMIT = 40;

/**
 * ⌘K: go to a screen, open a run or a task, or run an action. Destructive actions are here but ask first, and say so.
 * cmdk ranks the matches and moves the active row with the arrows; Enter runs it and Esc closes the palette.
 */
export function CommandPalette(props: PaletteProps) {
  const { open, onOpenChange, owner, halted } = props;
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const prefs = usePreferences();
  const [query, setQuery] = useState('');
  const runs = useQuery({ ...runsQuery({ limit: RUN_LIMIT }), enabled: open });
  const inbox = useInbox();

  const close = () => {
    onOpenChange(false);
    setQuery('');
  };
  const then = (fn: () => void) => () => {
    close();
    fn();
  };

  const screens = SCREENS.filter((screen) => owner || !screen.ownerOnly);
  const tasks = useMemo(() => {
    // Tasks the console already knows: those of the batches it fetched, and the ones that wait for you.
    const seen = new Map<string, { id: string; batch: string; meta: string }>();
    if (open)
      for (const [, body] of queryClient.getQueriesData<BatchBody>({ queryKey: ['batch'] }))
        for (const task of body?.tasks ?? [])
          seen.set(`${body?.run.run_id}/${task.id}`, {
            id: task.id,
            batch: body?.run.run_id ?? '',
            meta: `${baseName(body?.repository?.path)} · ${STATUS[taskStatus(task.state)].label.toLowerCase()}`,
          });
    for (const item of inbox.data?.items ?? [])
      if (item.task_id && item.batch_run_id)
        seen.set(`${item.batch_run_id}/${item.task_id}`, {
          id: item.task_id,
          batch: item.batch_run_id,
          meta: `${baseName(item.repository)} · ${item.kind === 'question' ? 'question' : item.kind === 'pull_request' ? 'pull request' : 'failed'}`,
        });
    return [...seen.values()].slice(0, 30);
  }, [open, inbox.data, queryClient]);

  const actions: Action[] = [
    {
      id: 'theme',
      label: 'Toggle dark and light theme',
      icon: prefs.theme === 'dark' ? 'sun' : 'moon',
      run: toggleTheme,
    },
    {
      id: 'density',
      label: 'Change row density',
      icon: 'menu',
      meta: prefs.density,
      run: cycleDensity,
    },
    {
      id: 'rail',
      label: prefs.railCollapsed ? 'Expand the rail' : 'Collapse the rail',
      icon: 'panel-left',
      keys: ['['],
      run: toggleRail,
    },
    {
      id: 'shortcuts',
      label: 'Keyboard shortcuts',
      icon: 'keyboard',
      keys: ['?'],
      run: props.onShortcuts,
    },
  ];
  if (browserNotificationsAvailable() && !prefs.browserNotifications)
    actions.push({
      id: 'notify',
      label: 'Turn on browser notifications',
      icon: 'bell',
      meta: 'asks the browser',
      run: props.onEnableNotifications,
    });
  if (owner)
    actions.push(
      halted
        ? { id: 'resume', label: 'Resume server', icon: 'play', run: props.onResume }
        : { id: 'halt', label: 'Halt server', icon: 'stop', meta: 'asks first', run: props.onHalt },
    );
  actions.push({ id: 'out', label: 'Sign out', icon: 'lock', run: props.onSignOut });

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => (next ? onOpenChange(true) : close())}
      title="Command palette"
      hideTitle
      surface="ch-palette"
    >
      <Command label="Search commands and runs" loop>
        <div className="ch-palette__input">
          <Icon name="search" />
          <Command.Input
            value={query}
            onValueChange={setQuery}
            placeholder="Type a command, a screen, a run id or a task"
            data-autofocus=""
          />
          <kbd className="ch-kbd">esc</kbd>
        </div>
        <Command.List className="ch-palette__list">
          <Command.Empty className="ch-empty ch-empty--tight">
            <p className="ch-empty__text">
              No match for this text. Try a run id suffix such as 1fdf01.
            </p>
          </Command.Empty>
          <Command.Group heading="Go to">
            {screens.map((screen) => (
              <CommandItem
                key={screen.to}
                label={screen.label}
                icon={screen.icon}
                keys={screen.go ? ['g', screen.go] : undefined}
                query={query}
                onSelect={then(() => void navigate({ to: screen.to }))}
              />
            ))}
          </Command.Group>
          {tasks.length > 0 ? (
            <Command.Group heading="Tasks">
              {tasks.map((task) => (
                <CommandItem
                  key={`${task.batch}/${task.id}`}
                  value={`task ${task.id} ${task.meta}`}
                  label={task.id}
                  icon="box"
                  meta={task.meta}
                  query={query}
                  onSelect={then(
                    () => void navigate({ to: '/runs/$runId', params: { runId: task.batch } }),
                  )}
                />
              ))}
            </Command.Group>
          ) : null}
          {runs.data && runs.data.length > 0 ? (
            <Command.Group heading="Runs">
              {runs.data.map((run) => (
                <CommandItem
                  key={run.run_id}
                  value={`run ${run.run_id} ${run.repository ?? ''} ${run.branch ?? ''} ${run.preset} ${run.status}`}
                  label={`${run.kind === 'batch' ? 'Batch' : 'Run'} ${shortId(run.run_id)}`}
                  icon={run.kind === 'batch' ? 'layers' : 'list'}
                  meta={`${baseName(run.repository)} · ${STATUS[runStatus(run.status)].label.toLowerCase()}`}
                  query={query}
                  onSelect={then(
                    () => void navigate({ to: '/runs/$runId', params: { runId: run.run_id } }),
                  )}
                />
              ))}
            </Command.Group>
          ) : null}
          <Command.Group heading="Actions">
            {actions.map((action) => (
              <CommandItem
                key={action.id}
                label={action.label}
                icon={action.icon}
                meta={action.meta}
                keys={action.keys}
                query={query}
                onSelect={then(action.run)}
              />
            ))}
          </Command.Group>
        </Command.List>
        <div className="ch-palette__foot">
          <span>
            <kbd className="ch-kbd">↑</kbd> <kbd className="ch-kbd">↓</kbd> move
          </span>
          <span>
            <kbd className="ch-kbd">↵</kbd> run
          </span>
          <span>
            <kbd className="ch-kbd">esc</kbd> close
          </span>
        </div>
      </Command>
    </Dialog>
  );
}
