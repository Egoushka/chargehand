import { useEffect, useState } from 'react';
import { formatDuration } from '../../lib/format';

/** How long a run has been going, ticking each second. */
export function Elapsed({ since }: { since: string }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);
  return <>{formatDuration((now - Date.parse(since)) / 1000)}</>;
}
