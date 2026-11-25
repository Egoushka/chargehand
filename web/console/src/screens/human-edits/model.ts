// What the human-fix heatmap draws from human-edits/v1: a lookup from (file, category) to a count, and the scale a cell's
// background is drawn from. Pure, so it is tested without a DOM.

import type { HumanEditCategory, HumanEditRepositoryBody } from '../../api/human-edits';

export const CATEGORY_LABELS: Record<HumanEditCategory, string> = {
  lint: 'Lint / format',
  missing_import: 'Missing import',
  unbounded_input: 'Unbounded input',
  ci_config: 'CI config',
  other: 'Other',
};

export const categoryLabel = (category: HumanEditCategory): string =>
  CATEGORY_LABELS[category] ?? category;

/** The count of one repository's grid at `(file, category)`; 0 where the server sent no cell. */
export function countAt(repo: HumanEditRepositoryBody, file: string, category: string): number {
  return repo.cells.find((c) => c.file === file && c.category === category)?.count ?? 0;
}

/** The highest single cell of a repository's grid; 0 for an empty grid, never negative. */
export function maxCount(repo: HumanEditRepositoryBody): number {
  return repo.cells.reduce((max, c) => Math.max(max, c.count), 0);
}

/** A cell's share of the grid's busiest cell, 0 to 1; 0 for a count of 0 or a grid with no cells at all (never divides by 0). */
export function intensity(count: number, max: number): number {
  return count <= 0 || max <= 0 ? 0 : Math.min(1, count / max);
}

/** Repositories with at least one file to show, most commits first; a repository whose files were all trimmed by the server's cap is left out. */
export function rankedRepositories(
  repositories: readonly HumanEditRepositoryBody[],
): HumanEditRepositoryBody[] {
  return [...repositories]
    .filter((r) => r.files.length > 0)
    .sort((a, b) => b.commits - a.commits || a.repository.localeCompare(b.repository));
}

/** The categories of a repository with at least one cell, in the grid's fixed order; a category with no commit draws no column. */
export function activeCategories(repo: HumanEditRepositoryBody): HumanEditCategory[] {
  const seen = new Set(repo.cells.map((c) => c.category));
  return repo.categories.filter((c) => seen.has(c));
}

/** Per file: its busiest category, for the phone list, which has no room for a grid. Null for a file with no cell (should not happen: every listed file has at least one). */
export function topCategory(
  repo: HumanEditRepositoryBody,
  file: string,
): { category: HumanEditCategory; count: number } | null {
  const cells = repo.cells.filter((c) => c.file === file);
  if (cells.length === 0) return null;
  return cells.reduce((best, c) => (c.count > best.count ? c : best));
}

/** A file's total across every category, for the phone list's count. */
export function fileTotal(repo: HumanEditRepositoryBody, file: string): number {
  return repo.cells.filter((c) => c.file === file).reduce((sum, c) => sum + c.count, 0);
}
