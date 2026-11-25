import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta } from './common';

/** One of the kinds issue #452's classifier sorts a human fix into; `other` is every commit it does not recognise. */
export type HumanEditCategory = 'lint' | 'missing_import' | 'unbounded_input' | 'ci_config' | 'other';

/** One file's count of one category, in one repository's heatmap. */
export interface HumanEditCell {
  file: string;
  category: HumanEditCategory;
  count: number;
}

/** One repository's heatmap: the busiest files first, every category in a fixed column order even where a cell has no count. */
export interface HumanEditRepositoryBody {
  repository: string;
  files: string[];
  categories: HumanEditCategory[];
  cells: HumanEditCell[];
  /** Human commits classified for this repository (a commit may add to several files' cells). */
  commits: number;
  /** How many distinct files were touched, before the heatmap keeps only the busiest. */
  files_total: number;
}

/** `human-edits/v1`: per repository, which files and rule categories humans fix most after a chargehand pull request. */
export interface HumanEditsBody extends ContractMeta {
  contract_version: 'human-edits/v1';
  repositories: HumanEditRepositoryBody[];
}

export const humanEditsQuery = () =>
  queryOptions({
    queryKey: ['human-edits'],
    queryFn: ({ signal }) => getJson<HumanEditsBody>('/v1/human-edits', signal),
    refetchInterval: 60_000,
  });

export const useHumanEdits = () => useQuery(humanEditsQuery());
