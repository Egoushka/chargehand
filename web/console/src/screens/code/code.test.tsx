import { render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { DIFF } from '../../testing/fixtures-runs';
import { CodeBlock } from './CodeBlock';
import { DiffView } from './DiffView';
import { hunks, langOf, parseDiff } from './diff';
import { renderTokens } from './Highlighted';
import { MAX_HIGHLIGHT, tokenize } from './highlight';

describe('the language of a file', () => {
  it('goes by the extension, and is plain for one the console has no grammar for', () => {
    expect(langOf('src/A.cs')).toBe('csharp');
    expect(langOf('web/app.TSX')).toBe('typescript');
    expect(langOf('a/b.mjs')).toBe('typescript');
    expect(langOf('package.json')).toBe('json');
    expect(langOf('docs/guide.md')).toBe('markdown');
    expect(langOf('ci.yml')).toBe('yaml');
    expect(langOf('fix.patch')).toBe('diff');
    expect(langOf('main.rs')).toBe('text');
    expect(langOf('Makefile')).toBe('text');
  });
});

describe('a unified diff', () => {
  const files = parseDiff(DIFF);

  it('is read into files with their kind of change and their line counts', () => {
    expect(files.map((f) => [f.path, f.change, f.added, f.removed])).toEqual([
      ['src/Payments/RetryPolicy.cs', 'modified', 4, 1],
      ['docs/guide/retries.md', 'modified', 1, 0],
      ['tests/Payments/RetryPolicyTests.cs', 'added', 5, 0],
    ]);
  });

  it('numbers the lines of each side, and marks context, additions and removals', () => {
    const rows = files[0]?.rows ?? [];
    expect(rows[0]).toMatchObject({ kind: 'hunk' });
    expect(rows[1]).toMatchObject({ kind: 'ctx', oldNo: 10, newNo: 10, text: '{' });
    const removed = rows.find((r) => r.kind === 'del');
    expect(removed).toMatchObject({ oldNo: 13, newNo: null });
    const added = rows.filter((r) => r.kind === 'add');
    expect(added.map((r) => r.newNo)).toEqual([13, 14, 15, 16]);
    expect(hunks(files[0] as never)).toHaveLength(1);
    expect(hunks(files[0] as never)[0]?.rows.length).toBe(rows.length - 1);
  });

  it('reads a deleted file, a rename, a binary file and a note about the last newline', () => {
    const parsed = parseDiff(
      [
        'diff --git a/old.cs b/old.cs',
        'deleted file mode 100644',
        '--- a/old.cs',
        '+++ /dev/null',
        '@@ -1,2 +0,0 @@',
        '-a',
        '-b',
        'diff --git a/x.cs b/y.cs',
        'similarity index 100%',
        'rename from x.cs',
        'rename to y.cs',
        'diff --git a/img.png b/img.png',
        'Binary files a/img.png and b/img.png differ',
        'diff --git a/n.txt b/n.txt',
        '--- a/n.txt',
        '+++ b/n.txt',
        '@@ -1 +1 @@',
        '-one',
        '\\ No newline at end of file',
        '+two',
      ].join('\n'),
    );
    expect(parsed.map((f) => [f.path, f.change, f.oldPath])).toEqual([
      ['old.cs', 'deleted', null],
      ['y.cs', 'renamed', 'x.cs'],
      ['img.png', 'binary', null],
      ['n.txt', 'modified', null],
    ]);
    expect(parsed[0]?.removed).toBe(2);
    expect(parsed[3]?.rows.map((r) => r.kind)).toEqual(['hunk', 'del', 'note', 'add']);
    expect(hunks(parsed[3] as never)[0]?.rows.map((r) => r.kind)).toEqual(['del', 'add']);
  });

  it('reads a patch without a git header, a diff cut at a line, and an empty text', () => {
    const plain = parseDiff('--- a/p.cs\n+++ b/p.cs\n@@ -1 +1 @@\n-a\n+b\n');
    expect(plain).toHaveLength(1);
    expect(plain[0]?.path).toBe('p.cs');
    expect(plain[0]?.added).toBe(1);
    const cut = parseDiff(DIFF.split('\n').slice(0, 14).join('\n'));
    expect(cut).toHaveLength(1);
    expect(parseDiff('')).toEqual([]);
    expect(parseDiff('nothing like a diff')).toEqual([]);
  });
});

describe('the colour of code', () => {
  it('gives each line of C# its tokens, with a colour that is a token of the theme, never a literal', async () => {
    const lines = await tokenize('public int N => 1;\nvar s = "x";', 'csharp');
    expect(lines).toHaveLength(2);
    const colours = lines.flat().flatMap((t) => (t.color ? [t.color] : []));
    expect(colours.length).toBeGreaterThan(0);
    expect(colours.every((c) => /^var\(--code-/.test(c))).toBe(true);
    expect(lines[0]?.map((t) => t.text).join('')).toBe('public int N => 1;');
  });

  it('colours JSON, and loads each grammar once', async () => {
    const a = await tokenize('{"a": 1}', 'json');
    const b = await tokenize('[1, 2]', 'json');
    expect(a[0]?.some((t) => t.color)).toBe(true);
    expect(b[0]?.map((t) => t.text).join('')).toBe('[1, 2]');
  });

  it('leaves text, and a block too large to colour, plain', async () => {
    expect(await tokenize('a\nb', 'text')).toEqual([[{ text: 'a' }], [{ text: 'b' }]]);
    const big = 'x'.repeat(MAX_HIGHLIGHT + 1);
    const lines = await tokenize(big, 'json');
    expect(lines).toEqual([[{ text: big }]]);
  });

  it('writes a colour and a weight through the style object, and an uncoloured piece as text', () => {
    const { container } = render(
      <p>
        {renderTokens(
          [
            { text: 'a', color: 'var(--code-keyword)', bold: true },
            { text: 'b' },
            { text: 'c', italic: true },
          ],
          'fallback',
        )}
      </p>,
    );
    const spans = container.querySelectorAll('span');
    expect(spans).toHaveLength(2);
    expect((spans[0] as HTMLElement).style.color).toBe('var(--code-keyword)');
    expect((spans[0] as HTMLElement).style.fontWeight).toBe('600');
    expect((spans[1] as HTMLElement).style.fontStyle).toBe('italic');
    expect(container.textContent).toBe('abc');
    expect(render(<p>{renderTokens(undefined, 'fallback')}</p>).container.textContent).toBe(
      'fallback',
    );
  });
});

describe('a block of code', () => {
  it('shows every line at once, and is reachable by keyboard', () => {
    render(<CodeBlock code={'{\n  "a": 1\n}'} lang="json" label="The request" />);
    const block = screen.getByRole('group', { name: 'The request' });
    expect(block.getAttribute('tabindex')).toBe('0');
    expect(block.querySelectorAll('.code-hl__line')).toHaveLength(3);
    expect(block.textContent).toBe('{\n  "a": 1\n}\n');
  });

  it('keeps the text the same once the colour arrives', async () => {
    render(<CodeBlock code={'{"a": 1}'} lang="json" label="Colourful" />);
    const block = screen.getByRole('group', { name: 'Colourful' });
    await waitFor(() => expect(block.querySelector('span span')).toBeTruthy());
    expect(block.textContent).toBe('{"a": 1}\n');
  });
});

describe('the diff viewer', () => {
  it('shows a header per file with its counts, a marker per changed line, and the line numbers', () => {
    render(<DiffView diff={DIFF} label="Changes" />);
    const first = screen.getByRole('group', { name: 'Changes: src/Payments/RetryPolicy.cs' });
    expect(within(first).getByText('src/Payments/RetryPolicy.cs')).toBeTruthy();
    expect(within(first).getByText('+4')).toBeTruthy();
    expect(within(first).getByText('−1')).toBeTruthy();
    const marks = [...first.querySelectorAll('.ch-dl')].map((l) => l.getAttribute('data-k'));
    expect(marks.filter((m) => m === 'add')).toHaveLength(4);
    expect(marks.filter((m) => m === 'del')).toHaveLength(1);
    expect(first.querySelector('[data-k="add"] .ch-dl__m')?.textContent).toBe('+');
    expect(first.querySelector('[data-k="del"] .ch-dl__m')?.textContent).toBe('−');
    const added = screen.getByRole('group', {
      name: 'Changes: tests/Payments/RetryPolicyTests.cs',
    });
    expect(within(added).getByText('added')).toBeTruthy();
  });

  it('flags a file the test-integrity check named', () => {
    render(
      <DiffView diff={DIFF} label="Changes" testPaths={['tests/Payments/RetryPolicyTests.cs']} />,
    );
    const flagged = screen.getByRole('group', {
      name: 'Changes: tests/Payments/RetryPolicyTests.cs',
    });
    expect(within(flagged).getByText('test file')).toBeTruthy();
    expect(
      within(screen.getByRole('group', { name: 'Changes: docs/guide/retries.md' })).queryByText(
        'test file',
      ),
    ).toBeNull();
  });

  it('colours the code once the grammar for its file has loaded', async () => {
    render(<DiffView diff={DIFF} label="Changes" />);
    const first = screen.getByRole('group', { name: 'Changes: src/Payments/RetryPolicy.cs' });
    await waitFor(() => expect(first.querySelector('.ch-dl__c span')).toBeTruthy());
    expect(first.textContent).toContain('Math.Min(30, Math.Pow(2, attempt))');
  });

  it('says a binary file has no lines, and an empty diff has nothing', () => {
    const { unmount } = render(
      <DiffView
        diff={'diff --git a/i.png b/i.png\nBinary files a/i.png and b/i.png differ\n'}
        label="Changes"
      />,
    );
    expect(screen.getByText('Binary file: no lines to show')).toBeTruthy();
    unmount();
    render(<DiffView diff="" label="Changes" />);
    expect(screen.getByText('Nothing to show')).toBeTruthy();
  });
});
