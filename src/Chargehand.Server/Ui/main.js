import { h, fill, field, notice, usd } from './dom.js';
import { call, getKey, setKey, clearKey, Unauthorized, problems } from './api.js';
import { runsView } from './runs.js';
import { runView } from './run.js';
import { newRunView } from './newrun.js';
import { learnView } from './learn.js';

const app = document.getElementById('app');
let stop = () => {};

/** What each view gets: the account, a way to refresh it, and one place to report a failed call. */
const ctx = {
  me: null,
  async refreshMe() {
    const r = await call('/v1/accounts/me');
    if (!r.ok) throw new Error(problems(r).join('; '));
    ctx.me = r.data;
    renderAccount();
  },
  report(error, into) {
    if (error instanceof Unauthorized) signIn('The server rejected the key. Sign in again.');
    else into.replaceChildren(notice(error.message ?? String(error), true));
  },
};

function signIn(message = '') {
  stop();
  stop = () => {};
  ctx.me = null;
  const key = h('input', { type: 'password', autocomplete: 'off', required: true, placeholder: 'chk1.… or the server key' });
  const submit = async event => {
    event.preventDefault();
    setKey(key.value.trim());
    key.value = '';
    await start();
  };
  app.replaceChildren(h('main', { class: 'signin card' },
    h('h1', {}, 'chargehand'),
    h('p', { class: 'muted' }, 'Paste an API key. It stays in this tab (sessionStorage) until you sign out or close it, and goes only to this server.'),
    h('form', { onsubmit: submit }, field('API key', key), h('p', {}, h('button', { class: 'primary', type: 'submit' }, 'Sign in')), message ? notice(message, true) : null)));
}

function signOut() {
  clearKey();
  signIn();
}

const account = h('div', { class: 'who' });
const banner = h('div');
const main = h('main', { class: 'wrap' });

function renderAccount() {
  const { me } = ctx;
  const day = me.spend.day;
  const cap = me.limits.max_usd_per_day;
  fill(account,
    `${me.name} · today ${usd(day.usd)}${cap == null ? '' : ` of ${usd(cap)}`}`,
    me.owner ? h('button', { class: me.halted ? '' : 'danger', onclick: toggleHalt }, me.halted ? 'Resume server' : 'Halt server') : null,
    h('button', { onclick: signOut }, 'Sign out'));
  fill(banner, me.halted ? h('div', { class: 'wrap' }, notice('The server is halted: new runs are refused until it is resumed.', true)) : null);
}

async function toggleHalt() {
  const halting = !ctx.me.halted;
  if (halting && !confirm('Halt the server? Every unfinished run is cancelled and new runs are refused.')) return;
  try {
    const r = await call(halting ? '/v1/halt' : '/v1/resume', { method: 'POST' });
    if (!r.ok) main.prepend(notice(problems(r), true));
    await ctx.refreshMe();
  } catch (e) {
    ctx.report(e, main);
  }
}

function route() {
  if (!ctx.me) return;
  stop();
  main.replaceChildren();
  const path = location.hash.slice(1) || '/';
  const run = /^\/runs\/([^/]+)$/.exec(path);
  if (run) stop = runView(main, ctx, decodeURIComponent(run[1]));
  else if (path === '/new') stop = newRunView(main, ctx);
  else if (path === '/learn') stop = learnView(main, ctx);
  else stop = runsView(main, ctx);
}

async function start() {
  if (!getKey()) return signIn();
  try {
    await ctx.refreshMe();
  } catch (e) {
    // A 401 already cleared the key; anything else (network, a bad Host) is shown on the sign-in page.
    return signIn(e instanceof Unauthorized ? 'The server rejected that key.' : e.message);
  }
  app.replaceChildren(
    h('header', { class: 'top' }, h('span', { class: 'brand' }, 'chargehand'),
      h('nav', {}, h('a', { href: '#/' }, 'Runs'), h('a', { href: '#/new' }, 'Start a run'), h('a', { href: '#/learn' }, 'Learning')), account),
    banner, main);
  route();
}

addEventListener('hashchange', route);
start();
