import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Claim, Evidence, RunResult } from '../../api/types';
import { COMMIT } from '../../testing/fixtures';
import { researchResult, singleRun } from '../../testing/fixtures-runs';
import { renderConsole } from '../../testing/render';
import { type Scenario, serveApi } from '../../testing/server';
import {
  claimRows,
  cleanWeb,
  codeHref,
  confidenceText,
  forgeOf,
  parseLocator,
  readWeb,
  supportedCount,
  supportOf,
  webFromPullRequest,
  writeWeb,
} from './model';

const ID = 'run-20261013-143000-5e0a11';
const WEB = 'https://github.com/example/payments-api';
const file = (locator: string, over: Partial<Evidence> = {}): Evidence => ({
  id: 'e',
  kind: 'file',
  locator,
  commit: COMMIT,
  ...over,
});

afterEach(() => localStorage.clear());

describe('the locator of a citation', () => {
  it('reads a path with a line, a range of lines, or no line', () => {
    expect(parseLocator('src/A.cs:12')).toEqual({ path: 'src/A.cs', start: 12, end: null });
    expect(parseLocator('src/A.cs:12-18')).toEqual({ path: 'src/A.cs', start: 12, end: 18 });
    expect(parseLocator('src/A.cs')).toEqual({ path: 'src/A.cs', start: null, end: null });
    expect(parseLocator('C:\\a\\b.cs:7').start).toBe(7);
  });
});

describe('the address of the repository', () => {
  it('tells the forge by its host, and a web address from a pull request link', () => {
    expect(forgeOf('https://github.com/o/n')).toBe('github');
    expect(forgeOf('https://gitlab.example.org/o/n')).toBe('gitlab');
    expect(forgeOf('https://codeberg.org/o/n')).toBe('gitea');
    expect(forgeOf('https://git.example.org/o/n')).toBe('github');
    expect(forgeOf('not a url')).toBe('github');
    expect(webFromPullRequest('https://github.com/o/n/pull/7')).toBe('https://github.com/o/n');
    expect(webFromPullRequest('https://gitlab.com/o/n/-/merge_requests/7')).toBe(
      'https://gitlab.com/o/n',
    );
    expect(webFromPullRequest('javascript:alert(1)')).toBeNull();
    expect(webFromPullRequest(null)).toBeNull();
    expect(webFromPullRequest('https://github.com/o/n')).toBeNull();
  });

  it('keeps an address to http or https, with no query, fragment, trailing slash or .git', () => {
    expect(cleanWeb(' https://github.com/o/n.git/?x=1#top ')).toBe('https://github.com/o/n');
    expect(cleanWeb('http://git.local/o/n/')).toBe('http://git.local/o/n');
    expect(cleanWeb('javascript:alert(1)')).toBeNull();
    expect(cleanWeb('github.com/o/n')).toBeNull();
    expect(cleanWeb('')).toBeNull();
  });
});

describe('the link of a citation', () => {
  it('opens a file at its commit with its lines, the way each forge addresses them', () => {
    expect(codeHref(WEB, null, file('src/A.cs:12-18'))).toBe(
      `${WEB}/blob/${COMMIT}/src/A.cs#L12-L18`,
    );
    expect(codeHref(WEB, null, file('src/A.cs:12'))).toBe(`${WEB}/blob/${COMMIT}/src/A.cs#L12`);
    expect(codeHref(WEB, null, file('src/A.cs'))).toBe(`${WEB}/blob/${COMMIT}/src/A.cs`);
    expect(codeHref('https://gitlab.com/o/n', null, file('src/A.cs:3-4'))).toBe(
      `https://gitlab.com/o/n/-/blob/${COMMIT}/src/A.cs#L3-4`,
    );
    expect(codeHref('https://codeberg.org/o/n', null, file('src/A.cs:3'))).toBe(
      `https://codeberg.org/o/n/src/commit/${COMMIT}/src/A.cs#L3`,
    );
  });

  it('uses the commit of the evidence, else the run’s, and encodes the path', () => {
    expect(codeHref(WEB, 'run-commit', file('a b/ü.cs:1', { commit: undefined }))).toBe(
      `${WEB}/blob/run-commit/a%20b/%C3%BC.cs#L1`,
    );
    expect(codeHref(WEB, null, file('A.cs:1', { commit: undefined }))).toBeNull();
  });

  it('links a url as it is when it is http or https, and a commit by its sha', () => {
    const url = { id: 'u', kind: 'url', locator: 'https://example.com/notes' } as const;
    expect(codeHref(null, null, url)).toBe('https://example.com/notes');
    expect(codeHref(null, null, { ...url, locator: 'javascript:alert(1)' })).toBeNull();
    expect(codeHref(WEB, null, { id: 'c', kind: 'commit', locator: 'abc123' })).toBe(
      `${WEB}/commit/abc123`,
    );
    expect(
      codeHref('https://gitlab.com/o/n', null, { id: 'c', kind: 'commit', locator: 'abc' }),
    ).toBe('https://gitlab.com/o/n/-/commit/abc');
  });

  it('has no link without a web address, or for evidence that is not a place in the code', () => {
    expect(codeHref(null, null, file('A.cs:1'))).toBeNull();
    expect(codeHref('javascript:alert(1)', null, file('A.cs:1'))).toBeNull();
    expect(codeHref(WEB, null, { id: 'm', kind: 'session_message', locator: 'msg-3' })).toBeNull();
    expect(codeHref(WEB, null, { id: 'i', kind: 'input', locator: 'text' })).toBeNull();
  });
});

