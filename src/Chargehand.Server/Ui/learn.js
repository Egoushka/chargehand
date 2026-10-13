import { h, usd, count, baseName, field, fill, notice } from './dom.js';
import { call, problems } from './api.js';

const percent = rate => (rate == null ? '—' : `${Math.round(rate * 100)}%`);
let since = '30d'; // kept across visits

/** What finished driven tasks taught (GET /v1/learn): one row per repository and size class. */
export function learnView(root, ctx) {
  const message = h('div');
  const rows = h('tbody');
  const total = h('p', { class: 'muted' });
  const choose = h('select', { onchange: e => { since = e.target.value; load(); } },
    ['7d', '30d', '90d', ''].map(v => h('option', { value: v, selected: since === v }, v || 'all time')));

  async function load() {
    try {
      const r = await call(`/v1/learn${since ? `?since=${since}` : ''}`);
      if (!r.ok) return fill(message, notice(problems(r), true));
      fill(message);
      total.textContent = r.data.tasks ? `${r.data.tasks} finished tasks` : 'No finished tasks in this period.';
      fill(rows, r.data.groups.map(g => h('tr', {},
        h('td', { title: g.repository }, baseName(g.repository)), h('td', {}, g.size_class), h('td', { class: 'num' }, count(g.tasks)),
        h('td', { class: 'num' }, percent(g.completion_rate)), h('td', { class: 'num' }, percent(g.partial_rate)),
        h('td', { class: 'num' }, usd(g.spend_median_usd)), h('td', { class: 'num' }, usd(g.spend_p80_usd)),
        h('td', { class: 'num' }, percent(g.checkpoint_rate)), h('td', { class: 'num' }, count(g.prs.total)), h('td', { class: 'num' }, percent(g.merged_unchanged_rate)))));
    } catch (e) {
      ctx.report(e, message);
    }
  }

  const head = ['Repository', 'Size', 'Tasks', 'Completed', 'Partial', 'Spend median', 'Spend p80', 'Checkpointed', 'PRs', 'Merged unchanged'];
  root.append(h('h1', {}, 'Learning'),
    h('section', { class: 'card' }, h('div', { class: 'row' }, field('Since', choose)), message, total,
      h('div', { class: 'scroll' }, h('table', {}, h('thead', {}, h('tr', {}, head.map(t => h('th', {}, t)))), rows))),
    h('p', { class: 'muted' }, 'Spend is the tasks\' dollar equivalent where the profile has prices. An account sees only its own batches.'));
  load();
  return () => {};
}
