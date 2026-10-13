/** The planned screens, in navigation order. Only Runs and Run are built so far; the rest are placeholders. */
export const SCREENS = [
  {
    to: '/',
    label: 'Overview',
    blurb: 'What the fleet is doing now, spend against caps, and what needs you.',
  },
  { to: '/runs', label: 'Runs', blurb: 'Every run, newest first, with filters.' },
  {
    to: '/needs-you',
    label: 'Needs you',
    blurb: 'Runs and tasks that wait for a person: questions, partial drafts, failures.',
  },
  { to: '/new', label: 'New run', blurb: 'Start a run or a batch.' },
  { to: '/analytics', label: 'Analytics', blurb: 'Cost, tokens and outcomes over time.' },
  {
    to: '/learning',
    label: 'Learning',
    blurb: 'What finished tasks taught: completion, spend and merge rates per repository.',
  },
  { to: '/intake', label: 'Intake', blurb: 'Issues and review comments that start runs.' },
  {
    to: '/repositories',
    label: 'Repositories',
    blurb: 'Repositories the server may work in, and their state.',
  },
  {
    to: '/accounts',
    label: 'Accounts',
    blurb: 'Who may call the server, their caps and their spend.',
  },
  { to: '/system', label: 'System', blurb: 'Server health, halt and resume, versions.' },
] as const;

export type ScreenPath = (typeof SCREENS)[number]['to'];
