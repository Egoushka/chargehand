import { type Slot, slotVar } from './colors';

export interface TipRow {
  label: string;
  value: string;
  /** The series' place in the hue order: a short line of its colour keys the row. */
  slot?: Slot;
}

/** What a hover or a focus shows: the value first, then who it belongs to, then where the number comes from. */
export interface TipData {
  title: string;
  rows: TipRow[];
  /** A sum under the rows. */
  total?: TipRow;
  /** The route and field the number is read from. */
  source: string;
}

/** The body of a chart's tooltip. Everything is text: names come from the server and are never markup. */
export function TipBody({ title, rows, total, source }: TipData) {
  return (
    <div className="tip">
      <div className="tip__title">{title}</div>
      {rows.map((row) => (
        <div key={`${row.label}`} className="tip__row" data-slot={row.slot}>
          {row.slot !== undefined ? <i className="tip__key" aria-hidden="true" /> : null}
          <b className="tip__value">{row.value}</b>
          <span className="tip__label">{row.label}</span>
        </div>
      ))}
      {total ? (
        <div className="tip__row tip__row--total">
          <b className="tip__value">{total.value}</b>
          <span className="tip__label">{total.label}</span>
        </div>
      ) : null}
      <div className="tip__source">{source}</div>
    </div>
  );
}

/** The sentence a screen reader gets for a mark: the same facts as the tooltip, in one line. */
export function tipText({ title, rows, total, source }: TipData): string {
  const body = [...rows, ...(total ? [total] : [])].map((r) => `${r.label} ${r.value}`).join(', ');
  return `${title}: ${body}. Source: ${source}`;
}

export { slotVar };
