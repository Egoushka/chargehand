import type { ReactNode } from 'react';
import { cx } from './cx';
import { Icon } from './Icon';

export interface DiffLineProps {
  kind?: 'add' | 'del' | 'hunk' | 'fold';
  oldNo?: number | null;
  newNo?: number | null;
  /** The code, or markup that wraps changed words in `<mark>`. */
  text: ReactNode;
  wrap?: boolean;
}

/** One line of a unified diff. The marker column is always there, so colour is never the only cue. */
export function DiffLine({ kind, oldNo, newNo, text, wrap }: DiffLineProps) {
  return (
    <div className={cx('ch-dl', wrap && 'ch-dl--wrap')} data-k={kind}>
      <span className="ch-dl__n">{oldNo ?? ''}</span>
      <span className="ch-dl__n">{newNo ?? ''}</span>
      <span className="ch-dl__m" aria-hidden="true">
        {kind === 'add' ? '+' : kind === 'del' ? '−' : ''}
      </span>
      <span className="ch-dl__c">{text}</span>
    </div>
  );
}

/** The diff container: sunken, scrolling sideways. */
export function Diff({ children, label }: { children: ReactNode; label: string }) {
  return (
    // biome-ignore lint/a11y/useSemanticElements: a fieldset brings borders and sizing the diff does not want
    <div className="ch-diff" role="group" aria-label={label}>
      {children}
    </div>
  );
}

/** A file's sticky header in a diff: its path, `+added −removed`, and flags such as `test file`. */
export function DiffFile({
  path,
  added,
  removed,
  flags,
}: {
  path: string;
  added: number;
  removed: number;
  flags?: ReactNode;
}) {
  return (
    <div className="ch-diff__file">
      <Icon name="file" />
      <span className="ch-diff__path" title={path}>
        {path}
      </span>
      {flags}
      <span className="ch-diff__stat">
        <span className="a">+{added}</span>
        <span className="d">−{removed}</span>
      </span>
    </div>
  );
}
