import { useQuery } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { analyticsQuery } from '../../api/analytics';
import type { RepositoryBody } from '../../api/repositories';
import { repositoriesQuery } from '../../api/repositories';
import { DASH, formatPercent } from '../../lib/format';
import { safeHttpUrl } from '../../lib/url';
import {
  Badge,
  Banner,
  Button,
  cssVars,
  EmptyState,
  ErrorState,
  Icon,
  KeyValue,
  Panel,
  Skeleton,
  Sparkline,
  StatusPill,
} from '../../ui';
import { lastSeen, NotRecorded, Planned } from '../admin/parts';
import { ConflictRadar } from './ConflictRadar';
import { MergeTrain } from './MergeTrain';
import {
  CHECK_LABEL,
  CHECK_STATUS,
  checkoutChecks,
  type History,
  prLabel,
  repoHistory,
  rootLabel,
  shortHead,
  testCommands,
} from './model';
import './repositories.css';

/** The checkouts the server may work in: what is detected in each, how it reads, the pull requests chargehand has open, and how its tasks ended. */
export function Repositories() {
  const repositories = useQuery(repositoriesQuery());
  const analytics = useQuery(
    analyticsQuery({ since: '70d', bucket: 'week', group_by: 'repository' }),
  );
  const data = repositories.data;
  const missing = data?.roots.filter((root) => !root.exists) ?? [];
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Repositories</h1>
          <p className="screen__sub">
            Checkouts under repository_roots on the server, with what chargehand detected and how
            their tasks ended.
          </p>
        </div>
        <div className="screen__actions">
          <Planned>
            <Button label="Run doctor on all" icon="shield-check" disabled />
          </Planned>
        </div>
      </div>

      {repositories.isError ? (
        <ErrorState
          title="Cannot read the checkouts"
          call="GET /v1/repositories"
          detail={repositories.error.message}
          age={data ? lastSeen(repositories.dataUpdatedAt) : undefined}
          onRetry={() => void repositories.refetch()}
        />
      ) : null}

      {missing.map((root) => (
        <Banner
          key={root.path}
          tone="warn"
          icon="alert-triangle"
          title="A repository root is missing"
        >
          <span className="mono">{rootLabel(root.path)}</span> is in repository_roots and is not a
          directory on the server.
        </Banner>
      ))}

      {!data && !repositories.isError ? (
        <div className="g3" aria-busy="true">
          {[1, 2, 3].map((n) => (
            <Panel key={n} title="Repository" busy>
              <div className="stack">
                <Skeleton width="50%" />
                <Skeleton width="80%" />
                <Skeleton width="60%" />
                <Skeleton width="90%" />
              </div>
            </Panel>
          ))}
        </div>
      ) : null}

      {data && data.repositories.length === 0 ? (
        <Panel title="Repositories">
          <EmptyState
            icon="folder"
            title="No repository to show"
            text={
              <>
                No checkout under repository_roots, or none that this key may use. Clone one into a
                root the server may read, then run <span className="mono">chargehand init</span> on
                it to detect its ecosystem and test command.
              </>
            }
          />
        </Panel>
      ) : null}

      {data && data.repositories.length > 0 ? (
        <>
          {analytics.isError ? (
            <ErrorState
              title="Cannot read the success history"
              call="GET /v1/analytics"
              detail={analytics.error.message}
              onRetry={() => void analytics.refetch()}
            />
          ) : null}
          <div className="g2e">
            <ConflictRadar />
            <MergeTrain />
          </div>
          <div className="g3">
            {data.repositories.map((repo) => (
              <RepositoryCard
                key={repo.path}
                repo={repo}
                history={
                  analytics.data ? repoHistory(analytics.data.tasks.points, repo) : undefined
                }
                historyFailed={analytics.isError}
              />
            ))}
          </div>
        </>
      ) : null}

      {data ? <NotRecorded lines={data.not_recorded} /> : null}
    </div>
  );
}

