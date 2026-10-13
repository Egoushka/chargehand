import { Mark } from '../../charts/Mark';
import { niceTicks } from '../../charts/scales';
import type { TipData } from '../../charts/Tip';
import { describeBudgetSource } from '../../lib/budgets';
import { formatCount, formatPercent, formatUsd } from '../../lib/format';
import { SizeBadge } from '../../ui';
import { type BudgetRow, budgetAxisMax, learnSource, type RepoSpend } from './model';

const ROW = 28;
/** Percent of the axis for a value; the axis is in percent of each row's own budget. */
const at = (percent: number, top: number) => `${((percent / top) * 100).toFixed(2)}%`;

export function repoTip(
  size: string,
  repo: RepoSpend,
  budget: NonNullable<BudgetRow['budget']>,
): TipData {
  const p80 = repo.p80 ?? 0;
  return {
    title: `${repo.repository} · size ${size}`,
    rows: [
      { label: 'p80 spend', value: formatUsd(repo.p80, true) },
      { label: 'of the budget', value: formatPercent((p80 / budget.usd) * 100) },
      { label: 'median spend', value: formatUsd(repo.median, true) },
      {
        label: 'priced tasks',
        value: `${formatCount(repo.spendKnown)} of ${formatCount(repo.tasks)}`,
      },
    ],
    source: learnSource(repo.group).replace('completed over tasks', 'spend_p80_usd'),
  };
}

export function budgetTip(row: BudgetRow): TipData | null {
  const b = row.budget;
  if (!b) return null;
  return {
    title: `Size ${row.size} budget`,
    rows: [
      { label: 'budget', value: formatUsd(b.usd, true) },
      { label: 'chosen from', value: describeBudgetSource(b.source) },
    ],
    source: `GET /v1/batches/{id} · tasks[${b.taskId}].budget · batch ${b.runId.slice(-6)}`,
  };
}

/**
 * Per size class: each repository's 80th-percentile spend (a dot) as a share of the budget a task of that size was last held to
 * (the rule, always at 100 %). Sizes cost very different amounts, so the axis is the share of the budget and the rows compare. A dot
 * right of the rule is a repository whose typical task of that size costs more than the budget allows.
 */
export function BudgetChart({ rows }: { rows: BudgetRow[] }) {
  const { top, ticks } = niceTicks(Math.max(125, budgetAxisMax(rows) * 110), 4);
  return (
    <div className="budgets">
      {rows.map((row) => {
        const budget = row.budget;
        const ruleTip = budgetTip(row);
        return (
          <div key={row.size} className="budgets__row">
            <div className="budgets__head">
              <SizeBadge size={row.size} />
              <span>
                {budget
                  ? `budget ${formatUsd(budget.usd, true)} · ${describeBudgetSource(budget.source)}`
                  : 'no budget seen in the latest batches, so there is nothing to compare with'}
              </span>
            </div>
            {budget ? (
              <svg className="plot" height={ROW} role="presentation">
                {ticks.slice(1).map((t) => (
                  <line
                    key={t}
                    className="gridline"
                    x1={at(t, top)}
                    x2={at(t, top)}
                    y1="0"
                    y2={ROW}
                  />
                ))}
                <line className="baseline" x1="0" x2="100%" y1={ROW / 2} y2={ROW / 2} />
                {row.repos.map((repo) => {
                  const x = at(((repo.p80 ?? 0) / budget.usd) * 100, top);
                  return (
                    <Mark key={repo.repository} tip={repoTip(row.size, repo, budget)}>
                      <circle className="hit" cx={x} cy={ROW / 2} r="12" />
                      <circle className="dot" data-slot={1} cx={x} cy={ROW / 2} r="5" />
                    </Mark>
                  );
                })}
                {ruleTip ? (
                  <Mark tip={ruleTip}>
                    <rect
                      className="hit"
                      x={at(100, top)}
                      y="0"
                      width="16"
                      height={ROW}
                      transform="translate(-8 0)"
                    />
                    <line
                      className="rule"
                      x1={at(100, top)}
                      x2={at(100, top)}
                      y1="2"
                      y2={ROW - 2}
                    />
                  </Mark>
                ) : null}
              </svg>
            ) : null}
          </div>
        );
      })}
      <svg className="plot" height="16" aria-hidden="true">
        {ticks.map((t, i) => (
          <text
            key={t}
            x={at(t, top)}
            y="11"
            textAnchor={i === 0 ? 'start' : i === ticks.length - 1 ? 'end' : 'middle'}
          >
            {t === 100 ? '100% = budget' : `${t}%`}
          </text>
        ))}
      </svg>
    </div>
  );
}
