import type { ReactNode } from 'react';
import type { Slot } from './colors';
import { MarkTip } from './MarkTip';
import { type TipData, tipText } from './Tip';

/**
 * A mark of an SVG chart (a dot, a bar, a whisker row) that can be reached with the keyboard: focus or hover shows its tooltip, and
 * its accessible name carries the same facts, so the tooltip is never the only copy of a number. `hit` shapes inside it make the
 * target larger than the painted mark.
 */
export function Mark({ tip, slot, children }: { tip: TipData; slot?: Slot; children: ReactNode }) {
  return (
    <MarkTip tip={tip}>
      {/* biome-ignore lint/a11y/noInteractiveElementToNoninteractiveRole: a focusable chart mark is a named image that shows its tooltip on focus */}
      <g className="mark" data-slot={slot} tabIndex={0} role="img" aria-label={tipText(tip)}>
        {children}
      </g>
    </MarkTip>
  );
}
