import type { IconName } from '../ui';

export interface Screen {
  to:
    | '/'
    | '/needs-you'
    | '/runs'
    | '/new'
    | '/analytics'
    | '/learning'
    | '/intake'
    | '/repositories'
    | '/accounts'
    | '/system';
  label: string;
  /** The short name on the phone's tab bar. */
  short?: string;
  icon: IconName;
  group: 'main' | 'insight' | 'configure';
  /** The letter after `g` that goes there. */
  go?: string;
  /** The server answers these `403` to an account: the rail leaves them out for one. */
  ownerOnly?: boolean;
  blurb: string;
}

/** The screens in navigation order. A screen's code lives in `src/screens/<name>/`; this list is the one place its entry is registered. */
export const SCREENS: readonly Screen[] = [
  {
    to: '/',
    label: 'Overview',
    icon: 'grid',
    group: 'main',
    go: 'o',
    blurb: 'What the fleet is doing now, spend against caps, and what needs you.',
  },
  {
    to: '/needs-you',
    label: 'Needs you',
    icon: 'inbox',
    group: 'main',
    go: 'n',
    blurb: 'Questions to answer, pull requests to merge, failures.',
  },
  {
    to: '/runs',
    label: 'Runs',
    icon: 'list',
    group: 'main',
    go: 'r',
    blurb: 'Every run, newest first, with filters.',
  },
  {
    to: '/new',
    label: 'Start a batch',
    short: 'Start',
    icon: 'play',
    group: 'main',
    go: 's',
    blurb: 'Start a run or a batch.',
  },
  {
    to: '/analytics',
    label: 'Analytics',
    icon: 'chart',
    group: 'insight',
    go: 'a',
    blurb: 'Cost, tokens and outcomes over time.',
  },
  {
    to: '/learning',
    label: 'Learning',
    icon: 'book',
    group: 'insight',
    blurb: 'What finished tasks taught: completion, spend and merge rates per repository.',
  },
  {
    to: '/intake',
    label: 'Intake and automation',
    icon: 'bolt',
    group: 'configure',
    ownerOnly: true,
    blurb: 'Issues and review comments that start runs.',
  },
  {
    to: '/repositories',
    label: 'Repositories',
    icon: 'folder',
    group: 'configure',
    blurb: 'Repositories the server may work in, and their state.',
  },
  {
    to: '/accounts',
    label: 'Accounts and keys',
    icon: 'key',
    group: 'configure',
    ownerOnly: true,
    blurb: 'Who may call the server, their caps and their spend.',
  },
  {
    to: '/system',
    label: 'System',
    icon: 'server',
    group: 'configure',
    ownerOnly: true,
    blurb: 'Server health, halt and resume, versions.',
  },
];

export type ScreenPath = Screen['to'];

export const GROUP_LABELS: Record<Screen['group'], string | null> = {
  main: null,
  insight: 'Insight',
  configure: 'Configure',
};

/** The phone's tab bar shows these four, then More. */
export const TAB_BAR: readonly ScreenPath[] = ['/', '/needs-you', '/runs', '/new'];
