import { useQuery } from '@tanstack/react-query';
import { runsQuery } from '../../api/queries';
import { formatTime } from '../../lib/format';
import { Panel } from '../../ui';
import { latestUsage, usageWindows } from '../runs/model';

const WORDS: Record<string, string> = { '5h': '5 hours', '7d': '7 days' };

/** Where the account stands in its subscription windows, as the newest finished run last saw them. */
export function SubscriptionWindows() {
  const runs = useQuery(runsQuery({ limit: 50 }));
  if (!runs.data) return null;
  const windows = usageWindows(latestUsage(runs.data)?.subscription_usage);
  return (
    <Panel title="Subscription windows">
      {windows.length === 0 ? (
        <div className="pad dim">No subscription windows</div>
      ) : (
        <ul className="fleet-list" aria-label="Subscription windows">
          {windows.map((w) => (
            <li key={w.name} className="fleet-row">
              <span>{WORDS[w.name] ?? w.name}</span>
              <span>{w.percent}% used</span>
              <span className="dim">{w.resetsAt ? `resets ${formatTime(w.resetsAt)}` : ''}</span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
