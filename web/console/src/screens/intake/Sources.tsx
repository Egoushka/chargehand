import { Link } from '@tanstack/react-router';
import { useId } from 'react';
import type { IntakeSourceRunBody, IntakeSourcesBody } from '../../api/intake';
import { useSetIntakeSource } from '../../api/intake';
import { DASH, formatDateTime } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Banner,
  Cell,
  cssVars,
  EmptyState,
  HeadCell,
  Panel,
  Switch,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import { ScrollBox } from '../admin/parts';
import { queueText, type SourceRow, sourceRows } from './model';
import { ReviewPolicy } from './ReviewPolicy';

const COLS = 'minmax(0,1.4fr) 132px minmax(0,1.3fr) 56px';
const PHASE: Record<IntakeSourceRunBody['phase'], string> = {
  running: 'running',
  asked: 'asked a question',
  done: 'done',
};

/** Every repository intake watches, with its switch and its queue (CHARGEHAND-242). */
export function SourcesPanel({ data }: { data: IntakeSourcesBody }) {
  const phone = useIsPhone();
  const set = useSetIntakeSource();
  const rows = sourceRows(data);
  const pending = set.isPending ? set.variables : undefined;
  const toggle = (row: SourceRow, enabled: boolean) => set.mutate({ id: row.id, enabled });
  const props = (row: SourceRow) => ({
    row,
    busy: pending?.id === row.id,
    onToggle: toggle,
  });
  return (
    <Panel title="Sources" count={rows.length} flush>
      {set.isError ? (
        <div className="pad">
          <Banner tone="danger" icon="alert-circle" title="The switch was not set" role="alert">
            PUT /v1/intake/sources: {set.error.message}
          </Banner>
        </div>
      ) : null}
      {rows.length === 0 ? (
        <EmptyState
          icon="folder"
          title="No source watched"
          text="The profile lists no repository under automation.github, and the App discovers none."
        />
      ) : phone ? (
        <ul className="list-reset">
          {rows.map((row) => (
            <SourceCard key={row.id} {...props(row)} />
          ))}
        </ul>
      ) : (
        <ScrollBox label="Sources">
          <Table cols={COLS} label="Sources">
            <TableHead>
              <HeadCell>Source</HeadCell>
              <HeadCell>Queue</HeadCell>
              <HeadCell>Recent runs</HeadCell>
              <HeadCell>On</HeadCell>
            </TableHead>
            {rows.map((row) => (
              <TableRow key={row.id}>
                <Cell strong className="wrap" title={row.detail}>
                  <div className="ch-trunc">{row.name}</div>
                  <div className="dim small mono ch-trunc">{row.detail}</div>
                </Cell>
                <Cell className="wrap">{queueText(row)}</Cell>
                <Cell className="wrap">
                  <RecentRuns runs={row.recent} />
                </Cell>
                <Cell>
                  <SourceSwitch {...props(row)} />
                </Cell>
              </TableRow>
            ))}
          </Table>
        </ScrollBox>
      )}
      {data.reviews ? <ReviewPolicy policy={data.reviews} /> : null}
      <p className="pad dim small sep-top">
        Off: nothing of the source is read or started; running batches still report. Queued counts
        review comments waiting for a round and triggers told to wait for a free slot.
      </p>
    </Panel>
  );
}

interface RowProps {
  row: SourceRow;
  busy: boolean;
  onToggle: (row: SourceRow, enabled: boolean) => void;
}

/** The label around the switch is its touch target on a phone; the switch keeps its own accessible name. */
function SourceSwitch({ row, busy, onToggle }: RowProps) {
  const id = useId();
  return (
    <label className="intake-switch" htmlFor={id}>
      <Switch
        id={id}
        aria-label={`Watch ${row.name}`}
        checked={row.enabled}
        disabled={busy}
        onChange={(e) => onToggle(row, e.target.checked)}
      />
    </label>
  );
}

function SourceCard(props: RowProps) {
  const { row } = props;
  return (
    <li className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
      <div className="li__main">
        <div className="row">
          <span className="b ch-trunc" title={row.name}>
            {row.name}
          </span>
        </div>
        <div className="li__meta">{queueText(row)}</div>
        <div className="li__meta">
          <RecentRuns runs={row.recent} />
        </div>
      </div>
      <SourceSwitch {...props} />
    </li>
  );
}

function RecentRuns({ runs }: { runs: IntakeSourceRunBody[] }) {
  if (runs.length === 0) return <span className="dim">{DASH}</span>;
  return (
    <span className="intake-runs">
      {runs.slice(0, 3).map((run) => (
        <Link
          key={`${run.batch_run_id}/${run.task_id}`}
          to="/runs/$runId"
          params={{ runId: run.batch_run_id }}
          className="intake-run"
          title={`${run.item}, ${PHASE[run.phase]}, started ${formatDateTime(run.started)}`}
        >
          <span className="mono">{run.item}</span> <span className="dim">{PHASE[run.phase]}</span>
        </Link>
      ))}
    </span>
  );
}
