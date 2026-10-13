import { useEffect, useRef } from 'react';

const TYPING = new Set(['INPUT', 'TEXTAREA', 'SELECT']);

/** True while the person is typing, or a modal layer holds the keyboard: a bare key then belongs to that. */
export function keysBelongElsewhere(target: EventTarget | null): boolean {
  const element = target instanceof HTMLElement ? target : null;
  if (element && (TYPING.has(element.tagName) || element.isContentEditable)) return true;
  return document.querySelector('[role="dialog"], [role="alertdialog"], dialog[open]') !== null;
}

export interface Hotkeys {
  palette: () => void;
  shortcuts: () => void;
  /** Returns whether a filter took the focus. */
  focusFilter: () => boolean;
  toggleRail: () => void;
  /** `g` then a letter. Returns whether the letter goes anywhere. */
  go: (letter: string) => boolean;
}

/** How long the key after `g` is waited for. */
export const SEQUENCE_MS = 1200;

/**
 * The shell's keys: ⌘K or Ctrl+K opens the palette wherever you are; `?`, `/`, `[` and `g` plus a letter work when you are not
 * typing and no dialog is open. Esc is left to the layers, which close themselves. Cancel and halt have no bare key.
 */
export function useHotkeys(hotkeys: Hotkeys): void {
  // One listener for the life of the shell: a `g` pressed just before the handlers change still meets its letter.
  const latest = useRef(hotkeys);
  latest.current = hotkeys;
  useEffect(() => {
    const keys = {
      palette: () => latest.current.palette(),
      shortcuts: () => latest.current.shortcuts(),
      focusFilter: () => latest.current.focusFilter(),
      toggleRail: () => latest.current.toggleRail(),
      go: (letter: string) => latest.current.go(letter),
    };
    let pending: number | undefined;
    const clear = () => {
      window.clearTimeout(pending);
      pending = undefined;
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        keys.palette();
        return;
      }
      if (event.metaKey || event.ctrlKey || event.altKey || event.defaultPrevented) return;
      if (keysBelongElsewhere(event.target)) {
        clear();
        return;
      }
      if (pending !== undefined) {
        clear();
        if (keys.go(event.key.toLowerCase())) event.preventDefault();
        return;
      }
      switch (event.key) {
        case '?':
          event.preventDefault();
          keys.shortcuts();
          break;
        case '/':
          if (keys.focusFilter()) event.preventDefault();
          break;
        case '[':
          keys.toggleRail();
          break;
        case 'g':
          pending = window.setTimeout(clear, SEQUENCE_MS);
          break;
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => {
      clear();
      document.removeEventListener('keydown', onKeyDown);
    };
  }, []);
}
