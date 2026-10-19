import type { RunResult } from '../../api/types';
import { safeHttpUrl } from '../../lib/url';
import { EmptyState, KeyValue, Panel } from '../../ui';
import { ChangesView } from '../batch/ArtifactsTab';
import { CodeBlock } from '../code/CodeBlock';

interface RunArtifact {
  kind: string;
  media_type: string;
  sha256?: string;
  uri?: string | null;
  content?: string | null;
}

const parse = (text: string): unknown => {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
};

/** The artifacts a run's result carries. */
export function runArtifacts(result: RunResult): RunArtifact[] {
  return Array.isArray(result.artifacts) ? (result.artifacts as RunArtifact[]) : [];
}

/** `changes` as a diff, other inline content as JSON or text, a reference with its hash. */
export function Artifacts({ artifacts }: { artifacts: RunArtifact[] }) {
  return (
    <Panel title="Artifacts" count={artifacts.length}>
      {artifacts.length === 0 ? (
        <EmptyState icon="box" title="No artifact" text="This result carries none." />
      ) : (
        <div className="stack">
          {artifacts.map((a, i) => (
            // biome-ignore lint/suspicious/noArrayIndexKey: a result may carry two of a kind and has nothing else to tell them apart
            <Artifact key={`${a.kind}${i}`} artifact={a} />
          ))}
        </div>
      )}
    </Panel>
  );
}

function Artifact({ artifact: a }: { artifact: RunArtifact }) {
  const data = a.content != null ? parse(a.content) : undefined;
  const url = (data as { url?: unknown } | null | undefined)?.url;
  const link = typeof url === 'string' ? safeHttpUrl(url) : null;
  return (
    <div className="stack stack--tight">
      <div className="b">
        {a.kind} <span className="dim">{a.media_type}</span>
      </div>
      {a.kind === 'changes' && data ? (
        <ChangesView changes={data} verification={null} />
      ) : a.content != null ? (
        <>
          {link ? (
            <a href={link} target="_blank" rel="noreferrer noopener">
              {link}
            </a>
          ) : null}
          <CodeBlock
            code={data === undefined ? a.content : JSON.stringify(data, null, 2)}
            lang={data === undefined ? 'text' : 'json'}
            label={`${a.kind} artifact`}
          />
        </>
      ) : (
        <KeyValue
          ruled
          items={[
            ['Stored', safeHttpUrl(a.uri) ?? a.uri ?? 'in the result', true],
            ['sha256', a.sha256 ?? '—', true],
          ]}
        />
      )}
    </div>
  );
}
