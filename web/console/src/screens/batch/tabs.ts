// The batch's sections. Apart from the model so the router can read a `tab` in the address without loading the batch screen.

export type TabId =
  | 'tasks'
  | 'timeline'
  | 'events'
  | 'governor'
  | 'spend'
  | 'prs'
  | 'artifacts'
  | 'request';

export const TABS: ReadonlyArray<{ id: TabId; label: string }> = [
  { id: 'tasks', label: 'Tasks' },
  { id: 'timeline', label: 'Timeline' },
  { id: 'events', label: 'Events' },
  { id: 'governor', label: 'Governor' },
  { id: 'spend', label: 'Spend' },
  { id: 'prs', label: 'Pull requests' },
  { id: 'artifacts', label: 'Artifacts' },
  { id: 'request', label: 'Request' },
];

export const isTab = (value: unknown): value is TabId => TABS.some((tab) => tab.id === value);
