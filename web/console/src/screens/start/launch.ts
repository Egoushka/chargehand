import type { RepositoryBody } from '../../api/repositories';
import { emptyDraft, emptyTask, type Json, toRequest } from './model';

// A launch template is a goal and a size kept in this browser's localStorage (not secret: no key, no path). Reads and writes can
// fail (a private window, a full quota), so each is wrapped and the screen works without them.
const STORAGE_KEY = 'chargehand.console.launch-templates';
export const MAX_LAUNCH_TEMPLATES = 30;

export interface LaunchTemplate {
  name: string;
  goal: string;
  size: string;
}

export function loadLaunchTemplates(): LaunchTemplate[] {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    const list: unknown = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(list)) return [];
    return list
      .filter(
        (t): t is LaunchTemplate =>
          typeof t === 'object' &&
          t !== null &&
          typeof t.name === 'string' &&
          t.name !== '' &&
          typeof t.goal === 'string' &&
          typeof t.size === 'string',
      )
      .slice(0, MAX_LAUNCH_TEMPLATES);
  } catch {
    return [];
  }
}

/** Saves under a name (one of that name is replaced). Null when the name is empty or the browser would not keep it. */
export function saveLaunchTemplate(template: LaunchTemplate): LaunchTemplate[] | null {
  const name = template.name.trim().slice(0, 80);
  if (name === '') return null;
  const next = [
    { ...template, name },
    ...loadLaunchTemplates().filter((t) => t.name !== name),
  ].slice(0, MAX_LAUNCH_TEMPLATES);
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    return next;
  } catch {
    return null;
  }
}

/** Why a repository cannot take a launched batch, or null when it can: a checkout that failed to read, one with no head, or no verify command. */
export function notReady(repo: RepositoryBody): string | null {
  if (repo.error) return 'unreadable';
  if (!repo.head) return 'no commit';
  if (!repo.verify) return 'no verify command';
  return null;
}

/** The request/v1 of one batch of one task in a repository, at its head. */
export function launchRequest(goal: string, size: string, repo: RepositoryBody): Json {
  const text = goal.trim();
  return toRequest(
    emptyDraft({
      text: text.slice(0, 80),
      repository: repo.path,
      commit: repo.head ?? '',
      tasks: [emptyTask({ id: 'task-1', goal: text, size })],
    }),
  );
}
