import { cx } from '../../ui';
import type { Lang } from './diff';
import { renderTokens, useTokens } from './Highlighted';
import './code.css';

/** A block of JSON, Markdown, YAML or code in the `code` frame, coloured when its grammar has loaded. */
export function CodeBlock({
  code,
  lang,
  label,
  className,
}: {
  code: string;
  lang: Lang;
  label: string;
  className?: string;
}) {
  const tokens = useTokens(code, lang);
  const lines = code.split('\n');
  return (
    // biome-ignore lint/a11y/noNoninteractiveTabindex: a code block that scrolls sideways must be reachable by keyboard
    // biome-ignore lint/a11y/useSemanticElements: a fieldset brings borders and sizing a code block does not want
    <pre className={cx('code code-hl', className)} tabIndex={0} role="group" aria-label={label}>
      <code>
        {lines.map((line, i) => (
          // biome-ignore lint/suspicious/noArrayIndexKey: lines of a block are positional
          <span key={i} className="code-hl__line">
            {renderTokens(tokens?.[i], line)}
            {'\n'}
          </span>
        ))}
      </code>
    </pre>
  );
}
