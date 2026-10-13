import { useId } from 'react';
import { Badge, Button, EmptyState, Icon, Panel, Textarea } from '../../ui';
import type { Issue } from './model';

const CURL = `curl -sS -X POST "$CHARGEHAND_URL/v1/runs" \\
  -H "Authorization: Bearer $CHARGEHAND_API_KEY" \\
  -H "Prefer: wait=0" \\
  --data-binary @request.json`;

export interface JsonViewProps {
  text: string;
  /** The parser's complaint about the text, or null when it parses. */
  error: string | null;
  issues: Issue[];
  onChange: (text: string) => void;
  onCopy: () => void;
}

/** The request as request/v1 JSON. It is the form's twin: what is typed here fills the form, and what is typed there shows here. */
export function JsonView({ text, error, issues, onChange, onCopy }: JsonViewProps) {
  const noteId = useId();
  return (
    <div className="start-split start-split--json">
      <div className="stack stack--tight">
        <div className="row row--wrap">
          <span className="b">request/v1</span>
          <span className="dim">Edit here; the form follows when the JSON parses.</span>
          <span className="spacer" />
          <Button label="Copy JSON" size="sm" icon="copy" onClick={onCopy} />
        </div>
        <Textarea
          mono
          className="start-json"
          aria-label="Request JSON"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? noteId : undefined}
          value={text}
          rows={28}
          wrap="off"
          spellCheck={false}
          autoCapitalize="off"
          autoCorrect="off"
          data-field="json"
          onChange={(e) => onChange(e.target.value)}
        />
        {error ? (
          <div id={noteId} className="ch-error" role="alert">
            {error.replace(/\.?$/, '.')} The form keeps the last JSON that parsed.
          </div>
        ) : null}
      </div>
      <div className="stack">
        <Panel title="Checks" count={issues.length}>
          {issues.length === 0 ? (
            <EmptyState
              icon="check"
              title="No problems found"
              text="The request matches request/v1 and the rules the server adds."
            />
          ) : (
            <ul className="start-issues list-reset">
              {issues.map((issue) => (
                <li key={`${issue.path}|${issue.message}`}>
                  <Icon name={issue.level === 'error' ? 'alert-circle' : 'alert-triangle'} />
                  <span>
                    {issue.message}{' '}
                    <Badge
                      size="s"
                      mono
                      label={issue.path}
                      tone={issue.level === 'error' ? 'danger' : 'warn'}
                    />
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
        <Panel title="The same request from the shell">
          <div className="stack stack--tight">
            {/* biome-ignore lint/a11y/useSemanticElements: a command is not a form group; the role only names the box */}
            {/* biome-ignore lint/a11y/noNoninteractiveTabindex: a command that scrolls sideways has to take the keyboard's focus */}
            <pre className="start-curl" role="group" tabIndex={0} aria-label="curl command">
              {CURL}
            </pre>
            <p className="dim small">
              Save the JSON as <span className="mono">request.json</span>. Then follow the batch at{' '}
              <span className="mono">GET /v1/runs/&lt;run-id&gt;/events</span>.
            </p>
          </div>
        </Panel>
      </div>
    </div>
  );
}
