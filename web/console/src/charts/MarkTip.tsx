import type { ReactElement } from 'react';
import { Tooltip } from '../ui';
import { TipBody, type TipData } from './Tip';

/**
 * Gives a chart mark (an SVG group, a dot, a bar) its tooltip on hover and on keyboard focus. The mark must be focusable and
 * carry its own accessible name: the tooltip adds to it and never holds the only copy of a number.
 */
export function MarkTip({ tip, children }: { tip: TipData; children: ReactElement }) {
  return (
    <Tooltip content={<TipBody {...tip} />} side="top">
      {children}
    </Tooltip>
  );
}
