import { type CSSProperties, type ReactNode, useEffect, useState } from 'react';
import type { Lang } from './diff';
import type { Token } from './highlight';

/** The pieces of one line: a token's colour and weight are set through the style object model, which the page's policy allows. */
export function renderTokens(tokens: readonly Token[] | undefined, fallback: string): ReactNode {
  if (!tokens || tokens.length === 0) return fallback;
  return tokens.map((token, i) => {
    if (!token.color && !token.bold && !token.italic) return token.text;
    const style: CSSProperties = {
      color: token.color,
      fontWeight: token.bold ? 600 : undefined,
      fontStyle: token.italic ? 'italic' : undefined,
    };
    return (
      // biome-ignore lint/suspicious/noArrayIndexKey: the tokens of a line have no identity and the line is rendered whole
      <span key={i} style={style}>
        {token.text}
      </span>
    );
  });
}

/** Tokenizes `code` when it is shown, and again when it changes. Until the grammar has loaded (or if it never does) the lines are plain. */
export function useTokens(code: string, lang: Lang): Token[][] | null {
  const [tokens, setTokens] = useState<Token[][] | null>(null);
  useEffect(() => {
    let live = true;
    setTokens(null);
    if (lang === 'text') return;
    import('./highlight').then(
      (m) => m.tokenize(code, lang).then((t) => live && setTokens(t)),
      () => {},
    );
    return () => {
      live = false;
    };
  }, [code, lang]);
  return tokens;
}
