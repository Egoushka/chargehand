import { type Draft, fromRequest, type Json, toRequest } from './model';

// Templates are named requests kept in this browser's localStorage (not secret: they hold goals and paths, never a key). Every
// read and write can fail (a private window, blocked site data, a full quota), so each is wrapped and the screen works without them.
const STORAGE_KEY = 'chargehand.console.start-templates';
export const MAX_TEMPLATES = 50;
export const MAX_NAME = 80;

export interface Template {
  name: string;
  savedAt: number;
  /** A request/v1 without its commit: a commit goes stale, the tasks and caps do not. */
  request: Json;
}

const isObject = (v: unknown): v is Json =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

/** The saved templates, newest first. Anything in storage that is not a template is dropped, not trusted. */
export function loadTemplates(): Template[] {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    const list: unknown = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(list)) return [];
    return list
      .filter(
        (t): t is Template =>
          isObject(t) &&
          typeof t.name === 'string' &&
          t.name !== '' &&
          t.name.length <= MAX_NAME &&
          typeof t.savedAt === 'number' &&
          isObject(t.request),
      )
      .slice(0, MAX_TEMPLATES)
      .sort((a, b) => b.savedAt - a.savedAt);
  } catch {
    return [];
  }
}

function write(templates: Template[]): boolean {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(templates));
    return true;
  } catch {
    return false;
  }
}

/** A template from the draft, with the commit left out. */
export function toTemplate(name: string, draft: Draft, savedAt = Date.now()): Template {
  return {
    name: name.trim().slice(0, MAX_NAME),
    savedAt,
    request: toRequest({ ...draft, commit: '' }),
  };
}

/** Saves under a name (a template of that name is replaced). Null when the browser would not keep it. */
export function saveTemplate(name: string, draft: Draft): Template[] | null {
  if (name.trim() === '') return null;
  const template = toTemplate(name, draft);
  const next = [template, ...loadTemplates().filter((t) => t.name !== template.name)].slice(
    0,
    MAX_TEMPLATES,
  );
  return write(next) ? next : null;
}

export function deleteTemplate(name: string): Template[] | null {
  const next = loadTemplates().filter((t) => t.name !== name);
  return write(next) ? next : null;
}

/** A template as a draft; its commit is empty, so the screen fills in the repository's head. */
export function applyTemplate(template: Template): Draft {
  return fromRequest(template.request).draft;
}
