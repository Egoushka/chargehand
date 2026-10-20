import { useQuery } from '@tanstack/react-query';
import type { ProfileRunner, RunnerBody, SystemBody } from '../../api/system';
import { systemQuery } from '../../api/system';
import { DASH, formatDateTime, formatTime } from '../../lib/format';
import { useShell } from '../../shell/ShellContext';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Button,
  CapMeter,
  Cell,
  cssVars,
  EmptyState,
  ErrorState,
  HeadCell,
  KeyValue,
  Panel,
  StatusPill,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import { isForbidden, useOwnerGate } from '../admin/gate';
import { OwnerOnly } from '../admin/OwnerOnly';
import { Later, lastSeen, NotRecorded, PanelSkeleton, ScrollBox } from '../admin/parts';
import { gib } from '../overview/model';
import {
  containerCounts,
  containerRows,
  imageRows,
  presetRows,
  profileText,
  type RunnerView,
  runnerStatusView,
  runnerView,
  uptime,
} from './model';
import './system.css';

const CONTAINER_COLS = 'minmax(0,1.6fr) 76px minmax(0,1.2fr) 88px 72px';
const PRESET_COLS = '92px 84px 92px minmax(0,1fr)';
const RUNNER_COLS = '96px minmax(0,1.4fr) 84px 64px 88px 108px';

/** The server, its runner and the containers it controls: version, redacted profile, presets, pinned images, and the halt switch. */
export function System() {
  const gate = useOwnerGate();
  const system = useQuery({ ...systemQuery(), enabled: gate.allowed });
  const shell = useShell();
  const data = system.data;

  if (gate.denied || isForbidden(system.error))
    return <OwnerOnly title="System" route="GET /v1/system" account={gate.name} />;

  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">System</h1>
          <p className="screen__sub">
            The server, the runner and the containers it controls. Secrets are shown as set or not
            set, never by value.
          </p>
        </div>
        <div className="screen__actions">
          {data?.halted ? (
            <Button label="Resume server" onClick={shell.resumeServer} loading={shell.haltBusy} />
          ) : data ? (
            <Button label="Halt server" variant="danger-quiet" onClick={shell.requestHalt} />
          ) : null}
        </div>
      </div>

      {system.isError ? (
        <ErrorState
          title="Cannot read the system"
          call="GET /v1/system"
          detail={system.error.message}
          age={data ? lastSeen(system.dataUpdatedAt) : undefined}
          onRetry={() => void system.refetch()}
        />
      ) : null}

      {!data && !system.isError ? (
        <div className="g2e" aria-busy="true">
          <div className="stack">
            <PanelSkeleton title="Live containers" lines={[100, 90, 95]} />
            <PanelSkeleton title="Presets and prompt versions" />
          </div>
          <div className="stack">
            <PanelSkeleton title="Version and engine" />
            <PanelSkeleton title="Pinned images" />
          </div>
        </div>
      ) : null}

      {data ? (
        <>
          <div className="g2">
            <div className="stack">
              <ContainersPanel runner={data.runner} />
              <PresetsPanel data={data} />
              <RunnersPanel data={data} />
            </div>
            <div className="stack">
              <EnginePanel data={data} />
              <HostPanel runner={data.runner} />
              <ImagesPanel data={data} />
              <ProfilePanel data={data} />
            </div>
          </div>
          <NotRecorded lines={data.not_recorded} />
        </>
      ) : null}
    </div>
  );
}

