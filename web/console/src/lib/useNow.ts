import { useEffect, useState } from 'react';

/** The time now in milliseconds, renewed every `everyMs`: for a figure that grows while the screen is open (a gantt's "now", an elapsed time). */
export function useNow(everyMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), everyMs);
    return () => window.clearInterval(timer);
  }, [everyMs]);
  return now;
}
