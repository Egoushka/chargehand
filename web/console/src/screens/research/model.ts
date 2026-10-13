import type { Claim, Evidence, RunResult } from '../../api/types';
import { safeHttpUrl } from '../../lib/url';

export interface Locator {
  path: string;
  start: number | null;
  end: number | null;
}

/** `path:12` or `path:12-18`; a locator with no line is the file. */
export function parseLocator(locator: string): Locator {
  const match = /^(.*?):(\d+)(?:-(\d+))?$/.exec(locator);
  if (!match) return { path: locator, start: null, end: null };
  return {
    path: match[1] as string,
    start: Number(match[2]),
    end: match[3] ? Number(match[3]) : null,
  };
}

export type Forge = 'github' | 'gitlab' | 'gitea';

/** Which forge a web address is, by its host: it decides how a file at a commit is addressed. */
export function forgeOf(base: string): Forge {
  const host = (() => {
    try {
      return new URL(base).hostname.toLowerCase();
    } catch {
      return '';
    }
  })();
  if (host.includes('gitlab')) return 'gitlab';
  if (host.includes('gitea') || host.includes('forgejo') || host.includes('codeberg'))
    return 'gitea';
  return 'github';
}

/** The repository's web address (`https://host/owner/name`), cleaned: http or https only, no trailing slash or `.git`. Null for anything else. */
export function cleanWeb(value: string): string | null {
  const url = safeHttpUrl(value.trim());
  if (!url) return null;
  const parsed = new URL(url);
  parsed.search = '';
  parsed.hash = '';
  return parsed.href.replace(/\/+$/, '').replace(/\.git$/, '');
}

/** The repository behind a pull request link: `https://host/owner/name/pull/412` is `https://host/owner/name`. */
export function webFromPullRequest(url: string | null | undefined): string | null {
  const safe = safeHttpUrl(url);
  if (!safe) return null;
  const m = /^(https?:\/\/[^/]+\/[^/]+\/[^/]+)\/(?:-\/)?(?:pull|pulls|merge_requests)\/\d+/.exec(
    safe,
  );
  return m ? (m[1] as string) : null;
}

/** The link to a file at a commit, with its lines. Null when there is no web address or the evidence is not a file at a commit. */
export function codeHref(
  web: string | null,
  commit: string | null | undefined,
  evidence: Evidence,
): string | null {
  if (evidence.kind === 'url') return safeHttpUrl(evidence.locator);
  if (!web) return null;
  const base = cleanWeb(web);
  if (!base) return null;
  if (evidence.kind === 'commit') {
    const forge = forgeOf(base);
    return `${base}${forge === 'gitlab' ? '/-' : ''}/commit/${encodeURIComponent(evidence.locator)}`;
  }
  if (evidence.kind !== 'file') return null;
  const sha = evidence.commit ?? commit;
  if (!sha) return null;
  const { path, start, end } = parseLocator(evidence.locator);
  const file = path.split('/').map(encodeURIComponent).join('/');
  const forge = forgeOf(base);
  const lines =
    start == null
      ? ''
      : forge === 'gitlab'
        ? `#L${start}${end ? `-${end}` : ''}`
        : `#L${start}${end ? `-L${end}` : ''}`;
  const root =
    forge === 'gitlab'
      ? `${base}/-/blob`
      : forge === 'gitea'
        ? `${base}/src/commit`
        : `${base}/blob`;
  return `${root}/${sha}/${file}${lines}`;
}

export interface ClaimRow {
  claim: Claim;
  evidence: Evidence[];
  /** Ids the claim cites that the result's evidence list does not hold. */
  missing: string[];
}

export function claimRows(result: RunResult): ClaimRow[] {
  const byId = new Map((result.evidence ?? []).map((e) => [e.id, e]));
  return (result.claims ?? []).map((claim) => ({
    claim,
    evidence: claim.evidence.flatMap((id) => {
      const e = byId.get(id);
      return e ? [e] : [];
    }),
    missing: claim.evidence.filter((id) => !byId.has(id)),
  }));
}

export const SUPPORT: Record<
  NonNullable<Claim['support']>,
  { status: 'done' | 'partial' | 'queued'; label: string }
> = {
  supported: { status: 'done', label: 'Supported' },
  partial: { status: 'partial', label: 'Partly supported' },
  unchecked: { status: 'queued', label: 'Not checked' },
};

export function supportOf(claim: Claim) {
  return claim.support
    ? SUPPORT[claim.support]
    : { status: 'queued' as const, label: 'No support check' };
}

export function supportedCount(claims: readonly Claim[]): string {
  return `${claims.filter((c) => c.support === 'supported').length} of ${claims.length}`;
}

export const confidenceText = (value: number): string => value.toFixed(2);

// The repository's web address is typed once and kept in this browser, by the repository's path. It is not a secret.
const WEB_KEY = 'chargehand.console.repoweb';

export function readWeb(repository: string | null | undefined): string | null {
  if (!repository) return null;
  try {
    const stored = JSON.parse(localStorage.getItem(WEB_KEY) ?? '{}') as Record<string, unknown>;
    const value = stored[repository];
    return typeof value === 'string' ? cleanWeb(value) : null;
  } catch {
    return null;
  }
}

export function writeWeb(repository: string, web: string | null): void {
  try {
    const stored = JSON.parse(localStorage.getItem(WEB_KEY) ?? '{}') as Record<string, unknown>;
    if (web) stored[repository] = web;
    else delete stored[repository];
    localStorage.setItem(WEB_KEY, JSON.stringify(stored));
  } catch {
    // Not kept: the address works for this page.
  }
}