function ContainersPanel({ runner }: { runner: RunnerBody }) {
  const phone = useIsPhone();
  const rows = containerRows(runner);
  const view = runnerView(runner);
  return (
    <Panel
      title="Live containers"
      count={runner.reachable ? rows.length : undefined}
      flush
      style={cssVars({ '--o': 2 })}
      aside={runner.container_count != null ? `${runner.container_count} on the runner` : undefined}
    >
      {runner.reachable === false ? (
        <div className="pad">
          <ErrorState title="The runner does not answer" detail={runner.error ?? undefined} />
        </div>
      ) : runner.reachable == null ? (
        <EmptyState
          icon="box"
          title="Driven sessions are off"
          text={`The runner is ${view.text}. Session, egress and service containers appear here while a batch runs.`}
        />
      ) : rows.length === 0 ? (
        <EmptyState
          icon="box"
          title="No container is running"
          text={
            <>
              Session, egress and service containers appear here while a batch runs.{' '}
              <span className="mono">chargehand runs kill --all</span> removes them by label when
              the server is down.
            </>
          }
        />
      ) : phone ? (
        <ul className="list-reset">
          {rows.map((row) => (
            <li key={row.name} className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
              <div className="li__main">
                <div className="row">
                  <Badge size="s" label={row.kind} />
                  <span className="mono ch-trunc" title={row.name}>
                    {row.name}
                  </span>
                </div>
                <div className="li__meta">
                  {row.owner} · {row.actor}
                </div>
              </div>
              {row.session ? <StopButton /> : null}
            </li>
          ))}
        </ul>
      ) : (
        <ScrollBox label="Live containers">
          <Table cols={CONTAINER_COLS} label="Live containers">
            <TableHead>
              <HeadCell>Container</HeadCell>
              <HeadCell>Kind</HeadCell>
              <HeadCell>Task</HeadCell>
              <HeadCell>Account</HeadCell>
              <HeadCell>
                Stop <Later />
              </HeadCell>
            </TableHead>
            {rows.map((row) => (
              <TableRow key={row.name}>
                <Cell mono title={row.name}>
                  {row.name}
                </Cell>
                <Cell>
                  <Badge size="s" label={row.kind} />
                </Cell>
                <Cell mono title={row.owner}>
                  {row.owner}
                </Cell>
                <Cell>{row.actor}</Cell>
                <Cell>{row.session ? <StopButton /> : null}</Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      <p className="pad dim small sep-top">
        {rows.length ? `${containerCounts(runner)}. ` : ''}
        Stopping one container has no route yet; Halt cancels every run. Start time, state and
        memory per container are not served.
      </p>
    </Panel>
  );
}

const StopButton = () => (
  <Button
    label="Stop"
    size="sm"
    variant="danger-quiet"
    disabled
    title="Stopping one container is not available yet"
  />
);

function PresetsPanel({ data }: { data: SystemBody }) {
  const phone = useIsPhone();
  const rows = presetRows(data.presets);
  return (
    <Panel
      title="Presets and prompt versions"
      count={rows.length}
      flush
      style={cssVars({ '--o': 4 })}
    >
      {rows.length === 0 ? (
        <EmptyState
          icon="layers"
          title="No preset"
          text="The presets directory lists none, so only a request that names no preset can run."
        />
      ) : phone ? (
        <ul className="list-reset">
          {rows.map((row) => (
            <li key={row.name} className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
              <div className="li__main">
                <div className="b">
                  {row.name} <span className="mono dim">{row.version}</span>
                </div>
                <div className="li__meta wrap">
                  {row.error ? `cannot load: ${row.error}` : `${row.mode} · ${row.actions}`}
                </div>
              </div>
            </li>
          ))}
        </ul>
      ) : (
        <ScrollBox label="Presets">
          <Table cols={PRESET_COLS} label="Presets and prompt versions">
            <TableHead>
              <HeadCell>Preset</HeadCell>
              <HeadCell>Version</HeadCell>
              <HeadCell>Kind</HeadCell>
              <HeadCell>Allowed actions</HeadCell>
            </TableHead>
            {rows.map((row) => (
              <TableRow key={row.name}>
                <Cell strong>{row.name}</Cell>
                <Cell mono>{row.version}</Cell>
                <Cell>
                  {row.error ? (
                    <StatusPill status="failed" label="Cannot load" bare />
                  ) : (
                    <Badge size="s" label={row.mode} />
                  )}
                </Cell>
                <Cell className="wrap dim" title={row.error ?? row.actions}>
                  {row.error ?? row.actions}
                </Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      <p className="pad dim small sep-top">
        Versions are the presets’ own. Whether a change passed Prompt CI is not recorded here.
      </p>
    </Panel>
  );
}

function RunnersPanel({ data }: { data: SystemBody }) {
  const phone = useIsPhone();
  const runners: ProfileRunner[] = data.profile?.driven?.runners ?? [];
  // A server older than `runners` probes only the first runner.
  const view = (name: string, index: number) =>
    runnerStatusView(
      data.runners?.find((r) => r.name === name),
      data.runners ? undefined : index === 0 ? data.runner : undefined,
    );
  const sessions = (runner: ProfileRunner) => {
    const running = data.runners?.find((r) => r.name === runner.name)?.running;
    if (running == null) return runner.max_sessions ?? DASH;
    return `${running} / ${runner.max_sessions ?? '∞'}`;
  };
  return (
    <Panel title="Runners" count={runners.length} flush style={cssVars({ '--o': 5 })}>
      {runners.length === 0 ? (
        <EmptyState
          icon="server"
          title="No runner service listed"
          text={
            data.profile?.driven?.enabled
              ? 'driven.runners is empty: sessions run on the local container engine.'
              : 'Driven sessions are off in this profile.'
          }
        />
      ) : phone ? (
        <ul className="list-reset">
          {runners.map((runner, index) => (
            <li
              key={runner.name}
              className="li"
              style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}
            >
              <div className="li__main">
                <div className="b">{runner.name}</div>
                <div className="li__meta mono" title={runner.host ?? undefined}>
                  {runner.host ?? 'local engine'} · {runner.platform}
                </div>
              </div>
              <RunnerPill view={view(runner.name, index)} />
            </li>
          ))}
        </ul>
      ) : (
        <ScrollBox label="Runners">
          <Table cols={RUNNER_COLS} label="Runners">
            <TableHead>
              <HeadCell>Runner</HeadCell>
              <HeadCell>Address</HeadCell>
              <HeadCell>Platform</HeadCell>
              <HeadCell num>Weight</HeadCell>
              <HeadCell num>Sessions</HeadCell>
              <HeadCell>State</HeadCell>
            </TableHead>
            {runners.map((runner, index) => (
              <TableRow key={runner.name}>
                <Cell strong>{runner.name}</Cell>
                <Cell mono className="wrap" title={runner.host ?? undefined}>
                  {runner.host ?? 'local engine'}
                </Cell>
                <Cell>{runner.platform}</Cell>
                <Cell num>{runner.weight}</Cell>
                <Cell num>{sessions(runner)}</Cell>
                <Cell>
                  <RunnerPill view={view(runner.name, index)} />
                </Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      <p className="pad dim small sep-top">
        State is the scheduler's last probe of each runner, asked again when older than five
        seconds; a runner that does not answer is not asked again until its back-off ends.
      </p>
    </Panel>
  );
}

function RunnerPill({ view }: { view: RunnerView }) {
  return (
    <span title={view.text}>
      <StatusPill status={view.status} label={view.short} bare />
    </span>
  );
}

function EnginePanel({ data }: { data: SystemBody }) {
  const view = runnerView(data.runner);
  const governor = data.profile?.driven?.governor?.mode;
  return (
    <Panel title="Version and engine" style={cssVars({ '--o': 1 })}>
      <KeyValue
        stacked
        items={[
          ['chargehand', data.version, true],
          [
            'Started',
            <span key="s" title={formatDateTime(data.started_at)}>
              {formatTime(data.started_at)}{' '}
              <span className="dim">· up {uptime(data.started_at)}</span>
            </span>,
          ],
          [
            'Runner',
            <span key="r" className="row">
              <i className="dot" data-tone={view.tone} />
              {data.runner.host ?? 'local container engine'} · {view.text}
            </span>,
          ],
          [
            'Containers',
            data.runner.container_count != null ? String(data.runner.container_count) : DASH,
          ],
          [
            'Governor',
            governor ? (
              <span key="gov" className="row">
                <Badge
                  size="s"
                  tone={
                    governor === 'observe' ? 'info' : governor === 'enforce' ? 'danger' : undefined
                  }
                  label={governor}
                />
                <span className="dim">profile driven.governor.mode</span>
              </span>
            ) : (
              DASH
            ),
          ],
          ['Halt', data.halted ? 'Halted: nothing starts' : 'Accepting runs'],
          ['Unfinished runs', String(data.unfinished)],
        ]}
      />
    </Panel>
  );
}

function HostPanel({ runner }: { runner: RunnerBody }) {
  const host = runner.host_snapshot;
  if (!host) return null;
  const { total_mb: total, available_mb: available } = host;
  const reservePct = total ? Math.min(100, (host.reserve_mb / total) * 100) : null;
  return (
    <Panel title="Runner host" style={cssVars({ '--o': 3 })} aside="what admission reads">
      <div className="stack stack--tight">
        {total != null && available != null ? (
          <CapMeter
            label="Memory available"
            head="Memory available"
            value={available}
            cap={total}
            text={`${gib(available)} of ${gib(total)}`}
            level={(host.free_after_reserve_mb ?? 1) <= 0 ? 'danger' : undefined}
            ticks={reservePct != null ? [reservePct] : undefined}
          />
        ) : null}
        <KeyValue
          stacked
          items={[
            ['Reserve kept free', gib(host.reserve_mb)],
            ['Free beyond the reserve', gib(host.free_after_reserve_mb)],
            [
              'Load, 1 min',
              host.load1 != null && host.cpus
                ? `${host.load1.toFixed(1)} on ${host.cpus} CPUs`
                : DASH,
            ],
          ]}
        />
      </div>
    </Panel>
  );
}

function ImagesPanel({ data }: { data: SystemBody }) {
  const rows = imageRows(data.images);
  return (
    <Panel title="Pinned images" count={rows.length} flush style={cssVars({ '--o': 6 })}>
      {rows.length === 0 ? (
        <EmptyState
          icon="layers"
          title="No image listed"
          text="driven.images is empty: a session cannot start from an image the profile does not name."
        />
      ) : (
        <ul className="list-reset">
          {rows.map((row) => (
            <li key={row.full} className="li" style={cssVars({ '--cols': 'minmax(0,1fr)' })}>
              <div className="row row--between">
                <span className="b ch-trunc" title={row.full}>
                  {row.name}
                </span>
                <StatusPill
                  status={row.pinned ? 'done' : 'partial'}
                  label={row.pinned ? 'Pinned' : 'Not pinned'}
                  bare
                />
              </div>
              <div className="mono dim ch-trunc" title={row.full}>
                {row.digest}
              </div>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function ProfilePanel({ data }: { data: SystemBody }) {
  const text = profileText(data);
  return (
    <Panel
      title="Profile"
      style={cssVars({ '--o': 7 })}
      aside={<Badge size="s" label="redacted" />}
    >
      {text ? (
        <div className="stack stack--tight">
          <ScrollBox label="Redacted profile" className="code system-profile">
            {text}
          </ScrollBox>
          <p className="dim small">
            As the route returns it: a secret item is set or not set, an address is a scheme, host
            and port, and commands, headers and environment values are left out.
          </p>
        </div>
      ) : (
        <EmptyState
          icon="file"
          title="No profile loaded"
          text="The server runs without a profile, so there are no settings to show."
        />
      )}
    </Panel>
  );
}