describe('the claims of a result', () => {
  const claim = (text: string, evidence: string[], support?: Claim['support']): Claim => ({
    text,
    evidence,
    confidence: 0.5,
    support,
  });

  it('pairs each claim with its evidence and names an id the result does not hold', () => {
    const result: RunResult = {
      ...researchResult(),
      claims: [claim('a', ['e1', 'gone']), claim('b', [])],
    };
    const rows = claimRows(result);
    expect(rows[0]?.evidence.map((e) => e.id)).toEqual(['e1']);
    expect(rows[0]?.missing).toEqual(['gone']);
    expect(rows[1]).toMatchObject({ evidence: [], missing: [] });
    expect(claimRows({ ...researchResult(), claims: undefined, evidence: undefined })).toEqual([]);
  });

  it('words the support check, and counts the supported claims', () => {
    expect(supportOf(claim('a', [], 'supported')).label).toBe('Supported');
    expect(supportOf(claim('a', [], 'partial')).label).toBe('Partly supported');
    expect(supportOf(claim('a', [], 'unchecked')).label).toBe('Not checked');
    expect(supportOf(claim('a', [])).label).toBe('No support check');
    expect(
      supportedCount([claim('a', [], 'supported'), claim('b', [], 'partial'), claim('c', [])]),
    ).toBe('1 of 3');
    expect(confidenceText(0.8)).toBe('0.80');
  });
});

describe('the repository address kept in the browser', () => {
  it('keeps one address per repository, and forgets one', () => {
    expect(readWeb('/srv/a')).toBeNull();
    writeWeb('/srv/a', WEB);
    writeWeb('/srv/b', 'https://gitlab.com/o/n');
    expect(readWeb('/srv/a')).toBe(WEB);
    expect(readWeb('/srv/b')).toBe('https://gitlab.com/o/n');
    writeWeb('/srv/a', null);
    expect(readWeb('/srv/a')).toBeNull();
    expect(readWeb(null)).toBeNull();
  });

  it('ignores a stored value that is not an http or https address, and a store that is not readable', () => {
    localStorage.setItem(
      'chargehand.console.repoweb',
      JSON.stringify({ '/srv/a': 'javascript:1' }),
    );
    expect(readWeb('/srv/a')).toBeNull();
    localStorage.setItem('chargehand.console.repoweb', 'not json');
    expect(readWeb('/srv/a')).toBeNull();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(() => writeWeb('/srv/a', WEB)).not.toThrow();
    expect(readWeb('/srv/a')).toBeNull();
  });
});

function serve(result: RunResult | null, extra: Scenario = {}, summary = singleRun(ID)) {
  return serveApi({
    runs: [summary],
    replies: result ? { [`/v1/runs/${ID}`]: [200, result] } : {},
    ...extra,
  });
}

