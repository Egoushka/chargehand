import { useEffect, useState } from 'react';
import { type ForecastBody, useForecast } from '../../api/forecast';
import { formatUsd } from '../../lib/format';

/** One line for a forecast: typical and p90 cost, turns, completion odds and how many tasks it stands on; a split hint when the task is large or often unfinished. */
export function forecastText(f: ForecastBody): string {
  if (f.basis === 'flat')
    return `No history yet: up to ${formatUsd(f.usd_p90)} (flat budget${f.samples > 0 ? `, ${f.samples} past ${f.samples === 1 ? 'task' : 'tasks'}` : ''})`;
  const where = f.basis === 'repository' ? 'in this repository' : 'across repositories';
  const done =
    f.completion_probability == null
      ? ''
      : ` · ${Math.round(f.completion_probability * 100)}% complete`;
  return `Forecast ${formatUsd(f.usd_p50, true)} typical, ${formatUsd(f.usd_p90, true)} at p90 · ${f.turns_p50} turns${done} (${f.samples} similar tasks ${where})`;
}

/** The pre-flight forecast under a task's goal, asked a moment after the goal stops changing. */
export function TaskForecastLine({
  repository,
  goal,
  size,
}: {
  repository: string;
  goal: string;
  size: string;
}) {
  const [settled, setSettled] = useState(goal);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(goal), 600);
    return () => clearTimeout(timer);
  }, [goal]);
  const forecast = useForecast({ repository, goal: settled, size: size || null });
  if (!forecast.data) return null;
  return (
    <p className="taskedit__forecast dim small" data-testid="forecast">
      {forecastText(forecast.data)}
      {forecast.data.suggest_split ? '. Consider splitting this task.' : ''}
    </p>
  );
}
