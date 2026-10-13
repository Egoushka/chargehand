import { h, badge, externalLink, usd, count, when, baseName, field, notice } from './dom.js';
import { call, problems } from './api.js';
import { spendCard } from './spend.js';

const STATES = ['queued', 'running', 'lost', 'completed', 'needs_input', 'failed', 'denied'];
// Kept across visits to the list.
const filters = { status: '', kind: '', repository: '', actor: '', calls: false, auto: true };
const seen = { repository: new Set(), actor: new Set() };

/** The runs list with filters, the spend card and a refresh every few seconds. Returns what stops it. */
export function runsView(root, ctx) {
  const spend = h('div');
  const message = h('div');
  const count_ = h('p', { class: 'muted' });
  const rows = h('tbody');
  const lists = { repository: h('datalist', { id: 'repository-list' }), actor: h('datalist', { id: 'actor-list' }) };

  const select = (label, key, values) => field(label, h('select', { onchange: e => change(key, e.target.value) },
    h('option', { value: '' }, 'any'), values.map(v => h('option', { value: v, selected: filters[key] === v }, v))));
  const text = (label, key, list) => field(label, h('input', { type: 'text', list, value: filters[key], onchange: e => change(key, e.target.value.trim()) }));
  const toggle = (label, key) => h('label', { class: 'inline' }, h('input', { type: 'checkbox', checked: filters[key], onchange: e => change(key, e.target.checked) }), label);

  function change(key, value) {
    filters[key] = value;
    load();
  }

  async function load() {
    const query = new URLSearchParams({ limit: '100' });
    for (const key of ['status', 'kind', 'repository', 'actor'])
      if (filters[key] && (key !== 'actor' || ctx.me.owner)) query.set(key, filters[key]);
    try {
      const [list] = await Promise.all([call(`/v1/runs?${query}`), ctx.refreshMe()]);
      if (!list.ok) return message.replaceChildren(notice(problems(list), true));
      message.replaceChildren();
      spend.replaceChildren(spendCard(ctx.me));
      show(list.data.runs);
    } catch (e) {
      ctx.report(e, message);
    }
  }

  function show(runs) {
    for (const r of runs) {
      if (r.repository) seen.repository.add(r.repository);
      if (r.actor) seen.actor.add(r.actor);
    }
    for (const [key, list] of Object.entries(lists)) list.replaceChildren(...[...seen[key]].map(v => h('option', { value: v })));
    // A batch's research and review calls are runs of their own; they hide unless asked for.
    const shown = runs.filter(r => filters.calls || !r.parent_run_id);
    count_.textContent = `${shown.length} runs${runs.length > shown.length ? `, ${runs.length - shown.length} session calls hidden` : ''}`;
    rows.replaceChildren(...shown.map(row));
  }

  const row = r => h('tr', {},
    h('td', {}, when(r.started_at)),
    h('td', {}, h('a', { href: `#/runs/${encodeURIComponent(r.run_id)}`, class: 'mono' }, r.run_id)),
    h('td', {}, badge(r.status), r.error_code ? h('div', { class: 'muted' }, r.error_code) : null),
    h('td', {}, r.kind ?? ''),
    h('td', {}, r.preset),
    h('td', { title: r.repository ?? '' }, baseName(r.repository)),
    ctx.me.owner ? h('td', {}, r.actor ?? '—') : null,
    h('td', { class: 'num' }, usd(r.usd)),
    h('td', { class: 'num' }, count(r.tokens)),
    h('td', { class: 'mono' }, r.branch ?? ''),
    h('td', {}, r.pr_url ? externalLink('PR', r.pr_url) : ''));

  const head = ['Started', 'Run', 'State', 'Kind', 'Preset', 'Repository', ctx.me.owner ? 'Actor' : null, 'Cost', 'Tokens', 'Branch', 'PR'];
  root.append(
    h('h1', {}, 'Runs'),
    spend,
    h('section', { class: 'card' },
      h('div', { class: 'row' },
        select('State', 'status', STATES),
        select('Kind', 'kind', ['single', 'batch']),
        text('Repository (exact path)', 'repository', 'repository-list'),
        ctx.me.owner ? text('Actor', 'actor', 'actor-list') : null,
        toggle('Show session calls', 'calls'),
        toggle('Auto-refresh', 'auto'),
        h('button', { onclick: load }, 'Refresh')),
      lists.repository, lists.actor, message, count_,
      h('div', { class: 'scroll' }, h('table', {}, h('thead', {}, h('tr', {}, head.filter(Boolean).map(t => h('th', {}, t)))), rows))));

  load();
  const timer = setInterval(() => { if (!document.hidden && filters.auto) load(); }, 5000);
  return () => clearInterval(timer);
}
