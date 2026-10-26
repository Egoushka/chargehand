import { describe, expect, it } from 'vitest';
import type { ForecastBody } from '../../api/forecast';
import { forecastText } from './Forecast';

const base: ForecastBody = {
  size_class: 'M',
  basis: 'repository',
  samples: 8,
  usd_p50: 1.2,
  usd_p90: 3.4,
  turns_p50: 40,
  completion_probability: 0.75,
  suggest_split: false,
};

describe('forecastText', () => {
  it('names the figures and what they stand on', () => {
    expect(forecastText(base)).toBe(
      'Forecast ≈ $1.20 typical, ≈ $3.40 at p90 · 40 turns · 75% complete (8 similar tasks in this repository)',
    );
  });

  it('says when the pool is every repository', () => {
    expect(forecastText({ ...base, basis: 'size_class' })).toContain('across repositories');
  });

  it('says there is no history and shows the flat budget', () => {
    expect(
      forecastText({
        size_class: 'L',
        basis: 'flat',
        samples: 0,
        usd_p90: 10,
        suggest_split: true,
      }),
    ).toBe('No history yet: up to $10.00 (flat budget)');
  });
});
