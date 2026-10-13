import { h, field, notice } from './dom.js';
import { call, problems } from './api.js';

/** The start-a-run form: a goal, a preset and a repository; accounts that may start a batch can list driven tasks. */
export function newRunView(root, ctx) {
  const { me } = ctx;
  const message = h('div');
  const goal = h('textarea', { required: true, placeholder: 'What should chargehand find out, or change?' });
  const preset = h('input', { type: 'text', list: 'preset-list', value: me.presets.includes('default') ? 'default' : (me.presets[0] ?? ''), required: true });
  const repository = h('input', { type: 'text', list: 'repository-list', placeholder: '/path/to/checkout on the server' });
  const commit = h('input', { type: 'text', placeholder: 'sha of the checkout (needed with a repository)' });
  const taskList = h('div');
  const tasksPanel = h('div', { hidden: true }, taskList, h('p', {}, h('button', { type: 'button', onclick: () => addTask() }, 'Add task')));
  const batch = h('input', { type: 'checkbox', onchange: () => {
    tasksPanel.hidden = !batch.checked;
    if (batch.checked && !taskList.children.length) addTask();
    const wanted = batch.checked ? 'driven' : 'default';
    if (me.presets.includes(wanted)) preset.value = wanted;
  } });
  const submit = h('button', { class: 'primary', type: 'submit' }, 'Start run');

  function addTask() {
    const id = h('input', { type: 'text', value: `t${taskList.children.length + 1}`, required: true, pattern: '[a-z0-9][a-z0-9_-]{0,63}' });
    const goalText = h('textarea', { required: true, placeholder: 'What this task should do' });
    const size = h('select', {}, ['S', 'M', 'L'].map(s => h('option', { value: s, selected: s === 'M' }, s)));
    const depends = h('input', { type: 'text', placeholder: 'ids, comma separated' });
    const card = h('div', { class: 'task' },
      h('div', { class: 'row' }, field('Task id', id), field('Size', size), field('Depends on', depends),
        h('button', { type: 'button', onclick: () => card.remove() }, 'Remove')),
      goalText);
    card.read = () => ({
      id: id.value.trim(), goal: goalText.value.trim(), size: size.value,
      ...(depends.value.trim() ? { depends_on: depends.value.split(',').map(d => d.trim()).filter(Boolean) } : {}),
    });
    taskList.append(card);
  }

  function request() {
    const body = { contract_version: 'request/v1', text: goal.value.trim(), context: { interactive: false, preset: preset.value.trim() } };
    if (repository.value.trim()) body.context.repository = { path: repository.value.trim(), commit: commit.value.trim() };
    if (batch.checked) body.driven = { tasks: [...taskList.children].map(card => card.read()) };
    return body;
  }

  async function start(event) {
    event.preventDefault();
    submit.disabled = true;
    try {
      // wait=0: the run goes on in the background and its page streams it.
      const r = await call('/v1/runs', { method: 'POST', body: request(), extra: { Prefer: 'wait=0' } });
      const id = /\/v1\/runs\/([^/]+)$/.exec(r.location ?? '')?.[1];
      if (r.ok && id) location.hash = `#/runs/${id}`;
      else message.replaceChildren(notice(problems(r), true));
    } catch (e) {
      ctx.report(e, message);
    } finally {
      submit.disabled = false;
    }
  }

  root.append(
    h('h1', {}, 'Start a run'),
    h('form', { class: 'card', onsubmit: start },
      field('Goal', goal),
      h('div', { class: 'row' }, field('Preset', preset), field('Repository', repository), field('Commit', commit)),
      h('p', { class: 'muted' }, 'Most presets need a repository on the server and the commit to read it at.'),
      h('datalist', { id: 'preset-list' }, me.presets.map(p => h('option', { value: p }))),
      h('datalist', { id: 'repository-list' }, (me.repositories ?? []).map(r => h('option', { value: r }))),
      me.driven ? [
        h('label', { class: 'inline' }, batch, 'Run as a batch: each task gets a container and ends in a draft pull request'),
        tasksPanel,
      ] : null,
      message, submit));
  return () => {};
}
