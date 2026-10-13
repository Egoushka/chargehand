import { useEffect, useMemo, useRef, useState } from 'react';
import type { BatchBody } from '../../api/batch';
import { useRunEvents } from '../../api/useRunEvents';
import { formatTime } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import { Button, EmptyState, ErrorState, LogLine, Segmented, Select, Skeleton } from '../../ui';
import {
  EVENT_GROUPS,
  type EventGroup,
  eventDetail,
  eventKey,
  eventKind,
  eventTone,
  filterEvents,
  mergeEvents,
  notesAbout,
} from './model';
import { NotRecorded } from './NotRecorded';

/** The newest this many matching events are drawn; older ones are a click away. */
const TAIL = 400;

/** The batch's events as they arrive: the recorded ones first, then the live stream, filtered by kind and task. */
export function EventsTab({ batch }: { batch: BatchBody }) {
  const phone = useIsPhone();
  const live = useRunEvents(batch.run.run_id, { retryMs: 5000 });
  const [group, setGroup] = useState<EventGroup>('all');
  const [task, setTask] = useState('all');
  const [paused, setPaused] = useState(false);
  const [shown, setShown] = useState(TAIL);
  const recorded = batch.timeline.available ? batch.timeline.events : [];
  const all = useMemo(
    () =>
      mergeEvents(
        recorded,
        live.events.map((e) => e.data),
      ),
    [recorded, live.events],
  );
  // Paused: the list stops where it was; the stream goes on underneath.
  const frozen = useRef(all.length);
  if (!paused) frozen.current = all.length;
  const visible = useMemo(
    () => filterEvents(all.slice(0, frozen.current), group, task),
    [all, group, task],
  );
  const tail = visible.slice(-shown);
  const hidden = visible.length - tail.length;
  const tasks = useMemo(
    () => [...new Set(all.map((e) => e.task_id).filter((t): t is string => !!t))],
    [all],
  );

  // Follow the newest line unless the person has scrolled up to read.
  const pane = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  useEffect(() => {
    const el = pane.current;
    if (el && following.current && !paused) el.scrollTop = el.scrollHeight;
  });

  const state = paused ? 'paused' : live.closed ? 'off' : live.reconnecting ? 'off' : '';
  const stateText = paused
    ? 'Paused'
    : live.reconnecting
      ? 'Reconnecting'
      : live.closed
        ? 'Stream ended'
        : 'Live';
  const loading = all.length === 0 && !live.closed && !live.error;
  return (
    <div className="stack stack--tight">
      <div className="toolbar">
        <Segmented
          label="Event kinds"
          value={group}
          touch={phone}
          onValueChange={(next) => {
            setGroup(next);
            setShown(TAIL);
          }}
          items={EVENT_GROUPS.map((g) => ({ id: g.id, label: g.label }))}
        />
        <Select
          aria-label="Task"
          value={task}
          touch={phone}
          onChange={(e) => setTask(e.target.value)}
        >
          <option value="all">Task: all</option>
          {tasks.map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </Select>
        <span className="spacer" />
        <Button
          size="sm"
          icon={paused ? 'play' : 'pause'}
          label={paused ? 'Resume' : 'Pause'}
          aria-pressed={paused}
          onClick={() => setPaused((p) => !p)}
        />
        <span className="ch-live" data-state={state}>
          {stateText}
        </span>
      </div>
      {live.reconnecting && live.error ? (
        <ErrorState
          title="The event stream dropped"
          call={`GET /v1/runs/${batch.run.run_id}/events`}
          detail={`${live.error}. Asking again in 5 s; the list below keeps what arrived.`}
        />
      ) : null}
      {loading ? (
        <div className="ch-stream" aria-busy="true">
          <div className="pad stack stack--tight">
            {[90, 70, 82, 60, 76].map((w) => (
              <Skeleton key={w} width={`${w}%`} />
            ))}
          </div>
        </div>
      ) : all.length === 0 ? (
        <div className="ch-stream">
          <EmptyState
            icon="activity"
            title="No events"
            text={
              live.closed
                ? 'The server holds no events for this batch.'
                : 'The batch was accepted a moment ago. Events appear here as sessions start.'
            }
          />
        </div>
      ) : visible.length === 0 ? (
        <div className="ch-stream">
          <EmptyState
            icon="filter"
            title="No event matches this filter"
            text="Choose All, or another task."
          />
        </div>
      ) : (
        <div
          className="ch-stream events-pane"
          role="log"
          aria-live="off"
          aria-label="Events of this batch"
          ref={pane}
          // biome-ignore lint/a11y/noNoninteractiveTabindex: a log that scrolls must be reachable by keyboard
          tabIndex={0}
          onScroll={(e) => {
            const el = e.currentTarget;
            following.current = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
          }}
        >
          {hidden > 0 ? (
            <div className="pad">
              <Button
                size="sm"
                label={`Show ${Math.min(hidden, TAIL)} earlier events (${hidden} more)`}
                onClick={() => setShown((n) => n + TAIL)}
              />
            </div>
          ) : null}
          {tail.map((e) => (
            <LogLine
              key={eventKey(e)}
              time={formatTime(e.at, { seconds: true })}
              kind={eventKind(e)}
              task={e.task_id}
              detail={eventDetail(e)}
              tone={eventTone(e)}
              wrap={phone}
            />
          ))}
          <div className="ch-stream__tail">
            {paused
              ? `paused: ${visible.length} of ${all.length} events shown`
              : live.closed
                ? `${visible.length} of ${all.length} events shown`
                : `following the tail · ${visible.length} of ${all.length} events shown`}
          </div>
        </div>
      )}
      <NotRecorded notes={notesAbout(batch.not_recorded, 'events')} />
    </div>
  );
}
