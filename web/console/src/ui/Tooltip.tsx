import * as RadixTooltip from '@radix-ui/react-tooltip';
import type { ReactNode } from 'react';

/** Wrap the app once: tooltips then share one delay, so moving between neighbours does not wait again. */
export function TooltipProvider({ children }: { children: ReactNode }) {
  return <RadixTooltip.Provider delayDuration={400}>{children}</RadixTooltip.Provider>;
}

/**
 * A hover and focus hint for a control whose label is hidden or short (the collapsed rail). It never holds the only copy of
 * something: the control keeps its own accessible name.
 */
export function Tooltip({
  content,
  side = 'right',
  disabled,
  children,
}: {
  content: ReactNode;
  side?: 'top' | 'right' | 'bottom' | 'left';
  disabled?: boolean;
  children: ReactNode;
}) {
  if (disabled) return <>{children}</>;
  return (
    <RadixTooltip.Root>
      <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
      <RadixTooltip.Portal>
        <RadixTooltip.Content className="ch-tooltip" side={side} sideOffset={6}>
          {content}
        </RadixTooltip.Content>
      </RadixTooltip.Portal>
    </RadixTooltip.Root>
  );
}
