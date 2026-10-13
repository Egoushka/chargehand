import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { runsQuery } from '../api/queries';
import { baseName, DASH, formatCount, formatUsd, formatWhen } from '../lib/format';
import { safeHttpUrl } from '../lib/url';

/** The runs from GET /v1/runs as a plain table, newest first. */
export function Runs() {
  const runs = useQuery(runsQuery());
  return (
    <section>
      <h1>Runs</h1>
      {runs.isPending ? <p className="muted">Loading…</p> : null}
      {runs.error ? (
        <p role="alert" className="notice error">
          {runs.error.message}
        </p>
      ) : null}
      {runs.data ? (
        runs.data.length === 0 ? (
          <p className="muted">No runs yet.</p>
        ) : (
          <div className="scroll">
            <table>
              <thead>
                <tr>
                  {[
                    'Started',
                    'Run',
                    'State',
                    'Kind',
                    'Preset',
                    'Repository',
                    'Actor',
                    'Cost',
                    'Tokens',
                    'Branch',
                    'PR',
                  ].map((name) => (
                    <th key={name} scope="col">
                      {name}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {runs.data.map((run) => {
                  const pr = safeHttpUrl(run.pr_url);
                  return (
                    <tr key={run.run_id}>
                      <td>{formatWhen(run.started_at)}</td>
                      <td>
                        <Link to="/runs/$runId" params={{ runId: run.run_id }} className="mono">
                          {run.run_id}
                        </Link>
                      </td>
                      <td>
                        {run.status}
                        {run.error_code ? <div className="muted">{run.error_code}</div> : null}
                      </td>
                      <td>{run.kind ?? DASH}</td>
                      <td>{run.preset}</td>
                      <td title={run.repository ?? ''}>{baseName(run.repository)}</td>
                      <td>{run.actor ?? DASH}</td>
                      <td className="num">{formatUsd(run.usd)}</td>
                      <td className="num">{formatCount(run.tokens)}</td>
                      <td className="mono">{run.branch ?? ''}</td>
                      <td>
                        {pr ? (
                          <a href={pr} target="_blank" rel="noreferrer noopener">
                            PR
                          </a>
                        ) : (
                          ''
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )
      ) : null}
    </section>
  );
}
