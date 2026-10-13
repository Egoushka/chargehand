// Syntax colour for code, by Shiki, loaded only when a diff or a block of JSON is shown, and only the grammars that file needs.
//
// Two choices come from the page's Content-Security-Policy (script-src 'self', style-src 'self'):
//  - the regular-expression engine is Shiki's JavaScript one: the default engine compiles WebAssembly, which the policy forbids;
//  - the theme is Shiki's CSS-variables theme, and the colours are tokens (`--code-token-*` in code.css), so no colour is written into the
//    markup and the same tokens follow the dark and the light theme.
import type { HighlighterCore, LanguageInput } from 'shiki/core';
import type { Lang } from './diff';

export interface Token {
  text: string;
  /** A CSS value, `var(--code-token-keyword)` and the like. */
  color?: string;
  bold?: boolean;
  italic?: boolean;
}

const THEME = 'console';
const FONT_ITALIC = 1;
const FONT_BOLD = 2;

const GRAMMARS: Record<Exclude<Lang, 'text'>, LanguageInput> = {
  csharp: () => import('shiki/langs/csharp.mjs'),
  typescript: () => import('shiki/langs/typescript.mjs'),
  json: () => import('shiki/langs/json.mjs'),
  markdown: () => import('shiki/langs/markdown.mjs'),
  yaml: () => import('shiki/langs/yaml.mjs'),
  diff: () => import('shiki/langs/diff.mjs'),
};

let core: Promise<HighlighterCore> | null = null;
const loaded = new Map<string, Promise<void>>();

function highlighter(): Promise<HighlighterCore> {
  core ??= (async () => {
    const [shiki, engine] = await Promise.all([
      import('shiki/core'),
      import('shiki/engine/javascript'),
    ]);
    return shiki.createHighlighterCore({
      themes: [
        shiki.createCssVariablesTheme({
          name: THEME,
          variablePrefix: '--code-',
          variableDefaults: {},
          fontStyle: true,
        }),
      ],
      langs: [],
      engine: engine.createJavaScriptRegexEngine(),
    });
  })();
  return core;
}

/** Past this size a block is shown plain: tokenizing it would stall the page for a colour. */
export const MAX_HIGHLIGHT = 200_000;

const plain = (code: string): Token[][] =>
  code
    .split('\n')
    .map((text) => ({ text }))
    .map((t) => [t]);

/** The tokens of each line of `code`. An unknown language, a block that is too large, or a grammar that fails to load comes back plain. */
export async function tokenize(code: string, lang: Lang): Promise<Token[][]> {
  if (lang === 'text' || code.length > MAX_HIGHLIGHT) return plain(code);
  try {
    const h = await highlighter();
    if (!loaded.has(lang)) loaded.set(lang, h.loadLanguage(GRAMMARS[lang]));
    await loaded.get(lang);
    const lines = h.codeToTokensBase(code, { lang, theme: THEME });
    return lines.map((line) =>
      line.map((t) => ({
        text: t.content,
        color: t.color,
        bold: ((t.fontStyle ?? 0) & FONT_BOLD) !== 0 || undefined,
        italic: ((t.fontStyle ?? 0) & FONT_ITALIC) !== 0 || undefined,
      })),
    );
  } catch {
    loaded.delete(lang);
    return plain(code);
  }
}
