import { h, externalLink, safeUrl, parseJson } from './dom.js';

// A citation links to the code when the repository has a web address: taken from a pull request's URL, or typed once by the reader.
// The typed address is a plain preference, not a secret, so it lives in localStorage.
const STORE = 'chargehand.repositoryUrls';
const PULL = /^(https?:\/\/[^/]+\/.+?)(\/-)?\/(pull|merge_requests)\/\d+/;
const SHA = /^[0-9a-f]{7,40}$/;

function saved() {
  try {
    return JSON.parse(localStorage.getItem(STORE) ?? '{}');
  } catch {
    return {};
  }
}

export function saveWebUrl(repository, url) {
  const all = saved();
  const safe = safeUrl(url);
  if (safe) all[repository] = safe.replace(/\/+$/, '');
  else delete all[repository];
  try {
    localStorage.setItem(STORE, JSON.stringify(all));
  } catch { /* the address is asked for again next time */ }
}

/** {url, gitlab} for the run's repository, or {url: null}. */
export function webBase(result, row) {
  const pr = parseJson(result.artifacts?.find(a => a.kind === 'pull-request')?.content ?? '')?.url;
  const batch = parseJson(result.artifacts?.find(a => a.kind === 'driven-batch')?.content ?? '');
  for (const url of [row?.pr_url, pr, ...(batch?.tasks ?? []).map(t => t.pr_url)]) {
    const m = PULL.exec(url ?? '');
    if (m && safeUrl(m[1])) return { url: m[1], gitlab: m[3] === 'merge_requests' };
  }
  const own = saved()[row?.repository];
  return own ? { url: own, gitlab: /gitlab/i.test(own) } : { url: null };
}

/** One piece of evidence: a file locator becomes a link to path:line at the commit, a URL a link, the rest text. */
export function citation(evidence, base, fallbackCommit) {
  const { kind, locator } = evidence;
  const text = h('code', {}, locator);
  if (kind === 'url') return externalLink(text, locator);
  if (!base.url) return text;
  const commit = evidence.commit ?? fallbackCommit;
  const blob = `${base.url}${base.gitlab ? '/-' : ''}`;
  if (kind === 'commit' && SHA.test(locator)) return externalLink(text, `${blob}/commit/${locator}`);
  const m = kind === 'file' && SHA.test(commit ?? '') ? /^(.*?)(?::(\d+)(?:-(\d+))?)?$/.exec(locator) : null;
  if (!m) return text;
  const lines = m[2] ? `#L${m[2]}${m[3] ? (base.gitlab ? `-${m[3]}` : `-L${m[3]}`) : ''}` : '';
  return externalLink(text, `${blob}/blob/${commit}/${m[1].split('/').map(encodeURIComponent).join('/')}${lines}`);
}
