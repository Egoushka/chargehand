import { h, usd, count, when } from './dom.js';

function meter(label, spent, cap, format) {
  return h('div', { class: 'meter' },
    h('div', {}, `${label}: ${format(spent)}`, cap == null ? h('span', { class: 'muted' }, ' (no cap)') : ` of ${format(cap)}`),
    cap == null ? null : h('progress', { max: cap, value: Math.min(spent, cap) }));
}

function windowBlock(title, spend, capUsd, capTokens) {
  return h('div', {},
    h('h3', {}, title),
    meter('Dollars', spend.usd, capUsd, usd),
    meter('Tokens', spend.tokens, capTokens, count),
    h('div', { class: 'muted' }, `resets ${when(spend.resets_at)}`));
}

/** The account's spend in the UTC day and month against its caps, and its unfinished runs against its limit. */
export function spendCard(me) {
  const cap = me.limits ?? {};
  return h('section', { class: 'card' },
    h('h2', {}, 'Spend'),
    h('div', { class: 'grid' },
      windowBlock('Today (UTC)', me.spend.day, cap.max_usd_per_day, cap.max_tokens_per_day),
      windowBlock('This month (UTC)', me.spend.month, cap.max_usd_per_month, cap.max_tokens_per_month),
      h('div', {}, h('h3', {}, 'Runs'), meter('Unfinished', me.unfinished_runs, cap.max_concurrent_runs, count))),
    h('p', { class: 'muted' }, 'Dollars count priced runs only; a subscription run adds none, so a token cap is the one that always binds.'));
}
