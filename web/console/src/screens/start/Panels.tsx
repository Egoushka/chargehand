import type { Me } from '../../api/types';
import { formatCount, formatTokens, formatUsd } from '../../lib/format';
import { Badge, KeyValue, Panel } from '../../ui';
import {
  type BatchEstimate,
  type EstimateRules,
  MAX_PARALLEL,
  parseCount,
  parseMoney,
} from './model';

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** What the batch may spend and how it will run, from the form and the history, with the source of every figure. */
export function EstimatePanel({
  estimate,
  rules,
  maxParallel,
  maxUsd,
  serverParallel,
}: {
  estimate: BatchEstimate;
  rules: EstimateRules;
  maxParallel: string;
  maxUsd: string;
  /** The server's own limit on parallel sessions, when the owner can read it. */
  serverParallel: number | null;
}) {
  const asked = parseCount(maxParallel);
  const limit = Math.min(asked ?? MAX_PARALLEL, serverParallel ?? MAX_PARALLEL);
  const atOnce = Math.max(1, Math.min(estimate.widest, limit));
  const cap = parseMoney(maxUsd);
  const sources = [...new Map(estimate.tasks.map((t) => [`${t.size}|${t.label}`, t])).values()];
  return (
    <Panel title="Estimate" aside="from history">
      <div className="stack stack--tight">
        <KeyValue
          stacked
          items={[
            [
              'Budget, all tasks',
              <span key="total">
                <b>{formatUsd(estimate.total)}</b>{' '}
                <span className="dim">the sum of the per-task budgets</span>
              </span>,
            ],
            [
              'Longest chain',
              estimate.chain <= 1
                ? 'All independent'
                : `${plural(estimate.chain, 'task')} in a row`,
            ],
            [
              `At once (max_parallel ${asked ?? 'not set'})`,
              serverParallel !== null && serverParallel < (asked ?? MAX_PARALLEL)
                ? `${plural(atOnce, 'session')}; the server runs at most ${serverParallel}`
                : plural(atOnce, 'session'),
            ],
            ...(cap !== null
              ? ([
                  [
                    'Against max_usd_total',
                    estimate.total > cap
                      ? `The budgets add up to more than the cap of ${formatUsd(cap)}: the cap stops the batch first.`
                      : `The budgets fit under the cap of ${formatUsd(cap)}.`,
                  ],
                ] as Array<[string, string]>)
              : []),
          ]}
        />
        <div className="stack stack--tight small dim">
          <div>Where each budget comes from:</div>
          <ul className="start-sources list-reset">
            {sources.map((t) => (
              <li key={`${t.size}|${t.label}`}>
                <Badge kind="size" size={t.size} /> {t.label}
              </li>
            ))}
          </ul>
          {estimate.historyMissing ? (
            <div>
              The history could not be read (GET /v1/learn), so every figure is the flat budget.
            </div>
          ) : (
            <div>
              History is GET /v1/learn over the last {rules.windowDays} days: p{rules.percentile} of
              past spend per size class, trusted from {rules.minTasks} tasks. The server chooses
              each budget when the task starts; a task's own budget wins.
            </div>
          )}
          {rules.fromProfile ? null : (
            <div>
              Those thresholds and the flat budgets (S {formatUsd(rules.flat.S)}, M{' '}
              {formatUsd(rules.flat.M)}, L {formatUsd(rules.flat.L)}) are the server's defaults; its
              profile can set others, and only the owner can read it.
            </div>
          )}
        </div>
      </div>
    </Panel>
  );
}

const left = (cap: number | undefined, used: number) =>
  cap === undefined ? null : Math.max(0, cap - used);

/** What the signed-in account may do here: batches allowed, repositories, caps and what is left of them, unfinished runs. */
export function AccountPanel({ me }: { me: Me }) {
  const rows: Array<[string, string]> = [];
  const money = (label: string, cap: number | undefined, used: number) => {
    const remaining = left(cap, used);
    if (remaining !== null && cap !== undefined)
      rows.push([label, `${formatUsd(remaining)} left of ${formatUsd(cap)}`]);
  };
  const tokens = (label: string, cap: number | undefined, used: number) => {
    const remaining = left(cap, used);
    if (remaining !== null && cap !== undefined)
      rows.push([label, `${formatTokens(remaining)} left of ${formatTokens(cap)}`]);
  };
  money('Dollars today', me.limits.max_usd_per_day, me.spend.day.usd);
  money('Dollars this month', me.limits.max_usd_per_month, me.spend.month.usd);
  tokens('Tokens today', me.limits.max_tokens_per_day, me.spend.day.tokens);
  tokens('Tokens this month', me.limits.max_tokens_per_month, me.spend.month.tokens);
  const most = me.limits.max_concurrent_runs;
  return (
    <Panel title="Your limits" aside={me.name}>
      <KeyValue
        stacked
        items={[
          ['Batches', me.driven ? 'Allowed' : 'Not allowed for this account'],
          [
            'Repositories',
            me.repositories && me.repositories.length > 0
              ? `${formatCount(me.repositories.length)} allowed`
              : 'All the server lists',
          ],
          ...rows,
          ...(rows.length === 0
            ? ([['Caps', 'No caps on this account']] as Array<[string, string]>)
            : []),
          [
            'Unfinished runs',
            most === undefined
              ? formatCount(me.unfinished_runs)
              : `${formatCount(me.unfinished_runs)} of ${formatCount(most)}`,
          ],
        ]}
      />
    </Panel>
  );
}
