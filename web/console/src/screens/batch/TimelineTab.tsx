import type { BatchBody } from '../../api/batch';
import { formatClock } from '../../lib/format';
import { useNow } from '../../lib/useNow';
import { Banner, cssVars, EmptyState, Panel } from '../../ui';
import { barBox, isRunning, notesAbout, timelineModel } from './model';
import { NotRecorded } from './NotRecorded';

/** The sessions of the batch in parallel slots: a bar per session, a dashed left edge for a continuation, a band for a rate-limit pause. */
export function TimelineTab({
  batch,
  onOpenTask,
}: {
  batch: BatchBody;
  onOpenTask: (id: string) => void;
}) {
  const now = useNow(15_000);
  const model = timelineModel(batch, now);
  const notes = notesAbout(batch.not_recorded, 'events', 'sessions');
  if (!model)
    return (
      <div className="stack">
        <Panel title="Parallel slots">
          <EmptyState
            icon="timeline"
            title="No session has started"
            text="Sessions appear here as tasks start, one bar per session in the slot that ran it."
          />
        </Panel>
        <NotRecorded notes={notes} />
      </div>
    );
  const waiting = batch.tasks.filter((t) => t.state === 'pending').length;
  // A batch that has ended starts nothing, held or not.
  const held = isRunning(batch)
    ? batch.rate_limits.find((p) => Date.parse(p.resets_at) > now)
    : undefined;
  return (
    <div className="stack">
      {held ? (
        <Banner
          tone="warn"
          icon="clock"
          title={`New starts are held until ${formatClock(held.resets_at)}`}
        >
          The provider's usage limit was seen on {held.task_id}. Sessions that run go on; no task
          starts until the limit resets.
        </Banner>
      ) : null}
      <Panel title="Parallel slots" aside={<span className="dim">Kyiv time</span>} flush>
        <div className="wide-scroll">
          {/* biome-ignore lint/a11y/useSemanticElements: a fieldset brings borders and sizing the chart does not want */}
          <div className="gantt" role="group" aria-label="Sessions by slot">
            <div className="gantt__axis" aria-hidden="true">
              {model.ticks.map((tick) => (
                <div key={tick.at} className="gantt__tick" style={cssVars({ '--l': tick.left })}>
                  {tick.label}
                </div>
              ))}
            </div>
            <div className="gantt__body">
              <div className="gantt__overlay" aria-hidden="true">
                {model.ticks.map((tick) => (
                  <div
                    key={tick.at}
                    className="gantt__grid"
                    style={cssVars({ '--l': tick.left })}
                  />
                ))}
                {model.pauses.map((pause) => (
                  <div
                    key={pause.left}
                    className="gpause"
                    style={cssVars({ '--l': pause.left, '--w': pause.width })}
                  >
                    <span>{pause.label}</span>
                  </div>
                ))}
                {model.nowLeft != null ? (
                  <div className="gnow" style={cssVars({ '--l': model.nowLeft })}>
                    <span className="gnow__tag">now {formatClock(now)}</span>
                  </div>
                ) : null}
              </div>
              {model.lanes.map((lane) => (
                <div key={lane.label} className="gantt__lane">
                  <div className="gantt__label">{lane.label}</div>
                  <div className="gantt__track">
                    {lane.bars.map((bar) => {
                      const box = barBox(bar, model);
                      return (
                        <button
                          key={bar.id}
                          type="button"
                          className="gbar"
                          data-status={bar.status}
                          data-cont={bar.continuation ? 'true' : undefined}
                          title={bar.title}
                          aria-label={bar.title}
                          style={cssVars({ '--l': box.left, '--w': box.width })}
                          onClick={() => onOpenTask(bar.task)}
                        >
                          {bar.task}
                          {bar.continuation ? ` · S${bar.session}` : ''}
                        </button>
                      );
                    })}
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </Panel>
      <div className="g2e">
        <Panel title="Reading the timeline">
          <div className="stack stack--tight">
            <div className="row row--wrap">
              {(['running', 'done', 'partial', 'question', 'failed'] as const).map((status) => (
                <span key={status} className="gbar gbar--key" data-status={status}>
                  {
                    {
                      running: 'running',
                      done: 'done',
                      partial: 'stopped',
                      question: 'asked',
                      failed: 'failed',
                    }[status]
                  }
                </span>
              ))}
            </div>
            <div className="dim">
              A dashed left edge marks a continuation: a fresh session on the same branch after a
              turn, time or checkpoint stop. The shaded band is a rate-limit pause: running sessions
              go on, new starts wait.
              {model.fromEvents
                ? ''
                : ' The batch events are not held, so each task is one bar, from its start to its end.'}
            </div>
          </div>
        </Panel>
        <Panel title="Slots now">
          <div className="stack stack--tight">
            <div>
              {model.busy} of {model.slots} {model.slots === 1 ? 'slot' : 'slots'} busy.
              {waiting > 0
                ? ` ${waiting} ${waiting === 1 ? 'task is' : 'tasks are'} waiting to start.`
                : ''}
            </div>
            <div className="dim">
              A slot is a place in the batch's parallel limit; the server does not name slots, so
              tasks are placed in the first one free when they start.
            </div>
          </div>
        </Panel>
      </div>
      <NotRecorded notes={notes} />
    </div>
  );
}
