export type ShortcutGroup = 'Anywhere' | 'Go to' | 'Lists' | 'On a task';

export interface ShortcutRow {
  group: ShortcutGroup;
  /** One entry per key; a sequence is its keys in order. */
  keys: string[];
  label: string;
}

/**
 * What the `?` sheet lists. A screen that adds a shortcut adds its row here, so the sheet never promises a key that does nothing.
 * Cancel and halt have no bare shortcut: they go through the palette and a confirm dialog.
 */
export const SHORTCUTS: readonly ShortcutRow[] = [
  { group: 'Anywhere', keys: ['⌘', 'K'], label: 'Command palette' },
  { group: 'Anywhere', keys: ['?'], label: 'This sheet' },
  { group: 'Anywhere', keys: ['/'], label: 'Focus the filter' },
  { group: 'Anywhere', keys: ['['], label: 'Collapse the rail' },
  { group: 'Anywhere', keys: ['esc'], label: 'Close a layer' },
  { group: 'Go to', keys: ['g', 'o'], label: 'Overview' },
  { group: 'Go to', keys: ['g', 'n'], label: 'Needs you' },
  { group: 'Go to', keys: ['g', 'r'], label: 'Runs' },
  { group: 'Go to', keys: ['g', 's'], label: 'Start a batch' },
  { group: 'Go to', keys: ['g', 'a'], label: 'Analytics' },
  { group: 'Lists', keys: ['j', 'k'], label: 'Move the cursor' },
  { group: 'Lists', keys: ['↵'], label: 'Open' },
  { group: 'On a task', keys: ['a'], label: 'Answer the question' },
];