describe('the research result', () => {
  it('shows the summary, each claim with its support and confidence, the open questions and the usage', async () => {
    serve(researchResult());
    renderConsole(`/console/runs/${ID}`);
    expect((await screen.findByRole('heading', { level: 1, name: ID })).textContent).toBe(ID);
    expect(await screen.findByText(/Payments retry through RetryPolicy/)).toBeTruthy();
    expect(screen.getByText('payments-api')).toBeTruthy();
    expect(screen.getByText('@a91c0ff')).toBeTruthy();
    expect(screen.getByText('Done')).toBeTruthy();

    const claims = screen
      .getByRole('heading', { name: 'Claims' })
      .closest('section') as HTMLElement;
    const items = within(claims).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(within(items[0] as HTMLElement).getByText('Supported')).toBeTruthy();
    expect(within(items[1] as HTMLElement).getByText('Partly supported')).toBeTruthy();
    expect(within(items[2] as HTMLElement).getByText('Not checked')).toBeTruthy();
    expect(within(items[0] as HTMLElement).getByText('0.94')).toBeTruthy();
    expect(screen.getByText('support check by a second model')).toBeTruthy();
    expect(screen.getByText('0.77')).toBeTruthy();
    expect(screen.getByText('1 of 3')).toBeTruthy();
    expect(screen.getByText('54.2k tokens · $0.31')).toBeTruthy();
    expect(screen.getByText(/Does the jitter in the design note exist/)).toBeTruthy();
    expect(screen.getByText(/Which callers bypass RetryPolicy/)).toBeTruthy();
  });

  it('links a citation to its lines at the commit once the repository’s web address is typed, and keeps it', async () => {
    serve(researchResult());
    const { unmount } = renderConsole(`/console/runs/${ID}`);
    await screen.findByText(/Payments retry through RetryPolicy/);
    // Without an address a file citation is a label; a url is a link already.
    expect(screen.queryByRole('link', { name: 'src/Payments/RetryPolicy.cs:12-18' })).toBeNull();
    expect(
      screen.getByRole('link', { name: 'https://example.com/notes/retry-logging' }),
    ).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Repository web address'), {
      target: { value: `${WEB}.git` },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Use this address' }));
    const link = await screen.findByRole('link', { name: 'src/Payments/RetryPolicy.cs:12-18' });
    expect(link.getAttribute('href')).toBe(
      `${WEB}/blob/${COMMIT}/src/Payments/RetryPolicy.cs#L12-L18`,
    );
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
    expect(link.getAttribute('title')).toBe('Opens src/Payments/RetryPolicy.cs:12-18 at a91c0ff');
    expect(readWeb('/srv/work/payments-api')).toBe(WEB);
    unmount();

    // The next visit has it.
    serve(researchResult());
    renderConsole(`/console/runs/${ID}`);
    expect(
      (
        await screen.findByRole('link', { name: 'src/Payments/PaymentsOptions.cs:31' })
      ).getAttribute('href'),
    ).toBe(`${WEB}/blob/${COMMIT}/src/Payments/PaymentsOptions.cs#L31`);
  });

  it('takes the address from the run’s pull request when it has one', async () => {
    serve(researchResult(), {}, singleRun(ID, Date.now(), { pr_url: `${WEB}/pull/9` }));
    renderConsole(`/console/runs/${ID}`);
    expect(
      (await screen.findByRole('link', { name: 'docs/guide/retries.md:3' })).getAttribute('href'),
    ).toBe(`${WEB}/blob/${COMMIT}/docs/guide/retries.md#L3`);
  });

  it('refuses an address that is not http or https, and says so', async () => {
    serve(researchResult());
    renderConsole(`/console/runs/${ID}`);
    fireEvent.change(await screen.findByLabelText('Repository web address'), {
      target: { value: 'javascript:alert(1)' },
    });
    expect(
      screen.getByText('Use a web address that starts with https:// or http://.'),
    ).toBeTruthy();
    expect(
      (screen.getByRole('button', { name: 'Use this address' }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('says a citation the result does not list is missing', async () => {
    const result = researchResult();
    serve({ ...result, evidence: result.evidence?.filter((e) => e.id !== 'e1') });
    renderConsole(`/console/runs/${ID}`);
    expect(await screen.findByText('e1: missing')).toBeTruthy();
  });

  it('shows the signature, says it is not checked here, and says when there is none', async () => {
    serve(researchResult());
    const first = renderConsole(`/console/runs/${ID}`);
    expect(await screen.findByText('ES256')).toBeTruthy();
    expect(screen.getByText('0123456789abcdef')).toBeTruthy();
    expect(screen.getByText('Signed, not checked here')).toBeTruthy();
    expect(screen.getByText(/The console does not check it/)).toBeTruthy();
    first.unmount();

    serve({ ...researchResult(), signature: undefined });
    renderConsole(`/console/runs/${ID}`);
    expect(await screen.findByText('This result carries no signature.')).toBeTruthy();
  });

  it('says no support check ran, and nothing is left open', async () => {
    const result = researchResult();
    serve({
      ...result,
      claims: result.claims?.map(({ support: _s, ...c }) => c),
      open_questions: [],
    });
    renderConsole(`/console/runs/${ID}`);
    expect(await screen.findByText('no support check ran')).toBeTruthy();
    expect(screen.getByText('Nothing is left open.')).toBeTruthy();
    expect(screen.getAllByText('No support check')).toHaveLength(3);
  });

  it('copies the result as JSON', async () => {
    serve(researchResult());
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    renderConsole(`/console/runs/${ID}`);
    fireEvent.click(await screen.findByRole('button', { name: 'Copy result JSON' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledOnce());
    expect(JSON.parse(writeText.mock.calls[0]?.[0] as string).task_id).toBe('research-1');
    expect(await screen.findByText('Result copied')).toBeTruthy();
  });
});

describe('a research run that has not produced a result', () => {
  it('says the workers are still reading, and lists the events as they come', async () => {
    const event = {
      contract_version: 'run-status/v1',
      run_id: ID,
      status: 'running',
      event: 'session_progress',
      at: new Date().toISOString(),
      turns: 4,
    };
    serve(
      null,
      {
        streams: { [`/v1/runs/${ID}/events`]: `event: status\ndata: ${JSON.stringify(event)}\n\n` },
      },
      singleRun(ID, Date.now(), { status: 'running', finished_at: null }),
    );
    renderConsole(`/console/runs/${ID}`);
    expect(await screen.findByText('The workers are still reading')).toBeTruthy();
    const log = await screen.findByRole('log', { name: 'Events of this run' });
    expect(within(log).getByText('4 turns')).toBeTruthy();
  });

  it('says a run the server lost has no result', async () => {
    serve(
      null,
      { replies: { [`/v1/runs/${ID}`]: [410, { run_id: ID, status: 'lost' }] } },
      singleRun(ID, Date.now(), { status: 'lost', finished_at: null }),
    );
    renderConsole(`/console/runs/${ID}`);
    expect(await screen.findByText('The process that ran it ended first')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Copy result JSON' })).toBeNull();
  });

  it('says why a failed run failed, and that it returned no claims', async () => {
    serve(
      {
        ...researchResult(),
        status: 'failed',
        claims: [],
        evidence: [],
        error: {
          code: 'cost_cap_reached',
          message: 'The run reached its dollar cap.',
          retryable: false,
          action: 'Raise the cap or choose a cheaper preset.',
        },
      },
      {},
      singleRun(ID, Date.now(), { status: 'failed', error_code: 'cost_cap_reached' }),
    );
    renderConsole(`/console/runs/${ID}`);
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('The run failed');
    expect(alert.textContent).toContain('cost_cap_reached');
    expect(alert.textContent).toContain('Raise the cap or choose a cheaper preset.');
    expect(screen.getByText('No claims: the run did not finish')).toBeTruthy();
  });

  it('shows a loading state while the result is read, and an error that retries', async () => {
    serve(null, { hang: [`/v1/runs/${ID}`] });
    const first = renderConsole(`/console/runs/${ID}`);
    await screen.findByRole('heading', { level: 1, name: ID });
    await waitFor(() => expect(document.querySelector('[aria-busy="true"]')).toBeTruthy());
    first.unmount();

    const { calls } = serve(null, {
      replies: { [`/v1/runs/${ID}`]: [500, { errors: ['the log is unreadable'] }] },
    });
    renderConsole(`/console/runs/${ID}`);
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('the log is unreadable');
    const before = calls.length;
    fireEvent.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls.length).toBeGreaterThan(before));
  });
});

describe('the artifacts of a single run', () => {
  it('lists what the result carries, inline content as JSON and a reference with its hash', async () => {
    serve({
      ...researchResult(),
      artifacts: [
        {
          kind: 'pull-request',
          media_type: 'application/json',
          sha256: 'a'.repeat(64),
          content: JSON.stringify({ url: `${WEB}/pull/9` }),
        },
        {
          kind: 'governor-log',
          media_type: 'text/plain',
          sha256: 'b'.repeat(64),
          uri: 'volume://x',
        },
      ],
    });
    renderConsole(`/console/runs/${ID}`);
    const panel = (await screen.findByRole('heading', { name: 'Artifacts' })).closest(
      'section',
    ) as HTMLElement;
    expect(within(panel).getByText('pull-request')).toBeTruthy();
    expect(within(panel).getByRole('link', { name: `${WEB}/pull/9` })).toBeTruthy();
    expect(within(panel).getByText('b'.repeat(64))).toBeTruthy();
  });

  it('shows no panel for a result without artifacts', async () => {
    serve(researchResult());
    renderConsole(`/console/runs/${ID}`);
    await screen.findByText(/Payments retry through RetryPolicy/);
    expect(screen.queryByRole('heading', { name: 'Artifacts' })).toBeNull();
  });
});

describe('cancelling a single run', () => {
  it('asks first, then sends the cancel', async () => {
    const api = serveApi({
      runs: [singleRun(ID, Date.now(), { status: 'running', finished_at: null })],
      replies: {
        [`/v1/runs/${ID}`]: [202, { status: 'running' }],
        [`POST /v1/runs/${ID}/cancel`]: [202, {}],
      },
    });
    renderConsole(`/console/runs/${ID}`);
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel run' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(api.calls.some((c) => c.path.endsWith('/cancel'))).toBe(false);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel run' }));
    await screen.findByText('Cancelling the run');
    expect(api.calls.some((c) => c.method === 'POST' && c.path.endsWith(`/${ID}/cancel`))).toBe(
      true,
    );
  });
});
