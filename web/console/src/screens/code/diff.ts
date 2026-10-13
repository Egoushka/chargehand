// A unified diff (`git diff --no-color`) read into files and rows. The server cuts the diff at 32 KiB at a line, so the last hunk may end early;
// nothing here needs the diff to be whole.

export type Lang = 'csharp' | 'typescript' | 'json' | 'markdown' | 'yaml' | 'diff' | 'text';

const EXTENSIONS: Record<string, Lang> = {
  cs: 'csharp',
  csx: 'csharp',
  ts: 'typescript',
  tsx: 'typescript',
  mts: 'typescript',
  cts: 'typescript',
  js: 'typescript',
  jsx: 'typescript',
  mjs: 'typescript',
  json: 'json',
  jsonl: 'json',
  md: 'markdown',
  markdown: 'markdown',
  yml: 'yaml',
  yaml: 'yaml',
  diff: 'diff',
  patch: 'diff',
};

/** The grammar for a file, by its extension: the six the console ships (C#, TypeScript, JSON, Markdown, YAML, diff); anything else is plain. */
export function langOf(path: string): Lang {
  const ext = /\.([a-z0-9]+)$/i.exec(path)?.[1]?.toLowerCase();
  return (ext && EXTENSIONS[ext]) || 'text';
}

export type RowKind = 'ctx' | 'add' | 'del' | 'hunk' | 'note';

export interface DiffRow {
  kind: RowKind;
  oldNo: number | null;
  newNo: number | null;
  text: string;
}

export type FileChange = 'modified' | 'added' | 'deleted' | 'renamed' | 'binary';

export interface DiffFileView {
  path: string;
  oldPath: string | null;
  change: FileChange;
  added: number;
  removed: number;
  rows: DiffRow[];
}

const HUNK = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;

const stripPrefix = (path: string) => path.replace(/^"?[ab]\//, '').replace(/"$/, '');

/** `diff --git a/x b/y` names two paths; the second is the file as it is now. */
function headerPath(line: string): { from: string; to: string } {
  const rest = line.slice('diff --git '.length);
  const at = rest.lastIndexOf(' b/');
  if (at < 0) return { from: rest, to: rest };
  return { from: stripPrefix(rest.slice(0, at)), to: stripPrefix(rest.slice(at + 1)) };
}

export function parseDiff(text: string): DiffFileView[] {
  const files: DiffFileView[] = [];
  let file: DiffFileView | null = null;
  let oldNo = 0;
  let newNo = 0;
  let inHunk = false;
  const lines = text.split('\n');
  if (lines.at(-1) === '') lines.pop();
  for (const line of lines) {
    if (line.startsWith('diff --git ')) {
      const { from, to } = headerPath(line);
      file = {
        path: to,
        oldPath: from === to ? null : from,
        change: 'modified',
        added: 0,
        removed: 0,
        rows: [],
      };
      files.push(file);
      inHunk = false;
      continue;
    }
    if (!file) {
      // A patch with no git header: start a file when its first marker shows.
      if (line.startsWith('--- ') || line.startsWith('+++ ') || HUNK.test(line)) {
        file = { path: 'patch', oldPath: null, change: 'modified', added: 0, removed: 0, rows: [] };
        files.push(file);
      } else continue;
    }
    const hunk = HUNK.exec(line);
    if (hunk) {
      oldNo = Number(hunk[1]);
      newNo = Number(hunk[2]);
      inHunk = true;
      file.rows.push({ kind: 'hunk', oldNo: null, newNo: null, text: line });
      continue;
    }
    if (!inHunk) {
      if (line.startsWith('new file mode')) file.change = 'added';
      else if (line.startsWith('deleted file mode')) file.change = 'deleted';
      else if (line.startsWith('rename from ')) {
        file.change = 'renamed';
        file.oldPath = line.slice('rename from '.length);
      } else if (line.startsWith('rename to ')) file.path = line.slice('rename to '.length);
      else if (line.startsWith('Binary files') || line.startsWith('GIT binary patch'))
        file.change = 'binary';
      else if (line.startsWith('+++ ') && !line.startsWith('+++ /dev/null'))
        file.path = stripPrefix(line.slice(4));
      else if (line.startsWith('--- ') && file.change === 'deleted')
        file.path = stripPrefix(line.slice(4));
      continue;
    }
    if (line.startsWith('+')) {
      file.rows.push({ kind: 'add', oldNo: null, newNo: newNo++, text: line.slice(1) });
      file.added++;
    } else if (line.startsWith('-')) {
      file.rows.push({ kind: 'del', oldNo: oldNo++, newNo: null, text: line.slice(1) });
      file.removed++;
    } else if (line.startsWith('\\')) {
      file.rows.push({ kind: 'note', oldNo: null, newNo: null, text: line.slice(2) });
    } else {
      file.rows.push({ kind: 'ctx', oldNo: oldNo++, newNo: newNo++, text: line.slice(1) });
    }
  }
  return files;
}

/** The rows of a file grouped by hunk, as the lines of code between two hunk headers. */
export function hunks(file: DiffFileView): Array<{ start: number; rows: DiffRow[] }> {
  const out: Array<{ start: number; rows: DiffRow[] }> = [];
  file.rows.forEach((row, index) => {
    if (row.kind === 'hunk') out.push({ start: index + 1, rows: [] });
    else if (row.kind !== 'note') out.at(-1)?.rows.push(row);
  });
  return out;
}
