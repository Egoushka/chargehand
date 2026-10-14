// DOM helpers. Run data reaches the page only as text nodes and as attribute values that pass through here:
// no markup is ever parsed from a string, and a link is built only for an http(s) URL.

const PROPERTIES = new Set(['value', 'checked', 'selected', 'disabled', 'hidden', 'required']);
const FORBIDDEN = new Set(['style', 'srcdoc']);

/** h('a', {href, class, onclick}, 'text', child, ...) */
export function h(tag, props = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [name, value] of Object.entries(props ?? {})) {
    if (value == null || value === false || FORBIDDEN.has(name)) continue;
    if (name.startsWith('on') && typeof value === 'function') el.addEventListener(name.slice(2), value);
    else if (name === 'class') el.className = value;
    else if (PROPERTIES.has(name)) el[name] = value;
    else el.setAttribute(name, value === true ? '' : String(value));
  }
  el.append(...kids.flat(Infinity).filter(kid => kid != null && kid !== false));
  return el;
}

/** The href of an http(s) URL, or null. Keeps javascript:, data: and the like out of every link. */
export function safeUrl(text) {
  try {
    const url = new URL(String(text));
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null;
  } catch {
    return null;
  }
}

/** A link that opens elsewhere when the URL is safe, plain text when it is not. */
export function externalLink(label, url) {
  const href = safeUrl(url);
  return href ? h('a', { href, target: '_blank', rel: 'noopener noreferrer' }, label) : h('span', {}, label);
}

/** replaceChildren that skips null and false, which it would otherwise print as text. */
export function fill(el, ...kids) {
  el.replaceChildren(...kids.flat(Infinity).filter(kid => kid != null && kid !== false));
}

/** The parsed JSON, or null when the text is not JSON (an artifact's inline content comes from outside this page). */
export function parseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

export function badge(state) {
  const text = String(state ?? 'unknown');
  return h('span', { class: `badge st-${/^[a-z_]+$/.test(text) ? text : 'unknown'}` }, text.replace('_', ' '));
}

export const usd = value => (value == null ? '—' : `$${value < 0.01 && value > 0 ? value.toFixed(4) : value.toFixed(2)}`);

export const count = value => (value == null ? '—' : Number(value).toLocaleString());

export const when = iso => (iso ? new Date(iso).toLocaleString() : '—');

export const clock = iso => (iso ? new Date(iso).toLocaleTimeString() : '');

export const baseName = path => String(path ?? '').split('/').filter(Boolean).pop() ?? '';

export const field = (label, control) => h('label', {}, label, control);

export function notice(text, bad = false) {
  return h('div', { class: bad ? 'notice bad' : 'notice' }, ...(Array.isArray(text) ? [h('ul', {}, ...text.map(line => h('li', {}, line)))] : [text]));
}
