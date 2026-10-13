import type { BatchBody } from '../../api/batch';
import { DASH, formatPercent, formatTokens, formatUsd } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  CapMeter,
  Cell,
  cssVars,
  EmptyState,
  HeadCell,
  Panel,
  Table,
  TableHead,
  TableRow,
  TableScroll,
} from '../../ui';
import { cacheShare, type SpendRow, spendRows, spendShares, spendTotal } from './model';

const COLS = 'minmax(160px,1.4fr) 84px 84px 92px 92px 100px';

const cell = (value: number | null) => (value == null ? DASH : formatTokens(value));
const money = (row: SpendRow) =>
  row.usd == null ? DASH : formatUsd(row.usd, row.estimate) + (row.partial ? '*' : '');

/** Tokens and dollars per task. The dollars are the dollar equivalent of the tokens, not a bill. */
export function SpendTab({ batch }: { batch: BatchBody }) {
  const phone = useIsPhone();
  const rows = spendRows(batch.tasks);
  const total = spendTotal(rows);
  const shares = spendShares(rows);
  const cache = cacheShare(rows);
  const sum = shares.reduce((n, s) => n + s.usd, 0);
  if (rows.length === 0)
    return (
      <Panel title="Spend per task">
        <EmptyState
          icon="gauge"
          title="Nothing spent yet"
          text="A task's tokens appear here while it runs, and its record when it ends."
        />
      </Panel>
    );
  return (
    <div className="g2">
      <Panel
        title="Spend per task"
        flush
        aside={<span className="dim">dollar equivalent, not a bill</span>}
      >
        {phone ? (
          <ul className="list-reset">
            {rows.map((r) => (
              <li key={r.id} className="li spend-card">
                <div className="row row--between">
                  <span className="mono-b ch-trunc">{r.id}</span>
                  <span className="b">{money(r)}</span>
                </div>
                <div className="li__meta">
                  in {cell(r.input)} · out {cell(r.output)} · cache {cell(r.cacheRead)}
                </div>
              </li>
            ))}
            <li className="li spend-card">
              <div className="row row--between">
                <span className="b">Total</span>
                <span className="b">{money(total)}</span>
              </div>
            </li>
          </ul>
        ) : (
          <TableScroll label="Spend per task">
            <Table cols={COLS} label="Spend per task">
              <TableHead>
                <HeadCell>Task</HeadCell>
                <HeadCell num>Input</HeadCell>
                <HeadCell num>Output</HeadCell>
                <HeadCell num>Cache read</HeadCell>
                <HeadCell num>Cache write</HeadCell>
                <HeadCell num>Spend</HeadCell>
              </TableHead>
              {rows.map((r) => (
                <TableRow key={r.id}>
                  <Cell mono title={r.id}>
                    {r.id}
                  </Cell>
                  <Cell num>{cell(r.input)}</Cell>
                  <Cell num>{cell(r.output)}</Cell>
                  <Cell num>{cell(r.cacheRead)}</Cell>
                  <Cell num>{cell(r.cacheWrite)}</Cell>
                  <Cell num>{money(r)}</Cell>
                </TableRow>
              ))}
              <TableRow className="ch-tr--group">
                <Cell strong>Total</Cell>
                <Cell num>{cell(total.input)}</Cell>
                <Cell num>{cell(total.output)}</Cell>
                <Cell num>{cell(total.cacheRead)}</Cell>
                <Cell num>{cell(total.cacheWrite)}</Cell>
                <Cell num strong>
                  {money(total)}
                </Cell>
              </TableRow>
            </Table>
          </TableScroll>
        )}
        {rows.some((r) => r.partial) ? (
          <p className="dim small pad">
            * Some message's model had no price, so the figure sums only the priced ones.
          </p>
        ) : null}
      </Panel>
      <div className="stack">
        <Panel title="Share of spend">
          {shares.length === 0 ? (
            <p className="dim">No task has a dollar figure yet.</p>
          ) : (
            <div className="stack stack--tight">
              <div className="ch-meter">
                <div
                  className="ch-meter__track ch-meter__track--tall"
                  role="img"
                  aria-label={`Spend share: ${shares.map((s) => `${s.id} ${formatPercent((s.usd / sum) * 100)}`).join(', ')}`}
                >
                  {shares.map((s, i) => (
                    <div
                      key={s.id}
                      className="ch-meter__seg share"
                      data-n={i}
                      title={`${s.id} ${formatUsd(s.usd, true)}`}
                      style={cssVars({ '--n': Math.max(1, Math.round(s.usd * 100)) })}
                    />
                  ))}
                </div>
              </div>
              <div className="legend">
                {shares.map((s, i) => (
                  <span key={s.id}>
                    <i className="swatch" data-n={i} />
                    {s.id} {formatUsd(s.usd, true)}
                  </span>
                ))}
              </div>
            </div>
          )}
        </Panel>
        <Panel title="Cache">
          <div className="stack stack--tight">
            <CapMeter
              label="Cache read share of input"
              value={cache ?? 0}
              cap={cache == null ? null : 100}
              text={cache == null ? '—' : `${cache}%`}
              level="info"
            />
            <div className="dim">
              Cache reads are priced at the model's cache-read rate: they make most of the tokens
              and little of the spend.
            </div>
          </div>
        </Panel>
      </div>
    </div>
  );
}
