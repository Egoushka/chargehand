import { describe, expect, it } from 'vitest';
import {
  baseName,
  capLevel,
  DASH,
  formatAgo,
  formatCount,
  formatDuration,
  formatPercent,
  formatTime,
  formatTokens,
  formatUsd,
  middleTruncate,
  shortId,
} from './format';
import { inboxStatus, runStatus, STATUS, taskStatus } from './status';
import { safeHttpUrl } from './url';

describe('safeHttpUrl', () => {
  it('keeps http and https addresses', () => {
    expect(safeHttpUrl('https://example.com/o/r/pull/7')).toBe('https://example.com/o/r/pull/7');
    expect(safeHttpUrl('http://example.com/x')).toBe('http://example.com/x');
  });

  it('refuses anything a link must not run or load', () => {
    for (const value of [
      'javascript:alert(1)',
      'data:text/html,<b>x</b>',
      'file:///etc/passwd',
      '//example.com',
      'not a url',
      '',
      null,
      undefined,
    ])
      expect(safeHttpUrl(value)).toBeNull();
  });
});

describe('numbers (design system, "Writing numbers")', () => {
  it('shows an unknown figure as an em dash, never as zero', () => {
    expect(formatUsd(null)).toBe(DASH);
    expect(formatTokens(undefined)).toBe(DASH);
    expect(formatCount(undefined)).toBe(DASH);
    expect(formatDuration(null)).toBe(DASH);
    expect(formatPercent(null)).toBe(DASH);
  });

  it('writes dollars with two decimals, under a cent as <$0.01, and from $1,000 with none', () => {
    expect(formatUsd(6.2)).toBe('$6.20');
    expect(formatUsd(0.17)).toBe('$0.17');
    expect(formatUsd(0.004)).toBe('<$0.01');
    expect(formatUsd(0)).toBe('$0.00');
    expect(formatUsd(1234.56)).toBe('$1,235');
  });

  it('puts ≈ before the dollar equivalent of tokens, and nothing before a priced cost', () => {
    expect(formatUsd(6.2, true)).toBe('≈ $6.20');
    expect(formatUsd(6.2)).toBe('$6.20');
  });

  it('writes tokens as is under 1,000, then k, M and B with three significant digits', () => {
    expect(formatTokens(812)).toBe('812');
    expect(formatTokens(48_200)).toBe('48.2k');
    expect(formatTokens(960_000)).toBe('960k');
    expect(formatTokens(1_240_000)).toBe('1.24M');
    expect(formatTokens(5_100_000)).toBe('5.10M');
    expect(formatTokens(12_400_000)).toBe('12.4M');
    expect(formatTokens(2_500_000_000)).toBe('2.50B');
  });

  it('writes a duration with at most two units', () => {
    expect(formatDuration(47)).toBe('47s');
    expect(formatDuration(252)).toBe('4m 12s');
    expect(formatDuration(8040)).toBe('2h 14m');
    expect(formatDuration(97_200)).toBe('1d 3h');
    expect(formatDuration(3600)).toBe('1h');
  });

  it('writes a percent whole, with one decimal under 10 % when it matters', () => {
    expect(formatPercent(38)).toBe('38%');
    expect(formatPercent(4.3)).toBe('4.3%');
    expect(formatPercent(94.4)).toBe('94%');
  });

  it('puts thousands separators on a count', () => {
    expect(formatCount(1234567)).toBe('1,234,567');
  });

  it('marks a cap level: none under 70 %, warn from 70 %, danger from 90 %', () => {
    expect([0, 69, 70, 89, 90, 100].map(capLevel)).toEqual([
      undefined,
      undefined,
      'warn',
      'warn',
      'danger',
      'danger',
    ]);
  });
});

describe('time', () => {
  const now = new Date('2026-10-13T10:30:00Z'); // 13:30 in Kyiv

  it('shows time in Kyiv: the clock today, the date and the clock on another day', () => {
    expect(formatTime('2026-10-13T09:42:00Z', { now })).toBe('12:42');
    expect(formatTime('2026-10-11T19:10:00Z', { now })).toBe('11 Oct 22:10');
    expect(formatTime('2026-10-13T09:51:09Z', { now, seconds: true })).toBe('12:51:09');
  });

  it('reads the day in Kyiv too: 23:30 UTC is already tomorrow there', () => {
    expect(formatTime('2026-10-12T22:30:00Z', { now })).toBe('01:30'); // 01:30 on the 13th: today
  });

  it('says ago in the shortest form, and "just now" for a moment', () => {
    expect(formatAgo('2026-10-13T10:26:00Z', now)).toBe('4m ago');
    expect(formatAgo('2026-10-13T10:29:50Z', now)).toBe('just now');
    expect(formatAgo('2026-10-13T07:16:00Z', now)).toBe('3h 14m ago');
  });

  it('passes through a time it cannot read, and dashes a missing one', () => {
    expect(formatTime('later')).toBe('later');
    expect(formatTime(null)).toBe(DASH);
  });
});

describe('names', () => {
  it('shows the six-hex suffix of a run id, and the id itself when it has none', () => {
    expect(shortId('run-20261013-124239-1fdf01')).toBe('1fdf01');
    expect(shortId('r-1')).toBe('r-1');
    expect(shortId(null)).toBe(DASH);
  });

  it('names a repository by its last path segment', () => {
    expect(baseName('/srv/app')).toBe('app');
    expect(baseName('example/payments-api')).toBe('payments-api');
    expect(baseName(null)).toBe(DASH);
  });

  it('truncates the middle, keeping both ends', () => {
    expect(middleTruncate('chargehand/run-20261013-ch-110-claim-extractor', 24)).toBe(
      'chargehand/r…m-extractor',
    );
    expect(middleTruncate('short', 24)).toBe('short');
  });
});

describe('status words', () => {
  it('has all fifteen, each with a label, a tone and a note', () => {
    expect(Object.keys(STATUS)).toHaveLength(15);
    for (const entry of Object.values(STATUS)) {
      expect(entry.label).toMatch(/^[A-Z]/);
      expect(entry.note.length).toBeGreaterThan(10);
    }
  });

  it('gives asked and question the accent, because they are the two states that need you', () => {
    expect(STATUS.asked.tone).toBe('accent');
    expect(STATUS.question.tone).toBe('accent');
    expect(Object.values(STATUS).filter((s) => s.tone === 'accent')).toHaveLength(2);
  });

  it('maps a run state to a word, and a denied run to failed', () => {
    expect(runStatus('completed')).toBe('done');
    expect(runStatus('needs_input')).toBe('question');
    expect(runStatus('denied')).toBe('failed');
    expect(runStatus('lost')).toBe('lost');
  });

  it('maps a batch task state to a word', () => {
    expect(taskStatus('pending')).toBe('queued');
    expect(taskStatus('not_started')).toBe('not_started');
    expect(taskStatus('cancelled')).toBe('cancelled');
    expect(taskStatus('anything else')).toBe('failed');
  });

  it('maps inbox items to the word that says what to do', () => {
    expect(inboxStatus({ kind: 'question' })).toBe('question');
    expect(inboxStatus({ kind: 'pull_request', reason: 'policy_ask' })).toBe('asked');
    expect(inboxStatus({ kind: 'pull_request', reason: 'partial_draft' })).toBe('partial');
    expect(inboxStatus({ kind: 'failure', reason: 'lost' })).toBe('lost');
    expect(inboxStatus({ kind: 'failure', error: { code: 'rate_limited' } })).toBe('rate_limited');
    expect(inboxStatus({ kind: 'failure', reason: 'verification_failed' })).toBe('failed');
  });
});
