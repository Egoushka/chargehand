import { describe, expect, it } from 'vitest';
import { baseName, DASH, formatCount, formatUsd } from './format';
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

describe('format', () => {
  it('shows a missing cost or count as a dash', () => {
    expect(formatUsd(null)).toBe(DASH);
    expect(formatCount(undefined)).toBe(DASH);
  });

  it('shows small costs with three decimals and larger ones with two', () => {
    expect(formatUsd(0.0421)).toBe('$0.042');
    expect(formatUsd(12.5)).toBe('$12.50');
    expect(formatCount(1234567)).toBe('1,234,567');
  });

  it('names a repository by its last path segment', () => {
    expect(baseName('/srv/app')).toBe('app');
    expect(baseName(null)).toBe(DASH);
  });
});
