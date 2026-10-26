import { useQuery } from '@tanstack/react-query';
import { ApiError, call, problems } from './client';

/** `POST /v1/forecast`: what a task is expected to cost before it starts, from the most similar finished tasks. */
export interface ForecastBody {
  size_class: string;
  /** `repository` (enough tasks here), `size_class` (the same across repositories) or `flat` (no history: only the flat budget). */
  basis: 'repository' | 'size_class' | 'flat';
  samples: number;
  /** Left out for `flat`. */
  usd_p50?: number | null;
  /** For `flat`, the flat budget. */
  usd_p90: number;
  turns_p50?: number | null;
  completion_probability?: number | null;
  suggest_split: boolean;
}

export interface ForecastQuery {
  repository: string;
  goal: string;
  size?: string | null;
}

export const forecastKey = (q: ForecastQuery) => ['forecast', q.repository, q.size ?? 'M', q.goal];

/** The forecast of one task; off while the repository or the goal is empty. */
export function useForecast(query: ForecastQuery) {
  const enabled = query.repository.trim() !== '' && query.goal.trim() !== '';
  return useQuery({
    queryKey: forecastKey(query),
    enabled,
    staleTime: 60_000,
    retry: false,
    queryFn: async ({ signal }): Promise<ForecastBody> => {
      const reply = await call<ForecastBody>('/v1/forecast', {
        method: 'POST',
        signal,
        body: { repository: query.repository, goal: query.goal, size: query.size || undefined },
      });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      return reply.data;
    },
  });
}