function RepositoryCard({
  repo,
  history,
  historyFailed,
}: {
  repo: RepositoryBody;
  history?: History;
  historyFailed: boolean;
}) {
  const [open, setOpen] = useState(false);
  const listId = useId();
  const commands = testCommands(repo);
  const checks = checkoutChecks(repo);
  return (
    <Panel
      title={repo.name}
      flush
      className="repo-card"
      aside={
        <>
          <Badge size="s" mode={repo.integrator_mode} />
          {repo.watched_by_intake ? <Badge size="s" tone="info" label="watched by intake" /> : null}
        </>
      }
    >
      <div className="stack pad">
        <KeyValue
          stacked
          items={[
            [
              'Default branch and HEAD',
              repo.default_branch || repo.head
                ? `${repo.default_branch ?? DASH} @ ${shortHead(repo.head) ?? DASH}`
                : DASH,
              true,
            ],
            [
              'Remote',
              repo.repository ? (
                <span key="remote" title={repo.remote ?? undefined}>
                  {repo.repository}
                </span>
              ) : (
                (repo.remote ?? DASH)
              ),
              true,
            ],
            [
              'Checkout',
              <span key="path" className="dim">
                {repo.path}
              </span>,
              true,
            ],
            [
              'Detected',
              repo.ecosystems.length ? (
                <span key="eco" className="row row--wrap">
                  {repo.ecosystems.map((eco) => (
                    <Badge
                      key={eco.name}
                      size="s"
                      mono
                      label={eco.name}
                      title={eco.markers.join(', ')}
                    />
                  ))}
                </span>
              ) : (
                <span key="eco" className="dim">
                  nothing detected
                </span>
              ),
            ],
            [
              'Test command',
              commands.best ? (
                <span key="cmd">
                  <span className="mono">{commands.best}</span>
                  {repo.verify ? <span className="dim"> from {repo.verify.source}</span> : null}
                  {commands.others.length ? (
                    <span className="dim small repo-others">
                      also found: {commands.others.join(' · ')}
                    </span>
                  ) : null}
                </span>
              ) : (
                <span key="cmd" className="dim">
                  none proposed
                </span>
              ),
            ],
          ]}
        />

        <div>
          <div className="label-caps repo-label">Checks from this read</div>
          <ul className="list-reset">
            {checks.map((check) => (
              <li key={check.text} className="repo-check">
                <span
                  className="ch-pill ch-pill--dot"
                  data-status={CHECK_STATUS[check.level]}
                  role="img"
                  aria-label={CHECK_LABEL[check.level]}
                />
                <span>{check.text}</span>
              </li>
            ))}
          </ul>
          <p className="dim small repo-doctor">
            <span className="mono">chargehand doctor</span>: result not recorded. The server does
            not run or keep it.
          </p>
        </div>

        <div className="row row--between">
          <div className="stack stack--tight repo-history">
            <span className="dim small">Success, last 10 weeks</span>
            {history ? (
              history.rate == null ? (
                <span className="dim">No finished task</span>
              ) : (
                <span className="b">
                  {formatPercent(history.rate)} of {history.tasks}{' '}
                  {history.tasks === 1 ? 'task' : 'tasks'}
                </span>
              )
            ) : historyFailed ? (
              <span className="dim">Not loaded</span>
            ) : (
              <Skeleton width="70%" />
            )}
          </div>
          {history && history.weeks.length > 0 ? (
            <Sparkline
              values={history.weeks}
              width={96}
              height={28}
              tone="ok"
              label={`Share of tasks completed, per week: ${history.weeks.map((w) => `${w}%`).join(', ')}`}
            />
          ) : null}
        </div>
      </div>

      <div className="repo-foot sep-top">
        <button
          type="button"
          className="link"
          aria-expanded={open}
          aria-controls={listId}
          onClick={() => setOpen((value) => !value)}
          disabled={repo.open_pull_requests.length === 0}
        >
          <Icon name={open ? 'chevron-down' : 'chevron-right'} />
          {repo.open_pull_requests.length === 0
            ? 'No open PR'
            : `${repo.open_pull_requests.length} open ${repo.open_pull_requests.length === 1 ? 'PR' : 'PRs'}`}
        </button>
      </div>
      {open ? (
        <ul id={listId} className="list-reset sep-top">
          {repo.open_pull_requests.map((pr) => {
            const url = safeHttpUrl(pr.url);
            const label = prLabel(pr);
            const title = pr.number != null ? `#${pr.number}` : pr.task_id;
            return (
              <li key={pr.url} className="li" style={cssVars({ '--cols': 'minmax(0,1fr) auto' })}>
                <div className="li__main">
                  <div className="b ch-trunc">
                    {url ? (
                      <a href={url} target="_blank" rel="noreferrer noopener">
                        {title}
                      </a>
                    ) : (
                      title
                    )}
                  </div>
                  <div className="li__meta mono" title={pr.task_id}>
                    {pr.task_id}
                  </div>
                </div>
                <span className="row">
                  {pr.test_integrity_flagged ? (
                    <Badge size="s" tone="warn" label="test-integrity flagged" />
                  ) : null}
                  <StatusPill status={label.status} label={label.text} bare />
                </span>
              </li>
            );
          })}
        </ul>
      ) : null}
    </Panel>
  );
}
