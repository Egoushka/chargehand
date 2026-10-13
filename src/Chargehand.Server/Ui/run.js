import { h, fill, badge, externalLink, safeUrl, parseJson, usd, count, when, clock, notice } from './dom.js';
import { call, stream, problems, Unauthorized } from './api.js';
import { citation, webBase, saveWebUrl } from './citations.js';

const enc = encodeURIComponent;
const sleep = (ms, signal) => new Promise(resolve => {
  const timer = setTimeout(resolve, ms);
  signal.addEventListener('abort', () => { clearTimeout(timer); resolve(); }, { once: true });
});
const artifact = (result, kind) => result.artifacts?.find(a => a.kind === kind);

/** One run: its state, a live event stream while it runs, and its result. Returns what stops it. */
export function runView(root, ctx, id) {
  const abort = new AbortController();
  const { signal } = abort;
  const head = h('div');
  const message = h('div');
  const events = h('ol', { class: 'events' });
  const liveTasks = h('div');
  const live = h('section', { class: 'card', hidden: true }, h('h2', {}, 'Live events'), liveTasks, events);
  const body = h('div');
  const tasks = new Map(); // batch task id -> the latest progress event about it
  let row = null;

  root.append(h('p', {}, h('a', { href: '#/' }, '← Runs')), head, message, live, body);
  follow().catch(e => { if (!signal.aborted) ctx.report(e, message); });
  return () => abort.abort();

  async function follow() {
    row = await summary();
    while (!signal.aborted) {
      const r = await call(`/v1/runs/${enc(id)}`, { signal });
      if (r.status === 404) return message.replaceChildren(notice('No such run, or it belongs to another account.', true));
      if (r.status === 200) {
        if (!row?.finished_at) row = await summary();
        return finished(r.data);
      }
      if (r.status === 410) return lost(r.data);
      if (r.status !== 202) return message.replaceChildren(notice(problems(r), true));
      header(r.data.status, null);
      live.hidden = false;
      events.replaceChildren();
      tasks.clear();
      // The stream ends when the run does, or at once for a run another process holds; then the loop asks again.
      await stream(`/v1/runs/${enc(id)}/events`, onEvent, signal).catch(e => { if (e instanceof Unauthorized) throw e; });
      await sleep(3000, signal);
    }
  }

  async function summary() {
    return (await call(`/v1/runs?run_id=${enc(id)}&limit=1`, { signal })).data?.runs?.[0] ?? null;
  }

  function onEvent({ data: e }) {
    events.append(h('li', {}, h('time', {}, clock(e.at)), h('strong', {}, e.event ?? e.status), h('span', {}, ...describe(e))));
    events.scrollTop = events.scrollHeight;
    if (e.task_id) {
      tasks.set(e.task_id, { ...tasks.get(e.task_id), ...e });
      liveTasks.replaceChildren(taskTable([...tasks.values()].map(t => ({ id: t.task_id, status: t.event, stage: t.stage, turns: t.turns, tokens: t.tokens, usd: t.usd_estimate, branch: t.branch, pr_url: t.pr_url, detail: t.detail })), true));
    }
    header(e.status, null);
  }

  function describe(e) {
    const bits = [];
    if (e.task_id) bits.push(`task ${e.task_id}`);
    if (e.node_id) bits.push(`node ${e.node_id}${e.node_status ? ` ${e.node_status}` : ''}`);
    if (e.stage) bits.push(e.stage);
    if (e.action) bits.push(e.executed_action && e.executed_action !== e.action ? `${e.action} → ${e.executed_action}` : e.action);
    if (e.turns != null) bits.push(`${e.turns} turns`);
    if (e.tokens != null) bits.push(`${count(e.tokens)} tokens`);
    if (e.usd_estimate != null) bits.push(`~${usd(e.usd_estimate)}${e.usd_estimate_partial ? ' (partial)' : ''}`);
    else if (e.usd != null) bits.push(usd(e.usd));
    if (e.usd_total != null) bits.push(`batch ${usd(e.usd_total)}`);
    if (e.branch) bits.push(e.branch);
    if (e.detail) bits.push(e.detail);
    const text = bits.join(' · ');
    return [text, e.pr_url ? [' ', externalLink('PR', e.pr_url)] : null];
  }

  function header(status, result) {
    const done = result != null || !['queued', 'running'].includes(status);
    fill(head,
      h('h1', {}, 'Run ', h('span', { class: 'mono' }, id), ' ', badge(status)),
      h('dl', { class: 'facts' },
        ...(row ? [
          h('dt', {}, 'Kind'), h('dd', {}, `${row.kind ?? ''} · preset ${row.preset}`),
          row.repository ? [h('dt', {}, 'Repository'), h('dd', { class: 'mono' }, row.repository, row.commit ? ` @ ${row.commit.slice(0, 12)}` : '')] : null,
          h('dt', {}, 'Actor'), h('dd', {}, row.actor ?? '—'),
          h('dt', {}, 'Started'), h('dd', {}, when(row.started_at)),
          row.finished_at ? [h('dt', {}, 'Finished'), h('dd', {}, when(row.finished_at))] : null,
        ] : [])),
      done ? null : h('p', {}, h('button', { class: 'danger', onclick: cancel }, 'Cancel run')));
  }

  async function cancel() {
    if (!confirm('Cancel this run?')) return;
    try {
      const r = await call(`/v1/runs/${enc(id)}/cancel`, { method: 'POST' });
      message.replaceChildren(notice(r.status === 202 ? 'Cancelling; the run ends failed with the code cancelled.' : problems(r), r.status !== 202));
    } catch (e) {
      ctx.report(e, message);
    }
  }

  function lost(status) {
    header('lost', status);
    message.replaceChildren(notice(status.detail ?? 'The process that ran it ended before it finished.', true));
  }

  function finished(result) {
    header(result.status, result);
    live.hidden = events.children.length === 0;
    const base = webBase(result, row);
    body.replaceChildren(resultCard(result, base), ...artifactCards(result));
  }

  function resultCard(result, base) {
    const evidence = new Map((result.evidence ?? []).map(e => [e.id, e]));
    const u = result.usage;
    return h('section', { class: 'card' },
      h('h2', {}, 'Result'),
      h('p', {}, result.summary),
      result.error ? notice([`${result.error.code}: ${result.error.message}`, result.error.action].filter(Boolean), true) : null,
      h('dl', { class: 'facts' },
        h('dt', {}, 'Confidence'), h('dd', {}, String(result.confidence)),
        h('dt', {}, 'Cost'), h('dd', {}, usd(u.usd)),
        h('dt', {}, 'Tokens'), h('dd', {}, `${count(u.input)} in, ${count(u.output)} out, ${count(u.cache_read)} cache read, ${count(u.cache_write)} cache write`)),
      batchTasks(result),
      result.claims?.length ? [h('h3', {}, `Claims (${result.claims.length})`), h('ol', {}, result.claims.map(c => claimItem(c, evidence, base)))] : null,
      result.open_questions?.length ? [h('h3', {}, 'Open questions'), h('ul', {}, result.open_questions.map(q => h('li', {}, q)))] : null,
      base.url || !row?.repository ? null : webAddress(row.repository, result));
  }

  function webAddress(repository, result) {
    const input = h('input', { type: 'text', placeholder: 'https://…' });
    const save = () => { saveWebUrl(repository, input.value.trim()); finished(result); };
    return h('div', { class: 'row' }, h('label', {}, 'Repository web address, to link citations to the code', input), h('button', { onclick: save }, 'Save'));
  }

  function claimItem(claim, evidence, base) {
    const refs = claim.evidence.map(ref => evidence.get(ref) ?? { id: ref, kind: 'input', locator: ref });
    return h('li', {},
      h('div', {}, claim.text),
      h('div', { class: 'muted' }, `confidence ${Number(claim.confidence).toFixed(2)} `, claim.support ? badge(claim.support) : null, ' ',
        refs.map((e, i) => [i ? ', ' : '', citation(e, base, row?.commit)])));
  }

  function batchTasks(result) {
    const batch = parseJson(artifact(result, 'driven-batch')?.content ?? '');
    return batch?.tasks?.length ? [h('h3', {}, `Tasks (${batch.tasks.length})`), taskTable(batch.tasks, false)] : null;
  }

  function taskTable(list, isLive) {
    const column = (title, cell) => ({ title, cell });
    const columns = [
      column('Task', t => (t.run_id && !isLive ? h('a', { href: `#/runs/${enc(t.run_id)}` }, t.id) : t.id)),
      column(isLive ? 'Last event' : 'State', t => (t.error_code ? `${t.status} (${t.error_code})` : t.status ?? '')),
      isLive ? column('Stage', t => t.stage ?? '') : null,
      isLive ? column('Turns', t => count(t.turns)) : null,
      column('Tokens', t => count(t.tokens)),
      column(isLive ? 'Cost (est.)' : 'Cost', t => usd(t.usd)),
      column('Branch', t => h('span', { class: 'mono' }, t.branch ?? '')),
      column('PR', t => (t.pr_url ? externalLink('PR', t.pr_url) : '')),
      column('Detail', t => t.detail ?? ''),
    ].filter(Boolean);
    return h('div', { class: 'scroll' }, h('table', {},
      h('thead', {}, h('tr', {}, columns.map(c => h('th', {}, c.title)))),
      h('tbody', {}, list.map(t => h('tr', {}, columns.map(c => h('td', {}, c.cell(t))))))));
  }

  function artifactCards(result) {
    const rest = (result.artifacts ?? []).filter(a => a.kind !== 'driven-batch');
    return rest.length ? [h('section', { class: 'card' }, h('h2', {}, `Artifacts (${rest.length})`), rest.map(artifactView))] : [];
  }
}

