import * as RadixToast from '@radix-ui/react-toast';
import type { ReactNode } from 'react';
import type { Tone } from '../lib/status';
import { Button } from './Button';
import { Icon, type IconName } from './Icon';
import { IconButton } from './IconButton';

export interface ToastAction {
  label: string;
  /** The first action is the one that resolves the notice. */
  onSelect: () => void;
}

export interface ToastProps {
  tone?: Tone;
  icon?: IconName;
  title: string;
  /** One line. */
  body?: ReactNode;
  /** At most two. A toast with an action never dismisses itself. */
  actions?: ToastAction[];
  onDismiss: () => void;
}

/** Wrap the app once: the provider keeps the swipe and the timers, the viewport is where toasts stack. */
export function ToastHost({ children, label }: { children: ReactNode; label: string }) {
  return (
    <RadixToast.Provider swipeDirection="right" label={label} duration={8000}>
      {children}
      {/* A div, not Radix's ol: it also holds the visually hidden live-region text, and an ol may hold only li. */}
      <RadixToast.Viewport asChild label={`${label} ({hotkey})`}>
        <div className="toasts" />
      </RadixToast.Viewport>
    </RadixToast.Provider>
  );
}

/** A short notice that something needs attention or finished. A failure is announced as an alert, the rest as a status. */
export function Toast({
  tone = 'info',
  icon = 'info',
  title,
  body,
  actions = [],
  onDismiss,
}: ToastProps) {
  const failure = tone === 'danger';
  return (
    <RadixToast.Root
      asChild
      type={failure ? 'foreground' : 'background'}
      role={failure ? 'alert' : 'status'}
      duration={actions.length > 0 || failure ? Number.POSITIVE_INFINITY : undefined}
      onOpenChange={(open) => {
        if (!open) onDismiss();
      }}
    >
      <div className="ch-toast" data-tone={tone}>
        <span className="ch-toast__g">
          <Icon name={icon} />
        </span>
        <RadixToast.Title className="ch-toast__title">{title}</RadixToast.Title>
        <RadixToast.Close asChild>
          <IconButton icon="x" size="sm" label="Dismiss" className="ch-toast__x" />
        </RadixToast.Close>
        {body ? (
          <RadixToast.Description className="ch-toast__body">{body}</RadixToast.Description>
        ) : null}
        {actions.length > 0 ? (
          <div className="ch-toast__actions">
            {actions.slice(0, 2).map((action, i) => (
              <RadixToast.Action key={action.label} altText={action.label} asChild>
                <Button
                  size="sm"
                  variant={i === 0 && !failure ? 'primary' : i === 0 ? undefined : 'quiet'}
                  label={action.label}
                  onClick={action.onSelect}
                />
              </RadixToast.Action>
            ))}
          </div>
        ) : null}
      </div>
    </RadixToast.Root>
  );
}
