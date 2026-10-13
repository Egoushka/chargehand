import { capLevel } from '../lib/format';
import { cssVars, cx } from './cx';

export interface CapMeterProps {
  label: string;
  value: number;
  /** The cap. Absent: "no cap" on a dashed empty track, never a full or empty bar. */
  cap?: number | null;
  /** The text on the right of the head line; defaults to the percentage. */
  text?: string;
  /** Overrides the level the percentage gives: none under 70 %, `warn` from 70 %, `danger` from 90 %. */
  level?: 'warn' | 'danger' | 'info' | 'ok';
  /** Marks on the track, in percent (governor checkpoint at 85, stop at 100). */
  ticks?: number[];
  size?: 'sm' | 'lg';
  /** What the head line shows on the left when it is richer than the label. */
  head?: React.ReactNode;
}

/** How much of a cap, budget or batch has been used. The fill is neutral on purpose: a calm bar means no problem. */
export function CapMeter({ label, value, cap, text, level, ticks, size, head }: CapMeterProps) {
  const capped = cap != null && cap > 0;
  const percent = capped ? Math.min(100, Math.round((value / cap) * 100)) : 0;
  const shown = level ?? (capped ? capLevel(percent) : undefined);
  return (
    // biome-ignore lint/a11y/useSemanticElements: the meter carries its own head line and track, which <meter> cannot hold
    <div
      className={cx('ch-meter', size && `ch-meter--${size}`)}
      data-level={shown}
      data-empty={capped ? undefined : 'true'}
      role="meter"
      aria-valuemin={0}
      aria-valuemax={capped ? Math.max(cap, value) : Math.max(value, 1)}
      aria-valuenow={value}
      aria-valuetext={capped ? (text ?? `${percent}%`) : `${text ?? value}, no cap`}
      aria-label={label}
    >
      <div className="ch-meter__head">
        <span>{head ?? label}</span>
        <span className="ch-meter__val">
          {capped ? (text ?? `${percent}%`) : (text ?? 'no cap')}
        </span>
      </div>
      <div className="ch-meter__track">
        {capped ? <div className="ch-meter__fill" style={cssVars({ '--p': percent })} /> : null}
        {capped
          ? (ticks ?? []).map((at) => (
              <div key={at} className="ch-meter__tick" style={cssVars({ '--at': at })} />
            ))
          : null}
      </div>
    </div>
  );
}

export interface MeterSegment {
  /** How many of the whole this group holds. */
  n: number;
  tone: 'ok' | 'info' | 'accent' | 'warn' | 'danger' | 'neutral';
  /** A running group pulses. */
  live?: boolean;
}

/** Batch progress: one segment per state group, sized by its count. The label lists the counts for assistive technology. */
export function SegmentedMeter({ segments, label }: { segments: MeterSegment[]; label: string }) {
  return (
    <div className="ch-meter">
      <div className="ch-meter__track" role="img" aria-label={label}>
        {segments
          .filter((s) => s.n > 0)
          .map((s) => (
            <div
              key={s.tone}
              className="ch-meter__seg"
              data-tone={s.tone}
              data-live={s.live ? 'true' : undefined}
              style={cssVars({ '--n': s.n })}
            />
          ))}
      </div>
    </div>
  );
}
