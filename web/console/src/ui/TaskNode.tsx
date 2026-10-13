import type { KeyboardEvent } from 'react';
import { STATUS, type Status } from '../lib/status';
import { SizeBadge } from './Badge';
import { cssVars, cx } from './cx';

export interface TaskNodeProps {
  id: string;
  goal: string;
  size: 'S' | 'M' | 'L';
  status: Status;
  /** 0 to 100. */
  progress?: number;
  /** Turns, or the reason the task is blocked. */
  meta?: string;
  cost?: string;
  selected?: boolean;
  /** Selecting a node opens the task drawer. */
  onSelect?: () => void;
}

/** A node of the task graph: size, id, state, a two-line goal, progress, and spend. 232 px wide; the id truncates with a title. */
export function TaskNode({
  id,
  goal,
  size,
  status,
  progress = 0,
  meta,
  cost,
  selected,
  onSelect,
}: TaskNodeProps) {
  const known = STATUS[status] ?? STATUS.queued;
  const onKey = (event: KeyboardEvent) => {
    if (onSelect && (event.key === 'Enter' || event.key === ' ')) {
      event.preventDefault();
      onSelect();
    }
  };
  return (
    // biome-ignore lint/a11y/useSemanticElements: the node holds block content (a bar, a footer) that a <button> may not
    <div
      className={cx('ch-node', selected && 'is-selected')}
      data-status={status}
      tabIndex={0}
      role="button"
      aria-pressed={selected ?? false}
      onClick={onSelect}
      onKeyDown={onKey}
    >
      <span className="ch-node__port ch-node__port--in" />
      <span className="ch-node__port ch-node__port--out" />
      <div className="ch-node__top">
        <SizeBadge size={size} />
        <span className="ch-node__id" title={id}>
          {id}
        </span>
        <span
          className="ch-pill ch-pill--dot"
          data-status={status}
          title={known.label}
          role="img"
          aria-label={known.label}
        />
      </div>
      <div className="ch-node__goal">{goal}</div>
      <div className="ch-node__bar">
        <i style={cssVars({ '--p': progress })} />
      </div>
      <div className="ch-node__foot">
        <span>{meta ?? ''}</span>
        <span>{cost ?? ''}</span>
      </div>
    </div>
  );
}