function artifactView(a) {
  const title = h('summary', {}, h('strong', {}, a.kind), ` ${a.media_type}`);
  const data = a.content != null ? parseJson(a.content) : null;
  if (a.kind === 'changes' && data) return h('details', {}, title, changesView(data));
  const link = data?.url ? externalLink(data.url, data.url) : null;
  const text = a.content == null ? null : data ? JSON.stringify(data, null, 2) : a.content;
  return h('details', { open: link != null }, title,
    link ? h('p', {}, link) : null,
    text != null ? h('pre', {}, text) : h('p', { class: 'muted' }, 'A reference, not inline: ', safeUrl(a.uri) ? externalLink(a.uri, a.uri) : h('code', {}, a.uri ?? ''), ` sha256 ${a.sha256}`));
}

function changesView(c) {
  return [
    h('p', {}, `${c.files_total} files changed`, h('span', { class: 'muted' }, ` · base ${String(c.base).slice(0, 12)} → ${String(c.commit ?? '').slice(0, 12)}`)),
    h('div', { class: 'scroll' }, h('table', {}, h('tbody', {}, (c.files ?? []).map(f => h('tr', {},
      h('td', { class: 'mono' }, f.path), h('td', { class: 'num' }, f.binary ? 'binary' : `+${f.added} −${f.removed}`)))))),
    c.diff ? h('pre', {}, c.diff) : h('p', { class: 'muted' }, c.diff_truncated ? 'The diff did not fit; fetch the branch for it.' : 'No diff.'),
  ];
}
