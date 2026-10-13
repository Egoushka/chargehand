import { useEffect, useMemo, useState } from 'react';
import { useIsPhone } from '../../shell/useIsPhone';
import { Badge, Diff, DiffFile, DiffLine, EmptyState } from '../../ui';
import { type DiffFileView, hunks, langOf, parseDiff } from './diff';
import { renderTokens } from './Highlighted';
import type { Token } from './highlight';
import './code.css';

/** Tokens by file index, then by row index within the file. */
type Coloured = Map<number, Map<number, Token[]>>;

/**
 * A unified diff as the design draws it: a sticky header per file with its line counts, a marker column so colour is never the only cue,
 * and syntax colour per hunk once the grammar for the file has loaded. `testPaths` are the files chargehand's test-integrity check named.
 */
export function DiffView({
  diff,
  label,
  testPaths = [],
}: {
  diff: string;
  label: string;
  testPaths?: readonly string[];
}) {
  const phone = useIsPhone();
  const files = useMemo(() => parseDiff(diff), [diff]);
  const [coloured, setColoured] = useState<Coloured>(new Map());

  useEffect(() => {
    let live = true;
    setColoured(new Map());
    import('./highlight').then(
      async ({ tokenize }) => {
        for (const [index, file] of files.entries()) {
          const lang = langOf(file.path);
          if (lang === 'text') continue;
          const rows = new Map<number, Token[]>();
          for (const hunk of hunks(file)) {
            const lines = await tokenize(hunk.rows.map((r) => r.text).join('\n'), lang);
            hunk.rows.forEach((row, n) => {
              const at = file.rows.indexOf(row);
              const tokens = lines[n];
              if (at >= 0 && tokens) rows.set(at, tokens);
            });
          }
          if (!live) return;
          setColoured((all) => new Map(all).set(index, rows));
        }
      },
      () => {},
    );
    return () => {
      live = false;
    };
  }, [files]);

  if (files.length === 0)
    return (
      <EmptyState
        icon="file"
        title="Nothing to show"
        text="The diff is empty or could not be read."
      />
    );
  return (
    <div className="stack stack--tight code-hl">
      {files.map((file, index) => (
        <FileDiff
          key={`${file.oldPath ?? ''}>${file.path}`}
          file={file}
          tokens={coloured.get(index)}
          wrap={phone}
          isTest={testPaths.includes(file.path)}
          label={`${label}: ${file.path}`}
        />
      ))}
    </div>
  );
}

function FileDiff({
  file,
  tokens,
  wrap,
  isTest,
  label,
}: {
  file: DiffFileView;
  tokens: Map<number, Token[]> | undefined;
  wrap: boolean;
  isTest: boolean;
  label: string;
}) {
  const flags = (
    <>
      {file.change !== 'modified' ? <Badge size="s" label={file.change} /> : null}
      {isTest ? <Badge size="s" tone="warn" label="test file" /> : null}
    </>
  );
  return (
    <Diff label={label}>
      <DiffFile path={file.path} added={file.added} removed={file.removed} flags={flags} />
      {file.rows.length === 0 ? (
        <DiffLine
          kind="fold"
          text={file.change === 'binary' ? 'Binary file: no lines to show' : 'No lines changed'}
        />
      ) : (
        file.rows.map((row, i) => (
          <DiffLine
            // biome-ignore lint/suspicious/noArrayIndexKey: rows of a diff are positional
            key={i}
            kind={row.kind === 'ctx' ? undefined : row.kind === 'note' ? 'fold' : row.kind}
            oldNo={row.oldNo}
            newNo={row.newNo}
            wrap={wrap}
            text={
              row.kind === 'ctx' || row.kind === 'add' || row.kind === 'del'
                ? renderTokens(tokens?.get(i), row.text)
                : row.text
            }
          />
        ))
      )}
    </Diff>
  );
}
